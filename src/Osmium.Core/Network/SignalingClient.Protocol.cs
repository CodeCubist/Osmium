using System.Net;
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
                    LastError = string.Empty;
                    SetState(SignalingConnectionState.Connected, $"Connected ({ObservedPublicIp})");
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
                            : AdvertisedStunPort
                    };
                    BridgeReady?.Invoke(args);
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

    private void SetState(SignalingConnectionState state, string detail)
    {
        State = state;
        StateChanged?.Invoke(state, detail);
    }

    public void Dispose()
    {
        Stop();
        _sendLock.Dispose();
    }
}