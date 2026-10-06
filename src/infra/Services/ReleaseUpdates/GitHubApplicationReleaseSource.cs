using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Services.ReleaseUpdates;

/// <summary>The newest published GitHub release on one channel.</summary>
public sealed record ApplicationReleaseInfo(
    ApplicationReleaseVersion Version,
    string? Name,
    DateTimeOffset? PublishedAt);

/// <summary>Source of the latest published PrintFarmer release for a channel.</summary>
public interface IApplicationReleaseSource
{
    /// <summary>Returns the newest non-draft release on <paramref name="channel"/>, or
    /// <see langword="null"/> when none is published. Throws on transport/protocol failure.</summary>
    Task<ApplicationReleaseInfo?> GetLatestReleaseAsync(ApplicationReleaseChannel channel, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the public GitHub Releases listing for <c>OlyForge3D/PrintFarmer</c> with a bounded
/// response size. The configured <see cref="HttpClient"/> supplies the base address, headers,
/// timeout and disables redirects (see <c>FeatureServicesStartup</c>).
/// </summary>
public sealed class GitHubApplicationReleaseSource(HttpClient httpClient) : IApplicationReleaseSource
{
    internal const string ReleasesPath = "repos/OlyForge3D/PrintFarmer/releases?per_page=50";
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<ApplicationReleaseInfo?> GetLatestReleaseAsync(
        ApplicationReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(ReleasesPath, UriKind.Relative));
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(DescribeFailure(response), null, response.StatusCode);
        }

        byte[] body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        List<GitHubReleaseListing>? releases;
        try
        {
            releases = JsonSerializer.Deserialize<List<GitHubReleaseListing>>(body, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("GitHub returned an unreadable release listing.", ex);
        }

        return SelectLatest(releases ?? [], channel);
    }

    /// <summary>Picks the newest non-draft release whose tag and pre-release flag both match
    /// <paramref name="channel"/>. Unrelated tags are ignored.</summary>
    internal static ApplicationReleaseInfo? SelectLatest(
        IEnumerable<GitHubReleaseListing> releases,
        ApplicationReleaseChannel channel)
    {
        ApplicationReleaseInfo? latest = null;
        foreach (GitHubReleaseListing release in releases)
        {
            if (release is null
                || release.Draft
                || !ApplicationReleaseVersion.TryParse(release.TagName, out ApplicationReleaseVersion? version)
                || version is null
                || !string.Equals(release.TagName, version.Tag, StringComparison.Ordinal)
                || version.Channel != channel
                || release.Prerelease != (channel == ApplicationReleaseChannel.Insider))
            {
                continue;
            }

            if (latest is null || version > latest.Version)
            {
                latest = new ApplicationReleaseInfo(version, Truncate(release.Name, 200), release.PublishedAt);
            }
        }

        return latest;
    }

    private static string DescribeFailure(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        bool rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden
                && response.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string>? remaining)
                && remaining.Contains("0", StringComparer.Ordinal));
        return rateLimited
            ? $"GitHub API rate limit reached (HTTP {status}); will retry at the next check."
            : $"GitHub release listing request failed with HTTP {status}.";
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException($"GitHub release listing exceeds the {MaximumResponseBytes}-byte limit.");
        }

        Stream source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            using MemoryStream destination = new();
            byte[] buffer = new byte[16 * 1024];
            int total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaximumResponseBytes)
                {
                    throw new InvalidDataException($"GitHub release listing exceeds the {MaximumResponseBytes}-byte limit.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return destination.ToArray();
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>The subset of the GitHub release payload this check consumes.</summary>
    internal sealed record GitHubReleaseListing(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt);
}
