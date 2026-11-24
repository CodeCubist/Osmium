using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Osmium.Core.Models;

public sealed class FlexibleTimestampStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var intVal)
                ? intVal.ToString(CultureInfo.InvariantCulture)
                : ((long)reader.GetDouble()).ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

public static class OsmiumErrorCode
{
    public const int NetworkError = -1;
    public const int InvalidResponse = -2;
    public const int ParseError = -3;
    public const int UpstreamError = -4;

    public const int Ok = 0;
    public const int CaptchaRequired = 100;
    public const int ChallengePending = 101;

    public const int InvalidInput = 1001;
    public const int UnsupportedMediaType = 1002;
    public const int PayloadTooLarge = 1003;
    public const int InvalidEmail = 1004;
    public const int InvalidPassword = 1005;
    public const int InvalidOtpFormat = 1006;
    public const int InvalidUsername = 1007;
    public const int InvalidAvatar = 1008;
    public const int InvalidDeviceId = 1009;
    public const int InvalidIdentifier = 1010;
    public const int AccountDeleteNotConfirmed = 1011;
    public const int InvalidEmailHeader = 1012;
    public const int NotFound = 1013;

    public const int Forbidden = 2001;
    public const int InvalidCredentials = 2002;
    public const int EmailAlreadyRegistered = 2003;
    public const int AccountNotFound = 2004;
    public const int AccountLocked = 2005;
    public const int AccountAlreadyLocked = 2006;
    public const int AccountNotLocked = 2007;
    public const int EmailMismatch = 2008;
    public const int DeviceLimitExceeded = 2009;
    public const int AccountAuthRequired = 2010;
    public const int BlacklistedByHost = 2011;

    public const int ChallengeExpired = 3001;
    public const int ChallengeNotFound = 3002;
    public const int CsrfInvalid = 3003;
    public const int CaptchaFailed = 3004;
    public const int CaptchaUpstreamError = 3005;
    public const int TicketExpired = 3006;
    public const int OtpExpired = 3007;
    public const int OtpInvalid = 3008;
    public const int OtpTooManyAttempts = 3009;
    public const int OtpCooldown = 3010;
    public const int QrExpired = 3011;
    public const int QrNotFound = 3012;
    public const int QrInvalidSignature = 3013;
    public const int QrStateInvalid = 3014;
    public const int QrConfirmCooldown = 3015;
    public const int QrCancelled = 3016;

    public const int RateLimited = 4001;
    public const int UserDailyOpsExceeded = 4002;
    public const int ConcurrentOperation = 4003;
    public const int D1QuotaExceeded = 4004;
    public const int ConnectCooldown = 4005;
    public const int DeviceOffline = 4006;
    public const int RemoteDisabled = 4007;
    public const int SelfConnect = 4008;
    public const int DuplicateSession = 4009;
    public const int SignalingProtocolError = 4010;

    public const int InternalError = 5001;
    public const int ServerMisconfigured = 5002;
    public const int GeetestNotConfigured = 5003;
    public const int SmtpNotConfigured = 5004;
    public const int SmtpSendFailed = 5005;
    public const int SmtpTimeout = 5006;
}

public sealed class AccountApiEnvelope
{
    [JsonPropertyName("ok")]
    public bool Ok
    {
        get;
        set;
    }

    [JsonPropertyName("code")]
    public int Code
    {
        get;
        set;
    }

    [JsonPropertyName("data")]
    public JsonElement? Data
    {
        get;
        set;
    }
}

public sealed class AccountApiResult<T>
{
    public bool Ok
    {
        get;
        init;
    }

    public int HttpStatusCode
    {
        get;
        init;
    }

    public TimeSpan? RetryAfter
    {
        get;
        init;
    }

    public int Code
    {
        get;
        init;
    }

    public T? Data
    {
        get;
        init;
    }

    public AccountChallengeInfo? Challenge
    {
        get;
        init;
    }

    public bool RequiresCaptcha =>
        Challenge != null && Code == OsmiumErrorCode.CaptchaRequired;

    public bool IsPending =>
        Code == OsmiumErrorCode.ChallengePending;

    public bool IsAccountLocked =>
        HttpStatusCode == 423 || Code == OsmiumErrorCode.AccountLocked;
}

public sealed class AccountChallengeInfo
{
    [JsonPropertyName("challenge_id")]
    public string ChallengeId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("challenge_secret")]
    public string ChallengeSecret
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("captcha_url")]
    public string CaptchaUrl
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("poll_url")]
    public string PollUrl
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn
    {
        get;
        set;
    }
}

