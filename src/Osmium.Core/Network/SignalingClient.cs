using System.Net;
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
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly LoggingService _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

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
    public event Action<IncomingConnectEventArgs>? IncomingConnect;
    public event Action<ConnectChallengeEventArgs>? ConnectChallengeReceived;
    public event Action<ConnectAuthEventArgs>? ConnectAuthReceived;
    public event Action<ConnectAuthVerifiedEventArgs>? ConnectAuthVerified;
    public event Action<VersionCheckResultEventArgs>? VersionCheckResultReceived;
    public event Action<BridgeReadyEventArgs>? BridgeReady;
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
        Stop();
        _serverUrl = NormalizeServerUrl(serverUrl);
        _deviceId = deviceId;
        _deviceName = deviceName;
        _allowRemote = allowRemote;
        if (ltoken != null) _ltoken = ltoken;

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
    }

    public void Stop()
    {
        try
        {
            _loopCts?.Cancel();
            _ws?.Abort();
        }
        catch
        {
        }

        _ws?.Dispose();
        _ws = null;
        _loopCts?.Dispose();
        _loopCts = null;
        SetState(SignalingConnectionState.Disconnected, "Stopped");
    }

    public async Task UpdateAuthTokenAsync(string? ltoken)
    {
        _ltoken = ltoken ?? string.Empty;
        if (State != SignalingConnectionState.Connected) return;

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
        if (State != SignalingConnectionState.Connected) return;

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
        if (State != SignalingConnectionState.Connected) return;
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
        if (State != SignalingConnectionState.Connected) return;
        await SendJsonAsync(new
        {
            type = "account_force_disconnect",
            targetHostDeviceId = DeviceIdentity.NormalizeDeviceId(targetHostDeviceId)
        });
    }

    public async Task SendAccountBlacklistUserAsync(string targetHostDeviceId)
    {
        if (State != SignalingConnectionState.Connected) return;
        await SendJsonAsync(new
        {
            type = "account_blacklist_user",
            targetHostDeviceId = DeviceIdentity.NormalizeDeviceId(targetHostDeviceId)
        });
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct = default)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;

        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(SignalingConnectionState.Connecting, $"Connecting to {_serverUrl}");
                var serverUri = new Uri(_serverUrl);
                if (IPAddress.TryParse(serverUri.DnsSafeHost, out var literalIp) &&
                    literalIp.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    throw new NotSupportedException("Signaling server only supports IPv4.");
                }

                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                ws.Options.HttpVersion = HttpVersion.Version11;
                ws.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;

                using var ipv4Handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (context, token) =>
                    {
                        string host = context.DnsEndPoint.Host;
                        int port = context.DnsEndPoint.Port;
                        IPAddress[] addresses;
                        if (IPAddress.TryParse(host, out var parsed))
                        {
                            if (parsed.AddressFamily != AddressFamily.InterNetwork)
                            {
                                throw new NotSupportedException("Signaling server only supports IPv4.");
                            }

                            addresses = [parsed];
                        }
                        else
                        {
                            addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token);
                            if (addresses.Length == 0)
                            {
                                throw new SocketException((int)SocketError.HostNotFound);
                            }
                        }

                        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                        {
                            NoDelay = true
                        };
                        try
                        {
                            await socket.ConnectAsync(addresses, port, token);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    }
                };
                using var invoker = new HttpMessageInvoker(ipv4Handler);
                await ws.ConnectAsync(serverUri, invoker, ct);

                _ws = ws;
                await SendJsonAsync(new
                {
                    type = "register",
                    deviceId = _deviceId,
                    deviceName = _deviceName,
                    allowRemote = _allowRemote,
                    osVersion = Environment.OSVersion.VersionString,
                    ltoken = _ltoken
                }, ct);

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pingTask = Task.Run(async () =>
                {
                    while (!pingCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(20), pingCts.Token);
                        await SendJsonAsync(new { type = "ping" }, pingCts.Token);
                    }
                }, pingCts.Token);

                await ReceiveLoopAsync(ws, ct);
                pingCts.Cancel();
                try
                {
                    await pingTask;
                }
                catch
                {
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log.Warn($"Signaling connection error: {ex.Message}");
                SetState(SignalingConnectionState.Error, ex.Message);
            }
            finally
            {
                _ws = null;
            }

            if (ct.IsCancellationRequested) break;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}