using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Osmium.Core.Models;
using Osmium.Core.Network;

namespace Osmium.Core.Services;

public sealed partial class AccountService : IDisposable
{
    public string FormatFriendlyError(int code, int statusCode = 0)
    {
        string Tr(string key, string fallback)
        {
            if (Localizer != null)
            {
                string s = Localizer(key, []);
                if (!string.IsNullOrEmpty(s) && s != key) return s;
            }

            return fallback;
        }

        return code switch
        {
            OsmiumErrorCode.AccountLocked or OsmiumErrorCode.AccountAlreadyLocked
                => Tr("Acc_ErrLocked", "Account is currently locked"),
            OsmiumErrorCode.AccountNotLocked
                => Tr("Acc_StatusNormal", "Account is not locked"),
            OsmiumErrorCode.InvalidCredentials
                => Tr("Acc_ErrInvalidCredentials", "Incorrect email or password"),
            OsmiumErrorCode.EmailAlreadyRegistered
                => Tr("Acc_ErrEmailRegistered", "Email is already registered"),
            OsmiumErrorCode.EmailMismatch or OsmiumErrorCode.InvalidEmail or OsmiumErrorCode.InvalidEmailHeader
                => Tr("Acc_ErrInvalidEmail", "Invalid email address"),
            OsmiumErrorCode.InvalidPassword
                => Tr("Acc_ErrPasswordLength", "Password must be 8-128 characters"),
            OsmiumErrorCode.AccountNotFound
                => Tr("Acc_ErrAccountNotFound", "Account not found"),
            OsmiumErrorCode.OtpExpired
                => Tr("Acc_ErrOtpExpired", "Verification code has expired"),
            OsmiumErrorCode.OtpInvalid or OsmiumErrorCode.InvalidOtpFormat
                => Tr("Acc_ErrOtpInvalid", "Incorrect verification code"),
            OsmiumErrorCode.OtpTooManyAttempts
                => Tr("Acc_ErrOtpAttempts", "Too many verification code attempts"),
            OsmiumErrorCode.OtpCooldown
                => Tr("Acc_ErrOtpCooldown", "Verification code requested too frequently"),
            OsmiumErrorCode.ChallengeExpired
                or OsmiumErrorCode.ChallengeNotFound
                or OsmiumErrorCode.CsrfInvalid
                or OsmiumErrorCode.CaptchaFailed
                or OsmiumErrorCode.CaptchaUpstreamError
                or OsmiumErrorCode.TicketExpired
                => Tr("Acc_ErrCaptchaFailed", "Security verification failed or expired"),
            OsmiumErrorCode.GeetestNotConfigured
                => Tr("Acc_ErrGeetestNotConfigured", "Server GeeTest credentials are not configured yet"),
            OsmiumErrorCode.SmtpNotConfigured
                => Tr("Acc_ErrSmtpNotConfigured", "Server SMTP email credentials are not configured yet"),
            OsmiumErrorCode.SmtpSendFailed or OsmiumErrorCode.SmtpTimeout
                => Tr("Acc_ErrSmtpSendFailed", "Failed to send verification email; please try again later"),
            OsmiumErrorCode.DeviceLimitExceeded
                => Tr("Acc_ErrDeviceLimit",
                    "Maximum of 10 logged-in devices reached; please remove an unused device first"),
            OsmiumErrorCode.InvalidAvatar or OsmiumErrorCode.PayloadTooLarge
                => Tr("Acc_ErrInvalidAvatar", "Invalid avatar image or file size too large"),
            OsmiumErrorCode.InvalidUsername
                => Tr("Acc_ErrInvalidUsername", "Please enter a valid username"),
            OsmiumErrorCode.Forbidden or OsmiumErrorCode.AccountAuthRequired
                => Tr("Acc_ErrAuthRequired", "Please sign in first"),
            OsmiumErrorCode.RateLimited or OsmiumErrorCode.UserDailyOpsExceeded or OsmiumErrorCode.ConcurrentOperation
                => Tr("Acc_ErrRateLimited", "Too many requests; please try again shortly"),
            OsmiumErrorCode.D1QuotaExceeded or OsmiumErrorCode.ServerMisconfigured or OsmiumErrorCode.InternalError
                => Tr("Acc_ErrServiceBusy", "Service is temporarily unavailable; please try again later"),
            OsmiumErrorCode.NetworkError
                => Tr("Acc_ErrNetwork", "Network connection failed; please check your connection"),
            _ when statusCode == 423 => Tr("Acc_ErrLocked", "Account is currently locked"),
            _ when statusCode == 429 => Tr("Acc_ErrRateLimited", "Too many requests; please try again shortly"),
            _ => code > 0
                ? $"{Tr("Acc_ErrGeneric", "Request failed; please try again")} · {code}"
                : Tr("Acc_ErrGeneric", "Request failed; please try again")
        };
    }

