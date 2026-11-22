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
    private string _ltoken = string.Empty;

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
    public event Action<ConnectAcceptEventArgs>? ConnectAccepted;
    public event Action<ConnectRejectEventArgs>? ConnectRejected;
    public event Action<int, string>? ConnectCooldownUpdated;
    public event Action<string, string, string>? SessionEnded;
    public event Action<string, string>? SignalingErrorReceived;
    public event Action<IReadOnlyList<AccountDeviceDto>>? AccountDevicesSyncReceived;
    public event Action<string>? AccountRevokedReceived;

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

    public void Start(string serverUrl, string deviceId, string deviceName, bool allowRemote, string? ltoken = null)
    {
        StopInternal(resetState: false);
        _serverUrl = NormalizeServerUrl(serverUrl);
        _deviceId = deviceId;
        _deviceName = deviceName;
        _allowRemote = allowRemote;
        if (ltoken != null) _ltoken = ltoken;

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

    public async Task UpdateStatusAsync(bool allowRemote, string deviceName)
    {
        _allowRemote = allowRemote;
        _deviceName = deviceName;
        if (!_isTransportRegistered) return;

        await SendJsonAsync(new
        {
            type = "update_status",
            allowRemote,
            deviceName
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
        bool enableEncryption = true)
    {
        await SendJsonAsync(new
        {
            type = "connect_challenge",
            sessionId,
            targetClientDeviceId,
            hostPublicKey = hostPublicKeyBase64,
            hostChallengeNonce = hostChallengeNonceBase64,
            enableEncryption
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
            candidates = candDoc.RootElement
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

    public async Task SendSessionEndAsync(string sessionId, string targetDeviceId, string reason, string? role = null)
    {
        if (!_isTransportRegistered) return;
        await SendJsonAsync(new
        {
            type = "session_end",
            sessionId,
            targetDeviceId,
            reason,
            role
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