public sealed class AccountLoginSessionData
{
    [JsonPropertyName("user_id")]
    public string UserId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("email")]
    public string Email
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("username")]
    public string Username
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("avatar_data")]
    public string? AvatarData
    {
        get;
        set;
    }

    [JsonPropertyName("stoken")]
    public string Stoken
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("stoken_expires_in")]
    public long StokenExpiresIn
    {
        get;
        set;
    }

    [JsonPropertyName("ltoken")]
    public string Ltoken
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("ltoken_expires_in")]
    public long LtokenExpiresIn
    {
        get;
        set;
    }

    [JsonPropertyName("account_status")]
    public string AccountStatus
    {
        get;
        set;
    } = "active";

    [JsonPropertyName("locked")]
    public bool Locked
    {
        get;
        set;
    }

    [JsonPropertyName("locked_at")]
    [JsonConverter(typeof(FlexibleTimestampStringConverter))]
    public string? LockedAt
    {
        get;
        set;
    }
}

public sealed class AccountLtokenIssueData
{
    [JsonPropertyName("user_id")]
    public string UserId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("email")]
    public string Email
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("username")]
    public string Username
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("avatar_data")]
    public string? AvatarData
    {
        get;
        set;
    }

    [JsonPropertyName("ltoken")]
    public string Ltoken
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("expires_in")]
    public long ExpiresIn
    {
        get;
        set;
    }

    [JsonPropertyName("account_status")]
    public string AccountStatus
    {
        get;
        set;
    } = "active";

    [JsonPropertyName("locked")]
    public bool Locked
    {
        get;
        set;
    }

    [JsonPropertyName("locked_at")]
    [JsonConverter(typeof(FlexibleTimestampStringConverter))]
    public string? LockedAt
    {
        get;
        set;
    }
}

public sealed class AccountStatusData
{
    [JsonPropertyName("user_id")]
    public string UserId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("email")]
    public string Email
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("username")]
    public string Username
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("avatar_data")]
    public string? AvatarData
    {
        get;
        set;
    }

    [JsonPropertyName("account_status")]
    public string AccountStatus
    {
        get;
        set;
    } = "active";

    [JsonPropertyName("locked")]
    public bool Locked
    {
        get;
        set;
    }

    [JsonPropertyName("locked_at")]
    [JsonConverter(typeof(FlexibleTimestampStringConverter))]
    public string? LockedAt
    {
        get;
        set;
    }

    [JsonPropertyName("device_count")]
    public int DeviceCount
    {
        get;
        set;
    }

    [JsonPropertyName("max_devices")]
    public int MaxDevices
    {
        get;
        set;
    } = 1000;
}

public sealed class AccountProfileUpdateData
{
    [JsonPropertyName("username")]
    public string Username
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("avatar_data")]
    public string? AvatarData
    {
        get;
        set;
    }
}

public sealed class AccountEmailUpdateData
{
    [JsonPropertyName("email")]
    public string Email
    {
        get;
        set;
    } = string.Empty;
}

public sealed class AccountOtpSentData
{
    [JsonPropertyName("expires_in")]
    public int ExpiresIn
    {
        get;
        set;
    }
}

public sealed class AccountDeviceDto
{
    [JsonPropertyName("device_id")]
    public string DeviceId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("device_name")]
    public string DeviceName
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("raw_device_name")]
    public string? RawDeviceName
    {
        get;
        set;
    }

    [JsonPropertyName("custom_name")]
    public string? CustomName
    {
        get;
        set;
    }

    [JsonPropertyName("os_version")]
    public string OsVersion
    {
        get;
        set;
    } = "Windows";

    [JsonPropertyName("last_ip")]
    public string LastIp
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("is_online")]
    public bool IsOnline
    {
        get;
        set;
    }

    [JsonPropertyName("allow_remote")]
    public bool AllowRemote
    {
        get;
        set;
    }

    [JsonPropertyName("is_being_controlled")]
    public bool IsBeingControlled
    {
        get;
        set;
    }

    [JsonPropertyName("active_session_id")]
    public string? ActiveSessionId
    {
        get;
        set;
    }

    [JsonPropertyName("controller_user_id")]
    public string? ControllerUserId
    {
        get;
        set;
    }

    [JsonPropertyName("controller_user_name")]
    public string? ControllerUserName
    {
        get;
        set;
    }

    [JsonPropertyName("controller_device_id")]
    public string? ControllerDeviceId
    {
        get;
        set;
    }

    [JsonPropertyName("controller_device_name")]
    public string? ControllerDeviceName
    {
        get;
        set;
    }

    [JsonPropertyName("host_display_name")]
    public string? HostDisplayName
    {
        get;
        set;
    }

    [JsonPropertyName("session_started_at")]
    public long SessionStartedAt
    {
        get;
        set;
    }

    [JsonPropertyName("last_active_at")]
    public long LastActiveAt
    {
        get;
        set;
    }

    [JsonIgnore] public bool HasCustomName => !string.IsNullOrWhiteSpace(CustomName);

    [JsonIgnore]
    public string OriginalDeviceName => !string.IsNullOrWhiteSpace(RawDeviceName) ? RawDeviceName : DeviceName;

    [JsonIgnore] public string FormattedDeviceId => DeviceIdentity.FormatDeviceId(DeviceId);

    [JsonIgnore]
    public string LastActiveText => LastActiveAt > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(LastActiveAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "-";
}

