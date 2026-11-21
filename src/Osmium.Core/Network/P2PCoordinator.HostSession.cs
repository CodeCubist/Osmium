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
            if (!_settings.Settings.AllowRemoteControl)
            {
                await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "REMOTE_DISABLED",
                    "Host disabled remote control");
                return;
            }

            if (IsHostStreaming)
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
            bool startedHost = false;

            var authTcs =
                new TaskCompletionSource<ConnectAuthEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var versionResultTcs =
                new TaskCompletionSource<VersionCheckResultEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var bridgeReadyTcs =
                new TaskCompletionSource<BridgeReadyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

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

            _signaling.ConnectAuthReceived += OnAuth;
            _signaling.VersionCheckResultReceived += OnVersionResult;
            _signaling.BridgeReady += OnBridgeReady;

            int transportRoute;
            var epSb = new StringBuilder(128);
            OsmiumClientConfig negotiatedProfile;
            try
            {
                await _signaling.SendConnectChallengeAsync(
                    args.SessionId,
                    args.ClientDeviceId,
                    Convert.ToBase64String(hostPubBytes),
                    Convert.ToBase64String(challengeNonce),
                    hostEncrypt);

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

                if (!versionRes.Compatible)
                {
                    _log.Warn(
                        $"Blocked incoming session {args.SessionId} due to incompatible versions (client={versionRes.ClientVersion}, host={versionRes.HostVersion}): {versionRes.Reason}");
                    return;
                }

                var (stunHost, stunPort) = GetStunEndpoint();
                var candSb = new StringBuilder(4096);
                if (NativeMethods.Osmium_CreateTransport(stunHost, stunPort, out transport, candSb, candSb.Capacity) ==
                    0 ||
                    transport == IntPtr.Zero)
                {
                    await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "TRANSPORT_ERROR",
                        "Host failed to bind UDP socket");
                    return;
                }

                string localCandidatesJson = candSb.ToString();
                await _signaling.SendBridgeCandidatesAsync(args.SessionId, localCandidatesJson);

                BridgeReadyEventArgs bridge;
                using (var bridgeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(18)))
                using (bridgeTimeout.Token.Register(() => bridgeReadyTcs.TrySetCanceled()))
                {
                    try
                    {
                        while (!bridgeReadyTcs.Task.IsCompleted && !bridgeTimeout.IsCancellationRequested)
                        {
                            using var retryDelayCts = CancellationTokenSource.CreateLinkedTokenSource(bridgeTimeout.Token);
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
                    }
                    catch (OperationCanceledException)
                    {
                        _log.Warn($"Timed out waiting for bridge candidates for session {args.SessionId}");
                        return;
                    }
                }

                aesKey = DeriveSessionAesKey(hostEcdh, clientPubBytes, sessionToken);

                int punchOk = NativeMethods.Osmium_ConnectPeerP2P(
                    transport,
                    sessionToken,
                    2,
                    bridge.CandidatesJson,
                    6000,
                    out transportRoute,
                    epSb,
                    epSb.Capacity);

                if (punchOk == 0)
                {
                    return;
                }

                NativeMethods.Osmium_SetTransportEncryption(transport, hostEncrypt ? 1 : 0);
                if (hostEncrypt)
                {
                    NativeMethods.Osmium_SetTransportKey(transport, aesKey);
                }

                CryptographicOperations.ZeroMemory(aesKey);
                aesKey = null;

                int authOk = NativeMethods.Osmium_WaitClientAuthHost(
                    transport,
                    tempTag,
                    fixedTag,
                    hasFixed ? 1 : 0,
                    12000,
                    out negotiatedProfile);

                if (authOk != 1)
                {
                    _log.Warn(
                        $"Rejected incoming connection from {args.ClientDeviceId}: tunnel auth failed or timed out");
                    await _signaling.SendConnectRejectAsync(args.SessionId, args.ClientDeviceId, "INVALID_ACCESS_CODE",
                        "Tunnel access code verification failed");
                    return;
                }

                var s = _settings.Settings;
                var hostCfg = new OsmiumHostConfig
                {
                    DisplayIndex = s.HostDisplayIndex,
                    MaxWidth = s.HostMaxWidth,
                    MaxHeight = s.HostMaxHeight,
                    MaxFps = s.HostMaxFps,
                    MaxBitrateKbps = s.HostMaxBitrateKbps,
                    CodecPreference = (int)s.HostCodec,
                    EncoderPreference = (int)s.HostEncoderMode,
                    CaptureAudio = s.HostCaptureAudio ? 1 : 0,
                    AllowInputControl = (!args.ViewOnlyRequested && s.HostAllowInputControl) ? 1 : 0,
                    CaptureCursor = s.HostCaptureCursor ? 1 : 0,
                    AllowRemoteDriverMgmt = (!args.ViewOnlyRequested && s.HostAllowRemoteDriverMgmt) ? 1 : 0,
                    RequireLocalDriverConfirm = s.HostRequireDriverConfirm ? 1 : 0
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

                var callbacks = new OsmiumHostCallbacks
                {
                    OnStatsUpdated =
                        System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_hostStatsDelegate),
                    OnEnded = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_hostEndedDelegate),
                    OnLog = IntPtr.Zero,
                    OnConfirmDriverAction =
                        System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_hostDriverConfirmDelegate)
                };

                if (NativeMethods.Osmium_StartHostSession(transport, in hostCfg, in negotiatedProfile, in callbacks,
                        out IntPtr hostHandle) == 0)
                {
                    return;
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
                lock (_hostLock)
                {
                    _activeHostHandle = hostHandle;
                    _activeHostTransport = transport;
                    _activeHostSessionId = args.SessionId;
                    _activeHostPeerId = args.ClientDeviceId;
                    HostSessionStartedAtUtc = DateTimeOffset.UtcNow;
                    ActiveHostSessionInfo = activeInfo;
                }

                if (!args.SameAccountVerified && _settings.Identity.RefreshPolicy != AccessCodeRefreshPolicy.ManualOnly)
                {
                    _settings.RefreshTemporaryAccessCode();
                }

                HostSessionStarted?.Invoke(activeInfo);
                RemoteSessionStateChanged?.Invoke();
            }
            finally
            {
                _signaling.ConnectAuthReceived -= OnAuth;
                _signaling.VersionCheckResultReceived -= OnVersionResult;
                _signaling.BridgeReady -= OnBridgeReady;
                if (aesKey != null) CryptographicOperations.ZeroMemory(aesKey);
                CryptographicOperations.ZeroMemory(tempTag);
                CryptographicOperations.ZeroMemory(fixedTag);
                if (!startedHost && transport != IntPtr.Zero)
                {
                    NativeMethods.Osmium_DestroyTransport(transport);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"HandleIncomingConnectAsync error: {ex}");
        }
    }

    private void OnSignalingSessionEnded(string sessionId, string fromDeviceId, string reason)
    {
        bool matchesHost;
        lock (_hostLock)
        {
            matchesHost = _activeHostHandle != IntPtr.Zero &&
                          (string.IsNullOrEmpty(sessionId) ||
                           string.Equals(_activeHostSessionId, sessionId, StringComparison.Ordinal) ||
                           string.Equals(_activeHostPeerId, fromDeviceId, StringComparison.Ordinal));
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
                            (string.IsNullOrEmpty(sessionId) ||
                             string.Equals(_activeClientSessionId, sessionId, StringComparison.Ordinal) ||
                             string.Equals(_activeClientTargetId, fromDeviceId, StringComparison.Ordinal));
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

    private void StopHostSession(bool notifyPeer, string notifyEndedReason)
    {
        IntPtr handle;
        IntPtr transport;
        string? sid;
        string? peerId;
        bool hadSession;
        lock (_hostLock)
        {
            handle = _activeHostHandle;
            transport = _activeHostTransport;
            sid = _activeHostSessionId;
            peerId = _activeHostPeerId;
            hadSession = handle != IntPtr.Zero || transport != IntPtr.Zero;
            _activeHostHandle = IntPtr.Zero;
            _activeHostTransport = IntPtr.Zero;
            _activeHostSessionId = null;
            _activeHostPeerId = null;
            HostSessionStartedAtUtc = null;
            ActiveHostSessionInfo = null;
        }

        if (!hadSession) return;

        if (handle != IntPtr.Zero)
        {
            NativeMethods.Osmium_StopHostSession(handle);
        }

        if (transport != IntPtr.Zero)
        {
            NativeMethods.Osmium_DestroyTransport(transport);
        }

        if (notifyPeer && !string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(peerId))
        {
            _ = _signaling.SendSessionEndAsync(sid, peerId, notifyEndedReason, role: "host");
        }

        HostSessionEnded?.Invoke(notifyEndedReason);
        RemoteSessionStateChanged?.Invoke();
    }

    public void StopClientSession() => NativeMethods.Osmium_StopClientSession();
    public void ToggleClientOverlay() => NativeMethods.Osmium_ToggleClientOverlay();

    public void Dispose()
    {
        _signaling.IncomingConnect -= OnIncomingConnect;
        _signaling.SessionEnded -= OnSignalingSessionEnded;
        _signaling.ConnectCooldownUpdated -= OnSignalingConnectCooldownUpdated;
        StopHostSession();
    }
}