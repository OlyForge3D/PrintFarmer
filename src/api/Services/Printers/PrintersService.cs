using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Printers
{
    public class PrintersService : IPrintersService
    {
        private readonly Farm.Infrastructure.Repositories.Printers.IPrintersRepository _repo;
        private readonly Catalog.ICatalogService _catalogService;
        private readonly IBackendClientFactory _backendFactory;
        private readonly ICircuitBreakerService _circuitBreaker;
        private readonly IPrinterCapabilityDiscoveryService _capabilityDiscovery;
        private readonly IDefaultCatalogService _defaultCatalog;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Farm.Infrastructure.Telemetry.IUnifiedLoggingService _logger;
        private readonly AutoMapper.IMapper _mapper;
        private readonly IHubContext<Hubs.PrinterHub> _hubContext;
        private readonly IPrinterStatusDtoBuilder _dtoBuilder;
        private readonly IMultiPrinterStatusCoordinator _coordinator;
        private readonly IPrinterStatusFallbackService _fallbackService;
        private readonly IPrinterStatusClientFactory _statusClientFactory;

        public PrintersService(
            Farm.Infrastructure.Repositories.Printers.IPrintersRepository repo,
            IBackendClientFactory backendFactory,
            ICircuitBreakerService circuitBreaker,
            IPrinterCapabilityDiscoveryService capabilityDiscovery,
            IDefaultCatalogService defaultCatalog,
            Catalog.ICatalogService catalogService,
            IHttpClientFactory httpClientFactory,
            Farm.Infrastructure.Telemetry.IUnifiedLoggingService logger,
            AutoMapper.IMapper mapper,
            IHubContext<Hubs.PrinterHub> hubContext,
            IPrinterStatusDtoBuilder dtoBuilder,
            IMultiPrinterStatusCoordinator coordinator,
            IPrinterStatusFallbackService fallbackService,
            IPrinterStatusClientFactory statusClientFactory)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
            _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
            _capabilityDiscovery = capabilityDiscovery ?? throw new ArgumentNullException(nameof(capabilityDiscovery));
            _defaultCatalog = defaultCatalog ?? throw new ArgumentNullException(nameof(defaultCatalog));
            _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _dtoBuilder = dtoBuilder ?? throw new ArgumentNullException(nameof(dtoBuilder));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _fallbackService = fallbackService ?? throw new ArgumentNullException(nameof(fallbackService));
            _statusClientFactory = statusClientFactory ?? throw new ArgumentNullException(nameof(statusClientFactory));
        }

        /// <summary>
        /// Gets the appropriate backend client for a printer based on its backend type.
        /// Uses the factory to retrieve and cast to the specific client interface.
        /// </summary>
        private T GetBackendClient<T>(PrinterBackend backend) where T : class
        {
            return (T)(object)_backendFactory.GetClient(backend);
        }

        // History helpers moved from controller: delegate to appropriate backend client
        public async Task<HistoryListResponse> GetHistoryListAsync(Guid printerId, int? limit, int? start, DateTime? since, DateTime? before, string? order, CancellationToken ct)
        {
            Printer? printer = await FindByIdAsync(printerId, ct).ConfigureAwait(false);
            if (printer == null)
            {
                throw new KeyNotFoundException();
            }

            try
            {
                // Delegate to appropriate backend client
                if (printer.Backend == (int)Farm.Infrastructure.PrinterBackend.Moonraker)
                {
                    string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort);
                    _logger.LogInformation($"[Moonraker History] Requesting history from {moonrakerUrl}");
                    HistoryListResponse? response = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetHistoryListAsync(moonrakerUrl, limit, start, since, before, order, ct).ConfigureAwait(false);
                    if (response == null)
                    {
                        _logger.LogWarning($"[Moonraker History] No response from Moonraker history API for printer {printerId}");
                        return new HistoryListResponse { Count = 0, Jobs = Array.Empty<HistoryJob>() };
                    }

                    _logger.LogInformation($"[Moonraker History] Got {response.Count} jobs from Moonraker");
                    // Set ThumbnailUrl for each job
                    foreach (var job in response.Jobs)
                    {
                        job.ThumbnailUrl = ExtractThumbnailUrl(job.Metadata ?? new Dictionary<string, object>(), printer.ServerUrl);
                    }
                    return response;
                }
                else if (printer.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
                {
                    _logger.LogInformation($"[OctoPrint History] Requesting history from {printer.BackendUrl}");
                    HistoryListResponse? response = await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).GetHistoryListAsync(printer.BackendUrl, printer.ApiKey ?? "", limit, start).ConfigureAwait(false);
                    if (response == null)
                    {
                        _logger.LogWarning($"[OctoPrint History] No response from OctoPrint history API for printer {printerId}");
                        return new HistoryListResponse { Count = 0, Jobs = Array.Empty<HistoryJob>() };
                    }

                    _logger.LogInformation($"[OctoPrint History] Got {response.Count} jobs from OctoPrint");
                    // Set ThumbnailUrl for each job
                    foreach (var job in response.Jobs)
                    {
                        job.ThumbnailUrl = ExtractThumbnailUrl(job.Metadata ?? new Dictionary<string, object>(), printer.ServerUrl);
                    }
                    return response;
                }
                else
                {
                    _logger.LogWarning($"[History] Printer {printerId} backend {printer.Backend} does not support history");
                    return new HistoryListResponse { Count = 0, Jobs = Array.Empty<HistoryJob>() };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"[History] Failed to retrieve history for printer {printerId}: {ex.Message}");
                return new HistoryListResponse { Count = 0, Jobs = Array.Empty<HistoryJob>() };
            }
        }

        public async Task<HistoryJob> GetHistoryJobAsync(Guid printerId, string jobId, CancellationToken ct)
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

            try
            {
                HistoryJob? job = null;

                if (printer.Backend == (int)Farm.Infrastructure.PrinterBackend.Moonraker)
                {
                    string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort);
                    job = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetHistoryJobAsync(moonrakerUrl, jobId, ct).ConfigureAwait(false);
                }
                else if (printer.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
                {
                    job = await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).GetHistoryJobAsync(printer.BackendUrl, printer.ApiKey ?? "", jobId).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException("History is only available for Moonraker and OctoPrint printers");
                }

                if (job == null)
                {
                    throw new KeyNotFoundException($"History job {jobId} not found");
                }

                // Set ThumbnailUrl
                job.ThumbnailUrl = ExtractThumbnailUrl(job.Metadata ?? new Dictionary<string, object>(), printer.ServerUrl);
                return job;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"[History] Failed to retrieve job {jobId} for printer {printerId}: {ex.Message}");
                if (ex is KeyNotFoundException || ex is InvalidOperationException)
                    throw;
                throw new KeyNotFoundException($"History job {jobId} not found", ex);
            }
        }

        public async Task<HistoryTotals> GetHistoryTotalsAsync(Guid printerId, CancellationToken ct)
        {
            Printer? printer = await FindByIdAsync(printerId, ct).ConfigureAwait(false);
            if (printer == null)
            {
                throw new KeyNotFoundException();
            }

            try
            {
                HistoryTotals? totals = null;

                if (printer.Backend == (int)Farm.Infrastructure.PrinterBackend.Moonraker)
                {
                    string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort);
                    totals = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetHistoryTotalsAsync(moonrakerUrl, ct).ConfigureAwait(false);
                    if (totals != null)
                        return totals;
                }
                else if (printer.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
                {
                    HistoryListResponse? response = await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).GetHistoryListAsync(printer.BackendUrl, printer.ApiKey ?? "", 10000, 0).ConfigureAwait(false);
                    if (response != null)
                        return CalculateOctoPrintHistoryTotals(response.Jobs);
                }

                return new HistoryTotals { JobTotals = new JobTotals() };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"[History] Failed to calculate totals for printer {printerId}: {ex.Message}");
                return new HistoryTotals { JobTotals = new JobTotals() };
            }
        }

        public async Task<bool> DeleteHistoryJobAsync(Guid printerId, string jobId, CancellationToken ct)
        {
            Printer? printer = await FindByIdAsync(printerId, ct).ConfigureAwait(false);
            if (printer == null)
            {
                throw new KeyNotFoundException();
            }

            if (printer.Backend != (int)Farm.Infrastructure.PrinterBackend.Moonraker)
            {
                throw new InvalidOperationException("History deletion is only available for Moonraker printers");
            }

            string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort);
            return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).DeleteHistoryJobAsync(moonrakerUrl, jobId, ct).ConfigureAwait(false);
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
        public async Task<PrinterDto[]> GetAllWithStatusDtosAsync(CancellationToken ct)
        {
            List<Printer> items = await _repo.GetAllWithIncludesAsync(ct);

            // Use coordinator to execute status retrieval in parallel with per-printer timeout
            PrinterDto?[] dtos = await _coordinator.ExecuteParallelWithTimeoutAsync<PrinterDto>(
                items,
                async (printer, timeoutCt) =>
                {
                    try
                    {
                        return await GetStatusDtoInternalAsync(printer, timeoutCt);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error getting status for printer {printer.Name} ({printer.Id}): {ex.Message}");
                        return CreateOfflinePrinterDto(printer);
                    }
                },
                TimeSpan.FromSeconds(2),
                printer =>
                {
                    // Timeout handler
                    _logger.LogWarning($"Fast timeout occurred for printer {printer.Name} ({printer.Id})");
                },
                (printer, ex) =>
                {
                    // Error handler
                    _logger.LogError($"Error getting status for printer {printer.Name} ({printer.Id}): {ex.Message}");
                },
                ct);

            // All returned DTOs should be non-null due to fallback, but filter just in case
            return dtos.Where(d => d != null).Cast<PrinterDto>().ToArray();
        }

        /// <summary>
        /// Internal method to get status DTO for a single printer.
        /// Handles backend-specific status retrieval and DTO construction.
        /// Never returns null - always returns PrinterDto (offline on error).
        /// </summary>
