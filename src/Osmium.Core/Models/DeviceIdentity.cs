using System.Security.Cryptography;

namespace Osmium.Core.Models;

public enum AccessCodeRefreshPolicy
{
    ManualOnly = 0,
    AfterEachSession = 1,
    OnAppStartup = 2
}

public sealed class DeviceIdentity
{
    private const string CodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public string DeviceId
    {
        get;
        set;
    } = string.Empty;

    public string DeviceName
    {
        get;
        set;
    } = Environment.MachineName;

    public string TemporaryAccessCode
    {
        get;
        set;
    } = string.Empty;

    public string FixedAccessCode
    {
        get;
        set;
    } = string.Empty;

    public bool UseFixedAccessCode
    {
        get;
        set;
    } = false;

    public AccessCodeRefreshPolicy RefreshPolicy
    {
        get;
        set;
    } = AccessCodeRefreshPolicy.AfterEachSession;

    public string FormattedDeviceId => FormatDeviceId(DeviceId);

    public static string FormatDeviceId(string raw)
    {
        var digits = NormalizeDeviceId(raw);
        if (digits.Length == 9)
        {
            return $"{digits[..3]} {digits[3..6]} {digits[6..9]}";
        }

        return digits;
    }

    public static string NormalizeDeviceId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        Span<char> buf = stackalloc char[input.Length];
        int len = 0;
        foreach (char c in input)
        {
            if (char.IsAsciiDigit(c))
            {
                buf[len++] = c;
            }
        }

        return new string(buf[..len]);
    }

    public static string GenerateDeviceId()
    {
        int val = RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
        return val.ToString();
    }

    public static string GenerateAccessCode(int length = 6)
    {
        Span<char> chars = stackalloc char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(0, CodeAlphabet.Length)];
        }

        return new string(chars);
    }

    public void EnsureValid()
    {
        DeviceId = NormalizeDeviceId(DeviceId);
        if (DeviceId.Length != 9)
        {
            DeviceId = GenerateDeviceId();
        }

        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            DeviceName = Environment.MachineName;
        }

        if (string.IsNullOrWhiteSpace(TemporaryAccessCode))
        {
            TemporaryAccessCode = GenerateAccessCode();
        }
    }
}