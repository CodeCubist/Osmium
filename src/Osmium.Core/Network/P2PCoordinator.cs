using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Osmium.Core.Interop;
using Osmium.Core.Models;
using Osmium.Core.Services;

namespace Osmium.Core.Network;

public sealed class HostStatusDisplayDto
{
    [JsonPropertyName("index")]
    public int Index
    {
        get;
        set;
    }

    [JsonPropertyName("width")]
    public int Width
    {
        get;
        set;
    }

    [JsonPropertyName("height")]
    public int Height
    {
        get;
        set;
    }

    [JsonPropertyName("refreshRate")]
    public int RefreshRate
    {
        get;
        set;
    }

    [JsonPropertyName("isPrimary")]
    public bool IsPrimary
    {
        get;
        set;
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName
    {
        get;
        set;
    } = string.Empty;
}

public sealed class HostSessionStatusDto
{
    [JsonPropertyName("activeDisplayIndex")]
    public int ActiveDisplayIndex
    {
        get;
        set;
    }

    [JsonPropertyName("nativeWidth")]
    public int NativeWidth
    {
        get;
        set;
    }

    [JsonPropertyName("nativeHeight")]
    public int NativeHeight
    {
        get;
        set;
    }

    [JsonPropertyName("streamWidth")]
    public int StreamWidth
    {
        get;
        set;
    }

    [JsonPropertyName("streamHeight")]
    public int StreamHeight
    {
        get;
        set;
    }

    [JsonPropertyName("fps")]
    public int Fps
    {
        get;
        set;
    }

    [JsonPropertyName("hostHdrEnabled")]
    public bool HostHdrEnabled
    {
        get;
        set;
    }

    [JsonPropertyName("hdr")]
    public bool Hdr
    {
        get;
        set;
    }

    [JsonPropertyName("yuv444")]
    public bool Yuv444
    {
        get;
        set;
    }

    [JsonPropertyName("mouseMode")]
    public int MouseMode
    {
        get;
        set;
    }

    [JsonPropertyName("virtualMouseEngaged")]
    public bool VirtualMouseEngaged
    {
        get;
        set;
    }

    [JsonPropertyName("virtualMouseHardware")]
    public bool VirtualMouseHardware
    {
        get;
        set;
    }

    [JsonPropertyName("smartFullscreenEngaged")]
    public bool SmartFullscreenEngaged
    {
        get;
        set;
    }

    [JsonPropertyName("virtualSpeakerActive")]
    public bool VirtualSpeakerActive
    {
        get;
        set;
    }

    [JsonPropertyName("virtualMicActive")]
    public bool VirtualMicActive
    {
        get;
        set;
    }

    [JsonPropertyName("screenLocked")]
    public bool ScreenLocked
    {
        get;
        set;
    }

    [JsonPropertyName("hasUsbIpDriver")]
    public bool HasUsbIpDriver
    {
        get;
        set;
    }

    [JsonPropertyName("allowRemoteDriverMgmt")]
    public bool AllowRemoteDriverMgmt
    {
        get;
        set;
    } = true;

    [JsonPropertyName("lastAction")]
    public string LastAction
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("displays")]
    public List<HostStatusDisplayDto> Displays
    {
        get;
        set;
    } = [];
}

public sealed class RemoteSessionLaunchArgs
{
    public required IntPtr Transport
    {
        get;
        init;
    }

    public required string SessionId
    {
        get;
        init;
    }

    public required string TargetDeviceId
    {
        get;
        init;
    }

    public required string HostName
    {
        get;
        init;
    }

    public string TargetDeviceName
    {
        get;
        init;
    } = string.Empty;

    public required int TransportRoute
    {
        get;
        init;
    }

    public required string ActiveEndpoint
    {
        get;
        init;
    }

    public required string SignalingServerUrl
    {
        get;
        init;
    }

    public required string SignalingServerHost
    {
        get;
        init;
    }

    public required string SignalingServerIp
    {
        get;
        init;
    }

    public required OsmiumClientConfig InitialConfig
    {
        get;
        init;
    }
}

public sealed class HostSessionActiveInfo
{
    public required string SessionId
    {
        get;
        init;
    }

    public required string PeerDeviceId
    {
        get;
        init;
    }

    public required string PeerName
    {
        get;
        init;
    }

    public string? ControllerUserId
    {
        get;
        init;
    }

    public string ControllerDeviceName
    {
        get;
        init;
    } = string.Empty;

    public bool IsSameAccount
    {
        get;
        init;
    }

    public string ControllerDisplayName
    {
        get;
        init;
    } = string.Empty;

    public string HostDisplayName
    {
        get;
        init;
    } = string.Empty;

