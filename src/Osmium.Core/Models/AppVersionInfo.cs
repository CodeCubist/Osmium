using System.Reflection;

namespace Osmium.Core.Models;

public static class AppVersionInfo
{
    public const string MinimumSupported = "1.0.2";
    private static readonly Version MinimumSupportedVersion = new(1, 0, 2);

    public static string Current
    {
        get;
    } = ResolveVersion();

    public static string GitBranch
    {
        get;
    } = ResolveAssemblyMetadata("GitBranch") ?? "unknown";

    public static string GitCommitHash
    {
        get;
    } = ResolveAssemblyMetadata("GitCommitHash") ?? "unknown";

    public static string BuildIdentifier
    {
        get
        {
            bool hasBranch = !string.IsNullOrWhiteSpace(GitBranch) &&
                             !string.Equals(GitBranch, "unknown", StringComparison.OrdinalIgnoreCase);
            bool hasHash = !string.IsNullOrWhiteSpace(GitCommitHash) &&
                           !string.Equals(GitCommitHash, "unknown", StringComparison.OrdinalIgnoreCase);
            if (hasBranch && hasHash) return $"{GitBranch}@{GitCommitHash}";
            if (hasBranch) return GitBranch;
            if (hasHash) return GitCommitHash;
            return "unknown";
        }
    }

    public static bool IsVersionSupported(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string trimmed = raw.Trim().TrimStart('v', 'V');
        string core = trimmed.Split('+', '-')[0].Trim();
        if (!Version.TryParse(core, out var parsed)) return false;
        var normalized = new Version(parsed.Major, Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build));
        return normalized >= MinimumSupportedVersion;
    }

    private static string ResolveVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppVersionInfo).Assembly;
        string? infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            string core = infoVer.Split('+', '-')[0].Trim();
            if (Version.TryParse(core, out var parsed))
            {
                return $"{parsed.Major}.{Math.Max(0, parsed.Minor)}.{Math.Max(0, parsed.Build)}";
            }
        }

        Version? v = asm.GetName().Version;
        if (v != null)
        {
            return $"{v.Major}.{Math.Max(0, v.Minor)}.{Math.Max(0, v.Build)}";
        }

        return "1.0.2";
    }

    private static string? ResolveAssemblyMetadata(string key)
    {
        Assembly?[] candidates = [Assembly.GetEntryAssembly(), typeof(AppVersionInfo).Assembly];
        foreach (var asm in candidates)
        {
            if (asm == null) continue;
            foreach (var attr in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(attr.Key, key, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(attr.Value))
                {
                    return attr.Value.Trim();
                }
            }
        }

        return null;
    }
}