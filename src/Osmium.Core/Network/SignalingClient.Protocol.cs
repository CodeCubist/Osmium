using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Osmium.Core.Models;
using Osmium.Core.Services;

namespace Osmium.Core.Network;

public sealed partial class SignalingClient : IDisposable
{
    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            string text = Encoding.UTF8.GetString(ms.ToArray());
            ProcessMessage(text);
        }
    }

    private static string ReadCodeProperty(JsonElement root, string fallback)
    {
        if (!root.TryGetProperty("code", out var c)) return fallback;
        return c.ValueKind switch
        {
            JsonValueKind.Number => c.TryGetInt32(out int num)
                ? num.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : c.GetRawText(),
            JsonValueKind.String => c.GetString() ?? fallback,
            _ => fallback
        };
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            string type = typeEl.GetString() ?? string.Empty;

            switch (type)
            {
                case "pong":
                {
                    MarkHeartbeatAlive();
                    break;
                }
                case "registered":
                {
                    if (root.TryGetProperty("stunHost", out var sh) && sh.ValueKind == JsonValueKind.String)
                        AdvertisedStunHost = sh.GetString() ?? string.Empty;
                    if (root.TryGetProperty("stunPort", out var p) && p.ValueKind == JsonValueKind.Number)
                        AdvertisedStunPort = (ushort)p.GetInt32();
                    if (root.TryGetProperty("observedIp", out var ip))
                        ObservedPublicIp = ip.GetString() ?? string.Empty;
                    int registeredCooldownMs = root.TryGetProperty("cooldownRemainingMs", out var cd) &&
                                               cd.ValueKind == JsonValueKind.Number
                        ? Math.Max(0, cd.GetInt32())
                        : 0;
                    _isTransportRegistered = true;
                    LastError = string.Empty;
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    CompletePendingRegister(true);
                    CompletePendingPong(true);
                    SetState(SignalingConnectionState.Connected, $"Connected ({ObservedPublicIp})");
                    Registered?.Invoke();
                    ConnectCooldownUpdated?.Invoke(registeredCooldownMs, "REGISTERED");
                    _log.Info(
                        $"Registered on signaling server as {_deviceId} (STUN {AdvertisedStunHost}:{AdvertisedStunPort})");
                    break;
                }
                case "connect_cooldown":
                {
                    int remainingMs = root.TryGetProperty("remainingMs", out var rm) &&
                                      rm.ValueKind == JsonValueKind.Number
                        ? Math.Max(0, rm.GetInt32())
                        : (root.TryGetProperty("cooldownMs", out var cm) && cm.ValueKind == JsonValueKind.Number
                            ? Math.Max(0, cm.GetInt32())
                            : 0);
                    string reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString() ?? "COOLDOWN"
                        : "COOLDOWN";
                    ConnectCooldownUpdated?.Invoke(remainingMs, reason);
                    break;
                }
                case "account_devices_sync":
                {
                    if (root.TryGetProperty("devices", out var devsEl) && devsEl.ValueKind == JsonValueKind.Array)
                    {
                        var list = devsEl.Deserialize<List<AccountDeviceDto>>(JsonOpts) ?? [];
                        AccountDevicesSyncReceived?.Invoke(list);
                    }

                    break;
                }
                case "account_revoked":
                {
                    string reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString() ?? "Revoked"
                        : "Revoked";
                    AccountRevokedReceived?.Invoke(reason);
                    break;
                }
                case "incoming_connect":
                {
                    var args = new IncomingConnectEventArgs
                    {
                        SessionId = root.GetProperty("sessionId").GetString() ?? string.Empty,
                        ClientDeviceId = root.GetProperty("clientDeviceId").GetString() ?? string.Empty,
                        ClientName = root.TryGetProperty("clientName", out var cn)
                            ? cn.GetString() ?? "Remote Peer"
                            : "Remote Peer",
                        ControllerUserId =
                            root.TryGetProperty("controllerUserId", out var cuid) &&
                            cuid.ValueKind == JsonValueKind.String
                                ? cuid.GetString()
                                : null,
                        ControllerUserName =
                            root.TryGetProperty("controllerUserName", out var cun) &&
                            cun.ValueKind == JsonValueKind.String
                                ? cun.GetString()
                                : null,
                        ControllerDeviceName =
                            root.TryGetProperty("controllerDeviceName", out var cdn) &&
                            cdn.ValueKind == JsonValueKind.String
                                ? cdn.GetString()
                                : null,
                        HostDisplayName =
                            root.TryGetProperty("hostDisplayName", out var hdn) && hdn.ValueKind == JsonValueKind.String
                                ? hdn.GetString()
                                : null,
                        ClientPublicKeyBase64 = root.GetProperty("clientPublicKey").GetString() ?? string.Empty,
                        EnableEncryption = !root.TryGetProperty("enableEncryption", out var ce) ||
                                           ce.ValueKind != JsonValueKind.False,
                        SameAccountVerified = root.TryGetProperty("sameAccountVerified", out var sav) &&
                                              sav.ValueKind == JsonValueKind.True,
                        SameAccountAuthSecret =
                            root.TryGetProperty("sameAccountAuthSecret", out var sas) &&
                            sas.ValueKind == JsonValueKind.String
                                ? sas.GetString()
                                : null,
                        ViewOnlyRequested = root.TryGetProperty("viewOnlyRequested", out var vor) &&
                                            vor.ValueKind == JsonValueKind.True,
                        CandidatesJson = root.TryGetProperty("candidates", out var cands) ? cands.GetRawText() : "[]",
                        ObservedClientIp = root.TryGetProperty("observedClientIp", out var oip)
                            ? oip.GetString() ?? string.Empty
                            : string.Empty
                    };
                    IncomingConnect?.Invoke(args);
                    break;
                }
                case "connect_challenge":
                {
                    var args = new ConnectChallengeEventArgs
                    {
                        SessionId = root.GetProperty("sessionId").GetString() ?? string.Empty,
                        HostDeviceId = root.GetProperty("hostDeviceId").GetString() ?? string.Empty,
                        HostName = root.TryGetProperty("hostName", out var hn)
                            ? hn.GetString() ?? "Osmium Host"
                            : "Osmium Host",
                        ControllerUserName =
                            root.TryGetProperty("controllerUserName", out var cun) &&
                            cun.ValueKind == JsonValueKind.String
                                ? cun.GetString()
                                : null,
                        HostPublicKeyBase64 = root.GetProperty("hostPublicKey").GetString() ?? string.Empty,
                        HostChallengeNonceBase64 = root.GetProperty("hostChallengeNonce").GetString() ?? string.Empty,
                        EnableEncryption = !root.TryGetProperty("enableEncryption", out var he) ||
                                           he.ValueKind != JsonValueKind.False,
                        SameAccountAuthSecret =
                            root.TryGetProperty("sameAccountAuthSecret", out var sas) &&
                            sas.ValueKind == JsonValueKind.String
                                ? sas.GetString()
                                : null,
                        StunHost = root.TryGetProperty("stunHost", out var sh) && sh.ValueKind == JsonValueKind.String
                            ? sh.GetString() ?? string.Empty
                            : AdvertisedStunHost,
                        StunPort = root.TryGetProperty("stunPort", out var up) && up.ValueKind == JsonValueKind.Number
                            ? (ushort)up.GetInt32()
                            : AdvertisedStunPort
                    };
                    ConnectChallengeReceived?.Invoke(args);
                    break;
                }
                case "connect_auth":
                {
                    var args = new ConnectAuthEventArgs
                    {
                        SessionId = root.GetProperty("sessionId").GetString() ?? string.Empty,
                        ClientDeviceId = root.GetProperty("clientDeviceId").GetString() ?? string.Empty,
                        AuthTagBase64 = root.GetProperty("authTag").GetString() ?? string.Empty
                    };
                    ConnectAuthReceived?.Invoke(args);
                    break;
                }
                case "connect_auth_verified":
                {
                    var args = new ConnectAuthVerifiedEventArgs
                    {
                        SessionId = root.GetProperty("sessionId").GetString() ?? string.Empty,
                        HostDeviceId = root.GetProperty("hostDeviceId").GetString() ?? string.Empty
                    };
                    ConnectAuthVerified?.Invoke(args);
                    break;
                }
                case "version_check_result":
                {
                    var args = new VersionCheckResultEventArgs
                    {
                        SessionId = root.GetProperty("sessionId").GetString() ?? string.Empty,
                        Compatible = root.TryGetProperty("compatible", out var comp) &&
                                     comp.ValueKind == JsonValueKind.True,
                        Code = ReadCodeProperty(root, OsmiumErrorCode.SignalingProtocolError.ToString()),
                        ClientVersion = root.TryGetProperty("clientVersion", out var cv)
                            ? cv.GetString() ?? string.Empty
                            : string.Empty,
                        HostVersion = root.TryGetProperty("hostVersion", out var hv)
                            ? hv.GetString() ?? string.Empty
                            : string.Empty,
                        Reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                            ? r.GetString() ?? string.Empty
                            : string.Empty
                    };
                    VersionCheckResultReceived?.Invoke(args);
                    break;
                }
                case "bridge_ready":
                {
                    var args = new BridgeReadyEventArgs
                    {
                        SessionId = root.GetProperty("sessionId").GetString() ?? string.Empty,
                        PeerDeviceId = root.TryGetProperty("peerDeviceId", out var pid)
                            ? pid.GetString() ?? string.Empty
                            : string.Empty,
                        CandidatesJson = root.TryGetProperty("candidates", out var cands) ? cands.GetRawText() : "[]",
                        StunHost = root.TryGetProperty("stunHost", out var sh) && sh.ValueKind == JsonValueKind.String
                            ? sh.GetString() ?? string.Empty
                            : AdvertisedStunHost,
                        StunPort = root.TryGetProperty("stunPort", out var up) && up.ValueKind == JsonValueKind.Number
                            ? (ushort)up.GetInt32()
                            : AdvertisedStunPort,
                        RelayToken = root.TryGetProperty("relayToken", out var rt) && rt.ValueKind == JsonValueKind.String
                            ? rt.GetString() ?? string.Empty
                            : string.Empty,
                        RelayPath = root.TryGetProperty("relayPath", out var rp) && rp.ValueKind == JsonValueKind.String
                            ? rp.GetString() ?? "/relay"
                            : "/relay"
                    };
                    BridgeReady?.Invoke(args);
                    break;
                }
                case "relay_fallback":
                {
                    var args = new RelayFallbackEventArgs
                    {
                        SessionId = root.TryGetProperty("sessionId", out var rsid) && rsid.ValueKind == JsonValueKind.String
                            ? rsid.GetString() ?? string.Empty
                            : string.Empty,
                        RelayToken = root.TryGetProperty("relayToken", out var rtok) && rtok.ValueKind == JsonValueKind.String
                            ? rtok.GetString() ?? string.Empty
                            : string.Empty,
                        RelayPath = root.TryGetProperty("relayPath", out var rpath) && rpath.ValueKind == JsonValueKind.String
                            ? rpath.GetString() ?? "/relay"
                            : "/relay",
                        FromDeviceId = root.TryGetProperty("fromDeviceId", out var rfid) && rfid.ValueKind == JsonValueKind.String
                            ? rfid.GetString() ?? string.Empty
                            : string.Empty
                    };
                    RelayFallbackReceived?.Invoke(args);
                    break;
                }
                case "connect_accept":
                {
                    var args = new ConnectAcceptEventArgs
                    {
                        SessionId = root.GetProperty("sessionId").GetString() ?? string.Empty,
                        HostDeviceId = root.GetProperty("hostDeviceId").GetString() ?? string.Empty,
                        HostName = root.TryGetProperty("hostName", out var hn)
                            ? hn.GetString() ?? "Osmium Host"
                            : "Osmium Host",
                        HostPublicKeyBase64 = root.GetProperty("hostPublicKey").GetString() ?? string.Empty,
                        HostChallengeNonceBase64 = root.GetProperty("hostChallengeNonce").GetString() ?? string.Empty,
                        EnableEncryption = !root.TryGetProperty("enableEncryption", out var he) ||
                                           he.ValueKind != JsonValueKind.False,
                        CandidatesJson = root.TryGetProperty("candidates", out var cands) ? cands.GetRawText() : "[]",
                        StunHost = root.TryGetProperty("stunHost", out var sh) && sh.ValueKind == JsonValueKind.String
                            ? sh.GetString() ?? string.Empty
                            : AdvertisedStunHost,
                        StunPort = root.TryGetProperty("stunPort", out var up) && up.ValueKind == JsonValueKind.Number
                            ? (ushort)up.GetInt32()
                            : AdvertisedStunPort
                    };
                    ConnectAccepted?.Invoke(args);
                    break;
                }
                case "connect_reject":
                {
                    string code = ReadCodeProperty(root, "REJECTED");
                    int cooldownMs = root.TryGetProperty("remainingMs", out var rm) &&
                                     rm.ValueKind == JsonValueKind.Number
                        ? Math.Max(0, rm.GetInt32())
                        : (root.TryGetProperty("cooldownMs", out var cm) && cm.ValueKind == JsonValueKind.Number
                            ? Math.Max(0, cm.GetInt32())
                            : 0);
                    if (cooldownMs > 0)
                    {
                        ConnectCooldownUpdated?.Invoke(cooldownMs, code);
                    }

                    AccountChallengeInfo? challenge = null;
                    if (root.TryGetProperty("challenge", out var chEl) && chEl.ValueKind == JsonValueKind.Object)
                    {
                        challenge = chEl.Deserialize<AccountChallengeInfo>(JsonOpts);
                    }

                    ConnectRejected?.Invoke(new ConnectRejectEventArgs
                    {
                        SessionId = root.GetProperty("sessionId").GetString() ?? string.Empty,
                        Code = code,
                        Reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                            ? r.GetString() ?? string.Empty
                            : string.Empty,
                        CooldownMs = cooldownMs,
                        Challenge = challenge
                    });
                    break;
                }
                case "session_end":
                {
                    string sid = root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString() ?? string.Empty
                        : string.Empty;
                    string fromId =
                        root.TryGetProperty("fromDeviceId", out var f) && f.ValueKind == JsonValueKind.String
                            ? f.GetString() ?? string.Empty
                            : string.Empty;
                    string reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString() ?? "Peer ended session"
                        : "Peer ended session";
                    int cooldownMs = root.TryGetProperty("remainingMs", out var rm) &&
                                     rm.ValueKind == JsonValueKind.Number
                        ? Math.Max(0, rm.GetInt32())
                        : (root.TryGetProperty("cooldownMs", out var cm) && cm.ValueKind == JsonValueKind.Number
                            ? Math.Max(0, cm.GetInt32())
                            : 0);
                    if (cooldownMs > 0)
                    {
                        ConnectCooldownUpdated?.Invoke(cooldownMs, "SESSION_ENDED");
                    }

                    SessionEnded?.Invoke(sid, fromId, reason);
                    break;
                }
                case "error":
                {
                    string code = ReadCodeProperty(root, OsmiumErrorCode.InternalError.ToString());
                    string msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() ?? string.Empty
                        : string.Empty;
                    string sid = root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString() ?? string.Empty
                        : string.Empty;
                    if (!string.IsNullOrEmpty(sid))
                    {
                        ConnectRejected?.Invoke(new ConnectRejectEventArgs
                            { SessionId = sid, Code = code, Reason = msg });
                    }

                    SignalingErrorReceived?.Invoke(code, msg);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to parse signaling message: {ex.Message}");
        }
    }

    private TaskCompletionSource<bool> BeginPendingRegister()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_heartbeatLock)
        {
            _pendingRegister?.TrySetResult(false);
            _pendingRegister = tcs;
        }

        return tcs;
    }

    private void CompletePendingRegister(bool registered)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_heartbeatLock)
        {
            tcs = _pendingRegister;
            _pendingRegister = null;
        }

        tcs?.TrySetResult(registered);
    }

    private void ClearPendingRegister(TaskCompletionSource<bool> expected)
    {
        lock (_heartbeatLock)
        {
            if (ReferenceEquals(_pendingRegister, expected))
            {
                _pendingRegister = null;
            }
        }
    }

    private TaskCompletionSource<bool> BeginPendingPong()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_heartbeatLock)
        {
            _pendingPong?.TrySetResult(false);
            _pendingPong = tcs;
        }

        return tcs;
    }

    private void CompletePendingPong(bool alive)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_heartbeatLock)
        {
            tcs = _pendingPong;
            _pendingPong = null;
        }

        tcs?.TrySetResult(alive);
    }

    private void ClearPendingPong(TaskCompletionSource<bool> expected)
    {
        lock (_heartbeatLock)
        {
            if (ReferenceEquals(_pendingPong, expected))
            {
                _pendingPong = null;
            }
        }
    }

    private void MarkHeartbeatAlive()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        CompletePendingPong(true);

        if (_isTransportRegistered && State != SignalingConnectionState.Connected)
        {
            LastError = string.Empty;
            SetState(SignalingConnectionState.Connected, $"Connected ({ObservedPublicIp})");
        }
    }

    private int RecordFailure(SignalingConnectionState offlineState, string detail)
    {
        int failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures >= OfflineFailureThreshold)
        {
            SetState(offlineState, detail);
        }

        return failures;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            NotifyNetworkRecovered();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        NotifyNetworkRecovered();
    }

    private void NotifyNetworkRecovered()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        CancellationTokenSource? delayWake = null;
        CancellationTokenSource? connectWake = null;
        CancellationTokenSource? heartbeatWake = null;

        lock (_wakeLock)
        {
            delayWake = _reconnectDelayWakeCts;
            if (!_isTransportRegistered)
            {
                long startedTick = _activeConnectStartedTickMs;
                if (_activeConnectWakeCts != null &&
                    startedTick > 0 &&
                    Environment.TickCount64 - startedTick >= StaleConnectAbortMinAgeMs)
                {
                    connectWake = _activeConnectWakeCts;
                }
            }
            else
            {
                heartbeatWake = _heartbeatWakeCts;
            }
        }

        try
        {
            delayWake?.Cancel();
        }
        catch
        {
        }

        try
        {
            connectWake?.Cancel();
        }
        catch
        {
        }

        try
        {
            heartbeatWake?.Cancel();
        }
        catch
        {
        }
    }

    private async Task DelayWithWakeAsync(TimeSpan delay, bool isHeartbeatWait, CancellationToken ct)
    {
        using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_wakeLock)
        {
            if (isHeartbeatWait)
                _heartbeatWakeCts = wakeCts;
            else
                _reconnectDelayWakeCts = wakeCts;
        }

        try
        {
            await Task.Delay(delay, wakeCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_wakeLock)
            {
                if (isHeartbeatWait && ReferenceEquals(_heartbeatWakeCts, wakeCts))
                    _heartbeatWakeCts = null;
                else if (!isHeartbeatWait && ReferenceEquals(_reconnectDelayWakeCts, wakeCts))
                    _reconnectDelayWakeCts = null;
            }
        }
    }

    private async Task RunHeartbeatLoopAsync(
        ClientWebSocket ws,
        TaskCompletionSource<bool> registerTcs,
        Action markHeartbeatAbort,
        CancellationToken ct)
    {
        bool registered;
        using (var regTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            regTimeoutCts.CancelAfter(ConnectTimeout);
            try
            {
                registered = await registerTcs.Task.WaitAsync(regTimeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                registered = false;
            }
            finally
            {
                ClearPendingRegister(registerTcs);
            }
        }

        if (!registered)
        {
            int failures = RecordFailure(SignalingConnectionState.Error, "Signaling registration timeout");
            _log.Warn($"Signaling registration timed out ({failures}/{OfflineFailureThreshold})");
            markHeartbeatAbort();
            try
            {
                ws.Abort();
            }
            catch
            {
            }

            return;
        }

        bool firstTick = true;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan waitDelay = firstTick || Volatile.Read(ref _consecutiveFailures) == 0
                ? HeartbeatInterval
                : HeartbeatRetryDelay;
            firstTick = false;

            await DelayWithWakeAsync(waitDelay, isHeartbeatWait: true, ct);
            if (ct.IsCancellationRequested) break;

            var pongTcs = BeginPendingPong();
            bool sent;
            try
            {
                sent = await SendJsonAsync(ws, new { type = "ping" }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ClearPendingPong(pongTcs);
                throw;
            }
            catch (Exception ex)
            {
                ClearPendingPong(pongTcs);
                sent = false;
                _log.Warn($"Signaling heartbeat send failed: {ex.Message}");
            }

            bool alive = false;
            if (sent)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(HeartbeatTimeout);
                try
                {
                    alive = await pongTcs.Task.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    alive = false;
                }
                finally
                {
                    ClearPendingPong(pongTcs);
                }
            }

            if (alive)
            {
                MarkHeartbeatAlive();
                continue;
            }

            int failures = RecordFailure(SignalingConnectionState.Error, "Signaling heartbeat timeout");
            _log.Warn($"Signaling heartbeat missed ({failures}/{OfflineFailureThreshold})");

            if (!sent || failures >= OfflineFailureThreshold - 1)
            {
                markHeartbeatAbort();
                try
                {
                    ws.Abort();
                }
                catch
                {
                }

                return;
            }
        }
    }

    private async Task RunLoopAsync(CancellationTokenSource loopCts)
    {
        using var ownedCts = loopCts;
        CancellationToken ct = ownedCts.Token;

        while (!ct.IsCancellationRequested)
        {
            int heartbeatAborted = 0;
            ClientWebSocket? activeWs = null;

            try
            {
                if (State == SignalingConnectionState.Disconnected &&
                    Volatile.Read(ref _consecutiveFailures) < OfflineFailureThreshold)
                {
                    SetState(SignalingConnectionState.Connecting, $"Connecting to {_serverUrl}");
                }

                var serverUri = new Uri(_serverUrl);
                if (IPAddress.TryParse(serverUri.DnsSafeHost, out var literalIp) &&
                    literalIp.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    throw new NotSupportedException("Signaling server only supports IPv4.");
                }

                using var ws = new ClientWebSocket();
                activeWs = ws;
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
                using var connectWakeCts = new CancellationTokenSource();
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, connectWakeCts.Token);
                connectCts.CancelAfter(ConnectTimeout);

                lock (_wakeLock)
                {
                    _activeConnectWakeCts = connectWakeCts;
                    _activeConnectStartedTickMs = Environment.TickCount64;
                }

                try
                {
                    await ws.ConnectAsync(serverUri, invoker, connectCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && connectWakeCts.IsCancellationRequested)
                {
                    continue;
                }
                finally
                {
                    lock (_wakeLock)
                    {
                        if (ReferenceEquals(_activeConnectWakeCts, connectWakeCts))
                        {
                            _activeConnectWakeCts = null;
                            _activeConnectStartedTickMs = 0;
                        }
                    }
                }

                _ws = ws;
                _isTransportRegistered = false;
                var registerTcs = BeginPendingRegister();
                bool registerSent = await SendJsonAsync(ws, new
                {
                    type = "register",
                    deviceId = _deviceId,
                    deviceName = _deviceName,
                    allowRemote = _allowRemote,
                    osVersion = Environment.OSVersion.VersionString,
                    ltoken = _ltoken
                }, ct);
                if (!registerSent)
                {
                    throw new IOException("Failed to send register payload");
                }

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pingTask = Task.Run(
                    () => RunHeartbeatLoopAsync(ws, registerTcs, () => Volatile.Write(ref heartbeatAborted, 1), pingCts.Token),
                    pingCts.Token);

                try
                {
                    await ReceiveLoopAsync(ws, ct);
                }
                finally
                {
                    pingCts.Cancel();
                    try
                    {
                        await pingTask;
                    }
                    catch
                    {
                    }
                }

                if (!ct.IsCancellationRequested && Volatile.Read(ref heartbeatAborted) == 0)
                {
                    int failures = RecordFailure(SignalingConnectionState.Disconnected, "Signaling connection closed");
                    _log.Warn($"Signaling connection closed ({failures}/{OfflineFailureThreshold})");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;

                if (Volatile.Read(ref heartbeatAborted) == 0)
                {
                    LastError = ex.Message;
                    int failures = RecordFailure(SignalingConnectionState.Error, ex.Message);
                    _log.Warn($"Signaling connection error ({failures}/{OfflineFailureThreshold}): {ex.Message}");
                }
            }
            finally
            {
                CompletePendingRegister(false);
                CompletePendingPong(false);
                if (activeWs != null && ReferenceEquals(_ws, activeWs))
                {
                    _isTransportRegistered = false;
                    Interlocked.CompareExchange(ref _ws, null, activeWs);
                }
            }

            if (ct.IsCancellationRequested) break;

            bool immediateRetry = Volatile.Read(ref heartbeatAborted) != 0 &&
                                  Volatile.Read(ref _consecutiveFailures) < OfflineFailureThreshold;
            if (!immediateRetry)
            {
                TimeSpan delay = Volatile.Read(ref _consecutiveFailures) < OfflineFailureThreshold
                    ? HeartbeatRetryDelay
                    : ReconnectDelay;
                try
                {
                    await DelayWithWakeAsync(delay, isHeartbeatWait: false, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private void SetState(SignalingConnectionState state, string detail)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = State != state;
            State = state;
        }

        if (changed)
        {
            StateChanged?.Invoke(state, detail);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        Stop();
        _sendLock.Dispose();
    }
}