public sealed class AccountDevicesListData
{
    [JsonPropertyName("devices")]
    public List<AccountDeviceDto> Devices
    {
        get;
        set;
    } = [];

    [JsonPropertyName("max_devices")]
    public int MaxDevices
    {
        get;
        set;
    } = 1000;
}

public sealed class AccountLoginHistoryRecordDto
{
    [JsonPropertyName("id")]
    public string Id
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("device_id")]
    public string DeviceId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("device_name")]
    public string DeviceName
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("os_version")]
    public string OsVersion
    {
        get;
        set;
    } = "Windows";

    [JsonPropertyName("ip")]
    public string Ip
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("event_type")]
    public string EventType
    {
        get;
        set;
    } = "login";

    [JsonPropertyName("created_at")]
    public string CreatedAt
    {
        get;
        set;
    } = string.Empty;

    [JsonIgnore] public string FormattedDeviceId => DeviceIdentity.FormatDeviceId(DeviceId);

    [JsonIgnore]
    public string LocalTimeText => DateTimeOffset.TryParse(CreatedAt, out var dt)
        ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : CreatedAt;
}

public sealed class AccountLoginHistoryData
{
    [JsonPropertyName("records")]
    public List<AccountLoginHistoryRecordDto> Records
    {
        get;
        set;
    } = [];
}

public sealed class AccountBlacklistItemDto
{
    [JsonPropertyName("blocked_user_id")]
    public string BlockedUserId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("blocked_username")]
    public string BlockedUsername
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("created_at")]
    public string CreatedAt
    {
        get;
        set;
    } = string.Empty;

    [JsonIgnore]
    public string LocalTimeText => DateTimeOffset.TryParse(CreatedAt, out var dt)
        ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : CreatedAt;
}

public sealed class AccountBlacklistData
{
    [JsonPropertyName("items")]
    public List<AccountBlacklistItemDto> Items
    {
        get;
        set;
    } = [];
}

public sealed class AccountPersistedSession
{
    public string UserId
    {
        get;
        set;
    } = string.Empty;

    public string Email
    {
        get;
        set;
    } = string.Empty;

    public string Username
    {
        get;
        set;
    } = string.Empty;

    public string? AvatarData
    {
        get;
        set;
    }

    public string Stoken
    {
        get;
        set;
    } = string.Empty;

    public long StokenExpiresAtUnix
    {
        get;
        set;
    }
}

public sealed class QrLoginCreateData
{
    [JsonPropertyName("qr_id")]
    public string QrId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("poll_secret")]
    public string PollSecret
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("qr_url")]
    public string QrUrl
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("poll_url")]
    public string PollUrl
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn
    {
        get;
        set;
    }

    [JsonPropertyName("expires_at")]
    public long ExpiresAt
    {
        get;
        set;
    }

    [JsonPropertyName("poll_interval_ms")]
    public int PollIntervalMs
    {
        get;
        set;
    } = 1500;
}

public sealed class QrLoginStatusData
{
    [JsonPropertyName("qr_id")]
    public string QrId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("state")]
    public string State
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("channel")]
    public string? Channel
    {
        get;
        set;
    }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn
    {
        get;
        set;
    }

    [JsonPropertyName("username")]
    public string? Username
    {
        get;
        set;
    }

    [JsonPropertyName("avatar_data")]
    public string? AvatarData
    {
        get;
        set;
    }

    [JsonPropertyName("session")]
    public AccountLoginSessionData? Session
    {
        get;
        set;
    }
}