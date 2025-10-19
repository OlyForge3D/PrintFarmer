using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Farm.Web.Api.Services.Interfaces;
using Farm.Infrastructure;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Farm.Web.Shared.Annotations;
using System.Text;
using System.Text.Json;

namespace Farm.Web.Api.Services.Printers
{
    public class PrintersService : IPrintersService
    {
        private readonly Farm.Infrastructure.Repositories.Printers.IPrintersRepository _repo;
        private readonly Farm.Web.Api.Services.Catalog.ICatalogService _catalogService;
        private readonly IMoonrakerClient _moon;
        private readonly IPrusaLinkClient _prusa;
        private readonly ISdcpClient _sdcp;
        private readonly IOctoPrintClient _octoprint;
        private readonly ICircuitBreakerService _circuitBreaker;
        private readonly IPrinterCapabilityDiscoveryService _capabilityDiscovery;
        private readonly IDefaultCatalogService _defaultCatalog;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Farm.Infrastructure.Telemetry.IUnifiedLoggingService _logger;
        private readonly AutoMapper.IMapper _mapper;

        public PrintersService(Farm.Infrastructure.Repositories.Printers.IPrintersRepository repo, IMoonrakerClient moon, IPrusaLinkClient prusa, ISdcpClient sdcp, IOctoPrintClient octoprint, ICircuitBreakerService circuitBreaker, IPrinterCapabilityDiscoveryService capabilityDiscovery, IDefaultCatalogService defaultCatalog, Farm.Web.Api.Services.Catalog.ICatalogService catalogService, IHttpClientFactory httpClientFactory, Farm.Infrastructure.Telemetry.IUnifiedLoggingService logger, AutoMapper.IMapper mapper)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _moon = moon ?? throw new ArgumentNullException(nameof(moon));
            _prusa = prusa ?? throw new ArgumentNullException(nameof(prusa));
            _sdcp = sdcp ?? throw new ArgumentNullException(nameof(sdcp));
            _octoprint = octoprint ?? throw new ArgumentNullException(nameof(octoprint));
            _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
            _capabilityDiscovery = capabilityDiscovery ?? throw new ArgumentNullException(nameof(capabilityDiscovery));
            _defaultCatalog = defaultCatalog ?? throw new ArgumentNullException(nameof(defaultCatalog));
            _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        }

        // History helpers moved from controller: call Moonraker client and map to shared DTOs
        public async Task<Farm.Web.Shared.HistoryListResponse> GetHistoryListAsync(Guid printerId, int? limit, int? start, DateTime? since, DateTime? before, string? order, CancellationToken ct)
        {
            Printer? printer = await FindByIdAsync(printerId, ct).ConfigureAwait(false);
            if (printer == null)
            {
                throw new KeyNotFoundException();
            }

            if (printer.Backend != (int)Farm.Web.Shared.PrinterBackend.Moonraker)
            {
                return new Farm.Web.Shared.HistoryListResponse { Count = 0, Jobs = Array.Empty<Farm.Web.Shared.HistoryJob>() };
            }

            Services.HistoryListResponse? moonrakerResponse = await _moon.GetHistoryListAsync(printer.ServerUrl, limit, start, since, before, order, ct).ConfigureAwait(false);
            if (moonrakerResponse == null)
            {
                return new Farm.Web.Shared.HistoryListResponse { Count = 0, Jobs = Array.Empty<Farm.Web.Shared.HistoryJob>() };
            }

            var jobs = moonrakerResponse.Jobs.Select(j =>
            {
                var mapped = _mapper.Map<Farm.Web.Shared.HistoryJob>(j);
                // set ThumbnailUrl using existing service helper
                mapped.ThumbnailUrl = ExtractThumbnailUrl(j.Metadata ?? new Dictionary<string, object>(), printer.ServerUrl);
                return mapped;
            }).ToArray();

            return new Farm.Web.Shared.HistoryListResponse { Count = moonrakerResponse.Count, Jobs = jobs };
        }

        public async Task<Farm.Web.Shared.HistoryJob> GetHistoryJobAsync(Guid printerId, string jobId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("Job ID is required", nameof(jobId));
            }

            Printer? printer = await FindByIdAsync(printerId, ct).ConfigureAwait(false);
            if (printer == null)
            {
                throw new KeyNotFoundException();
            }

            if (printer.Backend != (int)Farm.Web.Shared.PrinterBackend.Moonraker)
            {
                throw new InvalidOperationException("History is only available for Moonraker printers");
            }

            Services.HistoryJob? moonrakerJob = await _moon.GetHistoryJobAsync(printer.ServerUrl, jobId, ct).ConfigureAwait(false);
            if (moonrakerJob == null)
            {
                throw new KeyNotFoundException($"History job {jobId} not found");
            }

