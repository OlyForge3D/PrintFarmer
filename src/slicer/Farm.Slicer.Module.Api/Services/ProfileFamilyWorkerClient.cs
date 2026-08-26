using System.Net.Http.Json;
using System.Text.Json;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// HTTP adapter for the worker-owned custom profile source and cache lifecycle.
/// </summary>
public sealed class ProfileFamilyWorkerClient(
    HttpClient httpClient,
    ISlicersService slicersService,
    IConfiguration configuration,
    ILogger<ProfileFamilyWorkerClient> logger) : IProfileFamilyWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    private readonly ISlicersService _slicersService =
        slicersService ?? throw new ArgumentNullException(nameof(slicersService));

    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    private readonly ILogger<ProfileFamilyWorkerClient> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<(ProfileFamilyWorkerTarget Target, AllProfilesResponseDto Catalog)> GetCatalogAsync(
        string sourceManufacturer,
        string? orcaVersion,
        CancellationToken ct)
    {
        ProfileFamilyWorkerTarget target = await SelectWorkerAsync(orcaVersion, ct);
        string requestUri =
            $"{target.BaseUrl.TrimEnd('/')}/api/profiles?manufacturer={Uri.EscapeDataString(sourceManufacturer)}";
        using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OrcaSlicer worker catalog request failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        AllProfilesResponseDto? catalog = await response.Content.ReadFromJsonAsync<AllProfilesResponseDto>(
            JsonOptions,
            ct);
        return (
            target,
            catalog ?? throw new HttpRequestException(
                "OrcaSlicer worker returned an empty profile catalog response."));
    }

    /// <inheritdoc />
    public async Task WriteBundleAsync(
        ProfileFamilyWorkerTarget target,
        ProfileFamilyBundleDto bundle,
        CancellationToken ct)
    {
        string bundleName = $"PrintFarmer-{bundle.FamilyId:N}";
        string requestUri =
            $"{target.BaseUrl.TrimEnd('/')}/api/profiles/custom-bundles/{bundleName}";
        JsonElement manifest = JsonSerializer.Deserialize<JsonElement>(bundle.ManifestJson);
        List<CustomProfileFileRequest> files = bundle.Files
            .Select(file => new CustomProfileFileRequest(
                file.RelativePath,
                bundle.FamilyName,
                JsonSerializer.Deserialize<JsonElement>(file.Content)))
            .ToList();
        var body = new CustomProfileBundleRequest(manifest, files);
        using HttpRequestMessage request = CreateAuthenticatedRequest(
            HttpMethod.Put,
            requestUri,
            JsonContent.Create(body, options: JsonOptions));
        using HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OrcaSlicer worker bundle write failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
    }

    private async Task<ProfileFamilyWorkerTarget> SelectWorkerAsync(
        string? requestedVersion,
        CancellationToken ct)
    {
        IReadOnlyList<SlicerService> services = await _slicersService.ListAsync(ct);
        DateTime freshnessCutoff = DateTime.UtcNow.AddSeconds(-WorkerStatus.OnlineFreshnessSeconds);
        string? normalizedVersion = string.IsNullOrWhiteSpace(requestedVersion)
            ? null
            : requestedVersion.Trim();

        List<SlicerService> candidates = services
            .Where(service => service.SlicerType == (int)SlicerType.OrcaSlicer)
            .Where(service => string.Equals(
                service.Status,
                "Online",
                StringComparison.OrdinalIgnoreCase))
            .Where(service => service.LastSeen >= freshnessCutoff)
            .Where(service => !string.IsNullOrWhiteSpace(service.Host))
            .Where(service => !string.IsNullOrWhiteSpace(service.Version))
            .Where(service =>
                normalizedVersion is null ||
                string.Equals(
                    service.Version!.Trim(),
                    normalizedVersion,
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(service => service.LastSeen)
            .ToList();

        SlicerService? selected = candidates.FirstOrDefault();
        if (selected is null)
        {
            string versionSuffix = normalizedVersion is null
                ? string.Empty
                : $" for version '{normalizedVersion}'";
            throw new HttpRequestException(
                $"No fresh online OrcaSlicer worker is available{versionSuffix}.");
        }

        _logger.LogInformation(
            "Selected OrcaSlicer worker {WorkerId} version {Version} for profile-family rendering",
            selected.Id,
            selected.Version);
        return new ProfileFamilyWorkerTarget(
            selected.Host!.TrimEnd('/'),
            selected.Version!.Trim());
    }

    private HttpRequestMessage CreateAuthenticatedRequest(
        HttpMethod method,
        string requestUri,
        HttpContent content)
    {
        string sharedKey = WorkerAuthConfiguration.ResolveSharedKey(_configuration)?.Value
            ?? throw new InvalidOperationException(
                "WorkerAuth:SharedKey is required for custom profile writes.");

        HttpRequestMessage request = new(method, requestUri)
        {
            Content = content
        };
        request.Headers.Add("X-Slicer-Api-Key", sharedKey);
        return request;
    }

    private sealed record CustomProfileBundleRequest(
        JsonElement Manifest,
        IReadOnlyList<CustomProfileFileRequest> Files);

    private sealed record CustomProfileFileRequest(
        string RelativePath,
        string FamilyName,
        JsonElement Document);
}
