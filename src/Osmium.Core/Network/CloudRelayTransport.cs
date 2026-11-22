using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Osmium.Core.Interop;
using Osmium.Core.Services;

namespace Osmium.Core.Network;

public sealed class CloudRelayTransport : IDisposable
{
    private const int MaxBatchPayloadBytes = 16384;
    private const int MaxQueuedDroppablePackets = 64;
    private const int MaxIncomingFrameBytes = 131072;

    private readonly IntPtr _nativeTransport;
    private readonly LoggingService _log;
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _peerReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ConcurrentQueue<(byte[] Data, bool Droppable)> _sendQueue = new();
    private readonly SemaphoreSlim _sendSignal = new(0, 1);
    private int _signalPending;
    private int _queuedDroppableCount;
    private int _disposed;

    private OsmiumRelaySendDelegate? _sendDelegate;
    private Task? _sendTask;
    private Task? _recvTask;

    public string EndpointLabel { get; }

    public CloudRelayTransport(IntPtr nativeTransport, string endpointLabel, LoggingService log)
    {
        _nativeTransport = nativeTransport;
        EndpointLabel = string.IsNullOrWhiteSpace(endpointLabel) ? "Cloud Relay (WSS)" : endpointLabel;
        _log = log;
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    }

    public static Uri BuildRelayUri(string signalingServerUrl, string relayPath, string sessionId, string role, string relayToken)
    {
        string normalized = SignalingClient.NormalizeServerUrl(signalingServerUrl);
        var baseUri = new Uri(normalized);
        string path = string.IsNullOrWhiteSpace(relayPath) ? "/relay" : relayPath.Trim();
        if (!path.StartsWith('/')) path = "/" + path;

        var ub = new UriBuilder(baseUri)
        {
            Path = path,
            Query = $"sessionId={Uri.EscapeDataString(sessionId)}&role={Uri.EscapeDataString(role)}&token={Uri.EscapeDataString(relayToken)}"
        };
        return ub.Uri;
    }

    public async Task<bool> ConnectAndBindAsync(
        Uri relayUri,
        byte[] sessionToken16,
        int localRole,
        int peerReadyTimeoutMs,
        CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var connectLinked = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, connectTimeout.Token);

            await _ws.ConnectAsync(relayUri, connectLinked.Token);
            _log.Info($"CloudRelayTransport connected ({(localRole == 2 ? "host" : "client")}): {EndpointLabel}");

            _sendDelegate = OnNativeSendRelay;
            if (NativeMethods.Osmium_EnableTransportRelay(
                    _nativeTransport,
                    sessionToken16,
                    localRole,
                    _sendDelegate,
                    EndpointLabel) == 0)
            {
                _log.Warn("Osmium_EnableTransportRelay returned 0");
                return false;
            }

            _recvTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            _sendTask = Task.Run(() => SendLoopAsync(_cts.Token));

