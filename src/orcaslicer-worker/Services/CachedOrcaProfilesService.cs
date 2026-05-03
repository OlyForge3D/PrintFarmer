using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Worker.Core;
using Microsoft.Extensions.Logging;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// SQLite-cached wrapper around OrcaProfilesService.
/// Provides fast queries from SQLite after initial warm-up from JSON files.
/// </summary>
public sealed class CachedOrcaProfilesService : ISlicerProfilesService, IAsyncDisposable
{
    private const string CacheCompatibilityVersion = "3";

    private readonly OrcaProfilesService _innerService;
    private readonly ProfileCacheDb _cacheDb;
    private readonly ILogger<CachedOrcaProfilesService> _logger;
    private readonly string _profilesPath;
    private bool _cacheInitialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly TaskCompletionSource _cacheReadyTcs = new();

    /// <summary>
    /// Gets whether the cache is ready and the service can handle requests.
    /// This is used by RegistrationBackgroundService to delay registration until cache is populated.
    /// </summary>
    public bool IsCacheReady => _cacheInitialized;

    /// <summary>
    /// Task that completes when the cache is ready. Use this to await cache initialization.
    /// </summary>
    public Task CacheReadyTask => _cacheReadyTcs.Task;

    public CachedOrcaProfilesService(ILogger<CachedOrcaProfilesService> logger, string? profilesPath = null, string? dbPath = null)
    {
        _logger = logger;
        _innerService = new OrcaProfilesService(logger, profilesPath);

        // Determine profiles path for hash calculation
        string? envPath = Environment.GetEnvironmentVariable("ORCA_PROFILES_PATH");
        _profilesPath = profilesPath ?? envPath ?? "/opt/orcaslicer/resources/profiles";

        // Use a persistent path for the database
        string actualDbPath = dbPath ?? Path.Combine(
            Environment.GetEnvironmentVariable("PROFILE_CACHE_PATH") ?? "/app/cache",
            "orcaslicer-profiles.db");

        _cacheDb = new ProfileCacheDb(logger, actualDbPath);
    }

    /// <summary>
    /// Initializes the cache database and warms it if necessary.
    /// Call this during startup for best performance.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            if (_cacheInitialized)
            {
                return;
            }

            Stopwatch sw = Stopwatch.StartNew();

            await _cacheDb.InitializeAsync(ct);

            // Include parser/cache compatibility so expression-parser fixes invalidate stale compatibility data.
            string profilesHash = $"{CacheCompatibilityVersion}:{CalculateProfilesHash()}";

            if (await _cacheDb.IsCacheValidAsync(profilesHash, ct))
            {
                (int m, int f, int p) = await _cacheDb.GetCountsAsync(ct);
                _logger.LogInformation("SQLite cache is valid. Loaded {M} machines, {F} filaments, {P} processes in {SwElapsedMilliseconds}ms", m, f, p, sw.ElapsedMilliseconds);
                _cacheInitialized = true;
                _cacheReadyTcs.TrySetResult();
                return;
            }

            _logger.LogInformation("SQLite cache is stale or empty. Rebuilding from JSON profiles...");

            await _cacheDb.ClearCacheAsync(ct);

            // Load from inner service (JSON parsing)
            Stopwatch parseWatch = Stopwatch.StartNew();

            IList<MachineProfileDto> machines = await _innerService.ListAvailableMachineProfilesAsync(ct);
            long machineTime = parseWatch.ElapsedMilliseconds;

            IList<FilamentProfileDto> filaments = await _innerService.ListAvailableFilamentProfilesAsync(ct);
            long filamentTime = parseWatch.ElapsedMilliseconds - machineTime;

            IList<ProcessProfileDto> processes = await _innerService.ListAvailableProcessProfilesAsync(ct);
            long processTime = parseWatch.ElapsedMilliseconds - filamentTime - machineTime;

            _logger.LogInformation("Parsed JSON profiles in {ParseWatchElapsedMilliseconds}ms: machines={MachineTime}ms, filaments={FilamentTime}ms, processes={ProcessTime}ms", parseWatch.ElapsedMilliseconds, machineTime, filamentTime, processTime);

            // Store in SQLite
            Stopwatch storeWatch = Stopwatch.StartNew();

            await _cacheDb.StoreMachineProfilesAsync(machines, ct);
            await _cacheDb.StoreFilamentProfilesAsync(filaments, ct);
            await _cacheDb.StoreProcessProfilesAsync(processes, ct);
            await _cacheDb.SetMetadataAsync("profiles_hash", profilesHash, ct);
            await _cacheDb.SetMetadataAsync("cached_at", DateTime.UtcNow.ToString("O"), ct);

            _logger.LogInformation("Stored {MachinesCount} machines, {FilamentsCount} filaments, {ProcessesCount} processes in SQLite in {StoreWatchElapsedMilliseconds}ms", machines.Count, filaments.Count, processes.Count, storeWatch.ElapsedMilliseconds);