    public required string ActiveEndpoint
    {
        get;
        init;
    }

    public required int TransportRoute
    {
        get;
        init;
    }
}

public sealed partial class P2PCoordinator : IDisposable
{
    private readonly SettingsService _settings;
    private readonly SignalingClient _signaling;
    private readonly AccountService _account;
    private readonly LoggingService _log;

    private readonly object _hostLock = new();
    private IntPtr _activeHostHandle = IntPtr.Zero;
    private IntPtr _activeHostTransport = IntPtr.Zero;
    private string? _activeHostSessionId;
    private string? _activeHostPeerId;

    private readonly object _clientLock = new();
    private string? _activeClientSessionId;
    private string? _activeClientTargetId;
    private bool _peerEndedClientSession;
    private int _activeClientSessionCount;
    private long _connectCooldownUntilTickMs;

    private OsmiumHostStatsDelegate? _hostStatsDelegate;
    private OsmiumHostEndedDelegate? _hostEndedDelegate;
    private OsmiumHostDriverConfirmDelegate? _hostDriverConfirmDelegate;
    private OsmiumAuxDataDelegate? _hostAuxDataDelegate;
    private AuxTransferChannel? _hostAuxChannel;
    private CancellationTokenSource? _hostClipboardWatchCts;
    private volatile bool _hostViewOnlySession;
    private volatile bool _hostRemoteSharedClipboardEnabled;
    private volatile bool _hostRemoteFileTransferEnabled;
    private volatile int _hostRemoteFileTransferDirection;
    private volatile int _hostRemoteMaxFileTransferMb = 256;
    private volatile int _hostRemoteMaxClipboardMb = 16;
    private long _lastHostWrittenClipboardSeq = -1;
    private OsmiumClientStartedDelegate? _clientStartedDelegate;
    private OsmiumClientStatsDelegate? _clientStatsDelegate;
    private OsmiumClientEndedDelegate? _clientEndedDelegate;

    public Func<IncomingConnectEventArgs, Task<bool>>? ConfirmIncomingConnectionAsync
    {
        get;
        set;
    }

    public Func<bool, string, Task<bool>>? ConfirmDriverActionAsync
    {
        get;
        set;
    }

    public Func<RemoteSessionLaunchArgs, Task>? LaunchRemoteSessionAsync
    {
        get;
        set;
    }

    public Func<AccountChallengeInfo, Task<bool>>? SolveRemoteCaptchaAsync
    {
        get;
        set;
    }

    public Func<string, object[], string>? Localizer
    {
        get;
        set;
    }

    private string Tr(string key, string fallback, params object[] args)
    {
        if (Localizer != null)
        {
            string res = Localizer(key, args);
            if (!string.IsNullOrEmpty(res) && res != key) return res;
        }

        return args.Length > 0 ? string.Format(fallback, args) : fallback;
    }

    public event Action<HostSessionActiveInfo>? HostSessionStarted;
    public event Action<OsmiumHostStats>? HostStatsUpdated;
    public event Action<string>? HostSessionEnded;

    public event Action<string>? ClientStageUpdated;
    public event Action? ClientStreamStarted;
    public event Action<OsmiumClientStats>? ClientStatsUpdated;
    public event Action<string>? ClientSessionEnded;
    public event Action<int>? ConnectCooldownChanged;
    public event Action? RemoteSessionStateChanged;

    public bool IsHostStreaming => _activeHostHandle != IntPtr.Zero;
    public bool IsClientSessionActive => Volatile.Read(ref _activeClientSessionCount) > 0;
    public bool IsAnyRemoteSessionActive => IsHostStreaming || IsClientSessionActive;

    public DateTimeOffset? HostSessionStartedAtUtc
    {
        get;
        private set;
    }

    public HostSessionActiveInfo? ActiveHostSessionInfo
    {
        get;
        private set;
    }

    public int RemainingConnectCooldownMs
    {
        get
        {
            long until = Interlocked.Read(ref _connectCooldownUntilTickMs);
            if (until <= 0) return 0;
            long remaining = until - Environment.TickCount64;
            return remaining > 0 ? (int)Math.Min(int.MaxValue, remaining) : 0;
        }
    }

    public P2PCoordinator(SettingsService settings, SignalingClient signaling, AccountService account,
        LoggingService log)
    {
        _settings = settings;
        _signaling = signaling;
        _account = account;
        _log = log;

        _signaling.IncomingConnect += OnIncomingConnect;
        _signaling.SessionEnded += OnSignalingSessionEnded;
        _signaling.ConnectCooldownUpdated += OnSignalingConnectCooldownUpdated;
    }

