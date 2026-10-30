using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GTAModAssistant.Core.Services;

public sealed record AvailableRelease(string Version, string Url, string Name);

public sealed class GitHubUpdateService
{
    private static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/kenzobenzo2812-a11y/GTA-Mod-Assistant/releases/latest");
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly HttpClient _httpClient;

    public GitHubUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("GTAModAssistant", "1.0"));
        }
    }

    public async Task<AvailableRelease?> CheckForUpdateAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseUri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        string releaseUrl = root.GetProperty("html_url").GetString() ?? string.Empty;
        string name = root.GetProperty("name").GetString() ?? tag;

        if (!TryParseVersion(tag, out var latest))
        {
            throw new InvalidDataException("GitHub returned a release with an invalid version tag.");
        }
        if (!TryParseVersion(currentVersion, out var current))
        {
            throw new InvalidDataException("The installed application version could not be determined.");
        }
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out Uri? releaseUri) ||
            releaseUri.Scheme != Uri.UriSchemeHttps ||
            !releaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !releaseUri.AbsolutePath.StartsWith(
                "/kenzobenzo2812-a11y/GTA-Mod-Assistant/releases/",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub returned an invalid release page URL.");
        }
        if (latest <= current)
        {
            return null;
        }

        return new AvailableRelease(tag, releaseUri.AbsoluteUri, name);
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        string normalized = value.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (Version.TryParse(normalized, out var parsed))
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }
}
