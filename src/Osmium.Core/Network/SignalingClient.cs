using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Osmium.Core.Models;
using Osmium.Core.Services;

namespace Osmium.Core.Network;

public enum SignalingConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public sealed class IncomingConnectEventArgs : EventArgs
{
    public required string SessionId
    {
        get;
        init;
    }

    public required string ClientDeviceId
    {
        get;
        init;
    }

    public required string ClientName
    {
        get;
        init;
    }

    public string? ControllerUserId
    {
        get;
        init;
    }

    public string? ControllerUserName
    {
        get;
        init;
    }

    public string? ControllerDeviceName
    {
        get;
        init;
    }

    public string? HostDisplayName
    {
        get;
        init;
    }

    public required string ClientPublicKeyBase64
    {
        get;
        init;
    }

    public bool EnableEncryption
    {
        get;
        init;
    } = true;

    public bool SameAccountVerified
    {
        get;
        init;
    }

    public string? SameAccountAuthSecret
    {
        get;
        init;
    }

    public bool ViewOnlyRequested
    {
        get;
        init;
    }

    public bool WebRtcRequested
    {
        get;
        init;
    }

    public required string CandidatesJson
    {
        get;
        init;
    }

    public string ObservedClientIp
    {
        get;
        init;
    } = string.Empty;
}

public sealed class ConnectAcceptEventArgs : EventArgs
{
    public required string SessionId
    {
        get;
        init;
    }

    public required string HostDeviceId
    {
        get;
        init;
    }

    public required string HostName
    {
        get;
        init;
    }

    public required string HostPublicKeyBase64
    {
        get;
        init;
    }

    public required string HostChallengeNonceBase64
    {
        get;
        init;
    }

    public bool EnableEncryption
    {
        get;
        init;
    } = true;

    public required string CandidatesJson
    {
        get;
        init;
    }

    public string StunHost
    {
        get;
        init;
    } = string.Empty;

    public required ushort StunPort
    {
        get;
        init;
    }
}

public sealed class ConnectChallengeEventArgs : EventArgs
{
    public required string SessionId
    {
        get;
        init;
    }

    public required string HostDeviceId
    {
        get;
        init;
    }

    public required string HostName
    {
        get;
        init;
    }

    public string? ControllerUserName
    {
        get;
        init;
    }

    public required string HostPublicKeyBase64
    {
        get;
        init;
    }

    public required string HostChallengeNonceBase64
    {
        get;
        init;
    }

    public bool EnableEncryption
    {
        get;
        init;
    } = true;

    public string? SameAccountAuthSecret
    {
        get;
        init;
    }

    public string StunHost
    {
        get;
        init;
    } = string.Empty;

    public required ushort StunPort
    {
        get;
        init;
    }
}

public sealed class ConnectAuthEventArgs : EventArgs
{
    public required string SessionId
    {
        get;
        init;
    }

    public required string ClientDeviceId
    {
        get;
        init;
    }

    public required string AuthTagBase64
    {
        get;
        init;
    }
}

public sealed class ConnectAuthVerifiedEventArgs : EventArgs
{
    public required string SessionId
    {
        get;
        init;
    }

    public required string HostDeviceId
    {
        get;
        init;
    }
}

public sealed class VersionCheckResultEventArgs : EventArgs
{
    public required string SessionId
    {
        get;
        init;
    }

    public required bool Compatible
    {
        get;
        init;
    }

    public required string Code
    {
        get;
        init;
    }

    public required string ClientVersion
    {
        get;
        init;
    }

    public required string HostVersion
    {
        get;
        init;
    }

    public required string Reason
    {
        get;
        init;
    }

    public bool WebRtc
    {
        get;
        init;
    }

    public string StunHost
    {
        get;
        init;
    } = string.Empty;

    public ushort StunPort
    {
        get;
        init;
    }
}

public sealed class WebRtcSdpEventArgs : EventArgs
{
    public required string SessionId
    {
        get;
        init;
    }

    public string FromDeviceId
    {
        get;
        init;
    } = string.Empty;

    public required string Sdp
    {
        get;
        init;
    }
}

public sealed class WebRtcIceEventArgs : EventArgs
{
    public required string SessionId
    {
        get;
        init;
    }

    public string FromDeviceId
    {
        get;
        init;
    } = string.Empty;

    public required string Candidate
    {
        get;
        init;
    }