            _cacheInitialized = true;

            _logger.LogInformation("Total cache initialization: {SwElapsedMilliseconds}ms", sw.ElapsedMilliseconds);

            // Signal that the cache is ready for requests
            _cacheReadyTcs.TrySetResult();
        }
        finally
        {
            _initLock.Release();
        }
    }

    private string CalculateProfilesHash()
    {
        // Hash based on profile directory structure and modification times
        // This allows detecting when profiles change without reading all content
        try
        {
            if (!Directory.Exists(_profilesPath))
            {
                return "empty";
            }

            StringBuilder sb = new();

            // Get all JSON files and their modification times
            string[] files = Directory.GetFiles(_profilesPath, "*.json", SearchOption.AllDirectories);
            foreach (string file in files.OrderBy(f => f))
            {
                FileInfo fi = new(file);
                sb.Append(file);
                sb.Append(':');
                sb.Append(fi.LastWriteTimeUtc.Ticks);
                sb.Append(';');
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToBase64String(hash)[..16]; // Short hash is sufficient
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to calculate profiles hash: {Message}", ex.Message);
            return Guid.NewGuid().ToString(); // Force rebuild on error
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (!_cacheInitialized)
        {
            await InitializeAsync(ct);
        }
    }

    public async Task<IList<MachineModelProfileDto>> ListAvailableMachineModelProfilesAsync(CancellationToken ct = default)
    {
        // Machine models are less frequently used - delegate to inner service
        // Could add caching later if needed
        return await _innerService.ListAvailableMachineModelProfilesAsync(ct);
    }

    public async Task<IList<MachineProfileDto>> ListAvailableMachineProfilesAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _cacheDb.GetMachineProfilesAsync(ct);
    }

    public async Task<IList<FilamentProfileDto>> ListAvailableFilamentProfilesAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _cacheDb.GetFilamentProfilesAsync(ct);
    }

    public async Task<IList<ProcessProfileDto>> ListAvailableProcessProfilesAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _cacheDb.GetProcessProfilesAsync(ct);
    }

    /// <summary>
    /// Gets machine profiles for a specific manufacturer (optimized query).
    /// </summary>
    public async Task<List<MachineProfileDto>> GetMachineProfilesByManufacturerAsync(string manufacturer, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _cacheDb.GetMachineProfilesByManufacturerAsync(manufacturer, ct);
    }

    /// <summary>
    /// Gets machine profiles by printer_model only (indexed query).
    /// This is the simplest query - just match the printer_model field directly.
    /// </summary>
    public async Task<List<MachineProfileDto>> GetMachineProfilesByPrinterModelAsync(string printerModel, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _cacheDb.GetMachineProfilesByPrinterModelAsync(printerModel, ct);
    }

    /// <summary>
    /// Gets machine profiles for a specific manufacturer and printer model (indexed query).
    /// </summary>
    public async Task<List<MachineProfileDto>> GetMachineProfilesByModelAsync(
        string manufacturer,
        string printerModel,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _cacheDb.GetMachineProfilesByModelAsync(manufacturer, printerModel, ct);
    }

    /// <summary>
    /// Gets distinct printer models for a manufacturer.
    /// </summary>
    public async Task<List<string>> GetPrinterModelsAsync(string manufacturer, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _cacheDb.GetPrinterModelsAsync(manufacturer, ct);
    }

    /// <summary>
    /// Gets filament profiles for a specific manufacturer (optimized query).
    /// </summary>
    public async Task<List<FilamentProfileDto>> GetFilamentProfilesByManufacturerAsync(string manufacturer, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _cacheDb.GetFilamentProfilesByManufacturerAsync(manufacturer, ct);
    }

    /// <summary>
    /// Gets process profiles for a specific manufacturer (optimized query).
    /// </summary>
    public async Task<List<ProcessProfileDto>> GetProcessProfilesByManufacturerAsync(string manufacturer, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _cacheDb.GetProcessProfilesByManufacturerAsync(manufacturer, ct);
    }

    /// <summary>
    /// Gets all distinct manufacturers.
    /// </summary>
    public async Task<List<string>> GetManufacturersAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _cacheDb.GetManufacturersAsync(ct);
    }

    /// <summary>
    /// Gets all distinct materials.
    /// </summary>
    public async Task<List<string>> GetMaterialsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _cacheDb.GetMaterialsAsync(ct);
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public async Task<(int machineCount, int filamentCount, int processCount)> GetCacheStatsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _cacheDb.GetCountsAsync(ct);
    }

    /// <summary>
    /// Forces a cache rebuild.
    /// </summary>
    public async Task InvalidateCacheAsync(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            _cacheInitialized = false;
            await _cacheDb.ClearCacheAsync(ct);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        _cacheDb.Dispose();
        await Task.CompletedTask;
    }
}
