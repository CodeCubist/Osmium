using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using Osmium.Core.Interop;
using Osmium.Core.Services;
using SIPSorcery.Net;

namespace Osmium.Core.Network;

public sealed class WebRtcPeerTransport : IDisposable
{
    private const int MaxPacketBytes = 32768;
    private const int MaxQueuedVideoBytes = 512 * 1024;
    private const int MaxBufferedControlBytes = 1024 * 1024;
    private const int MaxBufferedMediaBytes = 256 * 1024;

    private readonly IntPtr _nativeTransport;
    private readonly SignalingClient _signaling;
    private readonly string _sessionId;
    private readonly LoggingService _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _mediaReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ConcurrentQueue<(byte[] Buf, int Len)> _ctrlQueue = new();
    private readonly object _videoQueueLock = new();
    private readonly Queue<(byte[] Buf, int Len, int GroupId)> _videoQueue = new();
    private readonly SemaphoreSlim _sendSignal = new(0, 1);
    private readonly object _iceLock = new();
    private readonly List<RTCIceCandidateInit> _pendingRemoteIce = [];

    private RTCPeerConnection? _pc;
    private RTCDataChannel? _ctrlChannel;
    private RTCDataChannel? _mediaChannel;
    private OsmiumRelaySendDelegate? _sendDelegate;
    private Task? _sendTask;
    private bool _remoteDescSet;
    private int _signalPending;
    private int _queuedVideoBytes;
    private int _droppedVideoGroupId;
    private int _disposed;

    public string EndpointLabel { get; private set; } = "WebRTC P2P";

    public WebRtcPeerTransport(
        IntPtr nativeTransport,
        SignalingClient signaling,
        string sessionId,
        LoggingService log)
    {
        _nativeTransport = nativeTransport;
        _signaling = signaling;
        _sessionId = sessionId;
        _log = log;
    }