    public string? SdpMid
    {
        get;
        init;
    }

    public int SdpMLineIndex
    {
        get;
        init;
    }
}

public sealed class BridgeReadyEventArgs : EventArgs
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

    public required string CandidatesJson
    {
        get;
        init;
    }

    public string StunHost
    {
        get;
        init;
    } = string.Empty;

    public required ushort StunPort
    {
        get;
        init;
    }

    public string RelayToken
    {
        get;
        init;
    } = string.Empty;

    public string RelayPath
    {
        get;
        init;
    } = "/relay";

    public bool KcpEnabled
    {
        get;
        init;
    }
}

public sealed class RelayFallbackEventArgs : EventArgs
{
    public required string SessionId
    {
        get;
        init;
    }

    public string RelayToken
    {
        get;
        init;
    } = string.Empty;

    public string RelayPath
    {
        get;
        init;
    } = "/relay";

    public string FromDeviceId
    {
        get;
        init;
    } = string.Empty;
}

public sealed class SessionEstablishedEventArgs : EventArgs
{
    public required string SessionId
    {
        get;
        init;
    }

    public long StartedAtUnixMs
    {
        get;
        init;
    }

    public string ClientDeviceId
    {
        get;
        init;
    } = string.Empty;

    public string HostDeviceId
    {
        get;
        init;
    } = string.Empty;
}

public sealed class ConnectRejectEventArgs : EventArgs
{
    public required string SessionId
    {
        get;
        init;
    }

    public required string Code
    {
        get;
        init;
    }

    public required string Reason
    {
        get;
        init;
    }

    public int CooldownMs
    {
        get;
        init;
    }

    public AccountChallengeInfo? Challenge
    {
        get;
        init;
    }
}

public sealed class PowerCommandEventArgs : EventArgs
{
    public string CommandId
    {
        get;
        init;
    } = string.Empty;

    public string Action
    {
        get;
        init;
    } = string.Empty;

    public string TargetDeviceId
    {
        get;
        init;
    } = string.Empty;

    public string RequesterUserId
    {
        get;
        init;
    } = string.Empty;

    public string RequesterDeviceId
    {
        get;
        init;
    } = string.Empty;

    public string RequesterDeviceName
    {
        get;
        init;
    } = string.Empty;

    public int DelaySeconds
    {
        get;
        init;
    } = 3;

    public long IssuedAtUnixMs
    {
        get;
        init;
    }
}

public sealed class PowerWolRelayEventArgs : EventArgs
{
    public string CommandId
    {
        get;
        init;
    } = string.Empty;

    public string TargetDeviceId
    {
        get;
        init;
    } = string.Empty;

    public string TargetDeviceName
    {
        get;
        init;
    } = string.Empty;

    public string RequesterUserId
    {
        get;
        init;
    } = string.Empty;

    public string RequesterDeviceId
    {
        get;
        init;
    } = string.Empty;

    public List<string> Macs
    {
        get;
        init;
    } = [];

    public List<string> Broadcasts
    {
        get;
        init;
    } = [];

    public List<int> Ports
    {
        get;
        init;
    } = [9, 7];

    public long IssuedAtUnixMs
    {
        get;
        init;
    }
}

public sealed class QuickLaunchBrowseRequestEventArgs : EventArgs
{
    public string RequestId
    {
        get;
        init;
    } = string.Empty;

    public string RequesterDeviceId
    {
        get;
        init;
    } = string.Empty;

    public string RequesterUserId
    {
        get;
        init;
    } = string.Empty;

    public string Path
    {
        get;
        init;
    } = string.Empty;
}

public sealed class QuickLaunchValidateRequestEventArgs : EventArgs
{
    public string RequestId
    {
        get;
        init;
    } = string.Empty;

    public string RequesterDeviceId
    {
        get;
        init;
    } = string.Empty;

    public string RequesterUserId
    {
        get;
        init;
    } = string.Empty;

    public string Path
    {
        get;
        init;
    } = string.Empty;
}

public sealed class QuickLaunchStartRequestEventArgs : EventArgs
{
    public string RequestId
    {
        get;
        init;
    } = string.Empty;

    public string RequesterDeviceId
    {
        get;
        init;
    } = string.Empty;

    public string RequesterUserId
    {
        get;
        init;
    } = string.Empty;

