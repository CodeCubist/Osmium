using System.Net.Http.Json;

namespace Osmium.App.Services;

internal sealed record UpdateRelease(string Version, string? Notes, Dictionary<string, string>? Downloads)
{
    public Uri? GetDownloadUri(string kind)
    {
        return Downloads != null &&
               Downloads.TryGetValue(kind, out var link) &&
               Uri.TryCreate(link, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               uri.Host.Length > 0 && uri.UserInfo.Length == 0
            ? uri
            : null;
    }
}

internal static class UpdateService
{
    private sealed record UpdateResponse(bool Ok, UpdateRelease? Data);

    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(8),
        MaxResponseContentBufferSize = 128 * 1024
    };

    public static async Task<UpdateRelease?> CheckAsync(Uri endpoint, string currentVersion, CancellationToken token)
    {
        using var response = await Client.GetAsync(endpoint, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var manifest = await response.Content.ReadFromJsonAsync<UpdateResponse>(cancellationToken: token)
            .ConfigureAwait(false);
        var release = manifest is { Ok: true } ? manifest.Data : null;
        return release != null &&
               Version.TryParse(release.Version, out var latest) && latest.Build >= 0 && latest.Revision < 0 &&
               Version.TryParse(currentVersion, out var current) && latest > current
            ? release
            : null;
    }
}
