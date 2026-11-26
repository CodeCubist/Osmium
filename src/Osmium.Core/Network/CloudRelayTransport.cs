using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Osmium.Core.Interop;
using Osmium.Core.Services;

namespace Osmium.Core.Network;

public sealed class CloudRelayTransport : IDisposable
{
    private const int MaxPacketBytes = 32768;
    private const int MaxBatchPayloadBytes = 24576;
    private const int MaxQueuedVideoBytes = 256 * 1024;
    private const int MaxQueuedAudioPackets = 8;
    private const int MaxIncomingFrameBytes = 131072;
    private const int SocketBufferBytes = 512 * 1024;

    private readonly IntPtr _nativeTransport;
    private readonly LoggingService _log;
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _peerReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ConcurrentQueue<(byte[] Buf, int Len)> _ctrlQueue = new();
    private readonly object _audioQueueLock = new();
    private readonly Queue<(byte[] Buf, int Len)> _audioQueue = new();
    private int _queuedAudioCount;
    private readonly object _videoQueueLock = new();
    private readonly Queue<(byte[] Buf, int Len, int GroupId)> _videoQueue = new();
    private readonly SemaphoreSlim _sendSignal = new(0, 1);
    private int _signalPending;
    private int _queuedVideoBytes;
    private int _droppedVideoGroupId;
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
        _ws.Options.HttpVersion = HttpVersion.Version11;
        _ws.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        _ws.Options.DangerousDeflateOptions = null;
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
            
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, token) =>
                {
                    string host = context.DnsEndPoint.Host;
                    int port = context.DnsEndPoint.Port;
                    Socket socket;
                    try
                    {
                        IPAddress[] v4Addrs = IPAddress.TryParse(host, out var parsed)
                            ? (parsed.AddressFamily == AddressFamily.InterNetwork ? [parsed] : [])
                            : await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token);

                        if (v4Addrs.Length > 0)
                        {
                            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                            {
                                NoDelay = true,
                                SendBufferSize = SocketBufferBytes,
                                ReceiveBufferSize = SocketBufferBytes
                            };
                            try
                            {
                                await socket.ConnectAsync(v4Addrs, port, token);
                                return new NetworkStream(socket, ownsSocket: true);
                            }
                            catch
                            {
                                socket.Dispose();
                                throw;
                            }
                        }
                    }
                    catch when (!token.IsCancellationRequested)
                    {
                        
                    }

                    socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true,
                        SendBufferSize = SocketBufferBytes,
                        ReceiveBufferSize = SocketBufferBytes
                    };
                    try
                    {
                        await socket.ConnectAsync(context.DnsEndPoint, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            using var invoker = new HttpMessageInvoker(handler);

            await _ws.ConnectAsync(relayUri, invoker, connectLinked.Token);
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

    private int OnNativeSendRelay(IntPtr dataPtr, int length, int dropGroupId)
    {
        if (Volatile.Read(ref _disposed) != 0 || dataPtr == IntPtr.Zero || length <= 0 || length > MaxPacketBytes)
        {
            return 0;
        }

        if (_ws.State != WebSocketState.Open)
        {
            return 0;
        }

        // dropGroupId convention:
        //   0      : critical control (Ping/Pong/Input/Auth/IDR/Status/Aux)
        //  -1      : real-time audio (bounded low-latency queue, drops oldest on overflow)
        //  <= -2   : video keyframe group (-(frameId + 2))
        //  > 0     : droppable video P-frame group (frameId)
        if (dropGroupId == 0)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            Marshal.Copy(dataPtr, rented, 0, length);
            _ctrlQueue.Enqueue((rented, length));
        }
        else if (dropGroupId == -1)
        {
            byte[] rentedAudio = ArrayPool<byte>.Shared.Rent(length);
            Marshal.Copy(dataPtr, rentedAudio, 0, length);
            lock (_audioQueueLock)
            {
                while (_audioQueue.Count >= MaxQueuedAudioPackets)
                {
                    var staleAudio = _audioQueue.Dequeue();
                    ArrayPool<byte>.Shared.Return(staleAudio.Buf);
                }

                _audioQueue.Enqueue((rentedAudio, length));
                Volatile.Write(ref _queuedAudioCount, _audioQueue.Count);
            }
        }
        else
        {
            bool isKeyframe = dropGroupId <= -2;
            if (!isKeyframe && dropGroupId == Volatile.Read(ref _droppedVideoGroupId))
            {
                return 1;
            }

            lock (_videoQueueLock)
            {
                if (!isKeyframe && dropGroupId == _droppedVideoGroupId)
                {
                    return 1;
                }

                int byteLimit = isKeyframe ? MaxQueuedVideoBytes * 2 : MaxQueuedVideoBytes;
                while (_queuedVideoBytes + length > byteLimit && _videoQueue.Count > 0)
                {
                    int targetGroup = _videoQueue.Peek().GroupId;
                    bool targetIsKeyframe = targetGroup <= -2;

                    // Protect an in-flight keyframe from being evicted by a P-frame or by itself.
                    if (targetIsKeyframe && (!isKeyframe || targetGroup == dropGroupId))
                    {
                        if (!isKeyframe)
                        {
                            Volatile.Write(ref _droppedVideoGroupId, dropGroupId);
                            return 1;
                        }
                        break;
                    }

                    if (!targetIsKeyframe)
                    {
                        Volatile.Write(ref _droppedVideoGroupId, targetGroup);
                    }

                    while (_videoQueue.Count > 0 && _videoQueue.Peek().GroupId == targetGroup)
                    {
                        var stale = _videoQueue.Dequeue();
                        _queuedVideoBytes -= stale.Len;
                        ArrayPool<byte>.Shared.Return(stale.Buf);
                    }

                    if (targetGroup == dropGroupId)
                    {
                        return 1;
                    }
                }

                byte[] rentedVideo = ArrayPool<byte>.Shared.Rent(length);
                Marshal.Copy(dataPtr, rentedVideo, 0, length);
                _queuedVideoBytes += length;
                _videoQueue.Enqueue((rentedVideo, length, dropGroupId));
            }
        }

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

    private bool TryDequeueAudioPacket(out (byte[] Buf, int Len) item)
    {
        if (Volatile.Read(ref _queuedAudioCount) == 0)
        {
            item = default;
            return false;
        }

        lock (_audioQueueLock)
        {
            if (_audioQueue.Count > 0)
            {
                item = _audioQueue.Dequeue();
                Volatile.Write(ref _queuedAudioCount, _audioQueue.Count);
                return true;
            }

            Volatile.Write(ref _queuedAudioCount, 0);
        }

        item = default;
        return false;
    }

    private bool TryDequeueVideoChunk(out (byte[] Buf, int Len, int GroupId) item)
    {
        lock (_videoQueueLock)
        {
            while (_videoQueue.Count > 0)
            {
                var next = _videoQueue.Dequeue();
                _queuedVideoBytes -= next.Len;
                if (next.GroupId > 0 && next.GroupId == _droppedVideoGroupId)
                {
                    ArrayPool<byte>.Shared.Return(next.Buf);
                    continue;
                }

                item = next;
                return true;
            }
        }

        item = default;
        return false;
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        byte[] batchBuf = new byte[MaxBatchPayloadBytes + MaxPacketBytes + 16];
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                await _sendSignal.WaitAsync(ct);
                Interlocked.Exchange(ref _signalPending, 0);

                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    int offset = 0;

                    // 1. Always pack critical control packets first (Ping/Pong/Input/Auth/IDR).
                    while (_ctrlQueue.TryDequeue(out var ctrl))
                    {
                        try
                        {
                            int needed = 2 + ctrl.Len;
                            if (offset > 0 && offset + needed > MaxBatchPayloadBytes)
                            {
                                await _ws.SendAsync(new ArraySegment<byte>(batchBuf, 0, offset),
                                    WebSocketMessageType.Binary, endOfMessage: true, ct);
                                offset = 0;
                            }

                            BinaryPrimitives.WriteUInt16LittleEndian(batchBuf.AsSpan(offset, 2), (ushort)ctrl.Len);
                            Buffer.BlockCopy(ctrl.Buf, 0, batchBuf, offset + 2, ctrl.Len);
                            offset += needed;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(ctrl.Buf);
                        }

                        if (offset >= MaxBatchPayloadBytes)
                        {
                            break;
                        }
                    }

                    // 2. Coalesce real-time audio packets into the SAME WebSocket binary batch
                    // instead of flushing a separate tiny WS/TLS frame per audio packet.
                    while (offset < MaxBatchPayloadBytes && TryDequeueAudioPacket(out var aud))
                    {
                        try
                        {
                            int needed = 2 + aud.Len;
                            if (offset > 0 && offset + needed > MaxBatchPayloadBytes)
                            {
                                await _ws.SendAsync(new ArraySegment<byte>(batchBuf, 0, offset),
                                    WebSocketMessageType.Binary, endOfMessage: true, ct);
                                offset = 0;
                            }

                            BinaryPrimitives.WriteUInt16LittleEndian(batchBuf.AsSpan(offset, 2), (ushort)aud.Len);
                            Buffer.BlockCopy(aud.Buf, 0, batchBuf, offset + 2, aud.Len);
                            offset += needed;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(aud.Buf);
                        }
                    }

                    // 3. Fill remaining batch space with video chunks while control & audio stay clear.
                    while (offset < MaxBatchPayloadBytes &&
                           _ctrlQueue.IsEmpty &&
                           Volatile.Read(ref _queuedAudioCount) == 0 &&
                           TryDequeueVideoChunk(out var vid))
                    {
                        try
                        {
                            int needed = 2 + vid.Len;
                            if (offset > 0 && offset + needed > MaxBatchPayloadBytes)
                            {
                                await _ws.SendAsync(new ArraySegment<byte>(batchBuf, 0, offset),
                                    WebSocketMessageType.Binary, endOfMessage: true, ct);
                                offset = 0;
                            }

                            BinaryPrimitives.WriteUInt16LittleEndian(batchBuf.AsSpan(offset, 2), (ushort)vid.Len);
                            Buffer.BlockCopy(vid.Buf, 0, batchBuf, offset + 2, vid.Len);
                            offset += needed;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(vid.Buf);
                        }

                        if (!_ctrlQueue.IsEmpty || Volatile.Read(ref _queuedAudioCount) > 0)
                        {
                            break;
                        }
                    }

                    if (offset == 0)
                    {
                        break;
                    }

                    await _ws.SendAsync(new ArraySegment<byte>(batchBuf, 0, offset),
                        WebSocketMessageType.Binary, endOfMessage: true, ct);
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
        byte[] recvBuf = new byte[65536];
        using var frameMs = new MemoryStream(65536);
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await _ws.ReceiveAsync(new ArraySegment<byte>(recvBuf), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _peerReadyTcs.TrySetResult(false);
                    return;
                }
                
                if (result.EndOfMessage)
                {
                    if (result.MessageType == WebSocketMessageType.Binary && result.Count >= 10)
                    {
                        _peerReadyTcs.TrySetResult(true);
                        if (Volatile.Read(ref _disposed) == 0)
                        {
                            NativeMethods.Osmium_PushTransportRelayBatch(_nativeTransport, recvBuf, result.Count);
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                    {
                        string text = Encoding.UTF8.GetString(recvBuf, 0, result.Count);
                        HandleControlText(text);
                    }

                    continue;
                }
                
                frameMs.SetLength(0);
                bool overflowed = false;
                if (result.Count > 0)
                {
                    frameMs.Write(recvBuf, 0, result.Count);
                }

                while (!result.EndOfMessage)
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(recvBuf), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _peerReadyTcs.TrySetResult(false);
                        return;
                    }

                    if (!overflowed)
                    {
                        if (frameMs.Length + result.Count > MaxIncomingFrameBytes)
                        {
                            overflowed = true;
                            frameMs.SetLength(0);
                        }
                        else if (result.Count > 0)
                        {
                            frameMs.Write(recvBuf, 0, result.Count);
                        }
                    }
                }

                if (overflowed) continue;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (frameMs.Length > 0)
                    {
                        string text = Encoding.UTF8.GetString(frameMs.GetBuffer(), 0, (int)frameMs.Length);
                        HandleControlText(text);
                    }

                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Binary && frameMs.Length >= 10)
                {
                    _peerReadyTcs.TrySetResult(true);
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        NativeMethods.Osmium_PushTransportRelayBatch(_nativeTransport, frameMs.GetBuffer(),
                            (int)frameMs.Length);
                    }
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

    private void DrainAndReturnPooledQueues()
    {
        while (_ctrlQueue.TryDequeue(out var hi))
        {
            ArrayPool<byte>.Shared.Return(hi.Buf);
        }

        lock (_audioQueueLock)
        {
            while (_audioQueue.Count > 0)
            {
                var aud = _audioQueue.Dequeue();
                ArrayPool<byte>.Shared.Return(aud.Buf);
            }

            Volatile.Write(ref _queuedAudioCount, 0);
        }

        lock (_videoQueueLock)
        {
            while (_videoQueue.Count > 0)
            {
                var vid = _videoQueue.Dequeue();
                _queuedVideoBytes -= vid.Len;
                ArrayPool<byte>.Shared.Return(vid.Buf);
            }

            _queuedVideoBytes = 0;
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

        DrainAndReturnPooledQueues();
        _ws.Dispose();
        _sendSignal.Dispose();
        _cts.Dispose();
        _sendDelegate = null;
    }
}