    public string AppId
    {
        get;
        init;
    } = string.Empty;

    public string ExePath
    {
        get;
        init;
    } = string.Empty;

    public string AppName
    {
        get;
        init;
    } = string.Empty;
}

public sealed partial class SignalingClient : IDisposable
{
    private const int OfflineFailureThreshold = 3;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatRetryDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(1500);
    private const long StaleConnectAbortMinAgeMs = 800;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly LoggingService _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _heartbeatLock = new();
    private readonly object _wakeLock = new();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private CancellationTokenSource? _reconnectDelayWakeCts;
    private CancellationTokenSource? _activeConnectWakeCts;
    private CancellationTokenSource? _heartbeatWakeCts;
    private long _activeConnectStartedTickMs;
    private int _disposed;
    private volatile bool _isTransportRegistered;
    private int _consecutiveFailures;
    private TaskCompletionSource<bool>? _pendingRegister;
    private TaskCompletionSource<bool>? _pendingPong;

    private string _serverUrl = "wss://osmium.fufu.skin";
    private string _deviceId = string.Empty;
    private string _deviceName = Environment.MachineName;
    private bool _allowRemote = true;
    private bool _allowQuickLaunch;
    private string _ltoken = string.Empty;
    private LocalPowerProfile? _powerProfile;

    public SignalingConnectionState State
    {
        get;
        private set;
    } = SignalingConnectionState.Disconnected;

    public string AdvertisedStunHost
    {
        get;
        private set;
    } = string.Empty;

    public ushort AdvertisedStunPort
    {
        get;
        private set;
    } = 3478;

    public string ObservedPublicIp
    {
        get;
        private set;
    } = string.Empty;

    public string LastError
    {
        get;
        private set;
    } = string.Empty;

    public event Action<SignalingConnectionState, string>? StateChanged;
    public event Action? Registered;
    public event Action<IncomingConnectEventArgs>? IncomingConnect;
    public event Action<ConnectChallengeEventArgs>? ConnectChallengeReceived;
    public event Action<ConnectAuthEventArgs>? ConnectAuthReceived;
    public event Action<ConnectAuthVerifiedEventArgs>? ConnectAuthVerified;
    public event Action<VersionCheckResultEventArgs>? VersionCheckResultReceived;
    public event Action<BridgeReadyEventArgs>? BridgeReady;
    public event Action<RelayFallbackEventArgs>? RelayFallbackReceived;
    public event Action<SessionEstablishedEventArgs>? SessionEstablishedReceived;
    public event Action<ConnectAcceptEventArgs>? ConnectAccepted;
    public event Action<ConnectRejectEventArgs>? ConnectRejected;
    public event Action<int, string>? ConnectCooldownUpdated;
    public event Action<string, string, string>? SessionEnded;
    public event Action<string, string>? SignalingErrorReceived;
    public event Action<IReadOnlyList<AccountDeviceDto>>? AccountDevicesSyncReceived;
    public event Action<string>? AccountRevokedReceived;
    public event Action<PowerCommandEventArgs>? PowerCommandReceived;
    public event Action<PowerWolRelayEventArgs>? PowerWolRelayReceived;
    public event Action? QuickLaunchAppsSyncReceived;
    public event Action<QuickLaunchBrowseRequestEventArgs>? QuickLaunchBrowseRequestReceived;
    public event Action<QuickLaunchValidateRequestEventArgs>? QuickLaunchValidateRequestReceived;
    public event Action<QuickLaunchStartRequestEventArgs>? QuickLaunchStartRequestReceived;
    public event Action<string, string>? QuickLaunchCancelReceived;
    public event Action<QuickLaunchBrowseResult>? QuickLaunchBrowseResponseReceived;
    public event Action<QuickLaunchValidateResult>? QuickLaunchValidateResponseReceived;
    public event Action<QuickLaunchStartResponse>? QuickLaunchStartResponseReceived;
    public event Action<WebRtcSdpEventArgs>? WebRtcOfferReceived;
    public event Action<WebRtcSdpEventArgs>? WebRtcAnswerReceived;
    public event Action<WebRtcIceEventArgs>? WebRtcIceReceived;
    public event Action? NetworkAddressUpdated;

    public SignalingClient(LoggingService log)
    {
        _log = log;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    public static string NormalizeServerUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "wss://osmium.fufu.skin";
        }

