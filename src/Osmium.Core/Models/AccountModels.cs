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
    public const int OAuthProviderInvalid = 1014;

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
    public const int AccountProfileIncomplete = 2012;
    public const int AccountEmailRequired = 2013;
    public const int PasswordAlreadySet = 2014;
    public const int OAuthAlreadyBound = 2015;
    public const int OAuthProviderAlreadyBound = 2016;

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
    public const int OAuthExpired = 3017;
    public const int OAuthFailed = 3018;

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
    public const int PowerDisabled = 4011;
    public const int PowerUnsupported = 4012;
    public const int PowerCooldown = 4013;
    public const int UsernameChangeCooldown = 4014;
    public const int AvatarChangeCooldown = 4015;
    public const int EmailChangeCooldown = 4016;
    public const int PasswordResetCooldown = 4017;
    public const int QuickLaunchDisabled = 4018;
    public const int QuickLaunchNotFound = 4019;
    public const int QuickLaunchLimitExceeded = 4020;
    public const int QuickLaunchUnsupported = 4021;

    public const int InternalError = 5001;
    public const int ServerMisconfigured = 5002;
    public const int GeetestNotConfigured = 5003;
    public const int SmtpNotConfigured = 5004;
    public const int SmtpSendFailed = 5005;
    public const int SmtpTimeout = 5006;
    public const int OAuthNotConfigured = 5007;
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

    [JsonPropertyName("has_email")]
    public bool HasEmail
    {
        get;
        set;
    } = true;

    [JsonPropertyName("has_password")]
    public bool HasPassword
    {
        get;
        set;
    } = true;
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

    [JsonPropertyName("has_email")]
    public bool HasEmail
    {
        get;
        set;
    } = true;

    [JsonPropertyName("has_password")]
    public bool HasPassword
    {
        get;
        set;
    } = true;
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

    [JsonPropertyName("has_email")]
    public bool HasEmail
    {
        get;
        set;
    } = true;

    [JsonPropertyName("has_password")]
    public bool HasPassword
    {
        get;
        set;
    } = true;
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

    [JsonPropertyName("has_email")]
    public bool HasEmail
    {
        get;
        set;
    } = true;

    [JsonPropertyName("has_password")]
    public bool HasPassword
    {
        get;
        set;
    } = true;
}

public sealed class AccountPasswordSetData
{
    [JsonPropertyName("has_email")]
    public bool HasEmail
    {
        get;
        set;
    } = true;

    [JsonPropertyName("has_password")]
    public bool HasPassword
    {
        get;
        set;
    } = true;

    [JsonPropertyName("profile_complete")]
    public bool ProfileComplete
    {
        get;
        set;
    } = true;
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

    [JsonPropertyName("allow_remote_power")]
    public bool AllowRemotePower
    {
        get;
        set;
    }

    [JsonPropertyName("allow_quick_launch")]
    public bool AllowQuickLaunch
    {
        get;
        set;
    }

    [JsonPropertyName("wol_macs")]
    public List<string> WolMacs
    {
        get;
        set;
    } = [];

    [JsonPropertyName("wol_broadcasts")]
    public List<string> WolBroadcasts
    {
        get;
        set;
    } = [];

    [JsonPropertyName("wol_ready")]
    public bool WolReady
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
    public bool IsComputerDevice =>
        string.IsNullOrWhiteSpace(OsVersion) ||
        (!OsVersion.Contains("android", StringComparison.OrdinalIgnoreCase) &&
         !OsVersion.Contains("ios", StringComparison.OrdinalIgnoreCase) &&
         !OsVersion.Contains("iphone", StringComparison.OrdinalIgnoreCase) &&
         !OsVersion.Contains("ipad", StringComparison.OrdinalIgnoreCase) &&
         !OsVersion.Contains("harmonyos", StringComparison.OrdinalIgnoreCase));

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

public sealed class AccountDevicePowerResultData
{
    [JsonPropertyName("action")]
    public string Action
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("target_device_id")]
    public string TargetDeviceId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("delivered")]
    public bool Delivered
    {
        get;
        set;
    }

    [JsonPropertyName("already_online")]
    public bool AlreadyOnline
    {
        get;
        set;
    }

    [JsonPropertyName("target_online")]
    public bool TargetOnline
    {
        get;
        set;
    }

    [JsonPropertyName("delay_seconds")]
    public int DelaySeconds
    {
        get;
        set;
    }

    [JsonPropertyName("relayed_count")]
    public int RelayedCount
    {
        get;
        set;
    }

    [JsonPropertyName("same_subnet_relayed_count")]
    public int SameSubnetRelayedCount
    {
        get;
        set;
    }

    [JsonPropertyName("wol_macs")]
    public List<string> WolMacs
    {
        get;
        set;
    } = [];

    [JsonPropertyName("wol_broadcasts")]
    public List<string> WolBroadcasts
    {
        get;
        set;
    } = [];
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

