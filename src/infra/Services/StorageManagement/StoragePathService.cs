using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.StorageManagement;

/// <summary>
/// Infrastructure implementation of IStoragePathService.
/// Manages file storage paths across Docker, Kubernetes, and local development deployments.
/// Uses IApplicationPathProvider abstraction instead of ASP.NET Core IWebHostEnvironment.
/// </summary>
public class StoragePathService : IStoragePathService
{
    private readonly IApplicationPathProvider _pathProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StoragePathService> _logger;

    public StoragePathService(
        IApplicationPathProvider pathProvider,
        IConfiguration configuration,
        ILogger<StoragePathService> logger)
    {
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Get the gcode storage directory.
    /// Priority:
    /// 1. GCODE_STORAGE_PATH environment variable (for Docker/K8s with external volumes)
    /// 2. STORAGE_PATHS__GCODE config section
    /// 3. Default: {ContentRootPath}/gcode-library (for local development)
    /// </summary>
    public string GetGcodeStorageDirectory()
    {
        // Check environment variable first (Docker/K8s deployments)
        string? envPath = Environment.GetEnvironmentVariable("GCODE_STORAGE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            _logger.LogInformation("Using GCODE_STORAGE_PATH from environment: {StoragePath}", envPath);
            return envPath;
        }

        // Check configuration
        string? configPath = _configuration.GetValue<string>("STORAGE_PATHS:GCODE");
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            _logger.LogInformation("Using STORAGE_PATHS:GCODE from configuration: {StoragePath}", configPath);
            return configPath;
        }

        // Default: local development path
        string defaultPath = Path.Combine(_pathProvider.GetContentRootPath(), "gcode-library");
        _logger.LogInformation("Using default gcode storage path: {StoragePath}", defaultPath);
        return defaultPath;
    }

    public string GetThumbnailDirectory()
    {
        // Thumbnails are now stored in the same directory as GCODE files with _thumb.png suffix
        // This maintains consistency with Model3D storage approach
        return GetGcodeStorageDirectory();
    }

    public string GetModelUploadDirectory()
    {
        // Check environment variable first (Docker/K8s deployments)
        string? envPath = Environment.GetEnvironmentVariable("MODEL_UPLOAD_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            _logger.LogInformation("Using MODEL_UPLOAD_PATH from environment: {StoragePath}", envPath);
            return envPath;
        }

        // Check configuration
        string? configPath = _configuration.GetValue<string>("STORAGE_PATHS:UPLOADS");
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            _logger.LogInformation("Using STORAGE_PATHS:UPLOADS from configuration: {StoragePath}", configPath);
            return configPath;
        }

        // Default: local development path
        string defaultPath = Path.Combine(_pathProvider.GetContentRootPath(), "uploads");
        _logger.LogInformation("Using default model upload path: {StoragePath}", defaultPath);
        return defaultPath;
    }

    public string GetSlicerProfilesDirectory()
    {
        // Check environment variable first (Docker/K8s deployments)
        string? envPath = Environment.GetEnvironmentVariable("SLICER_PROFILES_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            _logger.LogInformation("Using SLICER_PROFILES_PATH from environment: {StoragePath}", envPath);
            return envPath;
        }

        // Check configuration
        string? configPath = _configuration.GetValue<string>("STORAGE_PATHS:PROFILES");
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            _logger.LogInformation("Using STORAGE_PATHS:PROFILES from configuration: {StoragePath}", configPath);
            return configPath;
        }

        // Default: local development path
        string defaultPath = Path.Combine(_pathProvider.GetContentRootPath(), "profiles");
        _logger.LogInformation("Using default slicer profiles path: {StoragePath}", defaultPath);
        return defaultPath;
    }

    public async Task EnsureDirectoriesExistAsync()
    {
        try
        {
            string[] directories = new[]
            {
                GetGcodeStorageDirectory(),
                GetThumbnailDirectory(),
                GetModelUploadDirectory(),
                GetSlicerProfilesDirectory()
            };

            foreach (string? dir in directories)
            {
                if (!Directory.Exists(dir))
                {
                    _logger.LogInformation("Creating storage directory: {Directory}", dir);
                    _ = Directory.CreateDirectory(dir);
                }
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure storage directories exist");
            throw;
        }
    }
}
