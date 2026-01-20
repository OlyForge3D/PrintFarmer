using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Worker.Core;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Service that preloads OrcaSlicer profiles at worker startup.
/// Ensures profiles are cached in memory before the worker accepts requests.
/// </summary>
public interface IProfilePreloadService
{
    /// <summary>
    /// Preload all OrcaSlicer profiles, filtered by catalog manufacturers.
    /// This should be called before the worker registers as ready.
    /// </summary>
    /// <returns>Task that completes when preload is finished</returns>
    Task PreloadProfilesAsync(CancellationToken ct = default);
}

/// <summary>
/// Implementation of profile preload service.
/// </summary>
public class ProfilePreloadService(
    ISlicerProfilesService profileService,
    IUnifiedLoggingService logger,
    IHttpClientFactory httpClientFactory) : IProfilePreloadService
{
    private readonly ISlicerProfilesService _profileService = profileService;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task PreloadProfilesAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Starting OrcaSlicer profile preload for catalog manufacturers...");
            Stopwatch stopwatch = Stopwatch.StartNew();

            // First load all machines to get the list of available manufacturers
            Stopwatch machineStart = Stopwatch.StartNew();
            IList<MachineProfileDto> machines = await _profileService.ListAvailableMachineProfilesAsync(ct);
            machineStart.Stop();

            // Get the set of manufacturers available in OrcaSlicer profiles
            HashSet<string> availableManufacturers = machines
                .Where(m => !string.IsNullOrEmpty(m.Manufacturer))
                .Select(m => m.Manufacturer!)
                .Distinct()
                .ToHashSet();

            _logger.LogInformation($"Found {availableManufacturers.Count} manufacturers with {machines.Count} machine profiles in {machineStart.ElapsedMilliseconds}ms");

            // Load catalog manufacturers via HTTP (call the API)
            HttpClient httpClient = _httpClientFactory.CreateClient();
            string catalogUrl = Environment.GetEnvironmentVariable("CATALOG_API_URL") ?? "http://localhost:5245";

            try
            {
                HttpResponseMessage response = await httpClient.GetAsync(new Uri($"{catalogUrl}/api/catalog/manufacturers"), ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    List<ManufacturerDto>? manufacturerDtos = JsonSerializer.Deserialize<List<ManufacturerDto>>(
                        content,
                        s_jsonOptions);

                    HashSet<string> catalogManufacturers = manufacturerDtos?
                        .Select(m => m.Name)
                        .ToHashSet() ?? [];

                    _logger.LogInformation($"Catalog has {catalogManufacturers.Count} manufacturers");

                    // Load filament and process profiles only for manufacturers in catalog
                    Stopwatch filamentStart = Stopwatch.StartNew();
                    IList<FilamentProfileDto> filaments = await _profileService.ListAvailableFilamentProfilesAsync(ct).ConfigureAwait(false);
                    int catalogFilaments = filaments
                        .Count(f => string.IsNullOrEmpty(f.Manufacturer) || catalogManufacturers.Contains(f.Manufacturer));
                    filamentStart.Stop();

                    Stopwatch processStart = Stopwatch.StartNew();
                    IList<ProcessProfileDto> processes = await _profileService.ListAvailableProcessProfilesAsync(ct);
                    processStart.Stop();

                    stopwatch.Stop();

                    _logger.LogInformation($"OrcaSlicer profiles preloaded in {stopwatch.ElapsedMilliseconds}ms: {machines.Count} machines ({machineStart.ElapsedMilliseconds}ms), {catalogFilaments}/{filaments.Count} filaments for catalog ({filamentStart.ElapsedMilliseconds}ms), {processes.Count} processes ({processStart.ElapsedMilliseconds}ms)");
                }
                else
                {
                    _logger.LogWarning($"Failed to fetch catalog manufacturers: {response.StatusCode}. Skipping filtered preload.");
                    await LoadAllProfilesFallbackAsync(stopwatch, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error fetching catalog manufacturers: {ex.Message}. Loading all profiles instead.");
                await LoadAllProfilesFallbackAsync(stopwatch, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error preloading OrcaSlicer profiles: {Exception}", ex.Message);
            throw; // Re-throw so startup fails if profile preload fails
        }
    }

    private async Task LoadAllProfilesFallbackAsync(Stopwatch stopwatch, CancellationToken ct)
    {
        // Fallback: load all profiles if catalog API is unavailable
        Stopwatch filamentStart = Stopwatch.StartNew();
        IList<FilamentProfileDto> filaments = await _profileService.ListAvailableFilamentProfilesAsync(ct);
        filamentStart.Stop();

        Stopwatch processStart = Stopwatch.StartNew();
        IList<ProcessProfileDto> processes = await _profileService.ListAvailableProcessProfilesAsync(ct);
        processStart.Stop();

        stopwatch.Stop();

        _logger.LogInformation($"OrcaSlicer profiles preloaded (fallback) in {stopwatch.ElapsedMilliseconds}ms: {filaments.Count} filaments ({filamentStart.ElapsedMilliseconds}ms), {processes.Count} processes ({processStart.ElapsedMilliseconds}ms)");
    }
}
