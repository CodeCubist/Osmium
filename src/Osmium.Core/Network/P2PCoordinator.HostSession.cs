using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Osmium.Core.Interop;
using Osmium.Core.Models;
using Osmium.Core.Services;

namespace Osmium.Core.Network;

public sealed partial class P2PCoordinator : IDisposable
{
    private void OnIncomingConnect(IncomingConnectEventArgs args)
    {
        _ = Task.Run(() => HandleIncomingConnectAsync(args));
    }

    private async Task HandleIncomingConnectAsync(IncomingConnectEventArgs args)
    {
        try
        {
            if (!EffectiveAllowRemoteControl)
            {
                await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "REMOTE_DISABLED",
                    "Host disabled remote control");
                return;
            }

            if (HasActiveHostPipeline)
            {
                await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "HOST_BUSY",
                    "Another remote control session is already active");
                return;
            }

            bool hostEncrypt = _settings.Settings.EnableEncryption;
            if (args.EnableEncryption != hostEncrypt)
            {
                await _signaling.SendConnectRejectAsync(
                    args.SessionId,
                    args.ClientDeviceId,
                    "ENCRYPTION_MISMATCH",
                    "Encryption setting mismatch between host and controller");
                return;
            }

            if (!args.SameAccountVerified && _settings.Settings.RequireApprovalDialog &&
                ConfirmIncomingConnectionAsync != null)
            {
                bool allowed = await ConfirmIncomingConnectionAsync(args);
                if (!allowed)
                {
                    await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "USER_DECLINED",
                        "Host user declined connection");
                    return;
                }