    private sealed class RawHttpResponse
    {
        public int StatusCode
        {
            get;
            init;
        }

        public string Body
        {
            get;
            init;
        } = string.Empty;

        public TimeSpan? RetryAfter
        {
            get;
            init;
        }

        public bool IsNetworkError
        {
            get;
            init;
        }
    }

    private async Task<AccountApiResult<T>> SendAnonymousJsonAsync<T>(
        HttpMethod method,
        string path,
        object? payload = null,
        bool? retryOnTransient = null)
    {
        string? jsonPayload = payload == null ? null : JsonSerializer.Serialize(payload);
        bool shouldRetry = retryOnTransient ?? (method == HttpMethod.Get);
        var raw = await SendWithTransientRetryAsync(
            () => CreateJsonRequest(method, path, jsonPayload),
            shouldRetry);

        return raw.IsNetworkError
            ? NetworkErrorResult<T>()
            : ParseEnvelope<T>(raw.StatusCode, raw.Body, raw.RetryAfter);
    }

    private async Task<AccountApiResult<T>> SendAuthenticatedJsonAsync<T>(
        HttpMethod method,
        string path,
        object? payload = null,
        bool? retryOnTransient = null)
    {
        string? jsonPayload = payload == null ? null : JsonSerializer.Serialize(payload);
        bool shouldRetry = retryOnTransient ?? (method == HttpMethod.Get);

        if (!await EnsureAuthenticatedAsync())
        {
            return UnauthorizedResult<T>();
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            string usedLtoken = _ltoken;
            var raw = await SendWithTransientRetryAsync(() =>
            {
                var req = CreateJsonRequest(method, path, jsonPayload);
                req.Headers.Add("X-LToken", usedLtoken);
                return req;
            }, shouldRetry);

            if (raw.IsNetworkError)
            {
                return NetworkErrorResult<T>();
            }

            if (raw.StatusCode == 403 && attempt == 0 && IsLoggedIn)
            {
                var rotate = await RotateLtokenInternalAsync(usedLtoken);
                if (rotate.Ok) continue;
            }

            return ParseEnvelope<T>(raw.StatusCode, raw.Body, raw.RetryAfter);
        }

        return UnauthorizedResult<T>();
    }

    private HttpRequestMessage CreateJsonRequest(HttpMethod method, string path, string? jsonPayload)
    {
        var req = new HttpRequestMessage(method, ResolveServiceUri(path));
        if (jsonPayload != null)
        {
            req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        }

        return req;
    }