public sealed class AccountRemoteHistoryRecordDto
{
    [JsonPropertyName("id")]
    public string Id
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("role")]
    public string Role
    {
        get;
        set;
    } = "controller";

    [JsonPropertyName("same_account")]
    public bool SameAccount
    {
        get;
        set;
    }

    [JsonPropertyName("view_only")]
    public bool ViewOnly
    {
        get;
        set;
    }

    [JsonPropertyName("in_progress")]
    public bool InProgress
    {
        get;
        set;
    }

    [JsonPropertyName("local_device_id")]
    public string LocalDeviceId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("local_device_name")]
    public string LocalDeviceName
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("peer_device_id")]
    public string PeerDeviceId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("peer_device_name")]
    public string PeerDeviceName
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("peer_user_name")]
    public string PeerUserName
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("started_at")]
    public string StartedAt
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("ended_at")]
    public string? EndedAt
    {
        get;
        set;
    }

    [JsonPropertyName("duration_sec")]
    public long DurationSec
    {
        get;
        set;
    }

    [JsonIgnore] public string FormattedLocalDeviceId => DeviceIdentity.FormatDeviceId(LocalDeviceId);
    [JsonIgnore] public string FormattedPeerDeviceId => DeviceIdentity.FormatDeviceId(PeerDeviceId);

    [JsonIgnore]
    public string LocalStartedTimeText => DateTimeOffset.TryParse(StartedAt, out var dt)
        ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : StartedAt;
}

public sealed class AccountRemoteHistoryData
{
    [JsonPropertyName("records")]
    public List<AccountRemoteHistoryRecordDto> Records
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

    public bool? HasEmail
    {
        get;
        set;
    }

    public bool? HasPassword
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

public sealed class OAuthProvidersData
{
    [JsonPropertyName("google")]
    public bool Google
    {
        get;
        set;
    }

    [JsonPropertyName("qq")]
    public bool Qq
    {
        get;
        set;
    }

    [JsonPropertyName("wechat")]
    public bool Wechat
    {
        get;
        set;
    }

    [JsonPropertyName("providers")]
    public List<string> Providers
    {
        get;
        set;
    } = [];
}

public sealed class OAuthStartData
{
    [JsonPropertyName("provider")]
    public string Provider
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

    [JsonPropertyName("poll_secret")]
    public string PollSecret
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("authorize_url")]
    public string AuthorizeUrl
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

    [JsonPropertyName("poll_interval_ms")]
    public int PollIntervalMs
    {
        get;
        set;
    } = 1200;
}

public sealed class OAuthStatusData
{
    [JsonPropertyName("state")]
    public string State
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("intent")]
    public string Intent
    {
        get;
        set;
    } = "login";

    [JsonPropertyName("provider")]
    public string? Provider
    {
        get;
        set;
    }

    [JsonPropertyName("bound")]
    public bool Bound
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

    [JsonPropertyName("session")]
    public AccountLoginSessionData? Session
    {
        get;
        set;
    }
}

public sealed class OAuthBindingsData
{
    [JsonPropertyName("available")]
    public OAuthProvidersData Available
    {
        get;
        set;
    } = new();

    [JsonPropertyName("bound")]
    public OAuthProvidersData Bound
    {
        get;
        set;
    } = new();
}

public sealed class QuickLaunchAppDto
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

    [JsonPropertyName("device_cached")]
    public bool DeviceCached
    {
        get;
        set;
    }

    [JsonPropertyName("device_online")]
    public bool DeviceOnline
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

    [JsonPropertyName("allow_quick_launch")]
    public bool AllowQuickLaunch
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

    [JsonPropertyName("exe_path")]
    public string ExePath
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("icon_data")]
    public string? IconData
    {
        get;
        set;
    }

    [JsonPropertyName("created_at")]
    public string CreatedAt
    {
        get;
        set;
    } = string.Empty;
}

public sealed class QuickLaunchAppsListData
{
    [JsonPropertyName("apps")]
    public List<QuickLaunchAppDto> Apps
    {
        get;
        set;
    } = [];

    [JsonPropertyName("max_apps")]
    public int MaxApps
    {
        get;
        set;
    } = 100;
}

public sealed class QuickLaunchAddResultData
{
    [JsonPropertyName("app")]
    public QuickLaunchAppDto? App
    {
        get;
        set;
    }
}

public sealed class QuickLaunchBrowseEntry
{
    [JsonPropertyName("name")]
    public string Name
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("path")]
    public string Path
    {
        get;
        set;
    } = string.Empty;
}

public sealed class QuickLaunchBrowseResult
{
    [JsonPropertyName("requestId")]
    public string RequestId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("hostDeviceId")]
    public string HostDeviceId
    {
        get;
        set;
    } = string.Empty;

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

    [JsonPropertyName("reason")]
    public string Reason
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("currentPath")]
    public string CurrentPath
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("parentPath")]
    public string ParentPath
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("directories")]
    public List<QuickLaunchBrowseEntry> Directories
    {
        get;
        set;
    } = [];

    [JsonPropertyName("executables")]
    public List<QuickLaunchBrowseEntry> Executables
    {
        get;
        set;
    } = [];
}

public sealed class QuickLaunchValidateResult
{
    [JsonPropertyName("requestId")]
    public string RequestId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("hostDeviceId")]
    public string HostDeviceId
    {
        get;
        set;
    } = string.Empty;

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

    [JsonPropertyName("reason")]
    public string Reason
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("needsExeSelection")]
    public bool NeedsExeSelection
    {
        get;
        set;
    }

    [JsonPropertyName("directoryPath")]
    public string DirectoryPath
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("resolvedExePath")]
    public string ResolvedExePath
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("suggestedName")]
    public string SuggestedName
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("iconData")]
    public string? IconData
    {
        get;
        set;
    }

    [JsonPropertyName("candidates")]
    public List<QuickLaunchBrowseEntry> Candidates
    {
        get;
        set;
    } = [];
}

public sealed class QuickLaunchStartResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("hostDeviceId")]
    public string HostDeviceId
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("status")]
    public string Status
    {
        get;
        set;
    } = string.Empty;

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

    [JsonPropertyName("reason")]
    public string Reason
    {
        get;
        set;
    } = string.Empty;

    [JsonPropertyName("pid")]
    public int Pid
    {
        get;
        set;
    }
}