            using var peerTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(2000, peerReadyTimeoutMs)));
            using var waitLinked = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, peerTimeout.Token);
            using var reg = waitLinked.Token.Register(() => _peerReadyTcs.TrySetResult(false));

            bool ready = await _peerReadyTcs.Task;
            if (!ready)
            {
                _log.Warn($"CloudRelayTransport timed out waiting for peer ({(localRole == 2 ? "host" : "client")})");
            }

            return ready;
        }
        catch (Exception ex)
        {
            _log.Warn($"CloudRelayTransport connect failed: {ex.Message}");
            return false;
        }
    }

    private int OnNativeSendRelay(IntPtr dataPtr, int length, int isDroppable)
    {
        if (Volatile.Read(ref _disposed) != 0 || dataPtr == IntPtr.Zero || length <= 0 || length > 4096)
        {
            return 0;
        }

        if (_ws.State != WebSocketState.Open)
        {
            return 0;
        }

        bool droppable = isDroppable != 0;
        if (droppable && Volatile.Read(ref _queuedDroppableCount) >= MaxQueuedDroppablePackets)
        {
            // Shed excess queued video chunks under relay backpressure to protect input latency
            return 1;
        }

        byte[] copy = new byte[length];
        Marshal.Copy(dataPtr, copy, 0, length);
        if (droppable)
        {
            Interlocked.Increment(ref _queuedDroppableCount);
        }

        _sendQueue.Enqueue((copy, droppable));
        if (Interlocked.Exchange(ref _signalPending, 1) == 0)
        {
            try
            {
                _sendSignal.Release();
            }
            catch
            {
            }
        }

        return 1;
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        byte[] batchBuf = new byte[MaxBatchPayloadBytes + 4096];
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                await _sendSignal.WaitAsync(ct);
                Interlocked.Exchange(ref _signalPending, 0);

                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    int offset = 0;
                    bool hasImmediate = false;

                    while (_sendQueue.TryDequeue(out var item))
                    {
                        if (item.Droppable)
                        {
                            Interlocked.Decrement(ref _queuedDroppableCount);
                        }
                        else
                        {
                            hasImmediate = true;
                        }

                        int needed = 2 + item.Data.Length;
                        if (offset > 0 && offset + needed > MaxBatchPayloadBytes)
                        {
                            await _ws.SendAsync(new ArraySegment<byte>(batchBuf, 0, offset), WebSocketMessageType.Binary,
                                endOfMessage: true, ct);
                            offset = 0;
                            hasImmediate = !item.Droppable;
                        }

                        BinaryPrimitives.WriteUInt16LittleEndian(batchBuf.AsSpan(offset, 2), (ushort)item.Data.Length);
                        Buffer.BlockCopy(item.Data, 0, batchBuf, offset + 2, item.Data.Length);
                        offset += needed;

                        if (hasImmediate || offset >= MaxBatchPayloadBytes)
                        {
                            break;
                        }
                    }

                    if (offset == 0)
                    {
                        break;
                    }

                    await _ws.SendAsync(new ArraySegment<byte>(batchBuf, 0, offset), WebSocketMessageType.Binary,
                        endOfMessage: true, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _log.Warn($"CloudRelayTransport send loop ended: {ex.Message}");
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        byte[] recvBuf = new byte[32768];
        using var frameMs = new MemoryStream(32768);
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                frameMs.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(recvBuf), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _peerReadyTcs.TrySetResult(false);
                        return;
                    }

                    if (frameMs.Length + result.Count > MaxIncomingFrameBytes)
                    {
                        frameMs.SetLength(0);
                        break;
                    }

                    if (result.Count > 0)
                    {
                        frameMs.Write(recvBuf, 0, result.Count);
                    }
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (frameMs.Length > 0)
                    {
                        string text = Encoding.UTF8.GetString(frameMs.GetBuffer(), 0, (int)frameMs.Length);
                        HandleControlText(text);
                    }

                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Binary && frameMs.Length >= 2)
                {
                    // Any binary packet from peer also implies peer is already ready
                    _peerReadyTcs.TrySetResult(true);
                    DispatchBinaryBatch(frameMs.GetBuffer(), (int)frameMs.Length);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _log.Warn($"CloudRelayTransport receive loop ended: {ex.Message}");
            }
        }
        finally
        {
            _peerReadyTcs.TrySetResult(false);
        }
    }

    private void HandleControlText(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("type", out var tProp) &&
                string.Equals(tProp.GetString(), "relay_peer_ready", StringComparison.Ordinal))
            {
                _peerReadyTcs.TrySetResult(true);
            }
        }
        catch
        {
        }
    }

    private readonly byte[] _dispatchScratch = new byte[4096];

    private void DispatchBinaryBatch(byte[] buffer, int totalLen)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        int pos = 0;
        while (pos + 2 <= totalLen)
        {
            int pktLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos, 2));
            pos += 2;
            if (pktLen <= 0 || pktLen > _dispatchScratch.Length || pos + pktLen > totalLen) break;

            Buffer.BlockCopy(buffer, pos, _dispatchScratch, 0, pktLen);
            pos += pktLen;

            if (Volatile.Read(ref _disposed) != 0) return;
            NativeMethods.Osmium_PushTransportRelayPacket(_nativeTransport, _dispatchScratch, pktLen);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _peerReadyTcs.TrySetResult(false);

        try
        {
            _cts.Cancel();
        }
        catch
        {
        }

        try
        {
            if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
            {
                _ws.Abort();
            }
        }
        catch
        {
        }

        try
        {
            _recvTask?.Wait(300);
        }
        catch
        {
        }

        try
        {
            _sendTask?.Wait(300);
        }
        catch
        {
        }

        _ws.Dispose();
        _sendSignal.Dispose();
        _cts.Dispose();
        _sendDelegate = null;
    }
}