#pragma warning disable CS8603 // Possible null reference return - client methods are annotated as non-nullable
        private async Task<PrinterDto> GetStatusDtoInternalAsync(Printer p, CancellationToken ct)
        {
            try
            {
                // Delegate to the appropriate backend status client
                // Each backend client is responsible for:
                // - Retrieving typed status from its backend
                // - Handling circuit breaker and timeouts
                // - Building the complete PrinterDto
                // - Backend-specific integrations (e.g., Moonraker spoolman)
                var statusClient = _statusClientFactory.GetStatusClient(p.Backend);
                return await statusClient.GetPrinterDtoAsync(p, ct);
            }
            catch (ArgumentException)
            {
                // Unsupported backend type
                _logger.LogWarning($"Unsupported printer backend {p.Backend} for printer {p.Id}");
                return CreateOfflinePrinterDto(p);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to get DTO for printer {p.Id}: {ex.Message}");
                return CreateOfflinePrinterDto(p);
            }
        }
#pragma warning restore CS8603

#pragma warning disable CS8603 // Possible null reference return - PrinterStatusDto constructor returns non-nullable
        public async Task<PrinterStatusDto> GetStatusDtoAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await _repo.FindByIdAsync(id, ct);
            if (p is null)
            {
                throw new KeyNotFoundException();
            }

            try
            {
                // Delegate to the appropriate backend status client
                // Each backend client is responsible for creating the PrinterStatusDto
                var statusClient = _statusClientFactory.GetStatusClient(p.Backend);
                return await statusClient.GetPrinterStatusAsync(p, ct);
            }
            catch (ArgumentException)
            {
                // Unsupported backend type
                _logger.LogWarning($"Unsupported printer backend {p.Backend} for printer {p.Id}");
                return new PrinterStatusDto(Id: p.Id, IsOnline: false, State: "Unsupported", Progress: null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to get status for printer {p.Id}: {ex.Message}");
                return new PrinterStatusDto(Id: p.Id, IsOnline: false, State: "Offline", Progress: null);
            }
        }