    private async Task<RawHttpResponse> SendWithTransientRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        bool retryOnTransientFailure)
    {
        int maxRetries = retryOnTransientFailure ? TransientRetryDelays.Length : 0;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var req = requestFactory();
                using var resp = await _httpClient.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();

                bool isStructuredJson = !string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{');
                if (attempt < maxRetries &&
                    !isStructuredJson &&
                    resp.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
                        or HttpStatusCode.GatewayTimeout)
                {
                    await Task.Delay(TransientRetryDelays[attempt]);
                    continue;
                }

                return new RawHttpResponse
                {
                    StatusCode = (int)resp.StatusCode,
                    Body = body,
                    RetryAfter = GetRetryAfter(resp.Headers)
                };
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                await Task.Delay(TransientRetryDelays[attempt]);
            }
            catch
            {
                return new RawHttpResponse
                {
                    IsNetworkError = true
                };
            }
        }

        return new RawHttpResponse
        {
            IsNetworkError = true
        };
    }

    private static TimeSpan? GetRetryAfter(HttpResponseHeaders headers)
        => headers.RetryAfter?.Delta ?? (headers.RetryAfter?.Date - DateTimeOffset.UtcNow);

    private static AccountApiResult<T> NetworkErrorResult<T>() => new()
    {
        Ok = false,
        HttpStatusCode = 0,
        Code = OsmiumErrorCode.NetworkError
    };

    private static AccountApiResult<T> UnauthorizedResult<T>() => new()
    {
        Ok = false,
        HttpStatusCode = 403,
        Code = OsmiumErrorCode.Forbidden
    };

    private static AccountApiResult<T> ParseEnvelope<T>(int statusCode, string rawBody, TimeSpan? retryAfter = null)
    {
        if (string.IsNullOrWhiteSpace(rawBody) || !rawBody.TrimStart().StartsWith('{'))
        {
            return new AccountApiResult<T>
            {
                Ok = false,
                HttpStatusCode = statusCode,
                RetryAfter = retryAfter,
                Code = statusCode >= 500 ? OsmiumErrorCode.UpstreamError : OsmiumErrorCode.InvalidResponse
            };
        }

        try
        {
            var env = JsonSerializer.Deserialize<AccountApiEnvelope>(rawBody, JsonOptions);
            if (env == null)
            {
                return new AccountApiResult<T>
                {
                    Ok = false,
                    HttpStatusCode = statusCode,
                    RetryAfter = retryAfter,
                    Code = OsmiumErrorCode.InvalidResponse
                };
            }

            T? data = default;
            AccountChallengeInfo? challenge = null;

            if (env.Data.HasValue && env.Data.Value.ValueKind == JsonValueKind.Object)
            {
                if (statusCode == 202 && env.Code == OsmiumErrorCode.CaptchaRequired)
                {
                    challenge = env.Data.Value.Deserialize<AccountChallengeInfo>(JsonOptions);
                    if (typeof(T) == typeof(AccountChallengeInfo))
                    {
                        data = (T?)(object?)challenge;
                    }
                }
                else if (env.Code != OsmiumErrorCode.ChallengePending)
                {
                    data = env.Data.Value.Deserialize<T>(JsonOptions);
                }
            }

            return new AccountApiResult<T>
            {
                Ok = env.Ok,
                HttpStatusCode = statusCode,
                RetryAfter = retryAfter,
                Code = env.Code,
                Data = data,
                Challenge = challenge
            };
        }
        catch
        {
            return new AccountApiResult<T>
            {
                Ok = false,
                HttpStatusCode = statusCode,
                RetryAfter = retryAfter,
                Code = OsmiumErrorCode.ParseError
            };
        }
    }

    private bool IsStokenExpiredLocally()
        => _stokenExpiresAtUnix > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= _stokenExpiresAtUnix - 30;

    private bool IsLtokenExpiredLocally()
        => _ltokenExpiresAtUnix > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= _ltokenExpiresAtUnix - 30;

    private void ClearSessionState(bool clearEmail)
    {
        _userId = string.Empty;
        if (clearEmail)
        {
            _email = string.Empty;
            _username = string.Empty;
            _avatarData = null;
        }

        _stoken = string.Empty;
        _stokenExpiresAtUnix = 0;
        _ltoken = string.Empty;
        _ltokenExpiresAtUnix = 0;
        _accountStatus = "unknown";
        _isLocked = false;
        _lockedAt = null;

        lock (_devicesLock)
        {
            _sameAccountDevices = [];
        }

        SavePersistedSession();
        SessionChanged?.Invoke();
        SameAccountDevicesChanged?.Invoke([]);
    }

    private void LoadPersistedSession()
    {
        try
        {
            if (!File.Exists(_sessionFilePath)) return;
            byte[] cipher = File.ReadAllBytes(_sessionFilePath);
            if (cipher.Length == 0) return;

            byte[]? plain = UnprotectBytes(cipher);
            if (plain == null || plain.Length == 0) return;

            string json;
            try
            {
                json = Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }

            var session = JsonSerializer.Deserialize<AccountPersistedSession>(json, JsonOptions);
            if (session == null) return;

            _userId = session.UserId ?? string.Empty;
            _email = session.Email ?? string.Empty;
            _username = session.Username ?? string.Empty;
            _avatarData = session.AvatarData;
            _stoken = session.Stoken ?? string.Empty;
            _stokenExpiresAtUnix = session.StokenExpiresAtUnix;

            if (IsStokenExpiredLocally())
            {
                _stoken = string.Empty;
                _stokenExpiresAtUnix = 0;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load persisted account session: {ex.Message}");
        }
    }

    private void SavePersistedSession()
    {
        try
        {
            Directory.CreateDirectory(_settings.DataDirectory);
            if (string.IsNullOrWhiteSpace(_email) && string.IsNullOrWhiteSpace(_stoken))
            {
                if (File.Exists(_sessionFilePath)) File.Delete(_sessionFilePath);
                return;
            }

            var session = new AccountPersistedSession
            {
                UserId = _userId,
                Email = _email,
                Username = _username,
                AvatarData = _avatarData,
                Stoken = _stoken,
                StokenExpiresAtUnix = _stokenExpiresAtUnix
            };

            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(session));
            try
            {
                byte[]? cipher = ProtectBytes(plain);
                if (cipher != null)
                {
                    File.WriteAllBytes(_sessionFilePath, cipher);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to save persisted account session: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const uint CryptProtectUiForbidden = 0x01;
    private static readonly byte[] EntropyBytes = Encoding.UTF8.GetBytes("Osmium.Account.Session.v1");

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        in DataBlob dataIn,
        string? dataDescr,
        in DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        in DataBlob dataIn,
        IntPtr dataDescr,
        in DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static unsafe byte[]? ProtectBytes(byte[] plain)
    {
        if (plain.Length == 0) return [];
        fixed (byte* pPlain = plain)
        fixed (byte* pEntropy = EntropyBytes)
        {
            var blobIn = new DataBlob { Size = plain.Length, Data = (IntPtr)pPlain };
            var blobEnt = new DataBlob { Size = EntropyBytes.Length, Data = (IntPtr)pEntropy };
            if (!CryptProtectData(in blobIn, "OsmiumAccountSession", in blobEnt, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var blobOut))
            {
                return null;
            }

            try
            {
                byte[] res = new byte[blobOut.Size];
                Marshal.Copy(blobOut.Data, res, 0, blobOut.Size);
                return res;
            }
            finally
            {
                if (blobOut.Data != IntPtr.Zero) LocalFree(blobOut.Data);
            }
        }
    }

    private static unsafe byte[]? UnprotectBytes(byte[] cipher)
    {
        if (cipher.Length == 0) return [];
        fixed (byte* pCipher = cipher)
        fixed (byte* pEntropy = EntropyBytes)
        {
            var blobIn = new DataBlob { Size = cipher.Length, Data = (IntPtr)pCipher };
            var blobEnt = new DataBlob { Size = EntropyBytes.Length, Data = (IntPtr)pEntropy };
            if (!CryptUnprotectData(in blobIn, IntPtr.Zero, in blobEnt, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var blobOut))
            {
                return null;
            }

            try
            {
                byte[] res = new byte[blobOut.Size];
                Marshal.Copy(blobOut.Data, res, 0, blobOut.Size);
                return res;
            }
            finally
            {
                if (blobOut.Data != IntPtr.Zero) LocalFree(blobOut.Data);
            }
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _authLock.Dispose();
    }
}