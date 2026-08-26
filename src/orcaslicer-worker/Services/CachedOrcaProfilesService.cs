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
    private bool _databaseInitialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly AsyncReaderWriterLock _cacheAccessLock = new();
    private readonly TaskCompletionSource _cacheReadyTcs = new();

    /// <summary>
    /// Gets whether the cache is ready and the service can handle requests.
    /// This is used by RegistrationBackgroundService to delay registration until cache is populated.
    /// </summary>
    public bool IsCacheReady => Volatile.Read(ref _cacheInitialized);

    /// <summary>
    /// Task that completes when the cache is ready. Use this to await cache initialization.
    /// </summary>
    public Task CacheReadyTask => _cacheReadyTcs.Task;

    public CachedOrcaProfilesService(
        ILogger<CachedOrcaProfilesService> logger,
        string? profilesPath = null,
        string? dbPath = null,
        string? customProfilesPath = null)
    {
        _logger = logger;
        _innerService = new OrcaProfilesService(
            logger,
            profilesPath,
            customProfilesPath);

        // Determine profiles path for hash calculation
        string? envPath = Environment.GetEnvironmentVariable("ORCA_PROFILES_PATH");
        _profilesPath = profilesPath ?? envPath ?? "/opt/orcaslicer/resources/profiles";

        // Use a persistent path for the database
        string actualDbPath = dbPath ?? Path.Join(
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
        if (Volatile.Read(ref _cacheInitialized))
        {
            return;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _cacheInitialized))
            {
                return;
            }

            await using AsyncReaderWriterLock.Releaser access =
                await _cacheAccessLock.AcquireWriteAsync(ct);
            await InitializeCoreAsync(ct);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _cacheInitialized))
        {
            return;
        }

        Stopwatch sw = Stopwatch.StartNew();

        if (!_databaseInitialized)
        {
            await _cacheDb.InitializeAsync(ct);
            _databaseInitialized = true;
        }

        // Include parser/cache compatibility so expression-parser fixes invalidate stale compatibility data.
        string profilesHash = $"{CacheCompatibilityVersion}:{CalculateProfilesHash()}";

        if (await _cacheDb.IsCacheValidAsync(profilesHash, ct))
        {
            (int m, int f, int p) = await _cacheDb.GetCountsAsync(ct);
            _logger.LogInformation("SQLite cache is valid. Loaded {M} machines, {F} filaments, {P} processes in {SwElapsedMilliseconds}ms", m, f, p, sw.ElapsedMilliseconds);
            Volatile.Write(ref _cacheInitialized, true);
            _cacheReadyTcs.TrySetResult();
            return;
        }

        _logger.LogInformation("SQLite cache is stale or empty. Rebuilding from JSON profiles...");

        await _cacheDb.ClearCacheAsync(ct);

        Stopwatch parseWatch = Stopwatch.StartNew();

        IList<MachineProfileDto> machines = await _innerService.ListAvailableMachineProfilesAsync(ct);
        long machineTime = parseWatch.ElapsedMilliseconds;

        IList<FilamentProfileDto> filaments = await _innerService.ListAvailableFilamentProfilesAsync(ct);
        long filamentTime = parseWatch.ElapsedMilliseconds - machineTime;

        IList<ProcessProfileDto> processes = await _innerService.ListAvailableProcessProfilesAsync(ct);
        long processTime = parseWatch.ElapsedMilliseconds - filamentTime - machineTime;

        _logger.LogInformation("Parsed JSON profiles in {ParseWatchElapsedMilliseconds}ms: machines={MachineTime}ms, filaments={FilamentTime}ms, processes={ProcessTime}ms", parseWatch.ElapsedMilliseconds, machineTime, filamentTime, processTime);

        Stopwatch storeWatch = Stopwatch.StartNew();

        await _cacheDb.StoreMachineProfilesAsync(machines, ct);
        await _cacheDb.StoreFilamentProfilesAsync(filaments, ct);
        await _cacheDb.StoreProcessProfilesAsync(processes, ct);
        await _cacheDb.SetMetadataAsync("profiles_hash", profilesHash, ct);
        await _cacheDb.SetMetadataAsync("cached_at", DateTime.UtcNow.ToString("O"), ct);

        _logger.LogInformation("Stored {MachinesCount} machines, {FilamentsCount} filaments, {ProcessesCount} processes in SQLite in {StoreWatchElapsedMilliseconds}ms", machines.Count, filaments.Count, processes.Count, storeWatch.ElapsedMilliseconds);

        Volatile.Write(ref _cacheInitialized, true);

        _logger.LogInformation("Total cache initialization: {SwElapsedMilliseconds}ms", sw.ElapsedMilliseconds);
        _cacheReadyTcs.TrySetResult();
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
        if (!Volatile.Read(ref _cacheInitialized))
        {
            await InitializeAsync(ct);
        }
    }

    public async Task<IList<MachineModelProfileDto>> ListAvailableMachineModelProfilesAsync(CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _innerService.ListAvailableMachineModelProfilesAsync(token),
            ct);
    }

    public async Task<IList<MachineProfileDto>> ListAvailableMachineProfilesAsync(CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _cacheDb.GetMachineProfilesAsync(token),
            ct);
    }

    public async Task<IList<FilamentProfileDto>> ListAvailableFilamentProfilesAsync(CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _cacheDb.GetFilamentProfilesAsync(token),
            ct);
    }

    public async Task<IList<ProcessProfileDto>> ListAvailableProcessProfilesAsync(CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _cacheDb.GetProcessProfilesAsync(token),
            ct);
    }

    /// <summary>
    /// Gets machine profiles for a specific manufacturer (optimized query).
    /// </summary>
    public async Task<List<MachineProfileDto>> GetMachineProfilesByManufacturerAsync(string manufacturer, CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _cacheDb.GetMachineProfilesByManufacturerAsync(
                manufacturer,
                token),
            ct);
    }

    /// <summary>
    /// Gets machine profiles by printer_model only (indexed query).
    /// This is the simplest query - just match the printer_model field directly.
    /// </summary>
    public async Task<List<MachineProfileDto>> GetMachineProfilesByPrinterModelAsync(string printerModel, CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _cacheDb.GetMachineProfilesByPrinterModelAsync(
                printerModel,
                token),
            ct);
    }

    /// <summary>
    /// Gets machine profiles for a specific manufacturer and printer model (indexed query).
    /// </summary>
    public async Task<List<MachineProfileDto>> GetMachineProfilesByModelAsync(
        string manufacturer,
        string printerModel,
        CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _cacheDb.GetMachineProfilesByModelAsync(
                manufacturer,
                printerModel,
                token),
            ct);
    }

    /// <summary>
    /// Gets distinct printer models for a manufacturer.
    /// </summary>
    public async Task<List<string>> GetPrinterModelsAsync(string manufacturer, CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _cacheDb.GetPrinterModelsAsync(manufacturer, token),
            ct);
    }

    /// <summary>
    /// Gets filament profiles for a specific manufacturer (optimized query).
    /// </summary>
    public async Task<List<FilamentProfileDto>> GetFilamentProfilesByManufacturerAsync(string manufacturer, CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _cacheDb.GetFilamentProfilesByManufacturerAsync(
                manufacturer,
                token),
            ct);
    }

    /// <summary>
    /// Gets process profiles for a specific manufacturer (optimized query).
    /// </summary>
    public async Task<List<ProcessProfileDto>> GetProcessProfilesByManufacturerAsync(string manufacturer, CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _cacheDb.GetProcessProfilesByManufacturerAsync(
                manufacturer,
                token),
            ct);
    }

    /// <summary>
    /// Gets all distinct manufacturers.
    /// </summary>
    public async Task<List<string>> GetManufacturersAsync(CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _cacheDb.GetManufacturersAsync(token),
            ct);
    }

    /// <summary>
    /// Gets all distinct materials.
    /// </summary>
    public async Task<List<string>> GetMaterialsAsync(CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _cacheDb.GetMaterialsAsync(token),
            ct);
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public async Task<(int machineCount, int filamentCount, int processCount)> GetCacheStatsAsync(CancellationToken ct = default)
    {
        return await ReadCacheAsync(
            token => _cacheDb.GetCountsAsync(token),
            ct);
    }

    /// <summary>
    /// Forces a cache rebuild.
    /// </summary>
    public async Task InvalidateCacheAsync(CancellationToken ct = default)
    {
        _ = await ReloadProfilesAsync(ct);
    }

    /// <summary>
    /// Clears both SQLite and process-lifetime profile caches, then completes a
    /// full in-process rebuild before returning.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Counts and strict custom-profile load failures from the rebuilt cache.</returns>
    public async Task<ProfileReloadResult> ReloadProfilesAsync(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            await using AsyncReaderWriterLock.Releaser access =
                await _cacheAccessLock.AcquireWriteAsync(ct);
            return await ReloadProfilesCoreAsync(ct);
        }
        finally
        {
            _initLock.Release();
        }
    }

    internal async Task<(T MutationResult, ProfileReloadResult ReloadResult)>
        MutateAndReloadProfilesAsync<T>(
            Func<CancellationToken, Task<T>> mutation,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        await _initLock.WaitAsync(ct);
        try
        {
            await using AsyncReaderWriterLock.Releaser access =
                await _cacheAccessLock.AcquireWriteAsync(ct);
            T mutationResult = await mutation(ct);
            ProfileReloadResult reloadResult =
                await ReloadProfilesCoreAsync(ct);
            return (mutationResult, reloadResult);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<ProfileReloadResult> ReloadProfilesCoreAsync(
        CancellationToken ct)
    {
        Volatile.Write(ref _cacheInitialized, false);
        _innerService.ClearCaches();

        if (_databaseInitialized)
        {
            await _cacheDb.ClearCacheAsync(ct);
        }

        await InitializeCoreAsync(ct);
        (int machineCount, int filamentCount, int processCount) =
            await _cacheDb.GetCountsAsync(ct);
        return new ProfileReloadResult(
            machineCount,
            filamentCount,
            processCount,
            _innerService.CustomProfileLoadFailures);
    }

    private async Task<T> ReadCacheAsync<T>(
        Func<CancellationToken, Task<T>> read,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(read);

        while (true)
        {
            await EnsureInitializedAsync(ct);
            await using AsyncReaderWriterLock.Releaser access =
                await _cacheAccessLock.AcquireReadAsync(ct);
            if (!Volatile.Read(ref _cacheInitialized))
            {
                continue;
            }

            return await read(ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        _cacheAccessLock.Dispose();
        _cacheDb.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>
/// Result of a complete in-process profile cache rebuild.
/// </summary>
/// <param name="MachineCount">Number of selectable machine profiles.</param>
/// <param name="FilamentCount">Number of filament profiles.</param>
/// <param name="ProcessCount">Number of process profiles.</param>
/// <param name="Failures">Custom profiles excluded due to incomplete inheritance.</param>
public sealed record ProfileReloadResult(
    int MachineCount,
    int FilamentCount,
    int ProcessCount,
    IReadOnlyList<CustomProfileLoadFailure> Failures);
