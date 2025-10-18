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
using System.Text;
using System.Text.Json;

namespace Farm.Web.Api.Services.Printers
{
    public class PrintersService : IPrintersService
    {
        private readonly Farm.Infrastructure.Repositories.Printers.IPrintersRepository _repo;
        private readonly IMoonrakerClient _moon;
        private readonly IPrusaLinkClient _prusa;
        private readonly ISdcpClient _sdcp;
        private readonly IOctoPrintClient _octoprint;
        private readonly ICircuitBreakerService _circuitBreaker;
        private readonly IPrinterCapabilityDiscoveryService _capabilityDiscovery;
        private readonly IDefaultCatalogService _defaultCatalog;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Farm.Infrastructure.Telemetry.IUnifiedLoggingService _logger;

        public PrintersService(Farm.Infrastructure.Repositories.Printers.IPrintersRepository repo, IMoonrakerClient moon, IPrusaLinkClient prusa, ISdcpClient sdcp, IOctoPrintClient octoprint, ICircuitBreakerService circuitBreaker, IPrinterCapabilityDiscoveryService capabilityDiscovery, IDefaultCatalogService defaultCatalog, IHttpClientFactory httpClientFactory, Farm.Infrastructure.Telemetry.IUnifiedLoggingService logger)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _moon = moon ?? throw new ArgumentNullException(nameof(moon));
            _prusa = prusa ?? throw new ArgumentNullException(nameof(prusa));
            _sdcp = sdcp ?? throw new ArgumentNullException(nameof(sdcp));
            _octoprint = octoprint ?? throw new ArgumentNullException(nameof(octoprint));
            _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
            _capabilityDiscovery = capabilityDiscovery ?? throw new ArgumentNullException(nameof(capabilityDiscovery));
            _defaultCatalog = defaultCatalog ?? throw new ArgumentNullException(nameof(defaultCatalog));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        public async Task<Farm.Web.Shared.PrinterFastDto[]> GetAllFastDtosAsync(CancellationToken ct)
        {
            List<Printer> items = await _repo.GetAllWithIncludesAsync(ct);
            return items.Select(p => new Farm.Web.Shared.PrinterFastDto(Id: p.Id, Name: p.Name, ServerUrl: p.ServerUrl, Notes: p.Notes, IsOnline: false, State: null, ManufacturerName: p.Manufacturer?.Name, ModelName: p.Model?.Name, Backend: p.Backend == 1 ? Farm.Web.Shared.PrinterBackend.PrusaLink : p.Backend == 2 ? Farm.Web.Shared.PrinterBackend.SDCP : Farm.Web.Shared.PrinterBackend.Moonraker, ApiKey: p.ApiKey, OriginalServerUrl: p.OriginalServerUrl, IpAddress: p.IpAddress)).ToArray();
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
    }
}