    public async Task ForceDisconnectAccountDeviceAsync(string targetHostDeviceId)
    {
        string norm = DeviceIdentity.NormalizeDeviceId(targetHostDeviceId);
        await _signaling.SendAccountForceDisconnectAsync(norm);
        if (norm == _settings.Identity.DeviceId && IsHostStreaming)
        {
            StopHostSession();
        }
    }

    public async Task BlacklistControllerOnDeviceAsync(string targetHostDeviceId)
    {
        string norm = DeviceIdentity.NormalizeDeviceId(targetHostDeviceId);
        await _signaling.SendAccountBlacklistUserAsync(norm);
        if (norm == _settings.Identity.DeviceId && IsHostStreaming)
        {
            StopHostSession();
        }
    }

    private void OnSignalingConnectCooldownUpdated(int remainingMs, string reason)
    {
        if (remainingMs <= 0)
        {
            if (string.Equals(reason, "REGISTERED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "CLEARED", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref _connectCooldownUntilTickMs, 0);
                ConnectCooldownChanged?.Invoke(0);
            }

            return;
        }

        long until = Environment.TickCount64 + remainingMs;
        Interlocked.Exchange(ref _connectCooldownUntilTickMs, until);
        _log.Info($"Server granted controller connect cooldown: {remainingMs}ms ({reason})");
        ConnectCooldownChanged?.Invoke(remainingMs);
    }

    private async Task SendClientSessionEndAndAwaitCooldownAsync(string sessionId, string targetDeviceId, string reason)
    {
        if (_signaling.State != SignalingConnectionState.Connected)
        {
            return;
        }

        var ackTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCooldown(int remainingMs, string _)
        {
            if (remainingMs > 0)
            {
                ackTcs.TrySetResult(remainingMs);
            }
        }

        _signaling.ConnectCooldownUpdated += OnCooldown;
        try
        {
            await _signaling.SendSessionEndAsync(sessionId, targetDeviceId, reason, role: "client");
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            using var reg = timeoutCts.Token.Register(() => ackTcs.TrySetResult(0));
            await ackTcs.Task;
        }
        catch
        {
        }
        finally
        {
            _signaling.ConnectCooldownUpdated -= OnCooldown;
        }
    }

    private (string host, ushort stunPort) GetStunEndpoint()
    {
        var uri = new Uri(SignalingClient.NormalizeServerUrl(_settings.Settings.SignalingServerUrl));
        if (_settings.Settings.StunPortOverride > 0)
        {
            return (uri.Host, (ushort)_settings.Settings.StunPortOverride);
        }

        string host = !string.IsNullOrWhiteSpace(_signaling.AdvertisedStunHost)
            ? _signaling.AdvertisedStunHost
            : uri.Host;
        return (host, _signaling.AdvertisedStunPort);
    }

    private static byte[] DeriveSessionAesKey(
        ECDiffieHellman localEcdh,
        byte[] peerPublicKeyBytes,
        byte[] sessionToken16)
    {
        using var peerEcdh = ECDiffieHellman.Create();
        peerEcdh.ImportSubjectPublicKeyInfo(peerPublicKeyBytes, out _);
        byte[] rawSecret = localEcdh.DeriveRawSecretAgreement(peerEcdh.PublicKey);
        try
        {
            byte[] info = Encoding.ASCII.GetBytes("OSM1-SESSION-KEY-V1");
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, rawSecret, 32, sessionToken16, info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawSecret);
        }
    }