    public bool InitializePeerConnection(string stunHost, ushort stunPort, byte[] sessionToken16)
    {
        try
        {
            var iceServers = new List<RTCIceServer>();
            if (!string.IsNullOrWhiteSpace(stunHost))
            {
                ushort port = stunPort > 0 ? stunPort : (ushort)3478;
                iceServers.Add(new RTCIceServer { urls = $"stun:{stunHost}:{port}" });
            }
            iceServers.Add(new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" });
            iceServers.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });

            var config = new RTCConfiguration
            {
                iceServers = iceServers,
                X_ICEIncludeAllInterfaceAddresses = true
            };

            _pc = new RTCPeerConnection(config);

            _pc.onicecandidate += cand =>
            {
                if (Volatile.Read(ref _disposed) != 0 || cand == null) return;
                try
                {
                    string raw = cand.ToString();
                    if (string.IsNullOrWhiteSpace(raw)) return;
                    if (!raw.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
                    {
                        raw = "candidate:" + raw;
                    }
                    _ = _signaling.SendWebRtcIceAsync(
                        _sessionId,
                        raw,
                        cand.sdpMid,
                        cand.sdpMLineIndex);
                }
                catch
                {
                }
            };

            _pc.onconnectionstatechange += state =>
            {
                _log.Info($"WebRTC P2P connection state ({_sessionId}): {state}");
                if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
                {
                    _readyTcs.TrySetResult(false);
                    _mediaReadyTcs.TrySetResult(false);
                }
            };

            _pc.ondatachannel += dc =>
            {
                if (Volatile.Read(ref _disposed) != 0 || dc == null) return;
                AttachDataChannel(dc);
            };

            _sendDelegate = OnNativeSendRelay;
            if (NativeMethods.Osmium_EnableTransportRelay(
                    _nativeTransport,
                    sessionToken16,
                    2,
                    _sendDelegate,
                    EndpointLabel) == 0)
            {
                _log.Warn("Osmium_EnableTransportRelay returned 0 for WebRTC transport");
                return false;
            }

            _sendTask = Task.Run(() => SendLoopAsync(_cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"WebRtcPeerTransport initialize failed: {ex.Message}");
            return false;
        }
    }

    public async Task<string?> HandleRemoteOfferAndCreateAnswerAsync(string offerSdp)
    {
        if (_pc == null || Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(offerSdp))
        {
            return null;
        }

        try
        {
            var setResult = _pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offerSdp
            });

            if (setResult != SetDescriptionResultEnum.OK)
            {
                _log.Warn($"WebRTC setRemoteDescription failed: {setResult}");
                return null;
            }

            List<RTCIceCandidateInit> queued;
            lock (_iceLock)
            {
                _remoteDescSet = true;
                queued = [.. _pendingRemoteIce];
                _pendingRemoteIce.Clear();
            }

            foreach (var cand in queued)
            {
                try
                {
                    _pc.addIceCandidate(cand);
                }
                catch
                {
                }
            }

            var answer = _pc.createAnswer(null);
            await _pc.setLocalDescription(answer);
            return answer.sdp;
        }
        catch (Exception ex)
        {
            _log.Warn($"WebRTC offer handling failed: {ex.Message}");
            return null;
        }
    }

    public void AddRemoteIceCandidate(string candidate, string? sdpMid, int sdpMLineIndex)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(candidate)) return;
        var init = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = string.IsNullOrWhiteSpace(sdpMid) ? "0" : sdpMid,
            sdpMLineIndex = (ushort)Math.Clamp(sdpMLineIndex, 0, ushort.MaxValue)
        };

        bool canApply;
        lock (_iceLock)
        {
            canApply = _remoteDescSet && _pc != null;
            if (!canApply)
            {
                _pendingRemoteIce.Add(init);
            }
        }

        if (canApply)
        {
            try
            {
                _pc?.addIceCandidate(init);
            }
            catch
            {
            }
        }
    }

    public async Task<bool> WaitForChannelsReadyAsync(int timeoutMs, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(3000, timeoutMs)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, timeoutCts.Token);
        using var reg = linked.Token.Register(() => _readyTcs.TrySetResult(false));

        bool ctrlReady = await _readyTcs.Task;
        if (!ctrlReady) return false;

        if (!_mediaReadyTcs.Task.IsCompleted)
        {
            using var mediaWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            using var mediaLinked = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, mediaWait.Token);
            try
            {
                await _mediaReadyTcs.Task.WaitAsync(mediaLinked.Token);
            }
            catch
            {
            }
        }

        return true;
    }

    private void AttachDataChannel(RTCDataChannel dc)
    {
        string label = (dc.label ?? string.Empty).Trim().ToLowerInvariant();
        if (label == "media")
        {
            _mediaChannel = dc;
        }
        else
        {
            _ctrlChannel = dc;
        }

        dc.onopen += () =>
        {
            _log.Info($"WebRTC DataChannel opened: {label}");
            if (label == "media")
            {
                _mediaReadyTcs.TrySetResult(true);
            }
            else
            {
                _readyTcs.TrySetResult(true);
            }
            SignalSender();
        };

        dc.onclose += () =>
        {
            _log.Info($"WebRTC DataChannel closed: {label}");
            if (label != "media")
            {
                _readyTcs.TrySetResult(false);
            }
        };

        dc.onmessage += (channel, protocol, data) =>
        {
            if (Volatile.Read(ref _disposed) != 0 || data == null || data.Length < 8 || data.Length > MaxPacketBytes)
            {
                return;
            }
            NativeMethods.Osmium_PushTransportRelayPacket(_nativeTransport, data, data.Length);
        };

        if (dc.readyState == RTCDataChannelState.open)
        {
            if (label == "media")
            {
                _mediaReadyTcs.TrySetResult(true);
            }
            else
            {
                _readyTcs.TrySetResult(true);
            }
            SignalSender();
        }
    }

    private void SignalSender()
    {
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
    }

    private int OnNativeSendRelay(IntPtr dataPtr, int length, int dropGroupId)
    {
        if (Volatile.Read(ref _disposed) != 0 || dataPtr == IntPtr.Zero || length <= 0 || length > MaxPacketBytes)
        {
            return 0;
        }

        if (dropGroupId > 0)
        {
            if (dropGroupId == Volatile.Read(ref _droppedVideoGroupId))
            {
                return 1;
            }

            lock (_videoQueueLock)
            {
                if (dropGroupId == _droppedVideoGroupId)
                {
                    return 1;
                }

                while (_queuedVideoBytes + length > MaxQueuedVideoBytes && _videoQueue.Count > 0)
                {
                    int targetGroup = _videoQueue.Peek().GroupId;
                    Volatile.Write(ref _droppedVideoGroupId, targetGroup);
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
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            Marshal.Copy(dataPtr, rented, 0, length);
            _ctrlQueue.Enqueue((rented, length));
        }

        SignalSender();
        return 1;
    }

    private bool TryDequeueVideoChunk(out (byte[] Buf, int Len, int GroupId) item)
    {
        lock (_videoQueueLock)
        {
            while (_videoQueue.Count > 0)
            {
                var next = _videoQueue.Dequeue();
                _queuedVideoBytes -= next.Len;
                if (next.GroupId == _droppedVideoGroupId)
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
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _sendSignal.WaitAsync(ct);
                Interlocked.Exchange(ref _signalPending, 0);

                while (!ct.IsCancellationRequested)
                {
                    bool didWork = false;
                    var ctrl = _ctrlChannel;
                    var media = _mediaChannel;
                    bool ctrlOpen = ctrl != null && ctrl.readyState == RTCDataChannelState.open;
                    bool mediaOpen = media != null && media.readyState == RTCDataChannelState.open;

                    if (!ctrlOpen && !mediaOpen)
                    {
                        break;
                    }

                    while (ctrlOpen && _ctrlQueue.TryDequeue(out var hi))
                    {
                        didWork = true;
                        try
                        {
                            if (ctrl!.bufferedAmount <= MaxBufferedControlBytes)
                            {
                                ctrl.send(hi.Buf, 0, hi.Len);
                            }
                        }
                        catch
                        {
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(hi.Buf);
                        }
                    }

                    if (TryDequeueVideoChunk(out var vid))
                    {
                        didWork = true;
                        try
                        {
                            var targetCh = mediaOpen ? media : ctrl;
                            if (targetCh != null && targetCh.readyState == RTCDataChannelState.open)
                            {
                                if (targetCh.bufferedAmount > MaxBufferedMediaBytes)
                                {
                                    Volatile.Write(ref _droppedVideoGroupId, vid.GroupId);
                                }
                                else
                                {
                                    targetCh.send(vid.Buf, 0, vid.Len);
                                }
                            }
                        }
                        catch
                        {
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(vid.Buf);
                        }
                    }

                    if (!didWork)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"WebRtcPeerTransport send loop error: {ex.Message}");
        }
        finally
        {
            DrainQueues();
        }
    }

    private void DrainQueues()
    {
        while (_ctrlQueue.TryDequeue(out var hi))
        {
            ArrayPool<byte>.Shared.Return(hi.Buf);
        }

        lock (_videoQueueLock)
        {
            while (_videoQueue.Count > 0)
            {
                var vid = _videoQueue.Dequeue();
                ArrayPool<byte>.Shared.Return(vid.Buf);
            }
            _queuedVideoBytes = 0;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _readyTcs.TrySetResult(false);
        _mediaReadyTcs.TrySetResult(false);
        try
        {
            _cts.Cancel();
        }
        catch
        {
        }

        try
        {
            _ctrlChannel?.close();
        }
        catch
        {
        }

        try
        {
            _mediaChannel?.close();
        }
        catch
        {
        }

        try
        {
            _pc?.close();
            _pc?.Dispose();
        }
        catch
        {
        }

        DrainQueues();
        _cts.Dispose();
        _sendSignal.Dispose();
    }
}
