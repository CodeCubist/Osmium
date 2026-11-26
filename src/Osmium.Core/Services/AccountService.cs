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
    private const string SessionFileName = "osmium_account_session.dat";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan[] TransientRetryDelays =
    [
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromMilliseconds(900)
    ];

    private readonly SettingsService _settings;
    private readonly LoggingService _log;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly string _sessionFilePath;
    private readonly object _devicesLock = new();

    private string _userId = string.Empty;
    private string _email = string.Empty;
    private string _username = string.Empty;
    private string? _avatarData;
    private string _stoken = string.Empty;
    private long _stokenExpiresAtUnix;
    private string _ltoken = string.Empty;
    private long _ltokenExpiresAtUnix;
    private string _accountStatus = "unknown";
    private bool _isLocked;
    private string? _lockedAt;
    private bool _hasEmail = true;
    private bool _hasPassword = true;
    private bool _twoFaEnabled;
    private List<AccountDeviceDto> _sameAccountDevices = [];

    public Func<string, object[], string>? Localizer
    {
        get;
        set;
    }

    public event Action? SessionChanged;
    public event Action<IReadOnlyList<AccountDeviceDto>>? SameAccountDevicesChanged;
    public event Action<string>? DeviceRemoved;

    public AccountService(SettingsService settings, LoggingService log)
    {
        _settings = settings;
        _log = log;
        _sessionFilePath = Path.Combine(
            settings.DataDirectory,
            settings.IsServiceHostMode ? ServiceAutoStartHelper.ServiceSessionFileName : SessionFileName);

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"Osmium-AccountSecurity/{AppVersionInfo.Current}");
        _httpClient.DefaultRequestHeaders.Add("X-Client-Version", AppVersionInfo.Current);

        LoadPersistedSession();
    }

    public bool IsLoggedIn => !string.IsNullOrWhiteSpace(_stoken) && !IsStokenExpiredLocally();
    public bool HasLtoken => !string.IsNullOrWhiteSpace(_ltoken) && !IsLtokenExpiredLocally();
    public string CurrentUserId => _userId;
    public string CurrentEmail => _email;
    public string CurrentUsername => _username;
    public string? CurrentAvatarData => _avatarData;
    public string CurrentLtoken => HasLtoken ? _ltoken : string.Empty;
    public string AccountStatus => _accountStatus;
    public bool IsLocked => _isLocked;
    public string? LockedAt => _lockedAt;
    public bool HasEmail => _hasEmail;
    public bool HasPassword => _hasPassword;
    public bool TwoFaEnabled => _twoFaEnabled;
    public bool IsProfileComplete => _hasEmail && _hasPassword;

    public IReadOnlyList<AccountDeviceDto> SameAccountDevices
    {
        get
        {
            lock (_devicesLock)
            {
                return _sameAccountDevices.ToList();
            }
        }
    }

    public static string GenerateRandomUsername()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> buf = stackalloc byte[6];
        RandomNumberGenerator.Fill(buf);
        var sb = new StringBuilder("Osmium-", 13);
        for (int i = 0; i < 6; i++)
        {
            sb.Append(chars[buf[i] % chars.Length]);
        }

        return sb.ToString();
    }

    public Uri GetServiceBaseUri()
    {
        string sigUrl = SignalingClient.NormalizeServerUrl(_settings.Settings.SignalingServerUrl);
        if (!Uri.TryCreate(sigUrl, UriKind.Absolute, out var wsUri))
        {
            return new Uri("https://osmium.fufu.skin/", UriKind.Absolute);
        }

        string scheme = string.Equals(wsUri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        var builder = new UriBuilder(wsUri)
        {
            Scheme = scheme,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        if ((scheme == "https" && wsUri.Port == 443) || (scheme == "http" && wsUri.Port == 80))
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }

    public Uri ResolveServiceUri(string relativeOrAbsoluteUrl)
    {
        var baseUri = GetServiceBaseUri();
        if (Uri.TryCreate(relativeOrAbsoluteUrl, UriKind.Absolute, out var absolute))
        {
            if (string.Equals(absolute.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(absolute.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
                absolute.Port == baseUri.Port)
            {
                return absolute;
            }

            return new Uri(baseUri, absolute.PathAndQuery.TrimStart('/'));
        }

        return new Uri(baseUri, relativeOrAbsoluteUrl.TrimStart('/'));
    }

    public async Task InitializeOnStartupAsync()
    {
        if (!IsLoggedIn) return;
        try
        {
            var res = await RotateLtokenAsync();
            if (res.Ok)
            {
                _ = RefreshDevicesAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"AccountService startup token rotation failed: {ex.Message}");
        }
    }

    public async Task<bool> EnsureAuthenticatedAsync()
    {
        if (HasLtoken) return true;
        if (!IsLoggedIn) return false;
        var rotate = await RotateLtokenAsync();
        return rotate.Ok && HasLtoken;
    }

    public void UpdateSameAccountDevicesFromPush(IReadOnlyList<AccountDeviceDto> devices)
    {
        string myId = _settings.Identity.DeviceId;
        var filtered = devices
            .Where(d => !string.IsNullOrWhiteSpace(d.DeviceId))
            .OrderByDescending(d => d.DeviceId == myId)
            .ThenByDescending(d => d.IsOnline)
            .ThenByDescending(d => d.LastActiveAt)
            .ToList();

        lock (_devicesLock)
        {
            _sameAccountDevices = filtered;
        }

        SameAccountDevicesChanged?.Invoke(filtered);
    }

    public AccountDeviceDto? FindPeerDevice(string deviceId)
    {
        string norm = DeviceIdentity.NormalizeDeviceId(deviceId);
        lock (_devicesLock)
        {
            return _sameAccountDevices.FirstOrDefault(d => d.DeviceId == norm);
        }
    }

    public string GetEffectiveLocalDeviceName()
    {
        if (IsLoggedIn)
        {
            var self = FindPeerDevice(_settings.Identity.DeviceId);
            if (self != null && !string.IsNullOrWhiteSpace(self.DeviceName))
            {
                return self.DeviceName;
            }
        }

        return !string.IsNullOrWhiteSpace(_settings.Identity.DeviceName)
            ? _settings.Identity.DeviceName
            : Environment.MachineName;
    }

    public string ResolveAccountDeviceName(string? deviceId, string? fallback)
    {
        if (IsLoggedIn && !string.IsNullOrWhiteSpace(deviceId))
        {
            var dev = FindPeerDevice(deviceId);
            if (dev != null && !string.IsNullOrWhiteSpace(dev.DeviceName))
            {
                return dev.DeviceName;
            }
        }

        if (!string.IsNullOrWhiteSpace(fallback)) return fallback;
        return !string.IsNullOrWhiteSpace(deviceId) ? DeviceIdentity.FormatDeviceId(deviceId) : string.Empty;
    }

    public Task<AccountApiResult<AccountChallengeInfo>> StartLoginAsync(string email, string password)
        => SendAnonymousJsonAsync<AccountChallengeInfo>(HttpMethod.Post, "/api/auth/login", new
        {
            email = email.Trim(),
            password,
            device_id = _settings.Identity.DeviceId,
            device_name = _settings.Identity.DeviceName,
            os_version = Environment.OSVersion.VersionString,
            device_secret = _settings.Identity.DeviceSecret
        }, retryOnTransient: true);

    public Task<AccountApiResult<AccountChallengeInfo>> StartRegisterSendCodeAsync(string email)
        => SendAnonymousJsonAsync<AccountChallengeInfo>(HttpMethod.Post, "/api/auth/register/send-code", new
        {
            email = email.Trim()
        });

    public async Task<AccountApiResult<AccountLoginSessionData>> ConfirmRegisterAsync(
        string email,
        string username,
        string password,
        string code)
    {
        string trimmedEmail = email.Trim();
        var result = await SendAnonymousJsonAsync<AccountLoginSessionData>(HttpMethod.Post,
            "/api/auth/register/confirm", new
            {
                email = trimmedEmail,
                username = username.Trim(),
                password,
                code = code.Trim(),
                device_id = _settings.Identity.DeviceId,
                device_name = _settings.Identity.DeviceName,
                os_version = Environment.OSVersion.VersionString,
                device_secret = _settings.Identity.DeviceSecret
            });
        if (result.Ok && result.Data != null)
        {
            ApplyLoginSession(trimmedEmail, result.Data);
        }

        return result;
    }

    public Task<AccountApiResult<AccountChallengeInfo>> StartResetPasswordSendCodeAsync(string email)
        => SendAnonymousJsonAsync<AccountChallengeInfo>(HttpMethod.Post, "/api/auth/reset-password/send-code", new
        {
            email = email.Trim()
        });

    public Task<AccountApiResult<object>> ConfirmResetPasswordAsync(string email, string code, string newPassword)
        => SendAnonymousJsonAsync<object>(HttpMethod.Post, "/api/auth/reset-password/confirm", new
        {
            email = email.Trim(),
            code = code.Trim(),
            new_password = newPassword
        });

    public async Task<AccountApiResult<T>> PollChallengeAsync<T>(string pollUrl, string challengeSecret)
    {
        var raw = await SendWithTransientRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, ResolveServiceUri(pollUrl));
            req.Headers.Add("X-Challenge-Secret", challengeSecret);
            return req;
        }, retryOnTransientFailure: false);

        return raw.IsNetworkError
            ? NetworkErrorResult<T>()
            : ParseEnvelope<T>(raw.StatusCode, raw.Body, raw.RetryAfter);
    }

    public Task<AccountApiResult<QrLoginCreateData>> CreateQrLoginAsync()
        => SendAnonymousJsonAsync<QrLoginCreateData>(HttpMethod.Post, "/api/auth/qr/create", new
        {
            device_id = _settings.Identity.DeviceId,
            device_name = _settings.Identity.DeviceName,
            os_version = Environment.OSVersion.VersionString,
            device_secret = _settings.Identity.DeviceSecret
        }, retryOnTransient: true);

    public async Task<AccountApiResult<QrLoginStatusData>> PollQrLoginStatusAsync(string pollUrl, string pollSecret)
    {
        var raw = await SendWithTransientRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, ResolveServiceUri(pollUrl));
            req.Headers.Add("X-QR-Poll-Secret", pollSecret);
            return req;
        }, retryOnTransientFailure: false);

        return raw.IsNetworkError
            ? NetworkErrorResult<QrLoginStatusData>()
            : ParseEnvelope<QrLoginStatusData>(raw.StatusCode, raw.Body, raw.RetryAfter);
    }

    public async Task<AccountApiResult<object>> CancelQrLoginAsync(string qrId, string pollSecret)
    {
        string jsonPayload = JsonSerializer.Serialize(new { qid = qrId });
        var raw = await SendWithTransientRetryAsync(() =>
        {
            var req = CreateJsonRequest(HttpMethod.Post, "/api/auth/qr/cancel", jsonPayload);
            req.Headers.Add("X-QR-Poll-Secret", pollSecret);
            return req;
        }, retryOnTransientFailure: false);

        return raw.IsNetworkError
            ? NetworkErrorResult<object>()
            : ParseEnvelope<object>(raw.StatusCode, raw.Body, raw.RetryAfter);
    }

    public Task<AccountApiResult<OAuthProvidersData>> GetOAuthProvidersAsync()
        => SendAnonymousJsonAsync<OAuthProvidersData>(
            HttpMethod.Get,
            "/api/auth/oauth/providers?platform=windows",
            retryOnTransient: true);

    public Task<AccountApiResult<OAuthStartData>> StartOAuthLoginAsync(string provider)
        => SendAnonymousJsonAsync<OAuthStartData>(HttpMethod.Post, "/api/auth/oauth/start", new
        {
            provider = provider.Trim().ToLowerInvariant(),
            platform = "windows",
            device_id = _settings.Identity.DeviceId,
            device_name = _settings.Identity.DeviceName,
            os_version = Environment.OSVersion.VersionString,
            device_secret = _settings.Identity.DeviceSecret
        }, retryOnTransient: true);

    public async Task<AccountApiResult<OAuthStatusData>> PollOAuthStatusAsync(string pollUrl, string pollSecret)
    {
        var raw = await SendWithTransientRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, ResolveServiceUri(pollUrl));
            req.Headers.Add("X-OAuth-Secret", pollSecret);
            return req;
        }, retryOnTransientFailure: false);

        return raw.IsNetworkError
            ? NetworkErrorResult<OAuthStatusData>()
            : ParseEnvelope<OAuthStatusData>(raw.StatusCode, raw.Body, raw.RetryAfter);
    }

    public async Task<AccountApiResult<object>> CancelOAuthLoginAsync(string state, string pollSecret)
    {
        string jsonPayload = JsonSerializer.Serialize(new { state });
        var raw = await SendWithTransientRetryAsync(() =>
        {
            var req = CreateJsonRequest(HttpMethod.Post, "/api/auth/oauth/cancel", jsonPayload);
            req.Headers.Add("X-OAuth-Secret", pollSecret);
            return req;
        }, retryOnTransientFailure: false);

        return raw.IsNetworkError
            ? NetworkErrorResult<object>()
            : ParseEnvelope<object>(raw.StatusCode, raw.Body, raw.RetryAfter);
    }

    public Task<AccountApiResult<OAuthBindingsData>> GetOAuthBindingsAsync()
        => SendAuthenticatedJsonAsync<OAuthBindingsData>(
            HttpMethod.Get,
            "/api/account/oauth/bindings?platform=windows",
            retryOnTransient: true);

    public Task<AccountApiResult<OAuthStartData>> StartOAuthBindAsync(string provider)
        => SendAuthenticatedJsonAsync<OAuthStartData>(HttpMethod.Post, "/api/account/oauth/bind/start", new
        {
            provider = provider.Trim().ToLowerInvariant(),
            platform = "windows"
        }, retryOnTransient: true);

    public Task<AccountApiResult<object>> UnbindOAuthProviderAsync(string provider)
        => SendAuthenticatedJsonAsync<object>(HttpMethod.Post, "/api/account/oauth/unbind", new
        {
            provider = provider.Trim().ToLowerInvariant()
        }, retryOnTransient: false);

    public void ApplyLoginSession(string email, AccountLoginSessionData loginData)
    {
        if (!string.IsNullOrWhiteSpace(loginData.DeviceId))
        {
            string normAssigned = DeviceIdentity.NormalizeDeviceId(loginData.DeviceId);
            if (normAssigned.Length is >= 6 and <= 12 &&
                !string.Equals(normAssigned, _settings.Identity.DeviceId, StringComparison.Ordinal))
            {
                _settings.AdoptAssignedDeviceId(normAssigned);
            }
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _userId = loginData.UserId;
        _hasEmail = loginData.HasEmail;
        _hasPassword = loginData.HasPassword;
        _twoFaEnabled = loginData.TwoFaEnabled;
        _email = _hasEmail
            ? (!string.IsNullOrWhiteSpace(loginData.Email) ? loginData.Email : email.Trim())
            : string.Empty;
        _username = !string.IsNullOrWhiteSpace(loginData.Username) ? loginData.Username : _username;
        _avatarData = loginData.AvatarData;
        _stoken = loginData.Stoken;
        _stokenExpiresAtUnix = loginData.StokenExpiresIn > 0 ? now + loginData.StokenExpiresIn : 0;
        _ltoken = loginData.Ltoken;
        _ltokenExpiresAtUnix = loginData.LtokenExpiresIn > 0 ? now + loginData.LtokenExpiresIn : 0;
        _accountStatus = string.IsNullOrWhiteSpace(loginData.AccountStatus)
            ? (loginData.Locked ? "locked" : "active")
            : loginData.AccountStatus;
        _isLocked = loginData.Locked || string.Equals(_accountStatus, "locked", StringComparison.OrdinalIgnoreCase);
        _lockedAt = loginData.LockedAt;

        SavePersistedSession();
        SessionChanged?.Invoke();
        _ = RefreshDevicesAsync();
    }

    public Task<AccountApiResult<AccountLtokenIssueData>> RotateLtokenAsync()
        => RotateLtokenInternalAsync(staleLtoken: null);

    private async Task<AccountApiResult<AccountLtokenIssueData>> RotateLtokenInternalAsync(string? staleLtoken)
    {
        await _authLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(staleLtoken) &&
                !string.IsNullOrEmpty(_ltoken) &&
                !string.Equals(_ltoken, staleLtoken, StringComparison.Ordinal) &&
                !IsLtokenExpiredLocally())
            {
                return new AccountApiResult<AccountLtokenIssueData>
                {
                    Ok = true,
                    HttpStatusCode = 200,
                    Code = OsmiumErrorCode.Ok,
                    Data = new AccountLtokenIssueData
                    {
                        UserId = _userId,
                        Email = _email,
                        Username = _username,
                        AvatarData = _avatarData,
                        Ltoken = _ltoken,
                        AccountStatus = _accountStatus,
                        Locked = _isLocked,
                        LockedAt = _lockedAt,
                        HasEmail = _hasEmail,
                        HasPassword = _hasPassword,
                        TwoFaEnabled = _twoFaEnabled
                    }
                };
            }

            if (string.IsNullOrWhiteSpace(_stoken) || IsStokenExpiredLocally())
            {
                ClearSessionState(clearEmail: false);
                return UnauthorizedResult<AccountLtokenIssueData>();
            }

            var raw = await SendWithTransientRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, ResolveServiceUri("/api/auth/ltoken"));
                req.Headers.Add("X-SToken", _stoken);
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                return req;
            }, retryOnTransientFailure: true);

            if (raw.IsNetworkError)
            {
                return NetworkErrorResult<AccountLtokenIssueData>();
            }

            var parsed = ParseEnvelope<AccountLtokenIssueData>(raw.StatusCode, raw.Body, raw.RetryAfter);
            if (parsed.Ok && parsed.Data != null && !string.IsNullOrWhiteSpace(parsed.Data.Ltoken))
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!string.IsNullOrWhiteSpace(parsed.Data.UserId)) _userId = parsed.Data.UserId;
                _hasEmail = parsed.Data.HasEmail;
                _hasPassword = parsed.Data.HasPassword;
                _twoFaEnabled = parsed.Data.TwoFaEnabled;
                _email = _hasEmail
                    ? (!string.IsNullOrWhiteSpace(parsed.Data.Email) ? parsed.Data.Email : _email)
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(parsed.Data.Username)) _username = parsed.Data.Username;
                _avatarData = parsed.Data.AvatarData;
                _ltoken = parsed.Data.Ltoken;
                _ltokenExpiresAtUnix = parsed.Data.ExpiresIn > 0 ? now + parsed.Data.ExpiresIn : 0;
                _accountStatus = string.IsNullOrWhiteSpace(parsed.Data.AccountStatus)
                    ? (parsed.Data.Locked ? "locked" : "active")
                    : parsed.Data.AccountStatus;
                _isLocked = parsed.Data.Locked ||
                            string.Equals(_accountStatus, "locked", StringComparison.OrdinalIgnoreCase);
                _lockedAt = parsed.Data.LockedAt;
                SavePersistedSession();
                SessionChanged?.Invoke();
            }
            else if (raw.StatusCode == 403)
            {
                ClearSessionState(clearEmail: false);
            }

            return parsed;
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task<AccountApiResult<object>> LogoutAsync(bool revokeAll = false)
    {
        AccountApiResult<object> result;
        if (HasLtoken)
        {
            result = await SendAuthenticatedJsonAsync<object>(HttpMethod.Post, "/api/auth/logout", new
            {
                revoke_stoken = revokeAll,
                device_id = _settings.Identity.DeviceId
            });
        }
        else
        {
            result = new AccountApiResult<object> { Ok = true, HttpStatusCode = 200, Code = OsmiumErrorCode.Ok };
        }

        ClearSessionState(clearEmail: false);
        return result;
    }

    public void HandleRemoteRevoked()
    {
        ClearSessionState(clearEmail: false);
    }

    public async Task<AccountApiResult<AccountStatusData>> GetAccountStatusAsync()
    {
        var result = await SendAuthenticatedJsonAsync<AccountStatusData>(HttpMethod.Get, "/api/account/status");
        if (result.Ok && result.Data != null)
        {
            if (!string.IsNullOrWhiteSpace(result.Data.UserId)) _userId = result.Data.UserId;
            _hasEmail = result.Data.HasEmail;
            _hasPassword = result.Data.HasPassword;
            _twoFaEnabled = result.Data.TwoFaEnabled;
            _email = _hasEmail
                ? (!string.IsNullOrWhiteSpace(result.Data.Email) ? result.Data.Email : _email)
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(result.Data.Username)) _username = result.Data.Username;
            _avatarData = result.Data.AvatarData;
            _accountStatus = string.IsNullOrWhiteSpace(result.Data.AccountStatus)
                ? (result.Data.Locked ? "locked" : "active")
                : result.Data.AccountStatus;
            _isLocked = result.Data.Locked ||
                        string.Equals(_accountStatus, "locked", StringComparison.OrdinalIgnoreCase);
            _lockedAt = result.Data.LockedAt;
            SavePersistedSession();
            SessionChanged?.Invoke();
        }

        return result;
    }

    public async Task<AccountApiResult<AccountProfileUpdateData>> UpdateProfileAsync(string? username,
        string? avatarData)
    {
        var result = await SendAuthenticatedJsonAsync<AccountProfileUpdateData>(HttpMethod.Post, "/api/account/profile",
            new
            {
                username,
                avatar_data = avatarData
            });
        if (result.Ok && result.Data != null)
        {
            if (!string.IsNullOrWhiteSpace(result.Data.Username)) _username = result.Data.Username;
            _avatarData = result.Data.AvatarData;
            SavePersistedSession();
            SessionChanged?.Invoke();
        }

        return result;
    }

    public Task<AccountApiResult<AccountChallengeInfo>> StartChangeEmailSendCodeAsync(string? currentPassword,
        string newEmail)
    {
        var payload = new Dictionary<string, object>
        {
            ["new_email"] = newEmail.Trim()
        };
        if (!string.IsNullOrEmpty(currentPassword))
        {
            payload["password"] = currentPassword;
        }

        return SendAuthenticatedJsonAsync<AccountChallengeInfo>(HttpMethod.Post, "/api/account/email/send-code", payload);
    }

    public async Task<AccountApiResult<AccountEmailUpdateData>> ConfirmChangeEmailAsync(string? currentPassword,
        string newEmail, string code)
    {
        var payload = new Dictionary<string, object>
        {
            ["new_email"] = newEmail.Trim(),
            ["code"] = code.Trim()
        };
        if (!string.IsNullOrEmpty(currentPassword))
        {
            payload["password"] = currentPassword;
        }

        var result = await SendAuthenticatedJsonAsync<AccountEmailUpdateData>(HttpMethod.Post,
            "/api/account/email/confirm", payload);
        if (result.Ok && result.Data != null && !string.IsNullOrWhiteSpace(result.Data.Email))
        {
            _email = result.Data.Email;
            _hasEmail = result.Data.HasEmail;
            _hasPassword = result.Data.HasPassword;
            SavePersistedSession();
            SessionChanged?.Invoke();
        }

        return result;
    }

    public Task<AccountApiResult<AccountChallengeInfo>> StartSetPasswordSendCodeAsync()
        => SendAuthenticatedJsonAsync<AccountChallengeInfo>(HttpMethod.Post, "/api/account/password/send-code", new { });

    public async Task<AccountApiResult<AccountPasswordSetData>> ConfirmSetPasswordAsync(string code, string newPassword)
    {
        var result = await SendAuthenticatedJsonAsync<AccountPasswordSetData>(HttpMethod.Post,
            "/api/account/password/confirm", new
            {
                code = code.Trim(),
                new_password = newPassword
            });
        if (result.Ok && result.Data != null)
        {
            _hasEmail = result.Data.HasEmail;
            _hasPassword = result.Data.HasPassword;
            SavePersistedSession();
            SessionChanged?.Invoke();
            _ = RefreshDevicesAsync();
        }

        return result;
    }

    public Task<AccountApiResult<AccountChallengeInfo>> SendLockCodeAsync()
        => SendAuthenticatedJsonAsync<AccountChallengeInfo>(HttpMethod.Post, "/api/account/lock/send-code", new { });

    public async Task<AccountApiResult<AccountStatusData>> ConfirmLockAsync(string code)
    {
        var result = await SendAuthenticatedJsonAsync<AccountStatusData>(HttpMethod.Post, "/api/account/lock/confirm",
            new
            {
                code = code.Trim()
            });
        if (result.Ok && result.Data != null)
        {
            _accountStatus = "locked";
            _isLocked = true;
            _lockedAt = result.Data.LockedAt;
            SessionChanged?.Invoke();
        }

        return result;
    }

    public Task<AccountApiResult<AccountChallengeInfo>> SendUnlockCodeAsync()
        => SendAuthenticatedJsonAsync<AccountChallengeInfo>(HttpMethod.Post, "/api/account/unlock/send-code", new { });

    public async Task<AccountApiResult<AccountStatusData>> ConfirmUnlockAsync(string code)
    {
        var result = await SendAuthenticatedJsonAsync<AccountStatusData>(HttpMethod.Post, "/api/account/unlock/confirm",
            new
            {
                code = code.Trim()
            });
        if (result.Ok && result.Data != null)
        {
            _accountStatus = "active";
            _isLocked = false;
            _lockedAt = null;
            SessionChanged?.Invoke();
        }

        return result;
    }

    public async Task<AccountApiResult<object>> DeleteAccountAsync(string password)
    {
        var result = await SendAuthenticatedJsonAsync<object>(HttpMethod.Post, "/api/account/delete", new
        {
            password,
            confirm = true
        });
        if (result.Ok)
        {
            ClearSessionState(clearEmail: true);
        }

        return result;
    }

    public async Task<AccountApiResult<AccountDevicesListData>> RefreshDevicesAsync()
    {
        var result = await SendAuthenticatedJsonAsync<AccountDevicesListData>(HttpMethod.Get, "/api/account/devices");
        if (result.Ok && result.Data != null)
        {
            UpdateSameAccountDevicesFromPush(result.Data.Devices);
        }

        return result;
    }

    public async Task<AccountApiResult<AccountDevicesListData>> RemoveDeviceAsync(string deviceId)
    {
        var result = await SendAuthenticatedJsonAsync<AccountDevicesListData>(HttpMethod.Post,
            "/api/account/devices/remove", new
            {
                device_id = deviceId
            });
        if (result.Ok && result.Data != null)
        {
            string normalizedId = DeviceIdentity.NormalizeDeviceId(deviceId);
            UpdateSameAccountDevicesFromPush(result.Data.Devices);
            if (normalizedId == _settings.Identity.DeviceId)
            {
                ClearSessionState(clearEmail: false);
            }

            if (!string.IsNullOrEmpty(normalizedId))
            {
                try
                {
                    DeviceRemoved?.Invoke(normalizedId);
                }
                catch
                {
                }
            }
        }

        return result;
    }

    public async Task<AccountApiResult<AccountDevicesListData>> RenameDeviceAsync(string deviceId, string? customName)
    {
        var result = await SendAuthenticatedJsonAsync<AccountDevicesListData>(HttpMethod.Post,
            "/api/account/devices/rename", new
            {
                device_id = DeviceIdentity.NormalizeDeviceId(deviceId),
                custom_name = customName?.Trim() ?? string.Empty
            });
        if (result.Ok && result.Data != null)
        {
            UpdateSameAccountDevicesFromPush(result.Data.Devices);
        }

        return result;
    }

    public Task<AccountApiResult<AccountDevicePowerResultData>> SendDevicePowerCommandAsync(string deviceId,
        string action)
        => SendAuthenticatedJsonAsync<AccountDevicePowerResultData>(HttpMethod.Post,
            "/api/account/devices/power", new
            {
                device_id = DeviceIdentity.NormalizeDeviceId(deviceId),
                action = action.Trim().ToLowerInvariant()
            }, retryOnTransient: false);

    public Task<AccountApiResult<AccountLoginHistoryData>> GetLoginHistoryAsync()
        => SendAuthenticatedJsonAsync<AccountLoginHistoryData>(HttpMethod.Get, "/api/account/login-history");

    public Task<AccountApiResult<AccountRemoteHistoryData>> GetRemoteHistoryAsync()
        => SendAuthenticatedJsonAsync<AccountRemoteHistoryData>(HttpMethod.Get, "/api/account/remote-history");

    public Task<AccountApiResult<QuickLaunchAppsListData>> GetQuickLaunchAppsAsync()
        => SendAuthenticatedJsonAsync<QuickLaunchAppsListData>(HttpMethod.Get, "/api/account/quick-launch/apps");

    public Task<AccountApiResult<QuickLaunchAddResultData>> AddQuickLaunchAppAsync(
        string deviceId,
        string name,
        string exePath,
        string? iconData)
        => SendAuthenticatedJsonAsync<QuickLaunchAddResultData>(HttpMethod.Post, "/api/account/quick-launch/apps", new
        {
            device_id = DeviceIdentity.NormalizeDeviceId(deviceId),
            name = name.Trim(),
            exe_path = exePath.Trim(),
            icon_data = string.IsNullOrWhiteSpace(iconData) ? null : iconData.Trim()
        }, retryOnTransient: false);

    public Task<AccountApiResult<object>> DeleteQuickLaunchAppAsync(string appId)
        => SendAuthenticatedJsonAsync<object>(HttpMethod.Post, "/api/account/quick-launch/apps/delete", new
        {
            id = appId.Trim()
        }, retryOnTransient: false);

    public Task<AccountApiResult<AccountBlacklistData>> GetBlacklistAsync()
        => SendAuthenticatedJsonAsync<AccountBlacklistData>(HttpMethod.Get, "/api/account/blacklist");

    public Task<AccountApiResult<object>> RemoveBlacklistUserAsync(string blockedUserId)
        => SendAuthenticatedJsonAsync<object>(HttpMethod.Post, "/api/account/blacklist/remove", new
        {
            blocked_user_id = blockedUserId
        });

    public Task<AccountApiResult<AccountLoginSessionData>> VerifyTwoFaLoginAsync(string twoFaToken, string code)
        => SendAnonymousJsonAsync<AccountLoginSessionData>(HttpMethod.Post, "/api/auth/login/2fa/verify", new
        {
            two_fa_token = twoFaToken.Trim(),
            code = code.Trim()
        }, retryOnTransient: false);

    public async Task<AccountApiResult<AccountLoginSessionData>> LoginWithKeyAsync(string loginKey)
    {
        var result = await SendAnonymousJsonAsync<AccountLoginSessionData>(HttpMethod.Post, "/api/auth/login/key", new
        {
            login_key = loginKey.Trim(),
            device_id = _settings.Identity.DeviceId,
            device_name = _settings.Identity.DeviceName,
            os_version = Environment.OSVersion.VersionString,
            device_secret = _settings.Identity.DeviceSecret
        }, retryOnTransient: false);
        if (result.Ok && result.Data != null && !string.IsNullOrWhiteSpace(result.Data.Stoken))
        {
            ApplyLoginSession(result.Data.Email, result.Data);
        }

        return result;
    }

    public Task<AccountApiResult<TwoFaSetupStartData>> StartTwoFaSetupAsync(string password)
        => SendAuthenticatedJsonAsync<TwoFaSetupStartData>(HttpMethod.Post, "/api/account/2fa/setup/start", new
        {
            password
        }, retryOnTransient: false);

    public async Task<AccountApiResult<TwoFaToggleResultData>> ConfirmTwoFaSetupAsync(string setupToken, string code)
    {
        var result = await SendAuthenticatedJsonAsync<TwoFaToggleResultData>(
            HttpMethod.Post,
            "/api/account/2fa/setup/confirm",
            new
            {
                setup_token = setupToken.Trim(),
                code = code.Trim()
            },
            retryOnTransient: false);
        if (result.Ok)
        {
            _twoFaEnabled = true;
            SavePersistedSession();
            SessionChanged?.Invoke();
        }

        return result;
    }

    public async Task<AccountApiResult<TwoFaToggleResultData>> RemoveTwoFaAsync(string password)
    {
        var result = await SendAuthenticatedJsonAsync<TwoFaToggleResultData>(
            HttpMethod.Post,
            "/api/account/2fa/remove",
            new
            {
                password
            },
            retryOnTransient: false);
        if (result.Ok)
        {
            _twoFaEnabled = false;
            SavePersistedSession();
            SessionChanged?.Invoke();
        }

        return result;
    }

    public Task<AccountApiResult<LoginKeysListData>> GetLoginKeysAsync()
        => SendAuthenticatedJsonAsync<LoginKeysListData>(HttpMethod.Get, "/api/account/login-keys");

    public Task<AccountApiResult<AccountChallengeInfo>> StartCreateLoginKeySendCodeAsync()
        => SendAuthenticatedJsonAsync<AccountChallengeInfo>(HttpMethod.Post, "/api/account/login-keys/send-code", new { });

    public Task<AccountApiResult<LoginKeyCreateData>> CreateLoginKeyAsync(string code, string name)
        => SendAuthenticatedJsonAsync<LoginKeyCreateData>(HttpMethod.Post, "/api/account/login-keys/create", new
        {
            code = code.Trim(),
            name = name.Trim()
        }, retryOnTransient: false);

    public Task<AccountApiResult<object>> DeleteLoginKeyAsync(string id)
        => SendAuthenticatedJsonAsync<object>(HttpMethod.Post, "/api/account/login-keys/delete", new
        {
            id = id.Trim()
        }, retryOnTransient: false);
}