#pragma warning restore CS8603

        public async Task<PrinterDto> GetPrinterDtoAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await _repo.FindByIdWithIncludesAsync(id, ct);
            if (p is null)
            {
                throw new KeyNotFoundException();
            }

            // Delegate to the appropriate backend status client
            // Each status client is responsible for retrieving typed status from its backend
            // and building the complete PrinterDto
            var statusClient = _statusClientFactory.GetStatusClient(p.Backend);
            return await statusClient.GetPrinterDtoAsync(p, ct);
        }

        public async Task<PrinterCameraUrlsDto[]> GetCameraUrlsAsync(CancellationToken ct)
        {
            List<Printer> items = await _repo.GetAllAsync(ct);
            PrinterCameraUrlsDto[] dtos = await Task.WhenAll(items.Select(async p =>
            {
                string? streamUrl = null;
                string? snapshotUrl = null;

                // PrusaLink camera URLs are not supported due to encoding issues
                if (p.Backend != (int)Farm.Infrastructure.PrinterBackend.PrusaLink && 
                    await IsCameraAvailableAsync(p.BackendUrl, p.Backend, p.FrontendPort, ct))
                {
                    try
                    {
                        // Delegate to appropriate backend client for URL generation
                        if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.Moonraker)
                        {
                            streamUrl = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetCameraStreamUrlAsync(p.BackendUrl, p.FrontendPort, ct);
                            snapshotUrl = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetCameraSnapshotUrlAsync(p.BackendUrl, p.FrontendPort, ct);
                        }
                        else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.SDCP)
                        {
                            streamUrl = await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).GetCameraUrlAsync(p.BackendUrl, ct);
                            snapshotUrl = await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).GetCameraSnapshotUrlAsync(p.BackendUrl, ct);
                        }
                        else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
                        {
                            // OctoPrint camera URLs would go here
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"Failed to get camera URLs for printer {p.Id}: {ex.Message}");
                    }
                }
                return new PrinterCameraUrlsDto(Id: p.Id, Name: p.Name, CameraStreamUrl: streamUrl, CameraSnapshotUrl: snapshotUrl);
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

        public async Task<PrinterFastDto[]> GetAllFastDtosAsync(CancellationToken ct)
        {
            List<Printer> items = await _repo.GetAllWithIncludesAsync(ct);
            var dtos = new List<PrinterFastDto>();
            
            foreach (var p in items)
            {
                try
                {
                    // Get real-time status for each printer
                    PrinterStatusDto status = await GetStatusDtoAsync(p.Id, ct);
                    dtos.Add(new PrinterFastDto(
                        Id: p.Id, 
                        Name: p.Name, 
                        ServerUrl: p.ServerUrl, 
                        Notes: p.Notes, 
                        IsOnline: status.IsOnline,
                        State: status.State,
                        ManufacturerName: p.Manufacturer?.Name, 
                        ModelName: p.Model?.Name, 
                        Backend: MapBackendEnum(p.Backend), 
                        ApiKey: p.ApiKey, 
                        OriginalServerUrl: p.OriginalServerUrl, 
                        IpAddress: p.IpAddress, 
                        BackendPort: p.BackendPort, 
                        FrontendPort: p.FrontendPort, 
                        InMaintenance: p.InMaintenance, 
                        IsEnabled: p.IsEnabled));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to get status for printer {p.Id}: {ex.Message}. Using offline status.");
                    // Fallback to offline status if retrieval fails
                    dtos.Add(new PrinterFastDto(
                        Id: p.Id, 
                        Name: p.Name, 
                        ServerUrl: p.ServerUrl, 
                        Notes: p.Notes, 
                        IsOnline: false,
                        State: null,
                        ManufacturerName: p.Manufacturer?.Name, 
                        ModelName: p.Model?.Name, 
                        Backend: MapBackendEnum(p.Backend), 
                        ApiKey: p.ApiKey, 
                        OriginalServerUrl: p.OriginalServerUrl, 
                        IpAddress: p.IpAddress, 
                        BackendPort: p.BackendPort, 
                        FrontendPort: p.FrontendPort, 
                        InMaintenance: p.InMaintenance, 
                        IsEnabled: p.IsEnabled));
                }
            }
            
            return dtos.ToArray();
        }

        /// <summary>
        /// Maps an integer backend value to the PrinterBackend enum.
        /// </summary>
        private static PrinterBackend MapBackendEnum(int backendValue) => (PrinterBackend)backendValue;

        private static readonly JsonSerializerOptions _exportJsonOptions = new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new Serialization.ImportExportTypeInfoResolver(),
        };

        public async Task<byte[]> BuildExportCsvAsync(Guid[]? ids, CancellationToken ct)
        {
            // Delegate to StreamExportToResponseAsync using a memory stream wrapper
            using MemoryStream ms = new MemoryStream();
            using StreamWriter writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);
            
            List<Printer> printers = await GetPrintersForExportAsync(ids, ct);
            IQueryable<Printer> query = printers.AsQueryable();
            
            // Export fields matching AdminCli CSV format for consistency
            List<string> headerParts = new() { "Name", "IpAddress", "Backend", "BackendPort", "FrontendPort", "ManufacturerName", "ModelName", "Notes", "ApiKey", "IsEnabled", "CameraStreamUrl", "CameraSnapshotUrl", "DateAcquired" };
            
            await writer.WriteLineAsync(string.Join(',', headerParts));
            
            foreach (Printer p in query)
            {
                PrinterBackend backend = (PrinterBackend)p.Backend;
                string backendName = backend.ToString();
                
                string backendPort = p.BackendPort.ToString();
                string frontendPort = p.FrontendPort?.ToString() ?? "";
                string apiKey = p.ApiKey ?? "";
                string cameraStreamUrl = p.CameraStreamUrl ?? "";
                string cameraSnapshotUrl = p.CameraSnapshotUrl ?? "";
                string dateAcquired = p.DateAcquired?.ToString("O") ?? "";
                string csvLine = $"{EscapeCsvValue(p.Name)},{EscapeCsvValue(p.IpAddress)},{backendName},{backendPort},{frontendPort},{EscapeCsvValue(p.Manufacturer?.Name)},{EscapeCsvValue(p.Model?.Name)},{EscapeCsvValue(p.Notes)},{EscapeCsvValue(apiKey)},{p.IsEnabled},{EscapeCsvValue(cameraStreamUrl)},{EscapeCsvValue(cameraSnapshotUrl)},{dateAcquired}";
                await writer.WriteLineAsync(csvLine);
            }
            
            await writer.FlushAsync();
            return ms.ToArray();
        }

        public async Task StreamExportToResponseAsync(Guid[]? ids, string format, HttpResponse response, CancellationToken ct)
        {
            List<Printer> printers = await GetPrintersForExportAsync(ids, ct);

            IQueryable<Printer> query = printers.AsQueryable();

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<Guid, Farm.Infrastructure.Domain.PrinterCapabilities> capabilities = await GetCapabilitiesDictionaryAsync(ids, ct);
                await StreamJsonExportAsync(query, capabilities, response, ct);
                return;
            }

            // CSV - export fields matching AdminCli CSV format for consistency
            response.ContentType = "text/csv";
            string filename = $"printers-export-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.csv";
            response.Headers["Content-Disposition"] = $"attachment; filename={filename}";

            List<string> headerParts = new() { "Name", "IpAddress", "Backend", "BackendPort", "FrontendPort", "ManufacturerName", "ModelName", "Notes", "ApiKey", "IsEnabled", "CameraStreamUrl", "CameraSnapshotUrl", "DateAcquired" };

            await using StreamWriter writer = new StreamWriter(response.Body, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(string.Join(',', headerParts));

            foreach (Printer p in query)
            {
                PrinterBackend backend = (PrinterBackend)p.Backend;
                string backendName = backend.ToString();
                string backendPort = p.BackendPort.ToString();
                string frontendPort = p.FrontendPort?.ToString() ?? "";
                string apiKey = p.ApiKey ?? "";
                string cameraStreamUrl = p.CameraStreamUrl ?? "";
                string cameraSnapshotUrl = p.CameraSnapshotUrl ?? "";
                string dateAcquired = p.DateAcquired?.ToString("O") ?? "";
                string csvLine = $"{EscapeCsvValue(p.Name)},{EscapeCsvValue(p.IpAddress)},{backendName},{backendPort},{frontendPort},{EscapeCsvValue(p.Manufacturer?.Name)},{EscapeCsvValue(p.Model?.Name)},{EscapeCsvValue(p.Notes)},{EscapeCsvValue(apiKey)},{p.IsEnabled},{EscapeCsvValue(cameraStreamUrl)},{EscapeCsvValue(cameraSnapshotUrl)},{dateAcquired}";
                await writer.WriteLineAsync(csvLine);
                await writer.FlushAsync();
            }
        }

        public async Task<PrinterWithCapabilitiesDto[]> GetPrintersWithCapabilitiesDtosAsync(Guid[]? ids, CancellationToken ct)
        {
            List<Printer> printers = await GetPrintersForExportAsync(ids, ct);
            List<Farm.Infrastructure.Domain.PrinterCapabilities> capabilities = await GetCapabilitiesListAsync(ids, ct);

            PrinterWithCapabilitiesDto[] results = printers.Select(p =>
            {
                Farm.Infrastructure.Domain.PrinterCapabilities? cap = capabilities.Find(c => c.PrinterId == p.Id);
                return new PrinterWithCapabilitiesDto
                {
                    PrinterId = p.Id,
                    PrinterName = p.Name,
                    PrinterModel = p.Model != null ? p.Model.Name ?? string.Empty : string.Empty,
                    ManufacturerName = p.Manufacturer != null ? p.Manufacturer.Name : null,
                    Backend = MapBackendEnum(p.Backend),
                    IpAddress = p.IpAddress,
                    // Add import-friendly fields for re-importing
                    ServerUrl = p.ServerUrl,
                    ApiKey = p.ApiKey,
                    Notes = p.Notes,
                    Capabilities = cap == null ? null : new PrinterCapabilitiesExportDto
                    {
                        Id = cap.Id,
                        NozzleDiameter = cap.NozzleDiameter,
                        SupportedMaterials = cap.SupportedMaterials,
                        MaxBuildVolumeX = cap.MaxBuildVolumeX,
                        MaxBuildVolumeY = cap.MaxBuildVolumeY,
                        MaxBuildVolumeZ = cap.MaxBuildVolumeZ,
                        HasHeatedBed = cap.HasHeatedBed,
                        HasEnclosure = cap.HasEnclosure,
                        MultiMaterial = cap.MultiMaterial,
                        SupportsAutoLeveling = cap.SupportsAutoLeveling,
                        NumberOfExtruders = cap.NumberOfExtruders,
                        MinHotendTemp = cap.MinHotendTemp,
                        MaxHotendTemp = cap.MaxHotendTemp,
                        MinBedTemp = cap.MinBedTemp,
                        MaxBedTemp = cap.MaxBedTemp,
                        CurrentMaterial = cap.CurrentMaterial,
                        CurrentSpoolId = cap.CurrentSpoolId,
                        IsAvailable = cap.IsAvailable,
                        LastUpdated = cap.LastUpdated
                    }
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

        private static bool PropertySuppressedForExport(PropertyInfo? pi)
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

            await using StreamWriter writer = new StreamWriter(response.Body, Encoding.UTF8, leaveOpen: true);
            await writer.WriteAsync("[");
            bool first = true;
            await foreach (Printer? p in query.AsAsyncEnumerable().WithCancellation(ct))
            {
                if (!first)
                {
                    await writer.WriteAsync(",");
                }

                first = false;
                _ = capabilities.TryGetValue(p.Id, out Farm.Infrastructure.Domain.PrinterCapabilities? cap);
                Dictionary<string, object?> dtoDict = BuildExportPrinterDictionary(p, cap);
                string json = JsonSerializer.Serialize(dtoDict, _exportJsonOptions);
                await writer.WriteAsync(json);
                await writer.FlushAsync();
            }
            await writer.WriteAsync("]");
            await writer.FlushAsync();
        }

        private static Dictionary<string, object?> BuildExportPrinterDictionary(Printer p, Farm.Infrastructure.Domain.PrinterCapabilities? cap)
        {
            Dictionary<string, object?> dict = new Dictionary<string, object?>
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
                foreach (PropertyInfo prop in typeof(Farm.Infrastructure.Domain.PrinterCapabilities).GetProperties(BindingFlags.Public | BindingFlags.Instance))
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

        private async Task<bool> IsCameraAvailableAsync(string serverUrl, int backend, int? frontendPort, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                return false;
            }

            try
            {
                string? snapshotUrl = null;

                // Get camera snapshot URL from backend-specific client
                if (backend == (int)Farm.Infrastructure.PrinterBackend.Moonraker)
                {
                    snapshotUrl = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetCameraSnapshotUrlAsync(serverUrl, frontendPort, ct).ConfigureAwait(false);
                }
                else if (backend == (int)Farm.Infrastructure.PrinterBackend.PrusaLink)
                {
                    snapshotUrl = await GetBackendClient<IPrusaLinkClient>(PrinterBackend.PrusaLink).GetCameraSnapshotUrlAsync(serverUrl, frontendPort, ct).ConfigureAwait(false);
                }
                else if (backend == (int)Farm.Infrastructure.PrinterBackend.SDCP)
                {
                    snapshotUrl = await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).GetCameraSnapshotUrlAsync(serverUrl, ct).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(snapshotUrl))
                {
                    return false;
                }

                HttpClient httpClient = _httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(2);
                using HttpRequestMessage request = new(HttpMethod.Head, snapshotUrl);
                using HttpResponseMessage response = await httpClient.SendAsync(request, ct);
                return response.StatusCode < HttpStatusCode.InternalServerError;
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Camera availability check failed for printer {serverUrl} (backend {backend}): {ex.Message}");
                return false;
            }
        }

        private static PrinterDto CreateOfflinePrinterDto(Printer p)
        {
            return new PrinterDto(
                Id: p.Id,
                Name: p.Name,
                ServerUrl: p.ServerUrl,
                Notes: p.Notes,
                IsOnline: false,
                State: null,
                ManufacturerName: p.Manufacturer?.Name,
                ModelName: p.Model?.Name,
                Progress: null,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null,
                X: null,
                Y: null,
                Z: null,
                HotendTemp: null,
                BedTemp: null,
                HotendTarget: null,
                BedTarget: null,
                Backend: MapBackendEnum(p.Backend),
                ApiKey: p.ApiKey,
                OriginalServerUrl: p.OriginalServerUrl,
                IpAddress: p.IpAddress,
                SpoolInfo: null,
                BackendPort: p.BackendPort,
                FrontendPort: p.FrontendPort
            );
        }

        // Reuse controller's GetSpoolInfoAsync logic adapted for service
        private async Task<PrinterSpoolInfoDto?> GetSpoolInfoAsync(string serverUrl, CancellationToken ct)
        {
            try
            {
                int? activeSpoolId = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetSpoolmanActiveSpoolAsync(serverUrl, ct);
                if (activeSpoolId == null)
                {
                    return new PrinterSpoolInfoDto(HasActiveSpool: false);
                }

                string? spoolDetailsJson = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetSpoolmanSpoolByIdAsync(serverUrl, activeSpoolId.Value, ct);
                if (string.IsNullOrWhiteSpace(spoolDetailsJson))
                {
                    return new PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: activeSpoolId);
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

                    return new PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: activeSpoolId, SpoolName: spoolName, Material: material, ColorHex: colorHex, FilamentName: filamentName, Vendor: vendor, RemainingWeightG: remainingWeight, SpoolInUse: true);
                }
                catch
                {
                    return new PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: activeSpoolId);
                }
            }
            catch
            {
                return new PrinterSpoolInfoDto(HasActiveSpool: false);
            }
        }

        public async Task<PrinterDto> CreatePrinterFromDtoAsync(CreatePrinterDto dto, CancellationToken ct)
        {
            // Check for duplicate printer by serverUrl or name
            int defaultPort = PrinterBackendHelpers.GetDefaultPort(dto.Backend);
            string normalizedUrl = NormalizeServerUrl(dto.ServerUrl, defaultPort);
            List<Printer> existingPrinters = await _repo.GetAllAsync(ct).ConfigureAwait(false);
            Printer? duplicate = existingPrinters.FirstOrDefault(p =>
                NormalizeServerUrl(p.ServerUrl, defaultPort) == normalizedUrl ||
                p.IpAddress == normalizedUrl.Replace("http://", "").Replace("https://", "").Split(':')[0]);

            if (duplicate != null)
            {
                _logger.LogWarning($"Duplicate printer detected: {dto.Name} at {dto.ServerUrl} - existing printer: {duplicate.Name} ({duplicate.Id})");
                throw new InvalidOperationException($"A printer already exists at this address: {duplicate.Name}");
            }

            // resolve or create manufacturer/model
            Guid manufacturerId = dto.ManufacturerId ?? Guid.Empty;
            if (manufacturerId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
            {
                string name = dto.NewManufacturerName!.Trim();
                try
                {
                    ManufacturerDto created = await _catalogService.CreateManufacturerAsync(name, ct).ConfigureAwait(false);
                    manufacturerId = created.Id;
                }
                catch (Infrastructure.Exceptions.DuplicateEntityException ex)
                {
                    // Manufacturer already exists, use its ID
                    if (ex.ExistingDto is ManufacturerDto existingMfg)
                    {
                        manufacturerId = existingMfg.Id;
                    }
                }
            }

            Guid modelId = dto.ModelId ?? Guid.Empty;
            if (modelId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewModelName) && manufacturerId != Guid.Empty)
            {
                string mname = dto.NewModelName!.Trim();
                CreateModelRequest createReq = new CreateModelRequest(
                    ManufacturerId: manufacturerId,
                    Name: mname,
                    Type: null,
                    MaxX: null,
                    MaxY: null,
                    MaxZ: null,
                    DefaultBackend: null,
                    SupportedFilamentTypeIds: null);
                try
                {
                    PrinterModelDto createdModel = await _catalogService.CreateModelAsync(createReq, ct).ConfigureAwait(false);
                    modelId = createdModel.Id;
                }
                catch (Infrastructure.Exceptions.DuplicateEntityException ex)
                {
                    // Model already exists, use its ID
                    if (ex.ExistingDto is PrinterModelDto existingModel)
                    {
                        modelId = existingModel.Id;
                    }
                }
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

            // defaultPort was already calculated at the start for duplicate checking
            string normalizedInput = NormalizeServerUrl(dto.ServerUrl, defaultPort);
            string resolvedBase = normalizedInput;
            string? resolvedIp = null;
            try
            {
                Uri uri = new(normalizedInput);
                if (!IPAddress.TryParse(uri.Host, out _))
                {
                    string hostToResolve = EnsureLocalSuffix(uri.Host);
                    IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(hostToResolve, ct).ConfigureAwait(false);
                    IPAddress? firstIp = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork) ?? (addresses.Length > 0 ? addresses[0] : null);
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

            // ServerUrl is already normalized without explicit port by NormalizeServerUrl()
            // Port is managed separately via BackendPort field
            string serverUrlForStorage = resolvedBase;
            string originalUrlForStorage = normalizedInput;

            Printer p = new()
            {
                Id = Guid.NewGuid(),
                Name = dto.Name,
                ServerUrl = serverUrlForStorage,
                OriginalServerUrl = originalUrlForStorage,
                IpAddress = resolvedIp,
                Notes = dto.Notes,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
                DateAcquired = dto.DateAcquired?.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dto.DateAcquired.Value, DateTimeKind.Utc) : dto.DateAcquired,
                Backend = (int)dto.Backend,
                ApiKey = dto.ApiKey,
                // Use provided BackendPort and FrontendPort from discovery, or use defaults
                BackendPort = dto.BackendPort ?? 80,
                FrontendPort = dto.FrontendPort,
                // Use provided camera URLs from discovery, or leave null
                CameraStreamUrl = dto.CameraStreamUrl,
                CameraSnapshotUrl = dto.CameraSnapshotUrl,
                IsEnabled = dto.IsEnabled
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

            // Return offline DTO for newly imported printer (hasn't fetched status yet)
            return CreateOfflinePrinterDto(p);
        }

        // High-level operations moved from controller
        public async Task<byte[]?> GetCameraSnapshotAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return null;
            }

            try
            {
                // Delegate to appropriate backend client for snapshot
                if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.Moonraker)
                {
                    return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetCameraSnapshotAsync(p.BackendUrl, ct).ConfigureAwait(false);
                }
                else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.PrusaLink)
                {
                    string? snapshotUrl = await GetBackendClient<IPrusaLinkClient>(PrinterBackend.PrusaLink).GetCameraSnapshotUrlAsync(p.BackendUrl, p.FrontendPort, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(snapshotUrl))
                        return null;
                    return await FetchBytesFromUrlAsync(snapshotUrl, p.ApiKey, ct).ConfigureAwait(false);
                }
                else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.SDCP)
                {
                    string? snapshotUrl = await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).GetCameraSnapshotUrlAsync(p.BackendUrl, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(snapshotUrl))
                        return null;
                    return await FetchBytesFromUrlAsync(snapshotUrl, null, ct).ConfigureAwait(false);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Failed to get camera snapshot for printer {id}: {ex.Message}");
                return null;
            }
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
                    _ = req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
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

            try
            {
                // Delegate to appropriate backend client for URL generation
                if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.Moonraker)
                {
                    string? streamUrl = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetCameraStreamUrlAsync(p.BackendUrl, p.FrontendPort, ct).ConfigureAwait(false);
                    string? snapshotUrl = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetCameraSnapshotUrlAsync(p.BackendUrl, p.FrontendPort, ct).ConfigureAwait(false);
                    return (streamUrl, snapshotUrl);
                }
                else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.PrusaLink)
                {
                    string? streamUrl = await GetBackendClient<IPrusaLinkClient>(PrinterBackend.PrusaLink).GetCameraStreamUrlAsync(p.BackendUrl, p.FrontendPort, ct).ConfigureAwait(false);
                    string? snapshotUrl = await GetBackendClient<IPrusaLinkClient>(PrinterBackend.PrusaLink).GetCameraSnapshotUrlAsync(p.BackendUrl, p.FrontendPort, ct).ConfigureAwait(false);
                    return (streamUrl, snapshotUrl);
                }
                else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.SDCP)
                {
                    string? streamUrl = await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).GetCameraUrlAsync(p.BackendUrl, ct).ConfigureAwait(false);
                    string? snapshotUrl = await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).GetCameraSnapshotUrlAsync(p.BackendUrl, ct).ConfigureAwait(false);
                    return (streamUrl, snapshotUrl);
                }

                return (null, null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Failed to get camera URLs for printer {id}: {ex.Message}");
                return (null, null);
            }
        }

        public async Task<bool> SendHomeAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
            {
                return await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).SendHomeAsync(p.BackendUrl, p.ApiKey ?? string.Empty).ConfigureAwait(false);
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).SendHomeAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> HomeXYAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
            {
                return await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).HomeXYAsync(p.BackendUrl, p.ApiKey ?? string.Empty).ConfigureAwait(false);
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).HomeXYAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> HomeZAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
            {
                return await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).HomeZAsync(p.BackendUrl, p.ApiKey ?? string.Empty).ConfigureAwait(false);
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).HomeZAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> SetTempsAsync(Guid id, double? hotend, double? bed, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
            {
                // OctoPrint: Use native API for both bed and hotend temperatures
                bool success = true;
                
                if (bed.HasValue)
                {
                    bool bedSuccess = await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).SetBedTempAsync(p.BackendUrl, p.ApiKey ?? string.Empty, bed.Value).ConfigureAwait(false);
#pragma warning disable S2589 // Boolean expression always evaluates to true
                    success = success && bedSuccess;
#pragma warning restore S2589
                }
                
                if (hotend.HasValue)
                {
                    bool hotendSuccess = await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).SetHotendTempAsync(p.BackendUrl, p.ApiKey ?? string.Empty, hotend.Value).ConfigureAwait(false);
                    success = success && hotendSuccess;
                }
                
                return success;
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).SetTempsAsync(moonrakerUrl, hotend, bed, ct).ConfigureAwait(false);
        }

        public async Task<bool> MoveAsync(Guid id, double? x, double? y, double? z, double? f, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).MoveAsync(moonrakerUrl, x, y, z, f, ct).ConfigureAwait(false);
        }

        public async Task<bool> MoveToAsync(Guid id, double? x, double? y, double? z, double? f, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).MoveToAsync(moonrakerUrl, x, y, z, f, ct).ConfigureAwait(false);
        }

        public async Task<bool> PauseAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.SDCP)
            {
                return await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).PausePrintAsync(p.BackendUrl, ct).ConfigureAwait(false);
            }
            else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
            {
                return await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).PauseAsync(p.BackendUrl, p.ApiKey ?? string.Empty).ConfigureAwait(false);
            }
            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).PauseAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> ResumeAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.SDCP)
            {
                return await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).ResumePrintAsync(p.BackendUrl, ct).ConfigureAwait(false);
            }
            else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
            {
                return await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).ResumeAsync(p.BackendUrl, p.ApiKey ?? string.Empty).ConfigureAwait(false);
            }
            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).ResumeAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> EmergencyStopAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.SDCP)
            {
                return await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).CancelPrintAsync(p.BackendUrl, ct).ConfigureAwait(false);
            }
            else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
            {
                return await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).CancelPrintAsync(p.BackendUrl, p.ApiKey ?? string.Empty).ConfigureAwait(false);
            }
            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).EmergencyStopAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> FirmwareRestartAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend != (int)Farm.Infrastructure.PrinterBackend.Moonraker)
            {
                return false; // only moonraker
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).FirmwareRestartAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> DisableMotorsAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend != (int)Farm.Infrastructure.PrinterBackend.Moonraker)
            {
                return false; // only moonraker supports M84
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).SendGcodeAsync(moonrakerUrl, "M84", ct).ConfigureAwait(false);
        }

        public async Task<bool> StartPrintFromFileAsync(Guid id, string filename, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.Moonraker)
            {
                return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).StartPrintAsync(p.BackendUrl, filename, ct).ConfigureAwait(false);
            }
            else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.PrusaLink)
            {
                return await GetBackendClient<IPrusaLinkClient>(PrinterBackend.PrusaLink).StartPrintAsync(p.BackendUrl, filename, p.ApiKey, ct).ConfigureAwait(false);
            }
            else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.SDCP)
            {
                return await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).StartPrintAsync(p.BackendUrl, filename, ct).ConfigureAwait(false);
            }
            else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
            {
                return await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).StartJobAsync(p.BackendUrl, p.ApiKey ?? string.Empty, filename).ConfigureAwait(false);
            }
            return false;
        }



        public async Task<bool> DeletePrinterFileAsync(Guid id, string filename, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.Moonraker)
            {
                return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).DeleteFileAsync(p.BackendUrl, filename, ct).ConfigureAwait(false);
            }
            else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.PrusaLink)
            {
                // PrusaLink doesn't support file deletion
                return false;
            }
            else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.SDCP)
            {
                // SDCP doesn't support file deletion
                return false;
            }
            else if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint)
            {
                return await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).DeleteFileAsync(p.BackendUrl, p.ApiKey ?? string.Empty, filename).ConfigureAwait(false);
            }
            return false;
        }

        public async Task<bool> EnableCameraAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.SDCP)
            {
                return await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).EnableCameraAsync(p.BackendUrl, ct).ConfigureAwait(false);
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

            if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.SDCP)
            {
                return await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).DisableCameraAsync(p.BackendUrl, ct).ConfigureAwait(false);
            }
            return false;
        }

        public async Task<bool> UploadGcodeAsync(Guid id, string filename, Stream stream, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == (int)Farm.Infrastructure.PrinterBackend.Moonraker)
            {
                string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                return await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).UploadGcodeAsync(moonrakerUrl, filename, stream, ct).ConfigureAwait(false);
            }
            return p.Backend switch
            {
                1 => await GetBackendClient<IPrusaLinkClient>(PrinterBackend.PrusaLink).UploadGcodeAsync(p.BackendUrl, filename, stream, p.ApiKey, ct).ConfigureAwait(false),
                2 => await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).UploadGcodeAsync(p.BackendUrl, filename, stream, ct).ConfigureAwait(false),
                _ => false
            };
        }

        public async Task<PrinterFileDto[]> GetFileListAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return Array.Empty<PrinterFileDto>();
            }

            // For Moonraker, use GetFileListAsync to get file info with metadata
            if (((PrinterBackend)p.Backend) == PrinterBackend.Moonraker)
            {
                try
                {
                    string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                    string[] fileNames = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetFileListAsync(moonrakerUrl, ct).ConfigureAwait(false);
                    
                    if (fileNames.Length == 0)
                    {
                        return Array.Empty<PrinterFileDto>();
                    }

                    // Get detailed metadata for each file
                    List<PrinterFileDto> result = new();
                    foreach (string fileName in fileNames)
                    {
                        string? thumbnailUrl = null;
                        long? modified = null;
                        long? sizeBytes = null;

                        try
                        {
                            GCodeMetadata? metadata = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetFileMetadataAsync(
                                moonrakerUrl,
                                fileName,
                                ct)
                                .ConfigureAwait(false);

                            if (metadata != null)
                            {
                                // Capture file metadata
                                modified = (long)metadata.Modified;
                                sizeBytes = metadata.Size;

                                if (metadata.Thumbnails.Length > 0)
                                {
                                    // Get the largest thumbnail
                                    ThumbnailInfo? largestThumb = metadata.Thumbnails
                                        .OrderByDescending(t => t.Width * t.Height)
                                        .FirstOrDefault();

                                    if (largestThumb != null)
                                    {
                                        // Build full URL for thumbnail
                                        thumbnailUrl = $"{p.ServerUrl.TrimEnd('/')}/server/files/gcodes/{Uri.EscapeDataString(largestThumb.RelativePath)}";
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // Silently ignore metadata fetch errors, still add file to list
                        }

                        result.Add(new PrinterFileDto(fileName, thumbnailUrl, modified, sizeBytes));
                    }

                    return result.ToArray();
                }
                catch
                {
                    // Fallback if Moonraker file list fails
                    return Array.Empty<PrinterFileDto>();
                }
            }

            // For PrusaLink and SDCP, fetch file list
            string[] fileNames2 = ((PrinterBackend)p.Backend) switch
            {
                PrinterBackend.PrusaLink => await GetBackendClient<IPrusaLinkClient>(PrinterBackend.PrusaLink).GetFileListAsync(p.BackendUrl, p.ApiKey, ct).ConfigureAwait(false),
                PrinterBackend.SDCP => await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).GetFileListAsync(p.BackendUrl, ct).ConfigureAwait(false),
                PrinterBackend.OctoPrint => await GetBackendClient<IOctoPrintClient>(PrinterBackend.OctoPrint).GetFileListAsync(p.BackendUrl, p.ApiKey ?? string.Empty).ConfigureAwait(false),
                _ => Array.Empty<string>()
            };

            if (fileNames2.Length == 0)
            {
                return Array.Empty<PrinterFileDto>();
            }

            // For PrusaLink and SDCP, no metadata available currently
            // PrusaLink: thumbnail retrieval requires Digest Authentication
            // According to PrusaLink OpenAPI spec, v1 endpoints (/api/v1/files/{storage}/{path})
            // require Digest Auth, NOT X-Api-Key header authentication.
            // The legacy /api/files endpoint (OctoPrint compatible) works with X-Api-Key
            // but doesn't include thumbnail metadata in the response.
            // TODO: Implement Digest Authentication support for PrusaLink to enable v1 API access
            // SDCP: no metadata API currently exposed
            List<PrinterFileDto> result2 = new();
            foreach (string fileName in fileNames2)
            {
                result2.Add(new PrinterFileDto(fileName, null, null, null));
            }

            return result2.ToArray();
        }

        public async Task<ResolveHostnameResponse> ResolveHostnameAsync(string serverUrl, PrinterBackend backend, CancellationToken ct)
        {
            // First normalize with port for internal operations (URL comparison, parsing)
            int defaultPort = PrinterBackendHelpers.GetDefaultPort(backend);
            string normalizedWithPort = NormalizeServerUrl(serverUrl, defaultPort);

            string? resolvedIp = null;
            string resolvedBase = normalizedWithPort;
            try
            {
                Uri uri = new(normalizedWithPort);
                if (!IPAddress.TryParse(uri.Host, out _))
                {
                    string hostToResolve = EnsureLocalSuffix(uri.Host);
                    IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(hostToResolve, ct).ConfigureAwait(false);
                    IPAddress? firstIp = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork) ?? (addresses.Length > 0 ? addresses[0] : null);
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

            // ServerUrl is already normalized without explicit port by NormalizeServerUrl()
            // Port is managed separately via FrontendPort field
            return new ResolveHostnameResponse(normalizedWithPort, resolvedIp, resolvedBase);
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

                    if (thumbnailValue is JsonElement jsonElement && jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
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
            return IPAddress.TryParse(host, out _) ?
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
                // Build URL without explicit port (use default ports)
                UriBuilder ub = new UriBuilder(uri)
                {
                    Port = -1,  // -1 means use default port, not explicitly shown
                    Path = string.Empty,  // Remove any paths
                    Query = string.Empty
                };
                // Return without trailing slash for stable comparisons
                return ub.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                return trimmed;
            }
        }

        /// <summary>
        /// Bulk creates multiple printers with duplicate handling.
        /// </summary>
        /// <param name="printers">Array of printer DTOs to create</param>
        /// <param name="duplicateHandling">How to handle duplicates: 'skip', 'overwrite', or 'error'</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Response object with import counts and results</returns>
        public async Task<object> BulkCreatePrintersAsync(CreatePrinterDto[] printers, string duplicateHandling = "skip", CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(printers);

            List<PrinterDto> createdPrinters = new List<PrinterDto>();
            Dictionary<int, string> errorResults = new Dictionary<int, string>();
            int skippedCount = 0;
            List<dynamic> results = new List<dynamic>();

            for (int i = 0; i < printers.Length; i++)
            {
                try
                {
                    CreatePrinterDto printerDto = printers[i];
                    string status = "Imported";
                    string? reason = null;
                    PrinterDto? createdDto = null;

                    // Check for duplicates
                    bool exists = await ExistsByNameOrServerUrlAsync(printerDto.Name, printerDto.ServerUrl, ct);
                    if (exists)
                    {
                        if ((duplicateHandling ?? "skip") == "skip")
                        {
                            _logger.LogInformation($"[BulkCreate] Skipping duplicate printer: {printerDto.Name}");
                            skippedCount++;
                            status = "Skipped";
                            reason = $"Duplicate printer already exists";
                        }
                        else if ((duplicateHandling ?? "skip") == "overwrite")
                        {
                            // Find and delete existing printer
                            List<Printer> allPrinters = await GetAllAsync(ct);
                            Printer? existing = allPrinters.FirstOrDefault(p =>
                                p.Name == printerDto.Name ||
                                p.ServerUrl == printerDto.ServerUrl);
                            if (existing != null)
                            {
                                _logger.LogInformation($"[BulkCreate] Removing duplicate printer: {existing.Name}");
                                await RemoveAsync(existing, ct);
                                await SaveChangesAsync(ct);
                            }
                            createdDto = await CreatePrinterFromDtoAsync(printerDto, ct);
                            await SaveChangesAsync(ct);
                            createdPrinters.Add(createdDto);
                            _logger.LogInformation($"[BulkCreate] Successfully created printer: {createdDto.Name}");
                        }
                        else if ((duplicateHandling ?? "skip") == "error")
                        {
                            status = "Failed";
                            reason = $"Duplicate printer: {printerDto.Name} already exists";
                            errorResults[i] = reason;
                        }
                    }
                    else
                    {
                        // Create the printer
                        createdDto = await CreatePrinterFromDtoAsync(printerDto, ct);
                        await SaveChangesAsync(ct);
                        createdPrinters.Add(createdDto);
                        _logger.LogInformation($"[BulkCreate] Successfully created printer: {createdDto.Name}");
                    }

                    // Build result with status info
                    var result = new
                    {
                        index = i,
                        name = printerDto.Name,
                        status = status,
                        id = createdDto?.Id,
                        reason = reason
                    };
                    results.Add(result);

                    // Send SignalR update to all connected clients
                    await _hubContext.Clients.All.SendAsync("printerImportProgress", result, ct);
                }
                catch (Exception ex)
                {
                    string errorMessage = $"Failed to create printer: {ex.Message}";
                    errorResults[i] = errorMessage;
                    _logger.LogWarning($"[BulkCreate] Error creating printer at index {i}: {errorMessage}");

                    var result = new
                    {
                        index = i,
                        name = printers[i].Name,
                        status = "Failed",
                        id = (string?)null,
                        reason = errorMessage
                    };
                    results.Add(result);

                    // Send SignalR update for failure
                    await _hubContext.Clients.All.SendAsync("printerImportProgress", result, ct);
                }
            }

            return new
            {
                importedCount = createdPrinters.Count,
                skippedCount = skippedCount,
                failureCount = errorResults.Count,
                results = results,
                errors = errorResults.Count > 0 ? errorResults : null
            };
        }

        /// <summary>
        /// Retrieves the current print job status for a printer.
        /// Supports multiple printer backends: Moonraker, PrusaLink (OctoPrint), and SDCP.
        /// Returns null if no active job or if status cannot be retrieved.
        /// </summary>
        public async Task<PrintJobStatusDto?> GetPrintJobStatusAsync(Guid id, CancellationToken ct)
        {
            try
            {
                // Verify printer exists
                Printer? printer = await FindByIdAsync(id, ct).ConfigureAwait(false);
                if (printer == null)
                {
                    _logger.LogWarning($"[PrintJobStatus] Printer {id} not found");
                    return null;
                }

                _logger.LogInformation($"[PrintJobStatus] Getting print job status for printer {printer.Name} (Backend: {printer.Backend})");

                // Route to appropriate client based on backend
                switch (printer.Backend)
                {
                    case (int)Farm.Infrastructure.PrinterBackend.Moonraker:
                    {
                        string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort);
                        PrinterJob? job = await GetBackendClient<IMoonrakerClient>(PrinterBackend.Moonraker).GetJobAsync(moonrakerUrl, ct).ConfigureAwait(false);
                        if (job != null)
                        {
                            return new PrintJobStatusDto
                            {
                                State = job.PrintState,
                                Progress = job.Progress,
                                JobName = job.JobName,
                                ThumbnailUrl = job.ThumbnailUrl
                            };
                        }
                        return null;
                    }

                    case (int)Farm.Infrastructure.PrinterBackend.PrusaLink:
                    {
                        // Note: PrusaLink client may not have GetJobStatusAsync yet
                        // Fallback to null for now - implementation can be added later
                        _logger.LogInformation($"[PrintJobStatus] PrusaLink print job status not yet implemented");
                        return null;
                    }

                    case (int)Farm.Infrastructure.PrinterBackend.SDCP:
                    {
                        PrinterJob job = await GetBackendClient<ISdcpClient>(PrinterBackend.SDCP).GetJobAsync(printer.BackendUrl, ct).ConfigureAwait(false);
                        if (job != null)
                        {
                            return new PrintJobStatusDto
                            {
                                State = job.PrintState,
                                Progress = job.Progress,
                                JobName = job.JobName,
                                ThumbnailUrl = job.ThumbnailUrl
                            };
                        }
                        return null;
                    }

                    default:
                        _logger.LogWarning($"[PrintJobStatus] Unknown backend type {printer.Backend}");
                        return null;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"[PrintJobStatus] Timeout retrieving print job status for printer {id}");
                return null; // Return null on timeout
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[PrintJobStatus] Error getting print job status for printer {id}: {ex.Message}");
                return null; // Return null if unable to retrieve
            }
        }

        /// <summary>
        /// Imports printers from an uploaded CSV or JSON file.
        /// Supports duplicate handling strategies: 'skip' (default), 'overwrite', or 'error'.
        /// </summary>
        /// <param name="file">The uploaded file (CSV or JSON)</param>
        /// <param name="duplicateHandling">How to handle duplicates: 'skip', 'overwrite', or 'error'</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Import result with counts and error details</returns>
        public async Task<object> ImportFromFileAsync(IFormFile file, string duplicateHandling = "skip", CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(file);

            if (file.Length == 0)
            {
                throw new ArgumentException("File cannot be empty");
            }

            string fileExtension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (fileExtension != ".csv" && fileExtension != ".json")
            {
                throw new ArgumentException("File must be CSV or JSON format");
            }

            try
            {
                CreatePrinterDto[] printers;

                if (fileExtension == ".csv")
                {
                    printers = await ParseCsvFileAsync(file, ct);
                }
                else // JSON
                {
                    printers = await ParseJsonFileAsync(file, ct);
                }

                if (printers == null || printers.Length == 0)
                {
                    throw new InvalidOperationException("No valid printer entries found in file");
                }

                _logger.LogInformation($"[Import] Parsed {printers.Length} printers from {fileExtension} file");

                // Use existing BulkCreatePrintersAsync for actual creation
                object result = await BulkCreatePrintersAsync(printers, duplicateHandling, ct);
                _logger.LogInformation($"[Import] Successfully imported printers from file");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[Import] Failed to import printers from file: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Parses a CSV file into printer DTOs.
        /// Required columns: Name, IpAddress, Backend
        /// Optional columns: Notes, ManufacturerName, ModelName, ApiKey, IsEnabled, BackendPort, FrontendPort, CameraStreamUrl, CameraSnapshotUrl
        /// IDs are not portable between systems; use names instead.
        /// </summary>
        private async Task<CreatePrinterDto[]> ParseCsvFileAsync(IFormFile file, CancellationToken ct)
        {
            List<CreatePrinterDto> printers = new List<CreatePrinterDto>();
            List<string> errors = new List<string>();

            try
            {
                using (StreamReader reader = new StreamReader(file.OpenReadStream()))
                {
                    string? headerLine = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrWhiteSpace(headerLine))
                    {
                        throw new InvalidOperationException("CSV file is empty or has no header");
                    }

                    // Parse header
                    string[] headers = CsvImportParser.SplitCsvLine(headerLine).Select(h => h.Trim().ToLowerInvariant()).ToArray();
                    int nameIdx = Array.IndexOf(headers, "name");
                    int ipAddressIdx = Array.IndexOf(headers, "ipaddress");
                    int backendIdx = Array.IndexOf(headers, "backend");
                    int notesIdx = Array.IndexOf(headers, "notes");
                    int manufacturerNameIdx = Array.IndexOf(headers, "manufacturername");
                    int modelNameIdx = Array.IndexOf(headers, "modelname");
                    int apiKeyIdx = Array.IndexOf(headers, "apikey");
                    int isEnabledIdx = Array.IndexOf(headers, "isenabled");
                    int backendPortIdx = Array.IndexOf(headers, "backendport");
                    int frontendPortIdx = Array.IndexOf(headers, "frontendport");
                    int cameraStreamIdx = Array.IndexOf(headers, "camerastreamurl");
                    int cameraSnapshotIdx = Array.IndexOf(headers, "camerasnapshoturl");
                    int dateAcquiredIdx = Array.IndexOf(headers, "dateacquired");

                    // Validate required columns
                    if (nameIdx < 0 || ipAddressIdx < 0 || backendIdx < 0)
                    {
                        throw new InvalidOperationException("CSV must have required columns: 'Name', 'IpAddress', 'Backend'");
                    }

                    int lineNumber = 1;
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct)) != null)
                    {
                        lineNumber++;

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue; // Skip empty lines
                        }

                        try
                        {
                            string[] values = CsvImportParser.SplitCsvLine(line).Select(v => v.Trim()).ToArray();

                            if (values.Length < 3)
                            {
                                errors.Add($"Line {lineNumber}: Insufficient columns (need at least Name, IpAddress, Backend)");
                                continue;
                            }

                            // Validate backend
                            if (!Enum.TryParse(values[backendIdx], true, out PrinterBackend backendEnum))
                            {
                                errors.Add($"Line {lineNumber}: Invalid backend '{values[backendIdx]}' (must be Moonraker, PrusaLink, or SDCP)");
                                continue;
                            }

                            // Build ServerUrl from IpAddress and backend
                            string ipAddress = values[ipAddressIdx];
                            int defaultPort = PrinterBackendHelpers.GetDefaultPort(backendEnum);
                            string serverUrl = $"http://{ipAddress}:{defaultPort}";

                            CreatePrinterDto printer = new CreatePrinterDto
                            {
                                Name = values[nameIdx],
                                ServerUrl = serverUrl,
                                Backend = backendEnum,
                                NewManufacturerName = manufacturerNameIdx >= 0 && manufacturerNameIdx < values.Length && !string.IsNullOrWhiteSpace(values[manufacturerNameIdx]) ? values[manufacturerNameIdx] : null,
                                NewModelName = modelNameIdx >= 0 && modelNameIdx < values.Length && !string.IsNullOrWhiteSpace(values[modelNameIdx]) ? values[modelNameIdx] : null,
                                ApiKey = apiKeyIdx >= 0 && apiKeyIdx < values.Length ? values[apiKeyIdx] : null,
                                Notes = notesIdx >= 0 && notesIdx < values.Length ? values[notesIdx] : null,
                                IsEnabled = isEnabledIdx >= 0 && isEnabledIdx < values.Length && bool.TryParse(values[isEnabledIdx], out bool ie) ? ie : true,
                                BackendPort = backendPortIdx >= 0 && backendPortIdx < values.Length && int.TryParse(values[backendPortIdx], out int bp) ? bp : null,
                                FrontendPort = frontendPortIdx >= 0 && frontendPortIdx < values.Length && int.TryParse(values[frontendPortIdx], out int fp) ? fp : null,
                                CameraStreamUrl = cameraStreamIdx >= 0 && cameraStreamIdx < values.Length ? values[cameraStreamIdx] : null,
                                CameraSnapshotUrl = cameraSnapshotIdx >= 0 && cameraSnapshotIdx < values.Length ? values[cameraSnapshotIdx] : null,
#pragma warning disable S6580 // SonarSource: format provider is already specified (InvariantCulture)
                                DateAcquired = dateAcquiredIdx >= 0 && dateAcquiredIdx < values.Length && DateTime.TryParse(values[dateAcquiredIdx], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime da) ? da : null
#pragma warning restore S6580
                            };

                            if (string.IsNullOrWhiteSpace(printer.Name))
                            {
                                errors.Add($"Line {lineNumber}: Name is required");
                                continue;
                            }

                            printers.Add(printer);
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Line {lineNumber}: {ex.Message}");
                        }
                    }
                }

                if (errors.Count > 0)
                {
                    _logger.LogWarning($"[Import-CSV] Encountered {errors.Count} parsing errors while importing {printers.Count} valid printers");
                }

                return printers.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Import-CSV] Failed to parse CSV file");
                throw;
            }
        }

        /// <summary>
        /// Parses a JSON file into printer DTOs.
        /// Expected format: Array of printer objects with Name, ServerUrl, Backend, etc.
        /// </summary>
        private async Task<CreatePrinterDto[]> ParseJsonFileAsync(IFormFile file, CancellationToken ct)
        {
            try
            {
                using (StreamReader reader = new StreamReader(file.OpenReadStream()))
                {
                    string content = await reader.ReadToEndAsync(ct);

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        throw new InvalidOperationException("JSON file is empty");
                    }

                    JsonSerializerOptions options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        WriteIndented = false,
                        TypeInfoResolver = new Serialization.ImportExportTypeInfoResolver()
                    };

                    CreatePrinterDto[]? printers = JsonSerializer.Deserialize<CreatePrinterDto[]>(content, options);

                    if (printers == null || printers.Length == 0)
                    {
                        throw new InvalidOperationException("JSON file contains no valid printer entries");
                    }

                    return printers;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[Import-JSON] JSON parsing error");
                throw new InvalidOperationException($"Invalid JSON format: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Import-JSON] Failed to parse JSON file");
                throw;
            }
        }

        /// <summary>
        /// Builds the correct Moonraker API URL using the FrontendPort.
        /// For Moonraker/Klipper printers, ALL API requests go to the FrontendPort,
        /// and Moonraker automatically routes them to port 7125 internally.
        /// </summary>
        private static string BuildMoonrakerUrl(string serverUrl, int? frontendPort)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                return serverUrl;
            }

            try
            {
                Uri baseUri = new(serverUrl);
                // Use frontend port (default 80 for HTTP, 443 for HTTPS)
                // User can specify custom frontend port (e.g., 8080, 8808 for Phrozen Arco)
                int port = frontendPort ?? (baseUri.Scheme == "https" ? 443 : 80);

                UriBuilder ub = new(baseUri)
                {
                    Port = port
                };

                return ub.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                return serverUrl;
            }
        }


        /// <summary>
        /// Calculates aggregate statistics from OctoPrint history jobs.
        /// </summary>
        private static HistoryTotals CalculateOctoPrintHistoryTotals(HistoryJob[] jobs)
        {
            var totals = new HistoryTotals
            {
                JobTotals = new JobTotals()
            };

            if (jobs == null || jobs.Length == 0)
            {
                return totals;
            }

            totals.JobTotals.TotalJobs = jobs.Length;
            totals.JobTotals.TotalTime = jobs.Sum(j => j.TotalDuration);
            totals.JobTotals.TotalPrintTime = jobs.Sum(j => j.PrintDuration);
            totals.JobTotals.TotalFilamentUsed = jobs.Sum(j => j.FilamentUsed);
            totals.JobTotals.LongestJob = jobs.Max(j => j.TotalDuration);
            totals.JobTotals.LongestPrint = jobs.Max(j => j.PrintDuration);

            return totals;
        }
    }
}