        string trimmed = raw.Trim();
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "wss://" + trimmed["https://".Length..];
        }
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "ws://" + trimmed["http://".Length..];
        }
        else if (!trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) &&
                 !trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "wss://" + trimmed;
        }

        return trimmed.TrimEnd('/');
    }

    public void Start(string serverUrl, string deviceId, string deviceName, bool allowRemote, string? ltoken = null,
        LocalPowerProfile? powerProfile = null, bool allowQuickLaunch = false)
    {
        StopInternal(resetState: false);
        _serverUrl = NormalizeServerUrl(serverUrl);
        _deviceId = deviceId;
        _deviceName = deviceName;
        _allowRemote = allowRemote;
        _allowQuickLaunch = allowQuickLaunch;
        if (ltoken != null) _ltoken = ltoken;
        if (powerProfile != null) _powerProfile = powerProfile;

        var cts = new CancellationTokenSource();
        _loopCts = cts;
        _loopTask = Task.Run(() => RunLoopAsync(cts));
    }

    public void Stop()
    {
        StopInternal(resetState: true);
    }

    private void StopInternal(bool resetState)
    {
        _isTransportRegistered = false;
        CompletePendingRegister(false);
        CompletePendingPong(false);

        var oldCts = Interlocked.Exchange(ref _loopCts, null);
        var oldWs = Interlocked.Exchange(ref _ws, null);

        try
        {
            oldCts?.Cancel();
            oldWs?.Abort();
        }
        catch
        {
        }

        if (resetState)
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            SetState(SignalingConnectionState.Disconnected, "Stopped");
        }
    }

    public async Task UpdateAuthTokenAsync(string? ltoken)
    {
        _ltoken = ltoken ?? string.Empty;
        if (!_isTransportRegistered) return;

        await SendJsonAsync(new
        {
            type = "auth_update",
            ltoken = _ltoken
        });
    }

    public async Task UpdateStatusAsync(bool allowRemote, string deviceName, LocalPowerProfile? powerProfile = null,
        bool? allowQuickLaunch = null)
    {
        _allowRemote = allowRemote;
        _deviceName = deviceName;
        if (powerProfile != null) _powerProfile = powerProfile;
        if (allowQuickLaunch.HasValue) _allowQuickLaunch = allowQuickLaunch.Value;
        if (!_isTransportRegistered) return;

        var profile = _powerProfile;
        await SendJsonAsync(new
        {
            type = "update_status",
            allowRemote,
            allowQuickLaunch = _allowQuickLaunch,
            deviceName,
            powerProfile = profile == null
                ? null
                : new
                {
                    allowRemotePower = profile.AllowRemotePower,
                    wolMacs = profile.AllowRemotePower ? profile.MacAddresses : new List<string>(),
                    wolBroadcasts = profile.AllowRemotePower ? profile.BroadcastAddresses : new List<string>(),
                    wolReady = profile.AllowRemotePower && profile.WolReady
                }
        });
    }

    public async Task SendConnectRequestAsync(
        string sessionId,
        string targetDeviceId,
        string clientPublicKeyBase64,
        string candidatesJson,
        bool enableEncryption = true,
        string? ltoken = null,
        bool sameAccountConnect = false,
        bool viewOnly = false)
    {
        using var candDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(candidatesJson) ? "[]" : candidatesJson);
        await SendJsonAsync(new
        {
            type = "connect_request",
            sessionId,
            targetDeviceId,
            clientName = _deviceName,
            clientPublicKey = clientPublicKeyBase64,
            enableEncryption,
            ltoken = string.IsNullOrWhiteSpace(ltoken) ? _ltoken : ltoken,
            sameAccountConnect,
            viewOnly,
            candidates = candDoc.RootElement
        });
    }

    public async Task SendConnectChallengeAsync(
        string sessionId,
        string targetClientDeviceId,
        string hostPublicKeyBase64,
        string hostChallengeNonceBase64,
        bool enableEncryption = true,
        bool webrtcSupported = true)
    {
        await SendJsonAsync(new
        {
            type = "connect_challenge",
            sessionId,
            targetClientDeviceId,
            hostPublicKey = hostPublicKeyBase64,
            hostChallengeNonce = hostChallengeNonceBase64,
            enableEncryption,
            webrtcSupported
        });
    }

    public async Task SendWebRtcAnswerAsync(string sessionId, string sdp)
    {
        if (!_isTransportRegistered || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(sdp)) return;
        await SendJsonAsync(new
        {
            type = "webrtc_answer",
            sessionId,
            sdp
        });
    }

    public async Task SendWebRtcIceAsync(string sessionId, string candidate, string? sdpMid, int sdpMLineIndex)
    {
        if (!_isTransportRegistered || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(candidate)) return;
        await SendJsonAsync(new
        {
            type = "webrtc_ice",
            sessionId,
            candidate,
            sdpMid,
            sdpMLineIndex
        });
    }

    public async Task SendConnectAuthAsync(
        string sessionId,
        string targetDeviceId,
        string authTagBase64)
    {
        await SendJsonAsync(new
        {
            type = "connect_auth",
            sessionId,
            targetDeviceId,
            authTag = authTagBase64
        });
    }

    public async Task SendConnectAuthVerifiedAsync(
        string sessionId,
        string targetClientDeviceId,
        string hostVersion)
    {
        await SendJsonAsync(new
        {
            type = "connect_auth_verified",
            sessionId,
            targetClientDeviceId,
            hostVersion
        });
    }

    public async Task SendVersionReportAsync(string sessionId, string version)
    {
        await SendJsonAsync(new
        {
            type = "session_version_report",
            sessionId,
            version
        });
    }

    public async Task SendBridgeCandidatesAsync(string sessionId, string candidatesJson)
    {
        using var candDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(candidatesJson) ? "[]" : candidatesJson);
        await SendJsonAsync(new
        {
            type = "bridge_candidates",
            sessionId,
            candidates = candDoc.RootElement,
            transportCaps = new[] { "kcp-v1" }
        });
    }

    public async Task SendRelayFallbackAsync(string sessionId)
    {
        await SendJsonAsync(new
        {
            type = "relay_fallback",
            sessionId
        });
    }

    public async Task SendSessionConfirmAsync(string sessionId)
    {
        if (!_isTransportRegistered || string.IsNullOrWhiteSpace(sessionId)) return;
        await SendJsonAsync(new
        {
            type = "session_confirm",
            sessionId
        });
    }

    public async Task SendConnectAcceptAsync(
        string sessionId,
        string targetClientDeviceId,
        string hostPublicKeyBase64,
        string hostChallengeNonceBase64,
        string candidatesJson,
        bool enableEncryption = true)
    {
        using var candDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(candidatesJson) ? "[]" : candidatesJson);
        await SendJsonAsync(new
        {
            type = "connect_accept",
            sessionId,
            targetClientDeviceId,
            accepted = true,
            hostPublicKey = hostPublicKeyBase64,
            hostChallengeNonce = hostChallengeNonceBase64,
            enableEncryption,
            candidates = candDoc.RootElement
        });
    }

    public async Task SendConnectRejectAsync(string sessionId, string targetClientDeviceId, string code, string reason)
    {
        await SendJsonAsync(new
        {
            type = "connect_reject",
            sessionId,
            targetClientDeviceId,
            code,
            reason
        });
    }

    public async Task SendSessionEndAsync(string sessionId, string targetDeviceId, string reason, string? role = null,
        bool failed = false)
    {
        if (!_isTransportRegistered) return;
        await SendJsonAsync(new
        {
            type = "session_end",
            sessionId,
            targetDeviceId,
            reason,
            role,
            failed
        });
    }

    public async Task SendAccountForceDisconnectAsync(string targetHostDeviceId)
    {
        if (!_isTransportRegistered) return;
        await SendJsonAsync(new
        {
            type = "account_force_disconnect",
            targetHostDeviceId = DeviceIdentity.NormalizeDeviceId(targetHostDeviceId)
        });
    }

    public async Task SendAccountBlacklistUserAsync(string targetHostDeviceId)
    {
        if (!_isTransportRegistered) return;
        await SendJsonAsync(new
        {
            type = "account_blacklist_user",
            targetHostDeviceId = DeviceIdentity.NormalizeDeviceId(targetHostDeviceId)
        });
    }

    public Task<bool> SendQuickLaunchBrowseRequestAsync(string requestId, string targetDeviceId, string path)
    {
        if (!_isTransportRegistered) return Task.FromResult(false);
        return SendJsonAsync(new
        {
            type = "quick_launch_browse_req",
            requestId,
            targetDeviceId = DeviceIdentity.NormalizeDeviceId(targetDeviceId),
            path = path ?? string.Empty
        });
    }

    public Task<bool> SendQuickLaunchBrowseResponseAsync(string requesterDeviceId, QuickLaunchBrowseResult res)
    {
        if (!_isTransportRegistered) return Task.FromResult(false);
        return SendJsonAsync(new
        {
            type = "quick_launch_browse_res",
            requestId = res.RequestId,
            requesterDeviceId = DeviceIdentity.NormalizeDeviceId(requesterDeviceId),
            ok = res.Ok,
            code = res.Code,
            reason = res.Reason,
            currentPath = res.CurrentPath,
            parentPath = res.ParentPath,
            directories = res.Directories,
            executables = res.Executables
        });
    }

    public Task<bool> SendQuickLaunchValidateRequestAsync(string requestId, string targetDeviceId, string path)
    {
        if (!_isTransportRegistered) return Task.FromResult(false);
        return SendJsonAsync(new
        {
            type = "quick_launch_validate_req",
            requestId,
            targetDeviceId = DeviceIdentity.NormalizeDeviceId(targetDeviceId),
            path = path ?? string.Empty
        });
    }

    public Task<bool> SendQuickLaunchValidateResponseAsync(string requesterDeviceId, QuickLaunchValidateResult res)
    {
        if (!_isTransportRegistered) return Task.FromResult(false);
        return SendJsonAsync(new
        {
            type = "quick_launch_validate_res",
            requestId = res.RequestId,
            requesterDeviceId = DeviceIdentity.NormalizeDeviceId(requesterDeviceId),
            ok = res.Ok,
            code = res.Code,
            reason = res.Reason,
            needsExeSelection = res.NeedsExeSelection,
            directoryPath = res.DirectoryPath,
            resolvedExePath = res.ResolvedExePath,
            suggestedName = res.SuggestedName,
            iconData = res.IconData,
            candidates = res.Candidates
        });
    }

    public Task<bool> SendQuickLaunchStartRequestAsync(
        string requestId,
        string targetDeviceId,
        string appId,
        string exePath,
        string appName)
    {
        if (!_isTransportRegistered) return Task.FromResult(false);
        return SendJsonAsync(new
        {
            type = "quick_launch_start_req",
            requestId,
            targetDeviceId = DeviceIdentity.NormalizeDeviceId(targetDeviceId),
            appId = appId ?? string.Empty,
            exePath = exePath ?? string.Empty,
            appName = appName ?? string.Empty
        });
    }

    public Task<bool> SendQuickLaunchStartResponseAsync(string requesterDeviceId, QuickLaunchStartResponse res)
    {
        if (!_isTransportRegistered) return Task.FromResult(false);
        return SendJsonAsync(new
        {
            type = "quick_launch_start_res",
            requestId = res.RequestId,
            requesterDeviceId = DeviceIdentity.NormalizeDeviceId(requesterDeviceId),
            status = res.Status,
            ok = res.Ok,
            code = res.Code,
            reason = res.Reason,
            pid = res.Pid
        });
    }

    public Task<bool> SendQuickLaunchCancelAsync(string requestId, string targetDeviceId)
    {
        if (!_isTransportRegistered) return Task.FromResult(false);
        return SendJsonAsync(new
        {
            type = "quick_launch_cancel",
            requestId,
            targetDeviceId = DeviceIdentity.NormalizeDeviceId(targetDeviceId)
        });
    }

    private Task<bool> SendJsonAsync(object payload, CancellationToken ct = default)
        => SendJsonAsync(_ws, payload, ct);

    private async Task<bool> SendJsonAsync(ClientWebSocket? ws, object payload, CancellationToken ct = default)
    {
        if (ws == null || ws.State != WebSocketState.Open) return false;

        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        using var sendTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendTimeoutCts.CancelAfter(SendTimeout);
        CancellationToken sendToken = sendTimeoutCts.Token;

        bool lockAcquired = false;
        try
        {
            await _sendLock.WaitAsync(sendToken);
            lockAcquired = true;

            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, sendToken);
                return true;
            }

            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            try
            {
                ws.Abort();
            }
            catch
            {
            }

            return false;
        }
        finally
        {
            if (lockAcquired)
            {
                _sendLock.Release();
            }
        }
    }
}