                if (!EffectiveAllowRemoteControl)
                {
                    await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "REMOTE_DISABLED",
                        "Host disabled remote control");
                    return;
                }
            }

            byte[] sessionToken = Convert.FromHexString(args.SessionId);
            if (sessionToken.Length != 16)
            {
                await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "BAD_TOKEN",
                    "Invalid session token");
                return;
            }

            using var hostEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            byte[] hostPubBytes = hostEcdh.PublicKey.ExportSubjectPublicKeyInfo();
            byte[] challengeNonce = RandomNumberGenerator.GetBytes(16);
            byte[] clientPubBytes = Convert.FromBase64String(args.ClientPublicKeyBase64);

            var id = _settings.Identity;
            string primaryCode = args.SameAccountVerified && !string.IsNullOrWhiteSpace(args.SameAccountAuthSecret)
                ? args.SameAccountAuthSecret
                : id.TemporaryAccessCode;
            byte[] tempTag =
                ComputeAccessAuthTag(primaryCode, sessionToken, challengeNonce, clientPubBytes, hostPubBytes);
            bool hasFixed = id.UseFixedAccessCode && !string.IsNullOrWhiteSpace(id.FixedAccessCode);
            byte[] fixedTag = hasFixed
                ? ComputeAccessAuthTag(id.FixedAccessCode, sessionToken, challengeNonce, clientPubBytes, hostPubBytes)
                : new byte[32];
            byte[]? aesKey = null;
            IntPtr transport = IntPtr.Zero;
            Task<(IntPtr Handle, string CandidatesJson)>? preTransportTask = null;
            CloudRelayTransport? hostRelay = null;
            WebRtcPeerTransport? hostWebRtc = null;
            bool startedHost = false;

            var authTcs =
                new TaskCompletionSource<ConnectAuthEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var versionResultTcs =
                new TaskCompletionSource<VersionCheckResultEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var bridgeReadyTcs =
                new TaskCompletionSource<BridgeReadyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var relayFallbackTcs =
                new TaskCompletionSource<RelayFallbackEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var webrtcOfferTcs =
                new TaskCompletionSource<WebRtcSdpEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var earlyWebRtcIce = new List<WebRtcIceEventArgs>();
            var earlyIceLock = new object();

            void OnAuth(ConnectAuthEventArgs a)
            {
                if (a.SessionId == args.SessionId) authTcs.TrySetResult(a);
            }

            void OnVersionResult(VersionCheckResultEventArgs vr)
            {
                if (vr.SessionId == args.SessionId) versionResultTcs.TrySetResult(vr);
            }

            void OnBridgeReady(BridgeReadyEventArgs br)
            {
                if (br.SessionId == args.SessionId) bridgeReadyTcs.TrySetResult(br);
            }

            void OnRelayFallback(RelayFallbackEventArgs rf)
            {
                if (rf.SessionId == args.SessionId) relayFallbackTcs.TrySetResult(rf);
            }

            void OnWebRtcOffer(WebRtcSdpEventArgs offer)
            {
                if (offer.SessionId == args.SessionId) webrtcOfferTcs.TrySetResult(offer);
            }

            void OnWebRtcIce(WebRtcIceEventArgs ice)
            {
                if (ice.SessionId != args.SessionId) return;
                WebRtcPeerTransport? rtc;
                lock (earlyIceLock)
                {
                    rtc = hostWebRtc;
                    if (rtc == null)
                    {
                        earlyWebRtcIce.Add(ice);
                        return;
                    }
                }

                rtc.AddRemoteIceCandidate(ice.Candidate, ice.SdpMid, ice.SdpMLineIndex);
            }

            _signaling.ConnectAuthReceived += OnAuth;
            _signaling.VersionCheckResultReceived += OnVersionResult;
            _signaling.BridgeReady += OnBridgeReady;
            _signaling.RelayFallbackReceived += OnRelayFallback;
            _signaling.WebRtcOfferReceived += OnWebRtcOffer;
            _signaling.WebRtcIceReceived += OnWebRtcIce;

            int transportRoute = 0;
            var epSb = new StringBuilder(128);
            OsmiumClientConfig negotiatedProfile;
            bool bridgeReadyReached = false;
            try
            {
                await _signaling.SendConnectChallengeAsync(
                    args.SessionId,
                    args.ClientDeviceId,
                    Convert.ToBase64String(hostPubBytes),
                    Convert.ToBase64String(challengeNonce),
                    hostEncrypt);

                var (preStunHost, preStunPort) = GetStunEndpoint();
                preTransportTask = Task.Run(() =>
                {
                    var sb = new StringBuilder(4096);
                    int ok = NativeMethods.Osmium_CreateTransport(preStunHost, preStunPort, out IntPtr h, sb,
                        sb.Capacity);
                    return ok != 0 && h != IntPtr.Zero ? (h, sb.ToString()) : (IntPtr.Zero, string.Empty);
                });

                ConnectAuthEventArgs authMsg;
                using (var authTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                using (authTimeout.Token.Register(() => authTcs.TrySetCanceled()))
                {
                    try
                    {
                        authMsg = await authTcs.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        _log.Warn($"Timed out waiting for access code auth from {args.ClientDeviceId}");
                        return;
                    }
                }

                byte[] receivedTag;
                try
                {
                    receivedTag = Convert.FromBase64String(authMsg.AuthTagBase64);
                }
                catch
                {
                    await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "INVALID_ACCESS_CODE",
                        "Invalid remote access code");
                    return;
                }

                bool tagMatched = receivedTag.Length == 32 &&
                                  (CryptographicOperations.FixedTimeEquals(receivedTag, tempTag) ||
                                   (hasFixed && CryptographicOperations.FixedTimeEquals(receivedTag, fixedTag)));
                if (!tagMatched)
                {
                    _log.Warn($"Rejected incoming connection from {args.ClientDeviceId}: invalid access code");
                    await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "INVALID_ACCESS_CODE",
                        "Invalid remote access code");
                    return;
                }

                await _signaling.SendConnectAuthVerifiedAsync(args.SessionId, args.ClientDeviceId,
                    AppVersionInfo.Current);

                VersionCheckResultEventArgs versionRes;
                using (var verTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                using (verTimeout.Token.Register(() => versionResultTcs.TrySetCanceled()))
                {
                    try
                    {
                        versionRes = await versionResultTcs.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        _log.Warn($"Timed out waiting for server version check result for session {args.SessionId}");
                        return;
                    }
                }

                if (!versionRes.Compatible ||
                    !AppVersionInfo.IsVersionSupported(versionRes.ClientVersion) ||
                    !AppVersionInfo.IsVersionSupported(versionRes.HostVersion))
                {
                    _log.Warn(
                        $"Blocked incoming session {args.SessionId} due to incompatible versions (client={versionRes.ClientVersion}, host={versionRes.HostVersion}): {versionRes.Reason}");
                    return;
                }

                var (stunHost, stunPort) = GetStunEndpoint();
                string preCandidatesJson = string.Empty;
                if (preTransportTask != null)
                {
                    var pre = await preTransportTask;
                    preTransportTask = null;
                    transport = pre.Handle;
                    preCandidatesJson = pre.CandidatesJson;
                }
                else
                {
                    var candSb = new StringBuilder(4096);
                    if (NativeMethods.Osmium_CreateTransport(stunHost, stunPort, out transport, candSb,
                            candSb.Capacity) != 0 && transport != IntPtr.Zero)
                    {
                        preCandidatesJson = candSb.ToString();
                    }
                }

                if (transport == IntPtr.Zero)
                {
                    await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "TRANSPORT_ERROR",
                        "Host failed to bind UDP socket");
                    return;
                }

                bool reliableUnderlying = false;
                if (args.WebRtcRequested || versionRes.WebRtc)
                {
                    bridgeReadyReached = true;
                    aesKey = DeriveSessionAesKey(hostEcdh, clientPubBytes, sessionToken);
                    string rtcStunHost = !string.IsNullOrWhiteSpace(versionRes.StunHost)
                        ? versionRes.StunHost
                        : stunHost;
                    ushort rtcStunPort = versionRes.StunPort > 0 ? versionRes.StunPort : stunPort;

                    var rtc = new WebRtcPeerTransport(transport, _signaling, args.SessionId, _log);
                    if (!rtc.InitializePeerConnection(rtcStunHost, rtcStunPort, sessionToken))
                    {
                        rtc.Dispose();
                        await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "TRANSPORT_ERROR",
                            "Host failed to initialize WebRTC transport");
                        return;
                    }

                    List<WebRtcIceEventArgs> bufferedIce;
                    lock (earlyIceLock)
                    {
                        hostWebRtc = rtc;
                        bufferedIce = [.. earlyWebRtcIce];
                        earlyWebRtcIce.Clear();
                    }

                    foreach (var ice in bufferedIce)
                    {
                        rtc.AddRemoteIceCandidate(ice.Candidate, ice.SdpMid, ice.SdpMLineIndex);
                    }

                    WebRtcSdpEventArgs offer;
                    using (var offerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(18)))
                    using (offerTimeout.Token.Register(() => webrtcOfferTcs.TrySetCanceled()))
                    {
                        try
                        {
                            offer = await webrtcOfferTcs.Task;
                        }
                        catch (OperationCanceledException)
                        {
                            _log.Warn($"Timed out waiting for WebRTC offer for session {args.SessionId}");
                            return;
                        }
                    }

                    string? answerSdp = await rtc.HandleRemoteOfferAndCreateAnswerAsync(offer.Sdp);
                    if (string.IsNullOrWhiteSpace(answerSdp))
                    {
                        await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "TRANSPORT_ERROR",
                            "Host failed to generate WebRTC answer");
                        return;
                    }

                    await _signaling.SendWebRtcAnswerAsync(args.SessionId, answerSdp);

                    bool rtcReady = await rtc.WaitForChannelsReadyAsync(18000, CancellationToken.None);
                    if (!rtcReady)
                    {
                        _log.Warn($"WebRTC P2P channels failed to open for session {args.SessionId}");
                        return;
                    }

                    transportRoute = 3;
                    reliableUnderlying = true;
                    epSb.Clear();
                    epSb.Append(rtc.EndpointLabel);

                    NativeMethods.Osmium_SetTransportEncryption(transport, hostEncrypt ? 1 : 0);
                    if (hostEncrypt)
                    {
                        NativeMethods.Osmium_SetTransportKey(transport, aesKey);
                    }

                    NativeMethods.Osmium_SetTransportKcpEnabled(transport, 0);

                    int rtcAuthOk = NativeMethods.Osmium_WaitClientAuthHost(
                        transport,
                        tempTag,
                        fixedTag,
                        hasFixed ? 1 : 0,
                        12000,
                        out negotiatedProfile);

                    CryptographicOperations.ZeroMemory(aesKey);
                    aesKey = null;

                    if (rtcAuthOk != 1)
                    {
                        _log.Warn(
                            $"Rejected incoming WebRTC connection from {args.ClientDeviceId}: tunnel auth failed or timed out");
                        await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId,
                            "INVALID_ACCESS_CODE",
                            "Tunnel access code verification failed");
                        return;
                    }
                }
                else
                {
                    string localCandidatesJson = preCandidatesJson;
                    await _signaling.SendBridgeCandidatesAsync(args.SessionId, localCandidatesJson);

                    BridgeReadyEventArgs bridge;
                    using (var bridgeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(18)))
                    using (bridgeTimeout.Token.Register(() => bridgeReadyTcs.TrySetCanceled()))
                    {
                        try
                        {
                            while (!bridgeReadyTcs.Task.IsCompleted && !bridgeTimeout.IsCancellationRequested)
                            {
                                using var retryDelayCts =
                                    CancellationTokenSource.CreateLinkedTokenSource(bridgeTimeout.Token);
                                var retryDelay = Task.Delay(TimeSpan.FromSeconds(4), retryDelayCts.Token);
                                var step = await Task.WhenAny(bridgeReadyTcs.Task, retryDelay);
                                if (step == bridgeReadyTcs.Task)
                                {
                                    retryDelayCts.Cancel();
                                    break;
                                }

                                if (!bridgeTimeout.IsCancellationRequested)
                                {
                                    await _signaling.SendBridgeCandidatesAsync(args.SessionId, localCandidatesJson);
                                }
                            }

                            bridge = await bridgeReadyTcs.Task;
                            bridgeReadyReached = true;
                        }
                        catch (OperationCanceledException)
                        {
                            _log.Warn($"Timed out waiting for bridge candidates for session {args.SessionId}");
                            return;
                        }
                    }

                    aesKey = DeriveSessionAesKey(hostEcdh, clientPubBytes, sessionToken);

                    const int maxDirectAttempts = 2;
                    int punchOk = 0;
                    for (int attempt = 1; attempt <= maxDirectAttempts; attempt++)
                    {
                        if (attempt > 1)
                        {
                            try
                            {
                                await _signaling.SendBridgeCandidatesAsync(args.SessionId, localCandidatesJson);
                            }
                            catch
                            {
                            }
                        }

                        long attemptStartMs = Environment.TickCount64;
                        epSb.Clear();
                        punchOk = await Task.Run(() => NativeMethods.Osmium_ConnectPeerP2P(
                            transport,
                            sessionToken,
                            2,
                            bridge.CandidatesJson,
                            1400,
                            out transportRoute,
                            epSb,
                            epSb.Capacity));

                        if (punchOk != 0)
                        {
                            break;
                        }

                        if (Environment.TickCount64 - attemptStartMs < 200 || relayFallbackTcs.Task.IsCompleted)
                        {
                            break;
                        }
                    }

                    async Task<bool> SwitchHostToCloudRelayAsync()
                    {
                        try
                        {
                            await _signaling.SendRelayFallbackAsync(args.SessionId);
                        }
                        catch
                        {
                        }

                        string rToken = bridge.RelayToken;
                        string rPath = string.IsNullOrWhiteSpace(bridge.RelayPath) ? "/relay" : bridge.RelayPath;
                        if (string.IsNullOrWhiteSpace(rToken))
                        {
                            using var tokCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                            try
                            {
                                var rf = await relayFallbackTcs.Task.WaitAsync(tokCts.Token);
                                rToken = rf.RelayToken;
                                if (!string.IsNullOrWhiteSpace(rf.RelayPath)) rPath = rf.RelayPath;
                            }
                            catch
                            {
                            }
                        }

                        if (string.IsNullOrWhiteSpace(rToken)) return false;

                        hostRelay?.Dispose();
                        string sigNorm = SignalingClient.NormalizeServerUrl(_settings.Settings.SignalingServerUrl);
                        string relayLabel = $"{new Uri(sigNorm).Host} (WSS Relay)";
                        var relayUri =
                            CloudRelayTransport.BuildRelayUri(sigNorm, rPath, args.SessionId, "host", rToken);
                        hostRelay = new CloudRelayTransport(transport, relayLabel, _log);
                        bool relayOk = await hostRelay.ConnectAndBindAsync(relayUri, sessionToken, 2, 12000,
                            CancellationToken.None);
                        if (!relayOk)
                        {
                            hostRelay.Dispose();
                            hostRelay = null;
                            return false;
                        }

                        transportRoute = 4;
                        epSb.Clear();
                        epSb.Append(relayLabel);
                        return true;
                    }

                    if (punchOk == 0)
                    {
                        if (!await SwitchHostToCloudRelayAsync())
                        {
                            _log.Warn($"Host failed both direct P2P and Cloud Relay for {args.ClientDeviceId}");
                            return;
                        }
                    }

                    NativeMethods.Osmium_SetTransportEncryption(transport, hostEncrypt ? 1 : 0);
                    if (hostEncrypt)
                    {
                        NativeMethods.Osmium_SetTransportKey(transport, aesKey);
                    }

                    NativeMethods.Osmium_SetTransportKcpEnabled(transport,
                        (bridge.KcpEnabled && transportRoute != 4) ? 1 : 0);

                    int hostAuthTimeoutMs = transportRoute == 4
                        ? 12000
                        : (relayFallbackTcs.Task.IsCompleted ? 2500 : 5500);
                    int authOk = NativeMethods.Osmium_WaitClientAuthHost(
                        transport,
                        tempTag,
                        fixedTag,
                        hasFixed ? 1 : 0,
                        hostAuthTimeoutMs,
                        out negotiatedProfile);

                    if (authOk != 1 && transportRoute != 4)
                    {
                        if (await SwitchHostToCloudRelayAsync())
                        {
                            authOk = NativeMethods.Osmium_WaitClientAuthHost(
                                transport,
                                tempTag,
                                fixedTag,
                                hasFixed ? 1 : 0,
                                12000,
                                out negotiatedProfile);
                        }
                    }

                    CryptographicOperations.ZeroMemory(aesKey);
                    aesKey = null;

                    if (authOk != 1)
                    {
                        _log.Warn(
                            $"Rejected incoming connection from {args.ClientDeviceId}: tunnel auth failed or timed out");
                        await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId,
                            "INVALID_ACCESS_CODE",
                            "Tunnel access code verification failed");
                        return;
                    }

                    reliableUnderlying = transportRoute == 4 || bridge.KcpEnabled;
                }

                var s = _settings.Settings;
                bool viewOnly = args.ViewOnlyRequested;
                _hostViewOnlySession = viewOnly;
                _hostRemoteSharedClipboardEnabled = false;
                _hostRemoteFileTransferEnabled = false;
                _hostRemoteFileTransferDirection = (int)FileTransferDirection.Bidirectional;
                _hostRemoteMaxFileTransferMb = 256;
                _hostRemoteMaxClipboardMb = 16;
                Volatile.Write(ref _lastHostWrittenClipboardSeq, -1);

                _ = Task.Run(() => WindowsClipboardHelper.CleanupOldStagingDirs(TimeSpan.FromHours(6)));

                int maxBitrateKbps = s.HostLimitVideoRates ? s.HostMaxBitrateKbps : 0;
                if (transportRoute == 4 && (maxBitrateKbps <= 0 || maxBitrateKbps > 16000))
                    maxBitrateKbps = 16000;

                var hostCfg = new OsmiumHostConfig
                {
                    DisplayIndex = s.HostDisplayIndex,
                    MaxWidth = s.HostMaxWidth,
                    MaxHeight = s.HostMaxHeight,
                    MaxFps = s.HostLimitVideoRates ? s.HostMaxFps : 0,
                    MaxBitrateKbps = maxBitrateKbps,
                    CodecPreference = (int)s.HostCodec,
                    EncoderPreference = (int)s.HostEncoderMode,
                    CaptureAudio = s.HostCaptureAudio ? 1 : 0,
                    AllowInputControl = (!viewOnly && s.HostAllowInputControl) ? 1 : 0,
                    CaptureCursor = s.HostCaptureCursor ? 1 : 0,
                    AllowRemoteDriverMgmt = (!viewOnly && s.HostAllowRemoteDriverMgmt) ? 1 : 0,
                    RequireLocalDriverConfirm = s.HostRequireDriverConfirm ? 1 : 0
                };

                AuxTransferChannel? auxChannel = new AuxTransferChannel(
                    isController: false,
                    sendRaw: (data, len) =>
                    {
                        IntPtr h;
                        lock (_hostLock) h = _activeHostHandle;
                        if (h == IntPtr.Zero) return false;
                        return NativeMethods.Osmium_HostSendAuxData(h, data, len) != 0;
                    },
                    log: _log,
                    reliableUnderlyingTransport: reliableUnderlying);

                auxChannel.GetHostAllowsFileTransfer = () =>
                    !_hostViewOnlySession && _settings.Settings.HostAllowFileTransfer;

                auxChannel.ValidateIncomingTransfer = (type, totalBytes) =>
                {
                    if (_hostViewOnlySession) return AuxTransferRejectReason.Disabled;

                    if (type is AuxTransferType.ClipboardText or AuxTransferType.ClipboardImage)
                    {
                        long maxClip = (long)Math.Clamp(_hostRemoteMaxClipboardMb, 1, 64) * 1024 * 1024 + 65536;
                        return totalBytes <= maxClip
                            ? AuxTransferRejectReason.None
                            : AuxTransferRejectReason.TooLarge;
                    }

                    if (type == AuxTransferType.FileBatch)
                    {
                        if (!_settings.Settings.HostAllowFileTransfer || !_hostRemoteFileTransferEnabled)
                            return AuxTransferRejectReason.Disabled;

                        var dir = (FileTransferDirection)_hostRemoteFileTransferDirection;
                        if (dir == FileTransferDirection.HostToControllerOnly)
                            return AuxTransferRejectReason.DirectionNotAllowed;

                        long maxFiles = (long)Math.Clamp(_hostRemoteMaxFileTransferMb, 1, 4096) * 1024 * 1024 +
                                        4L * 1024 * 1024;
                        return totalBytes <= maxFiles
                            ? AuxTransferRejectReason.None
                            : AuxTransferRejectReason.TooLarge;
                    }

                    return AuxTransferRejectReason.Disabled;
                };

                auxChannel.RemoteFeatureConfigReceived += cfg =>
                {
                    _hostRemoteSharedClipboardEnabled = cfg.SharedClipboardEnabled;
                    _hostRemoteFileTransferEnabled = cfg.FileTransferEnabled;
                    _hostRemoteFileTransferDirection = (int)cfg.FileTransferDirection;
                    _hostRemoteMaxFileTransferMb = Math.Clamp(cfg.MaxFileTransferMb, 1, 4096);
                    _hostRemoteMaxClipboardMb = Math.Clamp(cfg.MaxClipboardMb, 1, 64);
                    if (!cfg.FileTransferEnabled)
                    {
                        auxChannel.CancelAllFileTransfers();
                    }
                };

                auxChannel.ClipboardTextReceived += text =>
                {
                    if (_hostViewOnlySession) return;
                    WindowsClipboardHelper.TrySetText(text,
                        seq => Volatile.Write(ref _lastHostWrittenClipboardSeq, seq));
                };

                auxChannel.ClipboardImageReceived += dibBytes =>
                {
                    if (_hostViewOnlySession) return;
                    WindowsClipboardHelper.TrySetDib(dibBytes,
                        seq => Volatile.Write(ref _lastHostWrittenClipboardSeq, seq));
                };

                auxChannel.FilesReceived += (roots, itemCount, totalBytes) =>
                {
                    if (_hostViewOnlySession || !_settings.Settings.HostAllowFileTransfer) return;
                    if (WindowsClipboardHelper.TrySetDropFiles(roots,
                            seq => Volatile.Write(ref _lastHostWrittenClipboardSeq, seq)))
                    {
                        _log.Info($"Host staged {itemCount} file item(s) ({totalBytes} bytes) to clipboard");
                    }
                };

                _hostStatsDelegate = (in OsmiumHostStats stats) => HostStatsUpdated?.Invoke(stats);
                _hostEndedDelegate = (code, msg) =>
                {
                    _log.Info($"Host session ended ({code}): {msg}");
                    _ = Task.Run(() => StopHostSession(notifyPeer: true, notifyEndedReason: msg));
                };
                _hostDriverConfirmDelegate = actionType =>
                {
                    if (ConfirmDriverActionAsync == null) return 0;
                    try
                    {
                        bool isInstall = actionType == 1;
                        var task = ConfirmDriverActionAsync(isInstall, args.ClientName);
                        if (task.Wait(45000))
                        {
                            return task.Result ? 1 : 0;
                        }

                        return 0;
                    }
                    catch
                    {
                        return 0;
                    }
                };
                _hostAuxDataDelegate = (dataPtr, length) =>
                {
                    if (dataPtr == IntPtr.Zero || length <= 0) return;
                    var ch = Volatile.Read(ref _hostAuxChannel);
                    if (ch == null) return;
                    byte[] copy = new byte[length];
                    System.Runtime.InteropServices.Marshal.Copy(dataPtr, copy, 0, length);
                    ch.HandleIncomingPacket(copy);
                };

                var callbacks = new OsmiumHostCallbacks
                {
                    OnStatsUpdated =
                        System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_hostStatsDelegate),
                    OnEnded = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_hostEndedDelegate),
                    OnLog = IntPtr.Zero,
                    OnConfirmDriverAction =
                        System.Runtime.InteropServices.Marshal
                            .GetFunctionPointerForDelegate(_hostDriverConfirmDelegate),
                    OnAuxData =
                        System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_hostAuxDataDelegate)
                };

                if (!EffectiveAllowRemoteControl)
                {
                    auxChannel.Dispose();
                    await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "REMOTE_DISABLED",
                        "Host disabled remote control");
                    return;
                }

                Volatile.Write(ref _hostAuxChannel, auxChannel);
                if (NativeMethods.Osmium_StartHostSession(transport, in hostCfg, in negotiatedProfile, in callbacks,
                        out IntPtr hostHandle) == 0)
                {
                    Interlocked.Exchange(ref _hostAuxChannel, null)?.Dispose();
                    return;
                }

                if (s.HostAllowPrivacyScreen && hostCfg.AllowInputControl != 0)
                {
                    string? wallpaper = s.HostUsePrivacyWallpaper
                        ? Path.Combine(_settings.DataDirectory, WindowsWallpaperHelper.PrivacyWallpaperFileName)
                        : null;
                    try
                    {
                        NativeMethods.Osmium_HostAllowPrivacyScreen(hostHandle, wallpaper);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        _log.Warn("Privacy screen unavailable in the installed native engine");
                    }
                }

                bool isSameAccount = args.SameAccountVerified ||
                                     (_account.IsLoggedIn &&
                                      !string.IsNullOrWhiteSpace(args.ControllerUserId) &&
                                      string.Equals(args.ControllerUserId, _account.CurrentUserId,
                                          StringComparison.OrdinalIgnoreCase));

                string rawControllerDev = !string.IsNullOrWhiteSpace(args.ControllerDeviceName)
                    ? args.ControllerDeviceName
                    : args.ClientName;
                string controllerDeviceName = isSameAccount
                    ? _account.ResolveAccountDeviceName(args.ClientDeviceId, rawControllerDev)
                    : rawControllerDev;
                string localDeviceName = _account.GetEffectiveLocalDeviceName();

                string controllerDisplay = isSameAccount
                    ? controllerDeviceName
                    : (!string.IsNullOrWhiteSpace(args.ControllerUserName) ? args.ControllerUserName : args.ClientName);
                string localHostDisplay = isSameAccount
                    ? localDeviceName
                    : (!string.IsNullOrWhiteSpace(_account.CurrentUsername)
                        ? _account.CurrentUsername
                        : localDeviceName);

                var activeInfo = new HostSessionActiveInfo
                {
                    SessionId = args.SessionId,
                    PeerDeviceId = args.ClientDeviceId,
                    PeerName = controllerDisplay,
                    ControllerUserId = args.ControllerUserId,
                    ControllerDeviceName = controllerDeviceName,
                    IsSameAccount = isSameAccount,
                    ControllerDisplayName = controllerDisplay,
                    HostDisplayName = localHostDisplay,
                    ActiveEndpoint = epSb.ToString(),
                    TransportRoute = transportRoute
                };

                startedHost = true;
                var confirmWatchdogCts = new CancellationTokenSource();
                lock (_hostLock)
                {
                    _activeHostHandle = hostHandle;
                    _activeHostTransport = transport;
                    _activeHostRelay = hostRelay;
                    _activeHostWebRtc = hostWebRtc;
                    _activeHostSessionId = args.SessionId;
                    _activeHostPeerId = args.ClientDeviceId;
                    _hostSessionEstablished = false;
                    _pendingHostSessionInfo = activeInfo;
                    _pendingHostRefreshTempCode = !args.SameAccountVerified &&
                                                  _settings.Identity.RefreshPolicy !=
                                                  AccessCodeRefreshPolicy.ManualOnly;
                    _hostConfirmTimeoutCts?.Cancel();
                    _hostConfirmTimeoutCts?.Dispose();
                    _hostConfirmTimeoutCts = confirmWatchdogCts;
                    HostSessionStartedAtUtc = null;
                    ActiveHostSessionInfo = null;
                }

                string confirmSid = args.SessionId;
                await _signaling.SendSessionConfirmAsync(confirmSid);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), confirmWatchdogCts.Token);
                        bool stillPending;
                        lock (_hostLock)
                        {
                            stillPending = _activeHostHandle != IntPtr.Zero &&
                                           !_hostSessionEstablished &&
                                           string.Equals(_activeHostSessionId, confirmSid, StringComparison.Ordinal);
                        }

                        if (stillPending)
                        {
                            await _signaling.SendSessionConfirmAsync(confirmSid);
                        }

                        await Task.Delay(TimeSpan.FromSeconds(13), confirmWatchdogCts.Token);
                        bool timedOut;
                        lock (_hostLock)
                        {
                            timedOut = _activeHostHandle != IntPtr.Zero &&
                                       !_hostSessionEstablished &&
                                       string.Equals(_activeHostSessionId, confirmSid, StringComparison.Ordinal);
                        }

                        if (timedOut)
                        {
                            _log.Warn(
                                $"Host session {confirmSid} timed out waiting for controller mutual confirmation");
                            StopHostSession(notifyPeer: true, notifyEndedReason: "Mutual confirmation timed out");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch
                    {
                    }
                });
            }
            finally
            {
                _signaling.ConnectAuthReceived -= OnAuth;
                _signaling.VersionCheckResultReceived -= OnVersionResult;
                _signaling.BridgeReady -= OnBridgeReady;
                _signaling.RelayFallbackReceived -= OnRelayFallback;
                _signaling.WebRtcOfferReceived -= OnWebRtcOffer;
                _signaling.WebRtcIceReceived -= OnWebRtcIce;
                if (aesKey != null) CryptographicOperations.ZeroMemory(aesKey);
                CryptographicOperations.ZeroMemory(tempTag);
                CryptographicOperations.ZeroMemory(fixedTag);
                if (!startedHost)
                {
                    if (bridgeReadyReached)
                    {
                        _ = _signaling.SendSessionEndAsync(args.SessionId, args.ClientDeviceId,
                            "Host failed to establish session", role: "host", failed: true);
                    }

                    if (preTransportTask != null)
                    {
                        var orphanTask = preTransportTask;
                        preTransportTask = null;
                        _ = orphanTask.ContinueWith(t =>
                        {
                            if (t.IsCompletedSuccessfully && t.Result.Handle != IntPtr.Zero)
                            {
                                NativeMethods.Osmium_DestroyTransport(t.Result.Handle);
                            }
                        }, TaskScheduler.Default);
                    }

                    if (transport != IntPtr.Zero)
                    {
                        Interlocked.Exchange(ref _hostAuxChannel, null)?.Dispose();
                        hostRelay?.Dispose();
                        hostWebRtc?.Dispose();
                        NativeMethods.Osmium_DestroyTransport(transport);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"HandleIncomingConnectAsync error: {ex}");
        }
    }

    private void OnSignalingSessionEstablished(SessionEstablishedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.SessionId)) return;

        HostSessionActiveInfo? confirmedHostInfo = null;
        bool shouldRefreshTempCode = false;
        AuxTransferChannel? auxCh = null;
        CancellationTokenSource? confirmCts = null;

        lock (_hostLock)
        {
            if (_activeHostHandle != IntPtr.Zero &&
                !_hostSessionEstablished &&
                string.Equals(_activeHostSessionId, args.SessionId, StringComparison.Ordinal))
            {
                _hostSessionEstablished = true;
                confirmedHostInfo = _pendingHostSessionInfo;
                _pendingHostSessionInfo = null;
                shouldRefreshTempCode = _pendingHostRefreshTempCode;
                _pendingHostRefreshTempCode = false;
                confirmCts = _hostConfirmTimeoutCts;
                _hostConfirmTimeoutCts = null;
                HostSessionStartedAtUtc = DateTimeOffset.UtcNow;
                ActiveHostSessionInfo = confirmedHostInfo;
                auxCh = Volatile.Read(ref _hostAuxChannel);
            }
        }

        if (confirmedHostInfo != null)
        {
            if (confirmCts != null)
            {
                try
                {
                    confirmCts.Cancel();
                }
                catch
                {
                }

                confirmCts.Dispose();
            }

            if (auxCh != null)
            {
                StartHostClipboardWatcher(auxCh);
                SendHostDesktopWallpaper(auxCh);
            }

            if (shouldRefreshTempCode)
            {
                _settings.RefreshTemporaryAccessCode();
            }

            _log.Info($"Host session mutually confirmed by both peers: {args.SessionId}");
            HostSessionStarted?.Invoke(confirmedHostInfo);
            RemoteSessionStateChanged?.Invoke();
        }

        bool matchesClient = false;
        lock (_clientLock)
        {
            if (string.Equals(_activeClientSessionId, args.SessionId, StringComparison.Ordinal))
            {
                _activeClientSessionEstablished = true;
                matchesClient = true;
            }
        }

        if (matchesClient)
        {
            _log.Info($"Client session mutually confirmed by both peers: {args.SessionId}");
            ClientSessionEstablished?.Invoke(args.SessionId, args.StartedAtUnixMs);
        }
    }

    private void OnSignalingSessionEnded(string sessionId, string fromDeviceId, string reason)
    {
        bool matchesHost;
        lock (_hostLock)
        {
            matchesHost = _activeHostHandle != IntPtr.Zero &&
                          !string.IsNullOrEmpty(sessionId) &&
                          string.Equals(_activeHostSessionId, sessionId, StringComparison.Ordinal);
        }

        if (matchesHost)
        {
            _log.Info($"Peer signaled session_end ({sessionId}): {reason}");
            _ = Task.Run(() => StopHostSession(notifyPeer: false, notifyEndedReason: reason));
        }

        bool matchesClient;
        lock (_clientLock)
        {
            matchesClient = !string.IsNullOrEmpty(_activeClientSessionId) &&
                            !string.IsNullOrEmpty(sessionId) &&
                            string.Equals(_activeClientSessionId, sessionId, StringComparison.Ordinal);
            if (matchesClient)
            {
                _peerEndedClientSession = true;
            }
        }

        if (matchesClient)
        {
            string endReason = string.IsNullOrWhiteSpace(reason) ? "Host ended the session" : reason;
            _log.Info($"Host signaled session_end ({sessionId}): {endReason}");
            ClientSessionEnded?.Invoke(endReason);
        }
    }

    public void StopHostSession() => StopHostSession(notifyPeer: true, notifyEndedReason: "Host stopped session");

    public void StopHostSession(string reason) => StopHostSession(notifyPeer: true,
        notifyEndedReason: string.IsNullOrWhiteSpace(reason) ? "Host stopped session" : reason);

    private void SendHostDesktopWallpaper(AuxTransferChannel channel)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                byte[]? jpeg = await WindowsWallpaperHelper.TryGetCompressedWallpaperJpegAsync();
                if (jpeg == null || jpeg.Length == 0) return;
                if (!ReferenceEquals(Volatile.Read(ref _hostAuxChannel), channel)) return;
                channel.SendDesktopWallpaper(jpeg);
            }
            catch (Exception ex)
            {
                _log.Warn($"SendHostDesktopWallpaper failed: {ex.Message}");
            }
        });
    }

    private void StartHostClipboardWatcher(AuxTransferChannel channel)
    {
        StopHostClipboardWatcher();
        var cts = new CancellationTokenSource();
        Volatile.Write(ref _hostClipboardWatchCts, cts);
        CancellationToken ct = cts.Token;

        _ = Task.Run(async () =>
        {
            uint lastSeq = WindowsClipboardHelper.GetSequenceNumber();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(200, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                uint curSeq = WindowsClipboardHelper.GetSequenceNumber();
                if (curSeq == lastSeq) continue;
                lastSeq = curSeq;

                long writtenSeq = Volatile.Read(ref _lastHostWrittenClipboardSeq);
                if (writtenSeq >= 0 && curSeq == (uint)writtenSeq) continue;

                if (_hostViewOnlySession || !_hostRemoteFileTransferEnabled ||
                    !_settings.Settings.HostAllowFileTransfer)
                    continue;

                var dir = (FileTransferDirection)_hostRemoteFileTransferDirection;
                if (dir != FileTransferDirection.Bidirectional && dir != FileTransferDirection.HostToControllerOnly)
                    continue;

                if (!WindowsClipboardHelper.HasDropFiles()) continue;

                try
                {
                    await Task.Delay(40, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!WindowsClipboardHelper.TryReadDropFiles(out var paths) || paths.Count == 0) continue;
                if (paths.Any(WindowsClipboardHelper.IsInsideStagingDir)) continue;

                long maxBytes = (long)Math.Clamp(_hostRemoteMaxFileTransferMb, 1, 4096) * 1024 * 1024;
                _ = channel.StartSendFilesAsync(paths, maxBytes);
            }
        }, ct);
    }

    private void StopHostClipboardWatcher()
    {
        var cts = Interlocked.Exchange(ref _hostClipboardWatchCts, null);
        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            cts.Dispose();
        }
    }

    private void StopHostSession(bool notifyPeer, string notifyEndedReason)
    {
        IntPtr handle;
        IntPtr transport;
        CloudRelayTransport? hostRelay;
        WebRtcPeerTransport? hostWebRtc;
        CancellationTokenSource? confirmCts;
        string? sid;
        string? peerId;
        bool hadSession;
        bool wasEstablished;
        lock (_hostLock)
        {
            handle = _activeHostHandle;
            transport = _activeHostTransport;
            hostRelay = _activeHostRelay;
            hostWebRtc = _activeHostWebRtc;
            confirmCts = _hostConfirmTimeoutCts;
            sid = _activeHostSessionId;
            peerId = _activeHostPeerId;
            wasEstablished = _hostSessionEstablished;
            hadSession = handle != IntPtr.Zero || transport != IntPtr.Zero || hostRelay != null || hostWebRtc != null;
            _activeHostHandle = IntPtr.Zero;
            _activeHostTransport = IntPtr.Zero;
            _activeHostRelay = null;
            _activeHostWebRtc = null;
            _hostConfirmTimeoutCts = null;
            _activeHostSessionId = null;
            _activeHostPeerId = null;
            _hostSessionEstablished = false;
            _pendingHostSessionInfo = null;
            _pendingHostRefreshTempCode = false;
            HostSessionStartedAtUtc = null;
            ActiveHostSessionInfo = null;
        }

        if (confirmCts != null)
        {
            try
            {
                confirmCts.Cancel();
            }
            catch
            {
            }

            confirmCts.Dispose();
        }

        if (!hadSession) return;

        StopHostClipboardWatcher();
        Interlocked.Exchange(ref _hostAuxChannel, null)?.Dispose();

        if (handle != IntPtr.Zero)
        {
            NativeMethods.Osmium_StopHostSession(handle);
        }

        hostRelay?.Dispose();
        hostWebRtc?.Dispose();

        if (transport != IntPtr.Zero)
        {
            NativeMethods.Osmium_DestroyTransport(transport);
        }

        if (notifyPeer && !string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(peerId))
        {
            _ = _signaling.SendSessionEndAsync(sid, peerId, notifyEndedReason, role: "host", failed: !wasEstablished);
        }

        if (wasEstablished)
        {
            HostSessionEnded?.Invoke(notifyEndedReason);
            RemoteSessionStateChanged?.Invoke();
        }
    }

    public void StopClientSession() => NativeMethods.Osmium_StopClientSession();
    public void ToggleClientOverlay() => NativeMethods.Osmium_ToggleClientOverlay();

    public void Dispose()
    {
        _signaling.IncomingConnect -= OnIncomingConnect;
        _signaling.SessionEnded -= OnSignalingSessionEnded;
        _signaling.SessionEstablishedReceived -= OnSignalingSessionEstablished;
        _signaling.ConnectCooldownUpdated -= OnSignalingConnectCooldownUpdated;
        StopHostSession();
    }
}