            var mapped = _mapper.Map<Farm.Web.Shared.HistoryJob>(moonrakerJob);
            mapped.ThumbnailUrl = ExtractThumbnailUrl(moonrakerJob.Metadata ?? new Dictionary<string, object>(), printer.ServerUrl);
            return mapped;
        }

        public async Task<Farm.Web.Shared.HistoryTotals> GetHistoryTotalsAsync(Guid printerId, CancellationToken ct)
        {
            Printer? printer = await FindByIdAsync(printerId, ct).ConfigureAwait(false);
            if (printer == null)
            {
                throw new KeyNotFoundException();
            }

            if (printer.Backend != (int)Farm.Web.Shared.PrinterBackend.Moonraker)
            {
                return new Farm.Web.Shared.HistoryTotals { JobTotals = new Farm.Web.Shared.JobTotals() };
            }

            Services.HistoryTotals? moonrakerTotals = await _moon.GetHistoryTotalsAsync(printer.ServerUrl, ct).ConfigureAwait(false);
            if (moonrakerTotals == null)
            {
                return new Farm.Web.Shared.HistoryTotals { JobTotals = new Farm.Web.Shared.JobTotals() };
            }

            var mapped = _mapper.Map<Farm.Web.Shared.HistoryTotals>(moonrakerTotals);
            return mapped;
        }

        public async Task<bool> DeleteHistoryJobAsync(Guid printerId, string jobId, CancellationToken ct)
        {
            Printer? printer = await FindByIdAsync(printerId, ct).ConfigureAwait(false);
            if (printer == null)
            {
                throw new KeyNotFoundException();
            }

            if (printer.Backend != (int)Farm.Web.Shared.PrinterBackend.Moonraker)
            {
                throw new InvalidOperationException("History deletion is only available for Moonraker printers");
            }

            return await _moon.DeleteHistoryJobAsync(printer.ServerUrl, jobId, ct).ConfigureAwait(false);
        }

        public async Task<List<Printer>> GetAllAsync(CancellationToken ct)
        {
            return await _repo.GetAllAsync(ct);
        }

        public async Task<List<Printer>> GetAllWithIncludesAsync(CancellationToken ct)
        {
            return await _repo.GetAllWithIncludesAsync(ct);
        }

        public async Task<Printer?> FindByIdAsync(Guid id, CancellationToken ct)
        {
            return await _repo.FindByIdAsync(id, ct);
        }

        public async Task<Printer?> FindByIdWithIncludesAsync(Guid id, CancellationToken ct)
        {
            return await _repo.FindByIdWithIncludesAsync(id, ct);
        }

        public async Task AddAsync(Printer p, CancellationToken ct)
        {
            await _repo.AddAsync(p, ct);
        }

        public async Task RemoveAsync(Printer p, CancellationToken ct)
        {
            await _repo.RemoveAsync(p, ct);
        }

        public async Task SaveChangesAsync(CancellationToken ct)
        {
            await _repo.SaveChangesAsync(ct);
        }

        public async Task<Dictionary<Guid, Farm.Infrastructure.Domain.PrinterCapabilities>> GetCapabilitiesDictionaryAsync(Guid[]? ids, CancellationToken ct)
        {
            return await _repo.GetCapabilitiesDictionaryAsync(ids, ct);
        }

        public async Task<List<Farm.Infrastructure.Domain.PrinterCapabilities>> GetCapabilitiesListAsync(Guid[]? ids, CancellationToken ct)
        {
            return await _repo.GetCapabilitiesListAsync(ids, ct);
        }

        public async Task<Farm.Infrastructure.Domain.PrinterCapabilities?> GetCapabilitiesByPrinterIdAsync(Guid id, CancellationToken ct)
        {
            return await _repo.GetCapabilitiesByPrinterIdAsync(id, ct);
        }

        // ----- Orchestration methods -----
        public async Task<Farm.Web.Shared.PrinterDto[]> GetAllWithStatusDtosAsync(CancellationToken ct)
        {
            List<Printer> items = await _repo.GetAllWithIncludesAsync(ct);

            using CancellationTokenSource fastTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fastTimeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            Farm.Web.Shared.PrinterDto[] dtos = await Task.WhenAll(items.Select(async p =>
            {
                try
                {
                    if (p.Backend == 1) // PrusaLink
                    {
                        var breaker = _circuitBreaker.GetCircuitBreaker($"prusalink-{p.Id}");
                        var status = await breaker.ExecuteAsync(async ct => await _prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct), fastTimeoutCts.Token);
                        return new Farm.Web.Shared.PrinterDto(
                            Id: p.Id,
                            Name: p.Name,
                            ServerUrl: p.ServerUrl,
                            Notes: p.Notes,
                            IsOnline: status.IsOnline,
                            State: status.State,
                            ManufacturerName: p.Manufacturer?.Name,
                            ModelName: p.Model?.Name,
                            Progress: status.Progress,
                            JobName: status.JobName,
                            ThumbnailUrl: status.ThumbnailUrl,
                            CameraStreamUrl: status.CameraStreamUrl,
                            CameraSnapshotUrl: status.CameraSnapshotUrl,
                            Backend: Farm.Web.Shared.PrinterBackend.PrusaLink,
                            ApiKey: p.ApiKey,
                            OriginalServerUrl: p.OriginalServerUrl,
                            IpAddress: p.IpAddress
                        );
                    }
                    else if (p.Backend == 2) // SDCP
                    {
                        var breaker = _circuitBreaker.GetCircuitBreaker($"sdcp-{p.Id}");
                        var status = await breaker.ExecuteAsync(async ct => await _sdcp.GetCompositeStatusAsync(p.ServerUrl, ct), fastTimeoutCts.Token);
                        return new Farm.Web.Shared.PrinterDto(
                            Id: p.Id,
                            Name: p.Name,
                            ServerUrl: p.ServerUrl,
                            Notes: p.Notes,
                            IsOnline: status.IsOnline,
                            State: status.State,
                            ManufacturerName: p.Manufacturer?.Name,
                            ModelName: p.Model?.Name,
                            Progress: status.Progress,
                            JobName: status.JobName,
                            ThumbnailUrl: status.ThumbnailUrl,
                            CameraStreamUrl: status.CameraStreamUrl,
                            CameraSnapshotUrl: status.CameraSnapshotUrl,
                            X: status.X,
                            Y: status.Y,
                            Z: status.Z,
                            HotendTemp: status.HotendTemp,
                            BedTemp: status.BedTemp,
                            HotendTarget: status.HotendTarget,
                            BedTarget: status.BedTarget,
                            Backend: Farm.Web.Shared.PrinterBackend.SDCP,
                            ApiKey: p.ApiKey,
                            OriginalServerUrl: p.OriginalServerUrl,
                            IpAddress: p.IpAddress
                        );
                    }
                    else if (p.Backend == 3) // OctoPrint
                    {
                        var breaker = _circuitBreaker.GetCircuitBreaker($"octoprint-{p.Id}");
                        string printerJson = await breaker.ExecuteAsync(async ct => await _octoprint.GetPrinterStateAsync(p.ServerUrl, p.ApiKey ?? string.Empty), fastTimeoutCts.Token);
                        string jobJson = await breaker.ExecuteAsync(async ct => await _octoprint.GetJobStatusAsync(p.ServerUrl, p.ApiKey ?? string.Empty), fastTimeoutCts.Token);
                        // plugin checks and parsing intentionally minimal here; keep parity with previous controller behavior
                        bool hasPositionPlugin = false;
                        bool hasSpoolManager = false;
                        bool hasSpoolmanPlugin = false;
                        try
                        {
                            HttpRequestMessage pluginsRequest = new(HttpMethod.Get, $"{p.ServerUrl.TrimEnd('/')}/api/plugins");
                            pluginsRequest.Headers.Add("X-Api-Key", p.ApiKey ?? string.Empty);
                            HttpResponseMessage pluginsResponse = await _octoprint.SendAsync(pluginsRequest, fastTimeoutCts.Token);
                            string pluginsJson = await pluginsResponse.Content.ReadAsStringAsync();
                            if (!string.IsNullOrWhiteSpace(pluginsJson))
                            {
                                using JsonDocument doc = JsonDocument.Parse(pluginsJson);
                                JsonElement root = doc.RootElement;
                                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("plugins", out JsonElement pluginsProp))
                                {
                                    foreach (JsonElement plugin in pluginsProp.EnumerateArray())
                                    {
                                        if (plugin.TryGetProperty("key", out JsonElement keyProp))
                                        {
                                            string? key = keyProp.GetString();
                                            if (!string.IsNullOrEmpty(key))
                                            {
                                                if (key.Equals("display_current_position", StringComparison.OrdinalIgnoreCase) || key.Equals("positioninfo", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    hasPositionPlugin = true;
                                                }
                                                if (key.Equals("spoolmanager", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    hasSpoolManager = true;
                                                }
                                                if (key.Equals("spoolman", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    hasSpoolmanPlugin = true;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }

                        // Very small parsing of printer/job JSON to extract a few fields
                        bool isOnline = false;
                        string? state = null;
                        double? hotendTemp = null;
                        double? bedTemp = null;
                        double? hotendTarget = null;
                        double? bedTarget = null;
                        double? x = null, y = null, z = null;
                        Farm.Web.Shared.PrinterSpoolInfoDto? spoolInfo = null;
                        if (!string.IsNullOrWhiteSpace(printerJson))
                        {
                            try
                            {
                                using JsonDocument doc = JsonDocument.Parse(printerJson);
                                JsonElement root = doc.RootElement;
                                if (root.TryGetProperty("state", out JsonElement stateProp))
                                {
                                    state = stateProp.GetString();
                                    isOnline = state != null && state != "Offline";
                                }
                                if (root.TryGetProperty("temperature", out JsonElement tempProp))
                                {
                                    if (tempProp.TryGetProperty("tool0", out JsonElement tool0))
                                    {
                                        if (tool0.TryGetProperty("actual", out JsonElement actual))
                                        {
                                            hotendTemp = actual.GetDouble();
                                        }
                                        if (tool0.TryGetProperty("target", out JsonElement target))
                                        {
                                            hotendTarget = target.GetDouble();
                                        }
                                    }
                                    if (tempProp.TryGetProperty("bed", out JsonElement bed))
                                    {
                                        if (bed.TryGetProperty("actual", out JsonElement actual))
                                        {
                                            bedTemp = actual.GetDouble();
                                        }
                                        if (bed.TryGetProperty("target", out JsonElement target))
                                        {
                                            bedTarget = target.GetDouble();
                                        }
                                    }
                                }

                                if (hasPositionPlugin && root.TryGetProperty("position", out JsonElement posProp))
                                {
                                    if (posProp.TryGetProperty("x", out JsonElement xProp))
                                    {
                                        x = xProp.GetDouble();
                                    }

                                    if (posProp.TryGetProperty("y", out JsonElement yProp))
                                    {
                                        y = yProp.GetDouble();
                                    }

                                    if (posProp.TryGetProperty("z", out JsonElement zProp))
                                    {
                                        z = zProp.GetDouble();
                                    }
                                }
                            }
                            catch { }
                        }

                        // Parse job minimal
                        double? progress = null;
                        string? jobName = null;
                        if (!string.IsNullOrWhiteSpace(jobJson))
                        {
                            try
                            {
                                using JsonDocument doc = JsonDocument.Parse(jobJson);
                                JsonElement root = doc.RootElement;
                                if (root.TryGetProperty("progress", out JsonElement progressProp))
                                {
                                    if (progressProp.TryGetProperty("completion", out JsonElement completion))
                                    {
                                        progress = completion.GetDouble();
                                    }
                                }
                                if (root.TryGetProperty("job", out JsonElement jobProp))
                                {
                                    if (jobProp.TryGetProperty("file", out JsonElement fileProp))
                                    {
                                        if (fileProp.TryGetProperty("name", out JsonElement nameProp))
                                        {
                                            jobName = nameProp.GetString();
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        return new Farm.Web.Shared.PrinterDto(
                            Id: p.Id,
                            Name: p.Name,
                            ServerUrl: p.ServerUrl,
                            Notes: p.Notes,
                            IsOnline: isOnline,
                            State: state,
                            ManufacturerName: p.Manufacturer?.Name,
                            ModelName: p.Model?.Name,
                            Progress: progress,
                            JobName: jobName,
                            ThumbnailUrl: null,
                            CameraStreamUrl: await _octoprint.GetCameraStreamUrlAsync(p.ServerUrl, p.ApiKey ?? string.Empty),
                            CameraSnapshotUrl: null,
                            HotendTemp: hotendTemp,
                            BedTemp: bedTemp,
                            HotendTarget: hotendTarget,
                            BedTarget: bedTarget,
                            X: x,
                            Y: y,
                            Z: z,
                            SpoolInfo: spoolInfo,
                            Backend: Farm.Web.Shared.PrinterBackend.OctoPrint,
                            ApiKey: p.ApiKey,
                            OriginalServerUrl: p.OriginalServerUrl,
                            IpAddress: p.IpAddress
                        );
                    }
                    else // Moonraker
                    {
                        var breaker = _circuitBreaker.GetCircuitBreaker($"moonraker-{p.Id}");
                        var status = await breaker.ExecuteAsync(async ct => await _moon.GetCompositeStatusAsync(p.ServerUrl, ct), fastTimeoutCts.Token);
                        var spoolInfo = await GetSpoolInfoAsync(p.ServerUrl, fastTimeoutCts.Token);
                        return new Farm.Web.Shared.PrinterDto(
                            Id: p.Id,
                            Name: p.Name,
                            ServerUrl: p.ServerUrl,
                            Notes: p.Notes,
                            IsOnline: status.IsOnline,
                            State: status.State,
                            ManufacturerName: p.Manufacturer?.Name,
                            ModelName: p.Model?.Name,
                            Progress: status.Progress,
                            JobName: status.JobName,
                            ThumbnailUrl: status.ThumbnailUrl,
                            CameraStreamUrl: status.CameraStreamUrl,
                            CameraSnapshotUrl: status.CameraSnapshotUrl,
                            X: status.X,
                            Y: status.Y,
                            Z: status.Z,
                            HotendTemp: status.HotendTemp,
                            BedTemp: status.BedTemp,
                            HotendTarget: status.HotendTarget,
                            BedTarget: status.BedTarget,
                            Backend: Farm.Web.Shared.PrinterBackend.Moonraker,
                            ApiKey: p.ApiKey,
                            OriginalServerUrl: p.OriginalServerUrl,
                            IpAddress: p.IpAddress,
                            SpoolInfo: spoolInfo
                        );
                    }
                }
                catch (OperationCanceledException) when (fastTimeoutCts.Token.IsCancellationRequested)
                {
                    _logger.LogWarning($"Fast timeout occurred for printer {p.Name} ({p.Id})");
                    return CreateOfflinePrinterDto(p);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error getting status for printer {p.Name} ({p.Id}): {ex.Message}");
                    return CreateOfflinePrinterDto(p);
                }
            }));

            return dtos;
        }

        public async Task<Farm.Web.Shared.PrinterStatusDto> GetStatusDtoAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await _repo.FindByIdAsync(id, ct);
            if (p is null)
            {
                throw new KeyNotFoundException();
            }

            using CancellationTokenSource statusCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            statusCts.CancelAfter(TimeSpan.FromSeconds(3));

            try
            {
                if (p.Backend == 1)
                {
                    var breaker = _circuitBreaker.GetCircuitBreaker($"prusalink-{p.Id}");
                    var status = await breaker.ExecuteAsync(async ct => await _prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct), statusCts.Token);
                    return new Farm.Web.Shared.PrinterStatusDto(Id: p.Id, IsOnline: status.IsOnline, State: status.State, Progress: status.Progress, JobName: status.JobName, ThumbnailUrl: status.ThumbnailUrl, CameraStreamUrl: status.CameraStreamUrl, CameraSnapshotUrl: status.CameraSnapshotUrl);
                }
                else if (p.Backend == 2)
                {
                    var breaker = _circuitBreaker.GetCircuitBreaker($"sdcp-{p.Id}");
                    var status = await breaker.ExecuteAsync(async ct => await _sdcp.GetCompositeStatusAsync(p.ServerUrl, ct), statusCts.Token);
                    return new Farm.Web.Shared.PrinterStatusDto(Id: p.Id, IsOnline: status.IsOnline, State: status.State, Progress: status.Progress, JobName: status.JobName, ThumbnailUrl: status.ThumbnailUrl, CameraStreamUrl: status.CameraStreamUrl, CameraSnapshotUrl: status.CameraSnapshotUrl, X: status.X, Y: status.Y, Z: status.Z, HotendTemp: status.HotendTemp, BedTemp: status.BedTemp, HotendTarget: status.HotendTarget, BedTarget: status.BedTarget);
                }
                else
                {
                    var breaker = _circuitBreaker.GetCircuitBreaker($"moonraker-{p.Id}");
                    var status = await breaker.ExecuteAsync(async ct => await _moon.GetCompositeStatusAsync(p.ServerUrl, ct), statusCts.Token);
                    var spoolInfo = await GetSpoolInfoAsync(p.ServerUrl, statusCts.Token);
                    return new Farm.Web.Shared.PrinterStatusDto(Id: p.Id, IsOnline: status.IsOnline, State: status.State, Progress: status.Progress, JobName: status.JobName, ThumbnailUrl: status.ThumbnailUrl, CameraStreamUrl: status.CameraStreamUrl, CameraSnapshotUrl: status.CameraSnapshotUrl, X: status.X, Y: status.Y, Z: status.Z, HotendTemp: status.HotendTemp, BedTemp: status.BedTemp, HotendTarget: status.HotendTarget, BedTarget: status.BedTarget, SpoolInfo: spoolInfo);
                }
            }
            catch (OperationCanceledException) when (statusCts.Token.IsCancellationRequested)
            {
                _logger.LogWarning($"Status timeout for printer {p.Id}");
                return new Farm.Web.Shared.PrinterStatusDto(Id: p.Id, IsOnline: false, State: null, Progress: null, JobName: null, ThumbnailUrl: null, CameraStreamUrl: null, CameraSnapshotUrl: null, SpoolInfo: null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error getting status for printer {p.Id}: {ex.Message}");
                return new Farm.Web.Shared.PrinterStatusDto(Id: p.Id, IsOnline: false, State: null, Progress: null, JobName: null, ThumbnailUrl: null, CameraStreamUrl: null, CameraSnapshotUrl: null, SpoolInfo: null);
            }
        }

        public async Task<Farm.Web.Shared.PrinterDto> GetPrinterDtoAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await _repo.FindByIdWithIncludesAsync(id, ct);
            if (p is null)
            {
                throw new KeyNotFoundException();
            }

            if (p.Backend == 1)
            {
                var status = await _prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct);
                return new Farm.Web.Shared.PrinterDto(Id: p.Id, Name: p.Name, ServerUrl: p.ServerUrl, Notes: p.Notes, IsOnline: status.IsOnline, State: status.State, ManufacturerName: p.Manufacturer?.Name, ModelName: p.Model?.Name, Progress: status.Progress, JobName: status.JobName, ThumbnailUrl: status.ThumbnailUrl, CameraStreamUrl: status.CameraStreamUrl, CameraSnapshotUrl: status.CameraSnapshotUrl, Backend: Farm.Web.Shared.PrinterBackend.PrusaLink, ApiKey: p.ApiKey, OriginalServerUrl: p.OriginalServerUrl, IpAddress: p.IpAddress);
            }
            else if (p.Backend == 2)
            {
                var status = await _sdcp.GetCompositeStatusAsync(p.ServerUrl, ct);
                return new Farm.Web.Shared.PrinterDto(Id: p.Id, Name: p.Name, ServerUrl: p.ServerUrl, Notes: p.Notes, IsOnline: status.IsOnline, State: status.State, ManufacturerName: p.Manufacturer?.Name, ModelName: p.Model?.Name, Progress: status.Progress, JobName: status.JobName, ThumbnailUrl: status.ThumbnailUrl, CameraStreamUrl: status.CameraStreamUrl, CameraSnapshotUrl: status.CameraSnapshotUrl, X: status.X, Y: status.Y, Z: status.Z, HotendTemp: status.HotendTemp, BedTemp: status.BedTemp, HotendTarget: status.HotendTarget, BedTarget: status.BedTarget, Backend: Farm.Web.Shared.PrinterBackend.SDCP, ApiKey: p.ApiKey, OriginalServerUrl: p.OriginalServerUrl, IpAddress: p.IpAddress);
            }
            else
            {
                var status = await _moon.GetCompositeStatusAsync(p.ServerUrl, ct);
                var spoolInfo = await GetSpoolInfoAsync(p.ServerUrl, ct);
                return new Farm.Web.Shared.PrinterDto(Id: p.Id, Name: p.Name, ServerUrl: p.ServerUrl, Notes: p.Notes, IsOnline: status.IsOnline, State: status.State, ManufacturerName: p.Manufacturer?.Name, ModelName: p.Model?.Name, Progress: status.Progress, JobName: status.JobName, ThumbnailUrl: status.ThumbnailUrl, CameraStreamUrl: status.CameraStreamUrl, CameraSnapshotUrl: status.CameraSnapshotUrl, X: status.X, Y: status.Y, Z: status.Z, HotendTemp: status.HotendTemp, BedTemp: status.BedTemp, HotendTarget: status.HotendTarget, BedTarget: status.BedTarget, Backend: Farm.Web.Shared.PrinterBackend.Moonraker, ApiKey: p.ApiKey, OriginalServerUrl: p.OriginalServerUrl, IpAddress: p.IpAddress, SpoolInfo: spoolInfo);
            }
        }

        public async Task<Farm.Web.Shared.PrinterCameraUrlsDto[]> GetCameraUrlsAsync(CancellationToken ct)
        {
            List<Printer> items = await _repo.GetAllAsync(ct);
            Farm.Web.Shared.PrinterCameraUrlsDto[] dtos = await Task.WhenAll(items.Select(async p =>
            {
                string? streamUrl = null;
                string? snapshotUrl = null;
                if (await IsCameraAvailableAsync(p.ServerUrl, p.Backend, ct))
                {
                    streamUrl = GenerateStaticCameraStreamUrl(p.ServerUrl, p.Backend);
                    snapshotUrl = GenerateStaticCameraSnapshotUrl(p.ServerUrl, p.Backend);
                }
                return new Farm.Web.Shared.PrinterCameraUrlsDto(Id: p.Id, Name: p.Name, CameraStreamUrl: streamUrl, CameraSnapshotUrl: snapshotUrl);
            }));
            return dtos;
        }

        public async Task SaveCapabilitiesAsync(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, CancellationToken ct)
        {
            await _repo.SaveCapabilitiesAsync(capabilities, ct);
        }

        public async Task<List<Printer>> GetPrintersForExportAsync(Guid[]? ids, CancellationToken ct)
        {
            return await _repo.GetPrintersForExportAsync(ids, ct);
        }

        public async Task<bool> ExistsByNameOrServerUrlAsync(string name, string serverUrl, CancellationToken ct)
        {
            return await _repo.ExistsByNameOrServerUrlAsync(name, serverUrl, ct);
        }

        public async Task<Farm.Web.Shared.PrinterFastDto[]> GetAllFastDtosAsync(CancellationToken ct)
        {
            List<Printer> items = await _repo.GetAllWithIncludesAsync(ct);
            return items.Select(p => new Farm.Web.Shared.PrinterFastDto(Id: p.Id, Name: p.Name, ServerUrl: p.ServerUrl, Notes: p.Notes, IsOnline: false, State: null, ManufacturerName: p.Manufacturer?.Name, ModelName: p.Model?.Name, Backend: p.Backend == 1 ? Farm.Web.Shared.PrinterBackend.PrusaLink : p.Backend == 2 ? Farm.Web.Shared.PrinterBackend.SDCP : Farm.Web.Shared.PrinterBackend.Moonraker, ApiKey: p.ApiKey, OriginalServerUrl: p.OriginalServerUrl, IpAddress: p.IpAddress)).ToArray();
        }

        private static readonly JsonSerializerOptions _exportJsonOptions = new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new Farm.Web.Api.Serialization.ImportExportTypeInfoResolver(),
        };

        public async Task<byte[]> BuildExportCsvAsync(Guid[]? ids, CancellationToken ct)
        {
            List<Printer> printers = await GetPrintersForExportAsync(ids, ct);
            Dictionary<Guid, Farm.Infrastructure.Domain.PrinterCapabilities> capabilities = await GetCapabilitiesDictionaryAsync(ids, ct);

            // build header
            List<string> headerParts = new() { "Name", "ServerUrl", "OriginalServerUrl", "Notes", "Manufacturer", "Model", "Backend", "ApiKey", "DateAcquired" };
            BuildCsvHeaderAndCapProps(ref headerParts, out List<string> capPropsForCsv, out List<System.Reflection.PropertyInfo> capPropInfos);

            StringBuilder csv = new();
            csv.AppendLine(string.Join(',', headerParts));

            foreach (var printer in printers)
            {
                csv.AppendLine($"{EscapeCsvValue(printer.Name)},{EscapeCsvValue(printer.ServerUrl)},{EscapeCsvValue(printer.OriginalServerUrl)},{EscapeCsvValue(printer.Notes)},{EscapeCsvValue(printer.Manufacturer?.Name)},{EscapeCsvValue(printer.Model?.Name)},{EscapeCsvValue(printer.Backend.ToString())},{EscapeCsvValue(printer.ApiKey)},{EscapeCsvValue(printer.DateAcquired?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))}");
                // capability row will be added via WriteCsvRowAsync when streaming; for BuildExportCsvAsync we'll append capability columns inline
                Farm.Infrastructure.Domain.PrinterCapabilities? cap = capabilities.TryGetValue(printer.Id, out var c) ? c : null;
                // append capability columns
                foreach (var prop in capPropInfos)
                {
                    object? val = cap == null ? null : prop.GetValue(cap);
                    csv.Append($",{EscapeCsvValue(val?.ToString())}");
                }
                csv.AppendLine();
            }

            return System.Text.Encoding.UTF8.GetBytes(csv.ToString());
        }

        public async Task StreamExportToResponseAsync(Guid[]? ids, string format, HttpResponse response, CancellationToken ct)
        {
            List<Printer> printers = await GetPrintersForExportAsync(ids, ct);
            Dictionary<Guid, Farm.Infrastructure.Domain.PrinterCapabilities> capabilities = await GetCapabilitiesDictionaryAsync(ids, ct);

            IQueryable<Printer> query = printers.AsQueryable();

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                await StreamJsonExportAsync(query, capabilities, response, ct);
                return;
            }

            // CSV
            response.ContentType = "text/csv";
            string filename = $"printers-export-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.csv";
            response.Headers["Content-Disposition"] = $"attachment; filename={filename}";

            List<string> headerParts = new() { "Name", "ServerUrl", "OriginalServerUrl", "Notes", "Manufacturer", "Model", "Backend", "ApiKey", "DateAcquired" };
            BuildCsvHeaderAndCapProps(ref headerParts, out List<string> capPropsForCsv, out List<System.Reflection.PropertyInfo> capPropInfos);

            await using var writer = new System.IO.StreamWriter(response.Body, System.Text.Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(string.Join(',', headerParts));

            foreach (var p in query)
            {
                Farm.Infrastructure.Domain.PrinterCapabilities? cap = capabilities.TryGetValue(p.Id, out var c) ? c : null;
                string baseLine = $"{EscapeCsvValue(p.Name)},{EscapeCsvValue(p.ServerUrl)},{EscapeCsvValue(p.OriginalServerUrl)},{EscapeCsvValue(p.Notes)},{EscapeCsvValue(p.Manufacturer?.Name)},{EscapeCsvValue(p.Model?.Name)},{EscapeCsvValue(p.Backend.ToString())},{EscapeCsvValue(p.ApiKey)},{EscapeCsvValue(p.DateAcquired?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))}";
                await writer.WriteAsync(baseLine);
                foreach (var prop in capPropInfos)
                {
                    object? val = cap == null ? null : prop.GetValue(cap);
                    await writer.WriteAsync("," + EscapeCsvValue(val?.ToString()));
                }
                await writer.WriteLineAsync();
                await writer.FlushAsync();
            }
        }

        public async Task<Farm.Web.Shared.PrinterWithCapabilitiesDto[]> GetPrintersWithCapabilitiesDtosAsync(Guid[]? ids, CancellationToken ct)
        {
            List<Printer> printers = await GetPrintersForExportAsync(ids, ct);
            List<Farm.Infrastructure.Domain.PrinterCapabilities> capabilities = await GetCapabilitiesListAsync(ids, ct);

            var results = printers.Select(p =>
            {
                var cap = capabilities.Find(c => c.PrinterId == p.Id);
                return new Farm.Web.Shared.PrinterWithCapabilitiesDto
                {
                    PrinterId = p.Id,
                    PrinterName = p.Name,
                    PrinterModel = p.Model != null ? p.Model.Name ?? string.Empty : string.Empty,
                    ManufacturerName = p.Manufacturer != null ? p.Manufacturer.Name : null,
                    Backend = p.Backend == 1 ? Farm.Web.Shared.PrinterBackend.PrusaLink : p.Backend == 2 ? Farm.Web.Shared.PrinterBackend.SDCP : Farm.Web.Shared.PrinterBackend.Moonraker,
                    IpAddress = p.IpAddress,
                    Capabilities = cap == null ? null : new Farm.Web.Shared.PrinterCapabilitiesDto(
                        cap.Id,
                        cap.PrinterId,
                        p.Name,
                        cap.NozzleDiameter,
                        cap.SupportedMaterials,
                        cap.MaxBuildVolumeX,
                        cap.MaxBuildVolumeY,
                        cap.MaxBuildVolumeZ,
                        cap.HasHeatedBed,
                        cap.HasEnclosure,
                        cap.MultiMaterial,
                        cap.SupportsAutoLeveling,
                        cap.NumberOfExtruders,
                        cap.MinHotendTemp,
                        cap.MaxHotendTemp,
                        cap.MinBedTemp,
                        cap.MaxBedTemp,
                        cap.CurrentMaterial,
                        cap.CurrentSpoolId,
                        cap.IsAvailable,
                        cap.LastUpdated
                    )
                };
            }).ToArray();

            return results;
        }

        private static string EscapeCsvValue(string? value)
        {
            if (value is null)
            {
                return string.Empty;
            }

            string raw = value.Replace("\r", "").Replace("\n", " ");
            if (raw.Contains(',') || raw.Contains('"') || raw.Contains('\n'))
            {
                return '"' + raw.Replace("\"", "\"\"") + '"';
            }
            return raw;
        }

        private static void BuildCsvHeaderAndCapProps(ref List<string> headerParts, out List<string> capPropsForCsv, out List<System.Reflection.PropertyInfo> capPropInfos)
        {
            Type capType = typeof(Farm.Infrastructure.Domain.PrinterCapabilities);
            var resolver = _exportJsonOptions?.TypeInfoResolver;
            List<System.Reflection.PropertyInfo> props = capType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(pi => !PropertySuppressedForExport(pi))
                .ToList();

            capPropsForCsv = props.Select(pi => pi.Name).ToList();
            capPropInfos = props;
            headerParts.AddRange(capPropsForCsv);
        }

        private static bool PropertySuppressedForExport(System.Reflection.PropertyInfo? pi)
        {
            if (pi == null)
            {
                return false;
            }

            ImportExportAttribute? attr = pi.GetCustomAttributes(typeof(ImportExportAttribute), inherit: true).FirstOrDefault() as ImportExportAttribute;
            return attr != null && (attr.IgnoreFor & ImportExportTargets.Export) != 0;
        }

        private async Task StreamJsonExportAsync(IQueryable<Printer> query, Dictionary<Guid, Farm.Infrastructure.Domain.PrinterCapabilities> capabilities, HttpResponse response, CancellationToken ct)
        {
            response.ContentType = "application/json";
            string filename = $"printers-export-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.json";
            response.Headers["Content-Disposition"] = $"attachment; filename={filename}";

            await using var writer = new System.IO.StreamWriter(response.Body, System.Text.Encoding.UTF8, leaveOpen: true);
            await writer.WriteAsync("[");
            bool first = true;
            await foreach (var p in query.AsAsyncEnumerable().WithCancellation(ct))
            {
                if (!first)
                {
                    await writer.WriteAsync(",");
                }

                first = false;
                capabilities.TryGetValue(p.Id, out var cap);
                Dictionary<string, object?> dtoDict = BuildExportPrinterDictionary(p, (Farm.Infrastructure.Domain.PrinterCapabilities?)cap);
                string json = System.Text.Json.JsonSerializer.Serialize(dtoDict, _exportJsonOptions);
                await writer.WriteAsync(json);
                await writer.FlushAsync();
            }
            await writer.WriteAsync("]");
            await writer.FlushAsync();
        }

        private static Dictionary<string, object?> BuildExportPrinterDictionary(Printer p, Farm.Infrastructure.Domain.PrinterCapabilities? cap)
        {
            var dict = new Dictionary<string, object?>
            {
                ["Id"] = p.Id,
                ["Name"] = p.Name,
                ["ServerUrl"] = p.ServerUrl,
                ["OriginalServerUrl"] = p.OriginalServerUrl,
                ["Notes"] = p.Notes,
                ["Manufacturer"] = p.Manufacturer?.Name,
                ["Model"] = p.Model?.Name,
                ["Backend"] = p.Backend,
                ["ApiKey"] = p.ApiKey,
                ["DateAcquired"] = p.DateAcquired
            };

            if (cap != null)
            {
                foreach (var prop in typeof(Farm.Infrastructure.Domain.PrinterCapabilities).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (PropertySuppressedForExport(prop))
                    {
                        continue;
                    }

                    dict[prop.Name] = prop.GetValue(cap);
                }
            }

            return dict;
        }

        private async Task<bool> IsCameraAvailableAsync(string serverUrl, int backend, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                return false;
            }

            try
            {
                string? snapshotUrl = GenerateStaticCameraSnapshotUrl(serverUrl, backend);
                if (string.IsNullOrWhiteSpace(snapshotUrl))
                {
                    return false;
                }

                HttpClient httpClient = _httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(2);
                using HttpRequestMessage request = new(HttpMethod.Head, snapshotUrl);
                using HttpResponseMessage response = await httpClient.SendAsync(request, ct);
                return response.StatusCode < System.Net.HttpStatusCode.InternalServerError;
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Camera availability check failed for printer {serverUrl} (backend {backend}): {ex.Message}");
                return false;
            }
        }

        private static string? GenerateStaticCameraStreamUrl(string serverUrl, int backend)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                return null;
            }

            try
            {
                Uri baseUri = new(serverUrl);
                return backend switch
                {
                    0 => new Uri(baseUri, "/webcam/?action=stream").ToString(),
                    1 => new Uri(baseUri, "/webcam/?action=stream").ToString(),
                    2 => new Uri(baseUri, "/camera/stream").ToString(),
                    _ => new Uri(baseUri, "/webcam/?action=stream").ToString()
                };
            }
            catch { return null; }
        }

        private static string? GenerateStaticCameraSnapshotUrl(string serverUrl, int backend)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                return null;
            }

            try
            {
                Uri baseUri = new(serverUrl);
                return backend switch
                {
                    0 => new Uri(baseUri, "/webcam/?action=snapshot").ToString(),
                    1 => new Uri(baseUri, "/webcam/?action=snapshot").ToString(),
                    2 => new Uri(baseUri, "/camera/snapshot").ToString(),
                    _ => new Uri(baseUri, "/webcam/?action=snapshot").ToString()
                };
            }
            catch { return null; }
        }

        private static Farm.Web.Shared.PrinterDto CreateOfflinePrinterDto(Printer p)
        {
            return new Farm.Web.Shared.PrinterDto(
                Id: p.Id,
                Name: p.Name,
                ServerUrl: p.ServerUrl,
                Notes: p.Notes,
                IsOnline: false,
                State: null,
                ManufacturerName: p.Manufacturer?.Name,
                ModelName: p.Model?.Name,
                Backend: p.Backend == 1 ? Farm.Web.Shared.PrinterBackend.PrusaLink : p.Backend == 2 ? Farm.Web.Shared.PrinterBackend.SDCP : Farm.Web.Shared.PrinterBackend.Moonraker,
                ApiKey: p.ApiKey,
                OriginalServerUrl: p.OriginalServerUrl,
                IpAddress: p.IpAddress
            );
        }

        // Reuse controller's GetSpoolInfoAsync logic adapted for service
        private async Task<Farm.Web.Shared.PrinterSpoolInfoDto?> GetSpoolInfoAsync(string serverUrl, CancellationToken ct)
        {
            try
            {
                int? activeSpoolId = await _moon.GetSpoolmanActiveSpoolAsync(serverUrl, ct);
                if (activeSpoolId == null)
                {
                    return new Farm.Web.Shared.PrinterSpoolInfoDto(HasActiveSpool: false);
                }

                string? spoolDetailsJson = await _moon.GetSpoolmanSpoolByIdAsync(serverUrl, activeSpoolId.Value, ct);
                if (string.IsNullOrWhiteSpace(spoolDetailsJson))
                {
                    return new Farm.Web.Shared.PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: activeSpoolId);
                }

                try
                {
                    using JsonDocument doc = System.Text.Json.JsonDocument.Parse(spoolDetailsJson);
                    JsonElement root = doc.RootElement;
                    string? spoolName = root.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
                    string? material = root.TryGetProperty("material", out JsonElement matEl) ? matEl.GetString() : null;
                    string? colorHex = root.TryGetProperty("color_hex", out JsonElement colorEl) ? colorEl.GetString() : null;
                    double? remainingWeight = root.TryGetProperty("remaining_weight", out JsonElement weightEl) && weightEl.ValueKind == JsonValueKind.Number ? weightEl.GetDouble() : (double?)null;
                    string? filamentName = null;
                    string? vendor = null;
                    if (root.TryGetProperty("filament", out JsonElement filamentEl) && filamentEl.ValueKind == JsonValueKind.Object)
                    {
                        filamentName = filamentEl.TryGetProperty("name", out JsonElement fnameEl) ? fnameEl.GetString() : null;
                        if (filamentEl.TryGetProperty("vendor", out JsonElement vendorEl) && vendorEl.ValueKind == JsonValueKind.Object)
                        {
                            vendor = vendorEl.TryGetProperty("name", out JsonElement vNameEl) ? vNameEl.GetString() : null;
                        }
                    }

                    return new Farm.Web.Shared.PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: activeSpoolId, SpoolName: spoolName, Material: material, ColorHex: colorHex, FilamentName: filamentName, Vendor: vendor, RemainingWeightG: remainingWeight, SpoolInUse: true);
                }
                catch
                {
                    return new Farm.Web.Shared.PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: activeSpoolId);
                }
            }
            catch
            {
                return new Farm.Web.Shared.PrinterSpoolInfoDto(HasActiveSpool: false);
            }
        }

        public async Task<Farm.Web.Shared.PrinterDto> CreatePrinterFromDtoAsync(Farm.Web.Shared.CreatePrinterDto dto, CancellationToken ct)
        {
            // resolve or create manufacturer/model
            Guid manufacturerId = dto.ManufacturerId ?? Guid.Empty;
            if (manufacturerId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
            {
                string name = dto.NewManufacturerName!.Trim();
                var created = await _catalogService.CreateManufacturerAsync(name, ct).ConfigureAwait(false);
                manufacturerId = created.Id;
            }

            Guid modelId = dto.ModelId ?? Guid.Empty;
            if (modelId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewModelName) && manufacturerId != Guid.Empty)
            {
                string mname = dto.NewModelName!.Trim();
                var createReq = new Farm.Web.Api.Controllers.Requests.CreateModelRequest(
                    ManufacturerId: manufacturerId,
                    Name: mname,
                    Type: null,
                    MaxX: null,
                    MaxY: null,
                    MaxZ: null,
                    DefaultBackend: null,
                    SupportedFilamentTypeIds: null);
                var createdModel = await _catalogService.CreateModelAsync(createReq, ct).ConfigureAwait(false);
                modelId = createdModel.Id;
            }

            if (manufacturerId == Guid.Empty || modelId == Guid.Empty)
            {
                (Guid defaultManufacturerId, Guid defaultModelId) = await _defaultCatalog.GetDefaultCatalogIdsAsync().ConfigureAwait(false);
                if (manufacturerId == Guid.Empty)
                {
                    manufacturerId = defaultManufacturerId;
                }

                if (modelId == Guid.Empty)
                {
                    modelId = defaultModelId;
                }
            }

            int defaultPort = dto.Backend == Farm.Web.Shared.PrinterBackend.PrusaLink ? 80 : dto.Backend == Farm.Web.Shared.PrinterBackend.SDCP ? 80 : 7125;
            string normalizedInput = NormalizeServerUrl(dto.ServerUrl, defaultPort);
            string resolvedBase = normalizedInput;
            string? resolvedIp = null;
            try
            {
                Uri uri = new(normalizedInput);
                if (!System.Net.IPAddress.TryParse(uri.Host, out _))
                {
                    string hostToResolve = EnsureLocalSuffix(uri.Host);
                    System.Net.IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(hostToResolve, ct).ConfigureAwait(false);
                    System.Net.IPAddress? firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? (addresses.Length > 0 ? addresses[0] : null);
                    if (firstIp is not null)
                    {
                        UriBuilder ub = new(uri) { Host = firstIp.ToString() };
                        resolvedBase = ub.Uri.ToString().TrimEnd('/');
                        resolvedIp = firstIp.ToString();
                    }
                }
                else
                {
                    resolvedIp = uri.Host;
                }
            }
            catch { }

            Printer p = new()
            {
                Id = Guid.NewGuid(),
                Name = dto.Name,
                ServerUrl = resolvedBase,
                OriginalServerUrl = normalizedInput,
                IpAddress = resolvedIp,
                Notes = dto.Notes,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
                DateAcquired = dto.DateAcquired?.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dto.DateAcquired.Value, DateTimeKind.Utc) : dto.DateAcquired,
                Backend = (int)dto.Backend,
                ApiKey = dto.ApiKey
            };

            await AddAsync(p, ct).ConfigureAwait(false);

            try
            {
                Printer? printerForDiscovery = await FindByIdWithIncludesAsync(p.Id, ct).ConfigureAwait(false);
                if (printerForDiscovery != null)
                {
                    Farm.Infrastructure.Domain.PrinterCapabilities? discoveredCapabilities = await _capabilityDiscovery.DiscoverCapabilitiesAsync(printerForDiscovery, ct).ConfigureAwait(false);
                    if (discoveredCapabilities == null)
                    {
                        _logger.LogDebug($"Could not discover capabilities for imported printer: {p.Name} ({p.Id})");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Error during capability discovery for imported printer: {p.Name} ({p.Id}) - {ex.Message}");
            }

            return new Farm.Web.Shared.PrinterDto(
                Id: p.Id,
                Name: p.Name,
                ServerUrl: p.ServerUrl,
                Notes: p.Notes,
                IsOnline: false,
                State: null,
                ManufacturerName: null,
                ModelName: null,
                Backend: (Farm.Web.Shared.PrinterBackend)p.Backend,
                ApiKey: p.ApiKey,
                OriginalServerUrl: p.OriginalServerUrl,
                IpAddress: p.IpAddress
            );
        }

        // High-level operations moved from controller
        public async Task<byte[]?> GetCameraSnapshotAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return null;
            }
            // Moonraker client exposes a direct snapshot method
            if (p.Backend == 0)
            {
                return await _moon.GetCameraSnapshotAsync(p.ServerUrl, ct).ConfigureAwait(false);
            }

            // SDCP exposes snapshot URL; fetch bytes via HTTP
            if (p.Backend == 2)
            {
                string? snapshotUrl = await _sdcp.GetCameraSnapshotUrlAsync(p.ServerUrl, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(snapshotUrl))
                {
                    return null;
                }

                return await FetchBytesFromUrlAsync(snapshotUrl, null, ct).ConfigureAwait(false);
            }

            // PrusaLink does not provide a direct snapshot API in the client; try a static snapshot URL and fetch via HTTP
            string? prusaSnapshot = GenerateStaticCameraSnapshotUrl(p.ServerUrl, p.Backend);
            if (string.IsNullOrWhiteSpace(prusaSnapshot))
            {
                return null;
            }

            return await FetchBytesFromUrlAsync(prusaSnapshot, p.ApiKey, ct).ConfigureAwait(false);
        }

        private async Task<byte[]?> FetchBytesFromUrlAsync(string url, string? apiKey, CancellationToken ct)
        {
            try
            {
                HttpClient client = _httpClientFactory.CreateClient();
                using HttpRequestMessage req = new(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    // Typical servers expect the API key in X-Api-Key header
                    req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                }

                using HttpResponseMessage resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }

                return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Failed to fetch snapshot from {url}: {ex.Message}");
                return null;
            }
        }

        public async Task<(string? streamUrl, string? snapshotUrl)> GetCameraUrlsForPrinterAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return (null, null);
            }

            if (p.Backend == 2)
            {
                string? streamUrl = await _sdcp.GetCameraUrlAsync(p.ServerUrl, ct).ConfigureAwait(false);
                string? snapshotUrl = await _sdcp.GetCameraSnapshotUrlAsync(p.ServerUrl, ct).ConfigureAwait(false);
                return (streamUrl, snapshotUrl);
            }
            // For other backends, generate static urls
            return (GenerateStaticCameraStreamUrl(p.ServerUrl, p.Backend), GenerateStaticCameraSnapshotUrl(p.ServerUrl, p.Backend));
        }

        public async Task<bool> SendHomeAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            return await _moon.SendHomeAsync(p.ServerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> HomeXYAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            return await _moon.HomeXYAsync(p.ServerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> HomeZAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            return await _moon.HomeZAsync(p.ServerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> SetTempsAsync(Guid id, double? hotend, double? bed, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            return await _moon.SetTempsAsync(p.ServerUrl, hotend, bed, ct).ConfigureAwait(false);
        }

        public async Task<bool> MoveAsync(Guid id, double? x, double? y, double? z, double? f, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            return await _moon.MoveAsync(p.ServerUrl, x, y, z, f, ct).ConfigureAwait(false);
        }

        public async Task<bool> MoveToAsync(Guid id, double? x, double? y, double? z, double? f, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            return await _moon.MoveToAsync(p.ServerUrl, x, y, z, f, ct).ConfigureAwait(false);
        }

        public async Task<bool> PauseAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            return p.Backend == 2 ? await _sdcp.PausePrintAsync(p.ServerUrl, ct).ConfigureAwait(false) : await _moon.PauseAsync(p.ServerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> ResumeAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            return p.Backend == 2 ? await _sdcp.ResumePrintAsync(p.ServerUrl, ct).ConfigureAwait(false) : await _moon.ResumeAsync(p.ServerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> EmergencyStopAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            return p.Backend == 2 ? await _sdcp.CancelPrintAsync(p.ServerUrl, ct).ConfigureAwait(false) : await _moon.EmergencyStopAsync(p.ServerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> FirmwareRestartAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend != 0)
            {
                return false; // only moonraker
            }

            return await _moon.FirmwareRestartAsync(p.ServerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> StartPrintFromFileAsync(Guid id, string filename, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == 2)
            {
                return await _sdcp.StartPrintAsync(p.ServerUrl, filename, ct).ConfigureAwait(false);
            }
            return false;
        }

        public async Task<bool> StartPrintAsync(Guid id, string filename, CancellationToken ct)
        {
            // alias for StartPrintFromFileAsync - keep compatibility
            return await StartPrintFromFileAsync(id, filename, ct).ConfigureAwait(false);
        }

        public async Task<bool> EnableCameraAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == 2)
            {
                return await _sdcp.EnableCameraAsync(p.ServerUrl, ct).ConfigureAwait(false);
            }
            return false;
        }

        public async Task<bool> DisableCameraAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == 2)
            {
                return await _sdcp.DisableCameraAsync(p.ServerUrl, ct).ConfigureAwait(false);
            }
            return false;
        }

        public async Task<bool> UploadGcodeAsync(Guid id, string filename, System.IO.Stream stream, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            return p.Backend switch
            {
                0 => await _moon.UploadGcodeAsync(p.ServerUrl, filename, stream, ct).ConfigureAwait(false),
                1 => await _prusa.UploadGcodeAsync(p.ServerUrl, filename, stream, p.ApiKey, ct).ConfigureAwait(false),
                2 => await _sdcp.UploadGcodeAsync(p.ServerUrl, filename, stream, ct).ConfigureAwait(false),
                _ => false
            };
        }

        public async Task<string[]> GetFileListAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return Array.Empty<string>();
            }

            return p.Backend switch
            {
                0 => await _moon.GetFileListAsync(p.ServerUrl, ct).ConfigureAwait(false),
                1 => await _prusa.GetFileListAsync(p.ServerUrl, p.ApiKey, ct).ConfigureAwait(false),
                2 => await _sdcp.GetFileListAsync(p.ServerUrl, ct).ConfigureAwait(false),
                _ => Array.Empty<string>()
            };
        }

        public async Task<Farm.Web.Shared.ResolveHostnameResponse> ResolveHostnameAsync(string serverUrl, Farm.Web.Shared.PrinterBackend backend, CancellationToken ct)
        {
            int defaultPort = backend == Farm.Web.Shared.PrinterBackend.PrusaLink ? 80 : backend == Farm.Web.Shared.PrinterBackend.SDCP ? 80 : 7125;
            string normalized = NormalizeServerUrl(serverUrl, defaultPort);
            string? resolvedIp = null;
            string resolvedBase = normalized;
            try
            {
                Uri uri = new(normalized);
                if (!System.Net.IPAddress.TryParse(uri.Host, out _))
                {
                    string hostToResolve = EnsureLocalSuffix(uri.Host);
                    System.Net.IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(hostToResolve, ct).ConfigureAwait(false);
                    System.Net.IPAddress? firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? (addresses.Length > 0 ? addresses[0] : null);
                    if (firstIp is not null)
                    {
                        UriBuilder ub = new(uri) { Host = firstIp.ToString() };
                        resolvedBase = ub.Uri.ToString().TrimEnd('/');
                        resolvedIp = firstIp.ToString();
                    }
                }
                else
                {
                    resolvedIp = uri.Host;
                }
            }
            catch { }

            return new Farm.Web.Shared.ResolveHostnameResponse(normalized, resolvedIp, resolvedBase);
        }

        public string? ExtractThumbnailUrl(Dictionary<string, object> metadata, string printerServerUrl)
        {
            if (metadata == null)
            {
                return null;
            }

            string[] thumbnailKeys = new[] { "thumbnail", "thumbnails", "gcode_thumbnail" };

            foreach (string? key in thumbnailKeys)
            {
                if (metadata.TryGetValue(key, out object? thumbnailValue))
                {
                    if (thumbnailValue is string thumbnailStr && !string.IsNullOrEmpty(thumbnailStr))
                    {
                        if (thumbnailStr.StartsWith("http://") || thumbnailStr.StartsWith("https://"))
                        {
                            return thumbnailStr;
                        }
                        return $"{printerServerUrl.TrimEnd('/')}/server/files/gcodes/{thumbnailStr}";
                    }

                    if (thumbnailValue is System.Text.Json.JsonElement jsonElement && jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        List<JsonElement> array = jsonElement.EnumerateArray().ToList();
                        if (array.Count > 0)
                        {
                            if (array[0].ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                string? thumbnailPath = array[0].GetString();
                                if (!string.IsNullOrEmpty(thumbnailPath))
                                {
                                    return thumbnailPath.StartsWith("http") ? thumbnailPath : $"{printerServerUrl.TrimEnd('/')}/server/files/gcodes/{thumbnailPath}";
                                }
                            }
                            else if (array[0].ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                JsonElement thumbnailObj = array
                                    .Where(t => t.TryGetProperty("relative_path", out _))
                                    .OrderByDescending(t =>
                                    {
                                        int width = t.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0;
                                        int height = t.TryGetProperty("height", out JsonElement h) ? h.GetInt32() : 0;
                                        return width * height;
                                    })
                                    .FirstOrDefault();

                                if (thumbnailObj.ValueKind == System.Text.Json.JsonValueKind.Object && thumbnailObj.TryGetProperty("relative_path", out JsonElement relativePathProp))
                                {
                                    string? relativePath = relativePathProp.GetString();
                                    if (!string.IsNullOrEmpty(relativePath))
                                    {
                                        return relativePath.StartsWith("http") ? relativePath : $"{printerServerUrl.TrimEnd('/')}/server/files/gcodes/{relativePath}";
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        public async Task<HashSet<string>> GetAllNormalizedServerUrlsAsync(int defaultPort, CancellationToken ct)
        {
            List<Printer> items = await _repo.GetAllAsync(ct).ConfigureAwait(false);
            HashSet<string> normalized = items.Select(p => NormalizeServerUrl(p.ServerUrl, defaultPort)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return normalized;
        }

        private static string EnsureLocalSuffix(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return host;
            }
            return System.Net.IPAddress.TryParse(host, out _) ?
                host :
                host.Contains('.', StringComparison.Ordinal) ? host : host + ".local";
        }

        public string NormalizeServerUrl(string? input, int defaultPort)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            string trimmed = input.Trim();
            // Ensure scheme
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "http://" + trimmed;
            }

            try
            {
                Uri uri = new Uri(trimmed);
                // If port is not specified, append default port for comparison purposes
                int port = uri.IsDefaultPort ? defaultPort : uri.Port;
                UriBuilder ub = new UriBuilder(uri)
                {
                    Port = port
                };
                // Return without trailing slash for stable comparisons
                return ub.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                return trimmed;
            }
        }
    }
}
