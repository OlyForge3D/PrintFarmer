using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Farm.Infrastructure.Services.DataManagement;

/// <inheritdoc/>
public class CatalogUpdateService : ICatalogUpdateService
{
    private const string DefaultGitHubBaseUrl = "https://raw.githubusercontent.com/OlyForge3D/PrintFarmer/main/src/api/Data/seed/";
    private const string ManifestFileName = "catalog-manifest.yaml";

    private static readonly Dictionary<string, string> FileCategoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["manufacturers"] = "Manufacturers",
        ["printer-models"] = "Printer Models",
        ["filament-types"] = "Filament Types",
        ["hotends"] = "Hotends",
        ["extruders"] = "Extruders",
        ["toolheads"] = "Toolheads",
        ["nozzles"] = "Nozzles",
        ["maintenance-tasks"] = "Maintenance Tasks",
        ["maintenance-components"] = "Maintenance Components",
        ["maintenance-plans"] = "Maintenance Plans"
    };

    private readonly AppDbContext _context;
    private readonly IDataSeedService _seedService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CatalogUpdateService> _logger;
    private readonly string _seedDataPath;
    private readonly string _gitHubBaseUrl;
    private readonly IDeserializer _yamlDeserializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogUpdateService"/> class.
    /// </summary>
    /// <param name="context">Database context.</param>
    /// <param name="seedService">Seed data service for applying updates.</param>
    /// <param name="httpClientFactory">HTTP client factory for remote manifest fetch.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="configuration">Application configuration for seed data path.</param>
    public CatalogUpdateService(
        AppDbContext context,
        IDataSeedService seedService,
        IHttpClientFactory httpClientFactory,
        ILogger<CatalogUpdateService> logger,
        IConfiguration configuration)
    {
        _context = context;
        _seedService = seedService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _seedDataPath = configuration["SeedData:Path"] ?? Path.Combine(AppContext.BaseDirectory, "Data", "seed");
        _gitHubBaseUrl = configuration["CatalogUpdate:GitHubBaseUrl"] ?? DefaultGitHubBaseUrl;
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <inheritdoc/>
    public async Task<CatalogUpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[CatalogUpdate] Checking for catalog updates...");

        CatalogUpdateCheckResult result = new() { CheckedAt = DateTime.UtcNow };

        try
        {
            // Get current applied version
            CatalogVersion? current = await GetLatestAppliedVersionAsync(ct);
            result.CurrentVersion = current?.Version;

            // Read local manifest
            CatalogManifest? localManifest = await ReadLocalManifestAsync();

            // Fetch remote manifest from GitHub
            CatalogManifest? remoteManifest = await FetchRemoteManifestAsync(ct);
            if (remoteManifest == null)
            {
                result.Error = "Unable to fetch remote catalog manifest. Check network connectivity.";
                _logger.LogWarning("[CatalogUpdate] Failed to fetch remote manifest");
                return result;
            }

            result.AvailableVersion = remoteManifest.Version;

            // Compare manifests
            if (localManifest != null && localManifest.Version == remoteManifest.Version)
            {
                // Same version — check individual file hashes for drift
                result.ChangedFiles = CompareManifests(localManifest, remoteManifest);
                result.UpdateAvailable = result.ChangedFiles.Count > 0;
            }
            else
            {
                // Different version or no local manifest — full comparison
                result.UpdateAvailable = true;
                if (localManifest != null)
                {
                    result.ChangedFiles = CompareManifests(localManifest, remoteManifest);
                }
                else
                {
                    // No local manifest — treat all remote files as new
                    foreach (KeyValuePair<string, CatalogFileEntry> entry in remoteManifest.Files)
                    {
                        result.ChangedFiles.Add(new CatalogFileChange
                        {
                            FileName = entry.Key,
                            Category = GetCategoryName(entry.Key),
                            ChangeType = "New"
                        });
                    }
                }
            }

            _logger.LogInformation(
                "[CatalogUpdate] Check complete. Update available: {Available}, changed files: {Count}",
                result.UpdateAvailable,
                result.ChangedFiles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CatalogUpdate] Error checking for updates: {Message}", ex.Message);
            result.Error = $"Error checking for updates: {ex.Message}";
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<CatalogUpdateApplyResult> ApplyUpdatesAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[CatalogUpdate] Applying catalog updates...");

        CatalogUpdateApplyResult result = new();

        try
        {
            // Get current version
            CatalogVersion? current = await GetLatestAppliedVersionAsync(ct);
            result.PreviousVersion = current?.Version;

            // Read local manifest for comparison
            CatalogManifest? localManifest = await ReadLocalManifestAsync();

            // Fetch remote manifest
            CatalogManifest? remoteManifest = await FetchRemoteManifestAsync(ct);
            if (remoteManifest == null)
            {
                result.Error = "Unable to fetch remote catalog manifest. Check network connectivity.";
                return result;
            }

            // Determine which files changed
            List<CatalogFileChange> changes = localManifest != null
                ? CompareManifests(localManifest, remoteManifest)
                : remoteManifest.Files.Select(e => new CatalogFileChange
                {
                    FileName = e.Key,
                    Category = GetCategoryName(e.Key),
                    ChangeType = "New"
                }).ToList();

            if (changes.Count == 0)
            {
                result.Success = true;
                result.AppliedVersion = remoteManifest.Version;
                result.AppliedAt = DateTime.UtcNow;
                _logger.LogInformation("[CatalogUpdate] No file changes detected, but version updated");
            }
            else
            {
                // Download changed YAML files and overwrite local copies
                foreach (CatalogFileChange change in changes)
                {
                    if (change.ChangeType == "Removed")
                    {
                        continue; // Don't delete local files
                    }

                    if (!remoteManifest.Files.TryGetValue(change.FileName, out CatalogFileEntry? entry))
                    {
                        continue;
                    }

                    await DownloadAndSaveFileAsync(entry.Path, ct);
                    result.UpdatedCategories.Add(change.Category);
                }

                // Download the updated manifest itself
                await DownloadAndSaveFileAsync(ManifestFileName, ct);

                // Re-seed the database with updated YAML files
                _logger.LogInformation("[CatalogUpdate] Re-seeding database with updated catalog data...");
                await _seedService.SeedAllAsync();

                result.Success = true;
                result.AppliedVersion = remoteManifest.Version;
                result.AppliedAt = DateTime.UtcNow;
            }

            // Record the applied version
            string manifestHash = await ComputeLocalManifestHashAsync();
            _context.CatalogVersions.Add(new CatalogVersion
            {
                Id = Guid.NewGuid(),
                Version = remoteManifest.Version,
                ManifestHash = manifestHash,
                AppliedAt = result.AppliedAt,
                Source = "github"
            });
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[CatalogUpdate] Update applied successfully. Version: {Version}, Categories: {Categories}",
                result.AppliedVersion,
                string.Join(", ", result.UpdatedCategories));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CatalogUpdate] Error applying updates: {Message}", ex.Message);
            result.Error = $"Error applying updates: {ex.Message}";
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<CatalogVersionDto?> GetCurrentVersionAsync(CancellationToken ct = default)
    {
        CatalogVersion? version = await GetLatestAppliedVersionAsync(ct);
        if (version == null)
        {
            return null;
        }

        return new CatalogVersionDto
        {
            Version = version.Version,
            AppliedAt = version.AppliedAt,
            Source = version.Source
        };
    }

    private async Task<CatalogVersion?> GetLatestAppliedVersionAsync(CancellationToken ct)
    {
        return await _context.CatalogVersions
            .OrderByDescending(v => v.AppliedAt)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<CatalogManifest?> ReadLocalManifestAsync()
    {
        string manifestPath = Path.Combine(_seedDataPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            _logger.LogWarning("[CatalogUpdate] Local manifest not found at {Path}", manifestPath);
            return null;
        }

        try
        {
            string yaml = await File.ReadAllTextAsync(manifestPath);
            return _yamlDeserializer.Deserialize<CatalogManifest>(yaml);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CatalogUpdate] Error reading local manifest: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<CatalogManifest?> FetchRemoteManifestAsync(CancellationToken ct)
    {
        try
        {
            using HttpClient client = _httpClientFactory.CreateClient("CatalogUpdate");
            string url = _gitHubBaseUrl + ManifestFileName;
            _logger.LogInformation("[CatalogUpdate] Fetching remote manifest from {Url}", url);

            HttpResponseMessage response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[CatalogUpdate] Remote manifest fetch failed with status {Status}", response.StatusCode);
                return null;
            }

            string yaml = await response.Content.ReadAsStringAsync(ct);
            return _yamlDeserializer.Deserialize<CatalogManifest>(yaml);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[CatalogUpdate] Network error fetching remote manifest: {Message}", ex.Message);
            return null;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("[CatalogUpdate] Remote manifest fetch timed out");
            return null;
        }
    }

    private async Task DownloadAndSaveFileAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            using HttpClient client = _httpClientFactory.CreateClient("CatalogUpdate");
            string url = _gitHubBaseUrl + relativePath;
            _logger.LogInformation("[CatalogUpdate] Downloading {File} from {Url}", relativePath, url);

            HttpResponseMessage response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync(ct);
            string localPath = Path.Combine(_seedDataPath, relativePath);

            // Ensure directory exists
            string? directory = Path.GetDirectoryName(localPath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(localPath, content, ct);
            _logger.LogInformation("[CatalogUpdate] Saved {File} to {Path}", relativePath, localPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CatalogUpdate] Error downloading {File}: {Message}", relativePath, ex.Message);
            throw;
        }
    }

    private async Task<string> ComputeLocalManifestHashAsync()
    {
        string manifestPath = Path.Combine(_seedDataPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return string.Empty;
        }

        byte[] bytes = await File.ReadAllBytesAsync(manifestPath);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static List<CatalogFileChange> CompareManifests(CatalogManifest local, CatalogManifest remote)
    {
        List<CatalogFileChange> changes = [];

        // Check for modified or new files in remote
        foreach (KeyValuePair<string, CatalogFileEntry> remoteEntry in remote.Files)
        {
            if (local.Files.TryGetValue(remoteEntry.Key, out CatalogFileEntry? localEntry))
            {
                if (!string.Equals(localEntry.Sha256, remoteEntry.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add(new CatalogFileChange
                    {
                        FileName = remoteEntry.Key,
                        Category = GetCategoryName(remoteEntry.Key),
                        ChangeType = "Modified"
                    });
                }
            }
            else
            {
                changes.Add(new CatalogFileChange
                {
                    FileName = remoteEntry.Key,
                    Category = GetCategoryName(remoteEntry.Key),
                    ChangeType = "New"
                });
            }
        }

        // Check for removed files (in local but not in remote)
        foreach (KeyValuePair<string, CatalogFileEntry> localEntry in local.Files)
        {
            if (!remote.Files.ContainsKey(localEntry.Key))
            {
                changes.Add(new CatalogFileChange
                {
                    FileName = localEntry.Key,
                    Category = GetCategoryName(localEntry.Key),
                    ChangeType = "Removed"
                });
            }
        }

        return changes;
    }

    private static string GetCategoryName(string fileName)
    {
        return FileCategoryNames.TryGetValue(fileName, out string? name) ? name : fileName;
    }
}