    private static byte[] ComputeAccessAuthTag(
        string accessCode,
        byte[] sessionToken16,
        byte[] challengeNonce,
        byte[] clientPub,
        byte[] hostPub)
    {
        byte[] codeBytes = Encoding.UTF8.GetBytes(accessCode.Trim().ToUpperInvariant());
        byte[] codeHash = SHA256.HashData(codeBytes);
        try
        {
            using var hmac = new HMACSHA256(codeHash);
            using var ms = new MemoryStream();
            ms.Write(sessionToken16);
            ms.Write(challengeNonce);
            ms.Write(clientPub);
            ms.Write(hostPub);
            return hmac.ComputeHash(ms.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(codeBytes);
            CryptographicOperations.ZeroMemory(codeHash);
        }
    }

    public Task<(bool success, string message)> ConnectToSameAccountDeviceAsync(
        string targetDeviceId,
        bool viewOnly,
        CancellationToken ct = default)
        => ConnectToRemoteAsync(targetDeviceId, string.Empty, ct, sameAccountConnect: true, viewOnly: viewOnly);

    public async Task<(bool success, string message)> ConnectToRemoteAsync(
        string targetDeviceId,
        string accessCode,
        CancellationToken ct = default,
        bool sameAccountConnect = false,
        bool viewOnly = false,
        bool captchaRetried = false)
    {
        string normalizedTarget = DeviceIdentity.NormalizeDeviceId(targetDeviceId);
        if (normalizedTarget.Length < 6)
        {
            return (false, Tr("Conn_InvalidId", "Please enter a valid target Device ID"));
        }

        if (!sameAccountConnect && string.IsNullOrWhiteSpace(accessCode))
        {
            return (false, Tr("Conn_EmptyCode", "Please enter an access code"));
        }

        if (_signaling.State != SignalingConnectionState.Connected)
        {
            return (false, Tr("Conn_Offline", "Device is offline; please check network connection or server URL"));
        }

        if (!_account.IsLoggedIn || !await _account.EnsureAuthenticatedAsync())
        {
            return (false,
                Tr("Conn_AccountRequired", "Please sign in to an Osmium account before initiating remote control"));
        }

        if (_account.IsLocked)
        {
            return (false, Tr("Acc_ErrLocked", "Account is currently locked"));
        }

        int activeCooldownMs = RemainingConnectCooldownMs;
        if (activeCooldownMs > 0)
        {
            int waitSeconds = Math.Max(1, (int)Math.Ceiling(activeCooldownMs / 1000.0));
            return (false, Tr("Conn_CooldownWait", "Please wait {0}s before connecting again", waitSeconds));
        }

        byte[] sessionToken = RandomNumberGenerator.GetBytes(16);
        string sessionId = Convert.ToHexString(sessionToken);
        var (stunHost, stunPort) = GetStunEndpoint();
        IntPtr transport = IntPtr.Zero;

        Interlocked.Increment(ref _activeClientSessionCount);
        RemoteSessionStateChanged?.Invoke();

        try
        {
            using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            byte[] clientPubBytes = clientEcdh.PublicKey.ExportSubjectPublicKeyInfo();
            string clientPubB64 = Convert.ToBase64String(clientPubBytes);

            var challengeTcs =
                new TaskCompletionSource<ConnectChallengeEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var authVerifiedTcs =
                new TaskCompletionSource<ConnectAuthVerifiedEventArgs>(TaskCreationOptions
                    .RunContinuationsAsynchronously);
            var versionResultTcs =
                new TaskCompletionSource<VersionCheckResultEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var bridgeReadyTcs =
                new TaskCompletionSource<BridgeReadyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rejectTcs =
                new TaskCompletionSource<ConnectRejectEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnChallenge(ConnectChallengeEventArgs c)
            {
                if (c.SessionId == sessionId) challengeTcs.TrySetResult(c);
            }

            void OnAuthVerified(ConnectAuthVerifiedEventArgs av)
            {
                if (av.SessionId == sessionId) authVerifiedTcs.TrySetResult(av);
            }

            void OnVersionResult(VersionCheckResultEventArgs vr)
            {
                if (vr.SessionId == sessionId) versionResultTcs.TrySetResult(vr);
            }

            void OnBridgeReady(BridgeReadyEventArgs br)
            {
                if (br.SessionId == sessionId) bridgeReadyTcs.TrySetResult(br);
            }

            void OnReject(ConnectRejectEventArgs r)
            {
                if (r.SessionId == sessionId) rejectTcs.TrySetResult(r);
            }

            _signaling.ConnectChallengeReceived += OnChallenge;
            _signaling.ConnectAuthVerified += OnAuthVerified;
            _signaling.VersionCheckResultReceived += OnVersionResult;
            _signaling.BridgeReady += OnBridgeReady;
            _signaling.ConnectRejected += OnReject;

            byte[]? aesKey = null;
            byte[]? authTag = null;
            try
            {
                bool clientEncrypt = _settings.Settings.EnableEncryption;
                ClientStageUpdated?.Invoke(Tr("Conn_Requesting", "Connecting to {0}...",
                    DeviceIdentity.FormatDeviceId(normalizedTarget)));
                await _signaling.SendConnectRequestAsync(
                    sessionId,
                    normalizedTarget,
                    clientPubB64,
                    "[]",
                    clientEncrypt,
                    _account.CurrentLtoken,
                    sameAccountConnect,
                    viewOnly);

                (bool rejected, string rejectMsg) FormatReject(ConnectRejectEventArgs rej)
                {
                    if (int.TryParse(rej.Code, out int numericCode))
                    {
                        switch (numericCode)
                        {
                            case OsmiumErrorCode.AccountAuthRequired:
                                return (true,
                                    Tr("Conn_AccountRequired",
                                        "Please sign in to an Osmium account before initiating remote control"));
                            case OsmiumErrorCode.AccountLocked:
                                return (true, Tr("Acc_ErrLocked", "Account is currently locked"));
                            case OsmiumErrorCode.BlacklistedByHost:
                                return (true,
                                    Tr("Conn_BlacklistedByHost", "Your account has been blocked by the target device"));
                            case OsmiumErrorCode.CaptchaRequired:
                                return (true,
                                    Tr("Conn_CaptchaRequired",
                                        "Daily remote control limit reached; please complete security verification"));
                            case OsmiumErrorCode.ConnectCooldown:
                            {
                                int cdMs = rej.CooldownMs > 0 ? rej.CooldownMs : RemainingConnectCooldownMs;
                                int waitSeconds = Math.Max(1, (int)Math.Ceiling(Math.Max(1000, cdMs) / 1000.0));
                                return (true,
                                    Tr("Conn_CooldownWait", "Please wait {0}s before connecting again", waitSeconds));
                            }
                            case OsmiumErrorCode.DeviceOffline:
                                return (true, Tr("Conn_TargetOffline", "Target device is currently offline"));
                            case OsmiumErrorCode.RemoteDisabled:
                                return (true,
                                    Tr("Conn_TargetRemoteDisabled", "Target device has disabled remote control"));
                            case OsmiumErrorCode.SelfConnect:
                                return (true, Tr("Conn_SelfConnect", "Cannot connect to this device itself"));
                            case OsmiumErrorCode.RateLimited:
                                return (true, Tr("Acc_ErrRateLimited", "Too many requests; please try again shortly"));
                        }
                    }

                    if (string.Equals(rej.Code, "REMOTE_DISABLED", StringComparison.OrdinalIgnoreCase))
                    {
                        return (true, Tr("Conn_TargetRemoteDisabled", "Target device has disabled remote control"));
                    }

                    if (string.Equals(rej.Code, "ENCRYPTION_MISMATCH", StringComparison.OrdinalIgnoreCase))
                    {
                        return (true,
                            Tr("Conn_EncryptionMismatch",
                                "Encryption setting mismatch: both host and controller must enable or disable encrypted transport"));
                    }

                    if (string.Equals(rej.Code, "INVALID_ACCESS_CODE", StringComparison.OrdinalIgnoreCase))
                    {
                        return (true, Tr("Conn_InvalidCode", "Incorrect access code"));
                    }

                    return string.IsNullOrWhiteSpace(rej.Reason)
                        ? (true, Tr("Conn_RejectedCode", "Connection rejected - {0}", rej.Code))
                        : (true, Tr("Conn_Rejected", "Connection rejected {0}: {1}", rej.Code, rej.Reason));
                }

                ConnectChallengeEventArgs challenge;
                using (var challengeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    challengeTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    using var reg =
                        challengeTimeout.Token.Register(() => challengeTcs.TrySetCanceled(challengeTimeout.Token));
                    var finished = await Task.WhenAny(challengeTcs.Task, rejectTcs.Task);
                    if (finished == rejectTcs.Task)
                    {
                        var rej = await rejectTcs.Task;
                        bool isCaptchaReject = int.TryParse(rej.Code, out int rejNum) &&
                                               rejNum == OsmiumErrorCode.CaptchaRequired;
                        if (!captchaRetried &&
                            isCaptchaReject &&
                            rej.Challenge != null &&
                            SolveRemoteCaptchaAsync != null)
                        {
                            ClientStageUpdated?.Invoke(Tr("Conn_CaptchaRequired",
                                "Daily remote control limit reached; please complete security verification"));
                            bool solved = await SolveRemoteCaptchaAsync(rej.Challenge);
                            if (solved)
                            {
                                return await ConnectToRemoteAsync(
                                    targetDeviceId,
                                    accessCode,
                                    ct,
                                    sameAccountConnect,
                                    viewOnly,
                                    captchaRetried: true);
                            }

                            return (false, Tr("Conn_CaptchaCancelled", "Security verification was canceled"));
                        }

                        var (_, msg) = FormatReject(rej);
                        return (false, msg);
                    }

                    challenge = await challengeTcs.Task;
                }

                if (challenge.EnableEncryption != clientEncrypt)
                {
                    return (false,
                        Tr("Conn_EncryptionMismatch",
                            "Encryption setting mismatch: both host and controller must enable or disable encrypted transport"));
                }

                string effectiveCode = sameAccountConnect && !string.IsNullOrEmpty(challenge.SameAccountAuthSecret)
                    ? challenge.SameAccountAuthSecret
                    : accessCode;
                if (string.IsNullOrWhiteSpace(effectiveCode))
                {
                    return (false, Tr("Conn_InvalidCode", "Incorrect access code"));
                }

                byte[] hostPubBytes = Convert.FromBase64String(challenge.HostPublicKeyBase64);
                byte[] challengeNonce = Convert.FromBase64String(challenge.HostChallengeNonceBase64);
                aesKey = DeriveSessionAesKey(clientEcdh, hostPubBytes, sessionToken);
                authTag = ComputeAccessAuthTag(effectiveCode, sessionToken, challengeNonce, clientPubBytes,
                    hostPubBytes);

                ClientStageUpdated?.Invoke(Tr("Conn_VerifyingAuth", "Verifying access code..."));
                await _signaling.SendConnectAuthAsync(sessionId, normalizedTarget, Convert.ToBase64String(authTag));

                using (var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    authTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    using var reg = authTimeout.Token.Register(() => authVerifiedTcs.TrySetCanceled(authTimeout.Token));
                    var finished = await Task.WhenAny(authVerifiedTcs.Task, rejectTcs.Task);
                    if (finished == rejectTcs.Task)
                    {
                        var (_, msg) = FormatReject(await rejectTcs.Task);
                        return (false, msg);
                    }

                    await authVerifiedTcs.Task;
                }

                ClientStageUpdated?.Invoke(Tr("Conn_CheckingVersion", "Checking version compatibility with server..."));
                await _signaling.SendVersionReportAsync(sessionId, AppVersionInfo.Current);

                VersionCheckResultEventArgs versionRes;
                using (var verTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    verTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    using var reg = verTimeout.Token.Register(() => versionResultTcs.TrySetCanceled(verTimeout.Token));
                    var finished = await Task.WhenAny(versionResultTcs.Task, rejectTcs.Task);
                    if (finished == rejectTcs.Task)
                    {
                        var (_, msg) = FormatReject(await rejectTcs.Task);
                        return (false, msg);
                    }

                    try
                    {
                        versionRes = await versionResultTcs.Task;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return (false, Tr("Conn_VersionTimeout", "Version compatibility check timed out"));
                    }
                }

                if (!versionRes.Compatible)
                {
                    string reasonText = string.IsNullOrWhiteSpace(versionRes.Reason)
                        ? $"{versionRes.ClientVersion} vs {versionRes.HostVersion}"
                        : versionRes.Reason;
                    return (false, Tr("Conn_VersionIncompatible", "Incompatible versions: {0}", reasonText));
                }

                ClientStageUpdated?.Invoke(Tr("Conn_Probing", "Detecting network endpoints..."));
                var candSb = new StringBuilder(4096);
                if (NativeMethods.Osmium_CreateTransport(stunHost, stunPort, out transport, candSb, candSb.Capacity) ==
                    0 ||
                    transport == IntPtr.Zero)
                {
                    return (false, Tr("Conn_BindFailed", "Unable to bind local UDP transport port"));
                }

                string localCandidatesJson = candSb.ToString();
                await _signaling.SendBridgeCandidatesAsync(sessionId, localCandidatesJson);

                BridgeReadyEventArgs bridge;
                using (var bridgeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    bridgeTimeout.CancelAfter(TimeSpan.FromSeconds(18));
                    using var reg =
                        bridgeTimeout.Token.Register(() => bridgeReadyTcs.TrySetCanceled(bridgeTimeout.Token));
                    var waitBridgeTask = Task.WhenAny(bridgeReadyTcs.Task, rejectTcs.Task);
                    while (!waitBridgeTask.IsCompleted && !bridgeTimeout.IsCancellationRequested)
                    {
                        using var retryDelayCts = CancellationTokenSource.CreateLinkedTokenSource(bridgeTimeout.Token);
                        var retryDelay = Task.Delay(TimeSpan.FromSeconds(4), retryDelayCts.Token);
                        var step = await Task.WhenAny(waitBridgeTask, retryDelay);
                        if (step == waitBridgeTask)
                        {
                            retryDelayCts.Cancel();
                            break;
                        }

                        if (!bridgeTimeout.IsCancellationRequested)
                        {
                            await _signaling.SendBridgeCandidatesAsync(sessionId, localCandidatesJson);
                        }
                    }

                    var finished = await waitBridgeTask;
                    if (finished == rejectTcs.Task)
                    {
                        var (_, msg) = FormatReject(await rejectTcs.Task);
                        return (false, msg);
                    }

                    try
                    {
                        bridge = await bridgeReadyTcs.Task;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return (false, Tr("Conn_BridgeTimeout", "Timed out establishing connection bridge"));
                    }
                }

                ClientStageUpdated?.Invoke(Tr("Conn_Punching", "Establishing direct connection..."));
                var epSb = new StringBuilder(128);
                int transportRoute = 0;
                int punchOk = await Task.Run(() => NativeMethods.Osmium_ConnectPeerP2P(
                    transport,
                    sessionToken,
                    1,
                    bridge.CandidatesJson,
                    6000,
                    out transportRoute,
                    epSb,
                    epSb.Capacity), ct);

                if (punchOk == 0)
                {
                    return (false, Tr("Conn_PunchFailed", "Failed to establish direct connection"));
                }

                NativeMethods.Osmium_SetTransportEncryption(transport, clientEncrypt ? 1 : 0);
                if (clientEncrypt)
                {
                    NativeMethods.Osmium_SetTransportKey(transport, aesKey);
                }

                CryptographicOperations.ZeroMemory(aesKey);
                aesKey = null;

                var s = _settings.Settings;
                var clientCfg = new OsmiumClientConfig
                {
                    WindowTitle = $"Osmium - {challenge.HostName} ({DeviceIdentity.FormatDeviceId(normalizedTarget)})",
                    TargetWidth = s.TargetWidth,
                    TargetHeight = s.TargetHeight,
                    Fps = s.Fps,
                    BitrateKbps = s.BitrateKbps,
                    WindowMode = (int)s.WindowMode,
                    CodecPreference = (int)s.VideoCodec,
                    DecoderPreference = (int)s.VideoDecoderMode,
                    CaptureSysKeysMode = (int)s.CaptureSysKeys,
                    EnableVsync = s.EnableVsync ? 1 : 0,
                    EnableHdr = s.EnableHdr ? 1 : 0,
                    EnableYuv444 = s.EnableYuv444 ? 1 : 0,
                    ShowPerformanceOverlay = 0,
                    AbsoluteMouseMode = s.AbsoluteMouseMode ? 1 : 0,
                    MouseMode = (int)s.PreferredMouseMode,
                    AbsoluteTouchMode = s.AbsoluteTouchMode ? 1 : 0,
                    SwapMouseButtons = s.SwapMouseButtons ? 1 : 0,
                    ReverseScrollDirection = s.ReverseScrollDirection ? 1 : 0,
                    MultiController = s.MultiController ? 1 : 0,
                    SwapFaceButtons = s.SwapFaceButtons ? 1 : 0,
                    GamepadMouse = s.GamepadMouse ? 1 : 0,
                    BackgroundGamepad = s.BackgroundGamepad ? 1 : 0,
                    MuteOnFocusLoss = s.MuteOnFocusLoss ? 1 : 0,
                    KeepAwake = s.KeepAwake ? 1 : 0,
                    EnableVirtualSpeaker = s.MuteHostSpeakers ? 1 : 0,
                    EnableVirtualMic = 0,
                    LocalMicDeviceId = string.IsNullOrWhiteSpace(s.PreferredLocalMicDeviceId)
                        ? null
                        : s.PreferredLocalMicDeviceId
                };

                ClientStageUpdated?.Invoke(
                    Tr("Conn_VerifyingDirect", "Direct channel active, verifying access code..."));
                int authRes;
                try
                {
                    authRes = await Task.Run(
                        () => NativeMethods.Osmium_AuthenticateClient(transport, authTag, in clientCfg, 8000), ct);
                }
                finally
                {
                    if (authTag != null)
                    {
                        CryptographicOperations.ZeroMemory(authTag);
                        authTag = null;
                    }
                }

                if (authRes == -1)
                {
                    return (false, Tr("Conn_InvalidCode", "Incorrect access code"));
                }

                if (authRes == 0)
                {
                    return (false, Tr("Conn_AuthTimeout", "Verification timed out"));
                }

                bool isSameAccountSession = !string.IsNullOrWhiteSpace(challenge.SameAccountAuthSecret) ||
                                            _account.FindPeerDevice(normalizedTarget) != null;
                string hostWhom = isSameAccountSession
                    ? _account.ResolveAccountDeviceName(normalizedTarget, challenge.HostName)
                    : challenge.HostName;
                string targetDeviceName = hostWhom;
                if (!isSameAccountSession && !string.IsNullOrWhiteSpace(hostWhom))
                {
                    int sepIdx = hostWhom.IndexOf(" - ", StringComparison.Ordinal);
                    if (sepIdx >= 0 && sepIdx + 3 < hostWhom.Length)
                    {
                        targetDeviceName = hostWhom[(sepIdx + 3)..];
                    }
                }

                _settings.RecordRecentSession(normalizedTarget, hostWhom);
                ClientStageUpdated?.Invoke(Tr("Conn_Starting", "Starting remote desktop..."));

                if (LaunchRemoteSessionAsync != null)
                {
                    string sigUrl = SignalingClient.NormalizeServerUrl(s.SignalingServerUrl);
                    string sigHost = new Uri(sigUrl).Host;
                    string sigIp = sigHost;
                    string controllerWho = isSameAccountSession
                        ? _account.GetEffectiveLocalDeviceName()
                        : (!string.IsNullOrWhiteSpace(_account.CurrentUsername)
                            ? _account.CurrentUsername
                            : (!string.IsNullOrWhiteSpace(challenge.ControllerUserName)
                                ? challenge.ControllerUserName
                                : _account.GetEffectiveLocalDeviceName()));
                    string displayHeader = Tr("Session_WhoControllingWhomFmt", "{0} is controlling {1}", controllerWho,
                        hostWhom);
                    try
                    {
                        var addrs = await Dns.GetHostAddressesAsync(sigHost, AddressFamily.InterNetwork, ct);
                        var ipv4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                        if (ipv4 != null) sigIp = ipv4.ToString();
                    }
                    catch
                    {
                    }

                    var launchArgs = new RemoteSessionLaunchArgs
                    {
                        Transport = transport,
                        SessionId = sessionId,
                        TargetDeviceId = normalizedTarget,
                        HostName = displayHeader,
                        TargetDeviceName = targetDeviceName,
                        TransportRoute = transportRoute,
                        ActiveEndpoint = epSb.ToString(),
                        SignalingServerUrl = sigUrl,
                        SignalingServerHost = sigHost,
                        SignalingServerIp = sigIp,
                        InitialConfig = clientCfg
                    };

                    lock (_clientLock)
                    {
                        _activeClientSessionId = sessionId;
                        _activeClientTargetId = normalizedTarget;
                        _peerEndedClientSession = false;
                    }

                    try
                    {
                        ClientStreamStarted?.Invoke();
                        await LaunchRemoteSessionAsync(launchArgs);
                    }
                    finally
                    {
                        bool peerEnded;
                        lock (_clientLock)
                        {
                            peerEnded = _peerEndedClientSession;
                            _activeClientSessionId = null;
                            _activeClientTargetId = null;
                            _peerEndedClientSession = false;
                        }

                        if (!peerEnded)
                        {
                            await SendClientSessionEndAndAwaitCooldownAsync(sessionId, normalizedTarget,
                                "Client closed session");
                        }
                    }

                    return (true, Tr("Conn_SessionEnded", "Session ended"));
                }

                _clientStartedDelegate = (w, h, codec, dec) =>
                {
                    _log.Info($"Client stream started: {w}x{h} {codec} via {dec}");
                    ClientStreamStarted?.Invoke();
                };
                _clientStatsDelegate = (in OsmiumClientStats stats) =>
                {
                    ClientStatsUpdated?.Invoke(stats);
                };
                _clientEndedDelegate = (code, msg) =>
                {
                    _log.Info($"Client stream ended ({code}): {msg}");
                    ClientSessionEnded?.Invoke(msg);
                };

                var callbacks = new OsmiumClientCallbacks
                {
                    OnStarted =
                        System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_clientStartedDelegate),
                    OnStatsUpdated =
                        System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_clientStatsDelegate),
                    OnEnded =
                        System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_clientEndedDelegate),
                    OnLog = IntPtr.Zero,
                    OnStatusJson = IntPtr.Zero
                };

                clientCfg.ShowPerformanceOverlay = s.ShowPerformanceOverlay ? 1 : 0;
                await Task.Run(() => NativeMethods.Osmium_RunClientSession(transport, in clientCfg, in callbacks),
                    CancellationToken.None);
                await SendClientSessionEndAndAwaitCooldownAsync(sessionId, normalizedTarget, "Client closed session");
                return (true, Tr("Conn_SessionEnded", "Session ended"));
            }
            finally
            {
                if (aesKey != null) CryptographicOperations.ZeroMemory(aesKey);
                if (authTag != null) CryptographicOperations.ZeroMemory(authTag);
                _signaling.ConnectChallengeReceived -= OnChallenge;
                _signaling.ConnectAuthVerified -= OnAuthVerified;
                _signaling.VersionCheckResultReceived -= OnVersionResult;
                _signaling.BridgeReady -= OnBridgeReady;
                _signaling.ConnectRejected -= OnReject;
            }
        }
        catch (OperationCanceledException)
        {
            return (false, Tr("Conn_Timeout", "Connection timed out or canceled"));
        }
        catch (Exception ex)
        {
            _log.Error($"ConnectToRemoteAsync error: {ex}");
            return (false, ex.Message);
        }
        finally
        {
            if (transport != IntPtr.Zero)
            {
                NativeMethods.Osmium_DestroyTransport(transport);
            }

            Interlocked.Decrement(ref _activeClientSessionCount);
            RemoteSessionStateChanged?.Invoke();
        }
    }
}