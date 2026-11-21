using System.Reflection;

namespace Osmium.Core.Models;

public static class AppVersionInfo
{
    public static string Current
    {
        get;
    } = ResolveVersion();

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

        return "1.0.0";
    }
}