using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Farm.Web.Shared.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

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
        private readonly INetworkUrlRewriteService _urlRewriter;
        private readonly Farm.Infrastructure.Telemetry.IUnifiedLoggingService _logger;
        private readonly AutoMapper.IMapper _mapper;
        private readonly IHubContext<Farm.Web.Api.Hubs.PrinterHub> _hubContext;

        public PrintersService(Farm.Infrastructure.Repositories.Printers.IPrintersRepository repo, IMoonrakerClient moon, IPrusaLinkClient prusa, ISdcpClient sdcp, IOctoPrintClient octoprint, ICircuitBreakerService circuitBreaker, IPrinterCapabilityDiscoveryService capabilityDiscovery, IDefaultCatalogService defaultCatalog, Farm.Web.Api.Services.Catalog.ICatalogService catalogService, IHttpClientFactory httpClientFactory, INetworkUrlRewriteService urlRewriter, Farm.Infrastructure.Telemetry.IUnifiedLoggingService logger, AutoMapper.IMapper mapper, IHubContext<Farm.Web.Api.Hubs.PrinterHub> hubContext)
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
            _urlRewriter = urlRewriter ?? throw new ArgumentNullException(nameof(urlRewriter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
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

            string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort);
            Services.HistoryListResponse? moonrakerResponse = await _moon.GetHistoryListAsync(moonrakerUrl, limit, start, since, before, order, ct).ConfigureAwait(false);
            if (moonrakerResponse == null)
            {
                return new Farm.Web.Shared.HistoryListResponse { Count = 0, Jobs = Array.Empty<Farm.Web.Shared.HistoryJob>() };
            }

            Shared.HistoryJob[] jobs = moonrakerResponse.Jobs.Select(j =>
            {
                Shared.HistoryJob mapped = _mapper.Map<Farm.Web.Shared.HistoryJob>(j);
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

            string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort);
            Services.HistoryJob? moonrakerJob = await _moon.GetHistoryJobAsync(moonrakerUrl, jobId, ct).ConfigureAwait(false);
            if (moonrakerJob == null)
            {
                throw new KeyNotFoundException($"History job {jobId} not found");
            }

            Shared.HistoryJob mapped = _mapper.Map<Farm.Web.Shared.HistoryJob>(moonrakerJob);
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

            string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort);
            Services.HistoryTotals? moonrakerTotals = await _moon.GetHistoryTotalsAsync(moonrakerUrl, ct).ConfigureAwait(false);
            if (moonrakerTotals == null)
            {
                return new Farm.Web.Shared.HistoryTotals { JobTotals = new Farm.Web.Shared.JobTotals() };
            }

            Shared.HistoryTotals mapped = _mapper.Map<Farm.Web.Shared.HistoryTotals>(moonrakerTotals);
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

            string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort);
            return await _moon.DeleteHistoryJobAsync(moonrakerUrl, jobId, ct).ConfigureAwait(false);
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
                        CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"prusalink-{p.Id}");
                        PrusaCompositeStatus status = await breaker.ExecuteAsync(async ct => await _prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct), fastTimeoutCts.Token);
                        // Delegate to PrusaLink client for DTO creation
                        return await _prusa.CreatePrinterDtoAsync(p, status, fastTimeoutCts.Token);
                    }
                    else if (p.Backend == 2) // SDCP
                    {
                        CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"sdcp-{p.Id}");
                        PrinterCompositeStatus status = await breaker.ExecuteAsync(async ct => await _sdcp.GetCompositeStatusAsync(p.ServerUrl, ct), fastTimeoutCts.Token);
                        // Delegate to SDCP client for DTO creation
                        return await _sdcp.CreatePrinterDtoAsync(p, status, fastTimeoutCts.Token);
                    }
                    else if (p.Backend == 3) // OctoPrint
                    {
                        CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"octoprint-{p.Id}");
                        string printerJson = await breaker.ExecuteAsync(async ct => await _octoprint.GetPrinterStateAsync(p.ServerUrl, p.ApiKey ?? string.Empty), fastTimeoutCts.Token);
                        string jobJson = await breaker.ExecuteAsync(async ct => await _octoprint.GetJobStatusAsync(p.ServerUrl, p.ApiKey ?? string.Empty), fastTimeoutCts.Token);
                        // Delegate to OctoPrint client for DTO creation
                        return await _octoprint.CreatePrinterDtoAsync(p, printerJson, jobJson, p.ApiKey ?? string.Empty, fastTimeoutCts.Token);
                    }
                    else // Moonraker
                    {
                        CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"moonraker-{p.Id}");
                        string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                        PrinterCompositeStatus status = await breaker.ExecuteAsync(async ct => await _moon.GetCompositeStatusAsync(moonrakerUrl, ct), fastTimeoutCts.Token);
                        PrinterSpoolInfoDto? spoolInfo = await GetSpoolInfoAsync(moonrakerUrl, fastTimeoutCts.Token);
                        // Delegate to Moonraker client for DTO creation
                        return await _moon.CreatePrinterDtoAsync(p, status, spoolInfo, fastTimeoutCts.Token);
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
                    CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"prusalink-{p.Id}");
                    PrusaCompositeStatus status = await breaker.ExecuteAsync(async ct => await _prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct), statusCts.Token);
                    return new Farm.Web.Shared.PrinterStatusDto(Id: p.Id, IsOnline: status.IsOnline, State: status.State, Progress: status.Progress, JobName: status.JobName, ThumbnailUrl: status.ThumbnailUrl, CameraStreamUrl: status.CameraStreamUrl, CameraSnapshotUrl: status.CameraSnapshotUrl);
                }
                else if (p.Backend == 2)
                {
                    CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"sdcp-{p.Id}");
                    PrinterCompositeStatus status = await breaker.ExecuteAsync(async ct => await _sdcp.GetCompositeStatusAsync(p.ServerUrl, ct), statusCts.Token);
                    return new Farm.Web.Shared.PrinterStatusDto(Id: p.Id, IsOnline: status.IsOnline, State: status.State, Progress: status.Progress, JobName: status.JobName, ThumbnailUrl: status.ThumbnailUrl, CameraStreamUrl: status.CameraStreamUrl, CameraSnapshotUrl: status.CameraSnapshotUrl, X: status.X, Y: status.Y, Z: status.Z, HotendTemp: status.HotendTemp, BedTemp: status.BedTemp, HotendTarget: status.HotendTarget, BedTarget: status.BedTarget);
                }
                else
                {
                    CircuitBreaker breaker = _circuitBreaker.GetCircuitBreaker($"moonraker-{p.Id}");
                    string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                    PrinterCompositeStatus status = await breaker.ExecuteAsync(async ct => await _moon.GetCompositeStatusAsync(moonrakerUrl, ct), statusCts.Token);
                    PrinterSpoolInfoDto? spoolInfo = await GetSpoolInfoAsync(moonrakerUrl, statusCts.Token);
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
                // Delegate to PrusaLink client for DTO creation
                PrusaCompositeStatus status = await _prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct);
                return await _prusa.CreatePrinterDtoAsync(p, status, ct);
            }
            else if (p.Backend == 2)
            {
                // Delegate to SDCP client for DTO creation
                PrinterCompositeStatus status = await _sdcp.GetCompositeStatusAsync(p.ServerUrl, ct);
                return await _sdcp.CreatePrinterDtoAsync(p, status, ct);
            }
            else
            {
                // Delegate to Moonraker client for DTO creation
                string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                PrinterCompositeStatus status = await _moon.GetCompositeStatusAsync(moonrakerUrl, ct);
                PrinterSpoolInfoDto? spoolInfo = await GetSpoolInfoAsync(moonrakerUrl, ct);
                return await _moon.CreatePrinterDtoAsync(p, status, spoolInfo, ct);
            }
        }

        public async Task<Farm.Web.Shared.PrinterCameraUrlsDto[]> GetCameraUrlsAsync(CancellationToken ct)
        {
            List<Printer> items = await _repo.GetAllAsync(ct);
            Farm.Web.Shared.PrinterCameraUrlsDto[] dtos = await Task.WhenAll(items.Select(async p =>
            {
                string? streamUrl = null;
                string? snapshotUrl = null;

                if (await IsCameraAvailableAsync(p.ServerUrl, p.Backend, p.FrontendPort, ct))
                {
                    // Delegate to backend-specific client for URL generation
                    if (p.Backend == 0) // Moonraker
                    {
                        streamUrl = await _moon.GetCameraStreamUrlAsync(p.ServerUrl, p.FrontendPort, ct);
                        snapshotUrl = await _moon.GetCameraSnapshotUrlAsync(p.ServerUrl, p.FrontendPort, ct);
                    }
                    else if (p.Backend == 1) // PrusaLink
                    {
                        streamUrl = await _prusa.GetCameraStreamUrlAsync(p.ServerUrl, p.FrontendPort, ct);
                        snapshotUrl = await _prusa.GetCameraSnapshotUrlAsync(p.ServerUrl, p.FrontendPort, ct);
                    }
                    else if (p.Backend == 2) // SDCP
                    {
                        streamUrl = await _sdcp.GetCameraUrlAsync(p.ServerUrl, ct);
                        snapshotUrl = await _sdcp.GetCameraSnapshotUrlAsync(p.ServerUrl, ct);
                    }
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
            return items.Select(p => new Farm.Web.Shared.PrinterFastDto(Id: p.Id, Name: p.Name, ServerUrl: p.ServerUrl, Notes: p.Notes, IsOnline: false, State: null, ManufacturerName: p.Manufacturer?.Name, ModelName: p.Model?.Name, Backend: p.Backend == 1 ? Farm.Web.Shared.PrinterBackend.PrusaLink : p.Backend == 2 ? Farm.Web.Shared.PrinterBackend.SDCP : Farm.Web.Shared.PrinterBackend.Moonraker, ApiKey: p.ApiKey, OriginalServerUrl: p.OriginalServerUrl, IpAddress: p.IpAddress, IsEnabled: p.IsEnabled)).ToArray();
        }

        private static readonly JsonSerializerOptions _exportJsonOptions = new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new Farm.Web.Api.Serialization.ImportExportTypeInfoResolver(),
        };

        public async Task<byte[]> BuildExportCsvAsync(Guid[]? ids, CancellationToken ct)
        {
            List<Printer> printers = await GetPrintersForExportAsync(ids, ct);

            // Export minimum required fields for re-import (IDs are not portable between systems)
            List<string> headerParts = new() { "Name", "IpAddress", "Backend", "ManufacturerName", "ModelName", "Notes", "IsEnabled" };

            StringBuilder csv = new();
            csv.AppendLine(string.Join(',', headerParts));

            foreach (Printer printer in printers)
            {
                string backendName = printer.Backend == 1 ? "PrusaLink" : printer.Backend == 2 ? "SDCP" : "Moonraker";
                csv.AppendLine($"{EscapeCsvValue(printer.Name)},{EscapeCsvValue(printer.IpAddress)},{backendName},{EscapeCsvValue(printer.Manufacturer?.Name)},{EscapeCsvValue(printer.Model?.Name)},{EscapeCsvValue(printer.Notes)},{printer.IsEnabled}");
            }

            return System.Text.Encoding.UTF8.GetBytes(csv.ToString());
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

            // CSV - export minimum required fields for re-import
            response.ContentType = "text/csv";
            string filename = $"printers-export-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.csv";
            response.Headers["Content-Disposition"] = $"attachment; filename={filename}";

            List<string> headerParts = new() { "Name", "IpAddress", "Backend", "ManufacturerName", "ModelName", "Notes", "IsEnabled" };

            await using var writer = new System.IO.StreamWriter(response.Body, System.Text.Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(string.Join(',', headerParts));

            foreach (Printer p in query)
            {
                string backendName = p.Backend == 1 ? "PrusaLink" : p.Backend == 2 ? "SDCP" : "Moonraker";
                string csvLine = $"{EscapeCsvValue(p.Name)},{EscapeCsvValue(p.IpAddress)},{backendName},{EscapeCsvValue(p.Manufacturer?.Name)},{EscapeCsvValue(p.Model?.Name)},{EscapeCsvValue(p.Notes)},{p.IsEnabled}";
                await writer.WriteLineAsync(csvLine);
                await writer.FlushAsync();
            }
        }

        public async Task<Farm.Web.Shared.PrinterWithCapabilitiesDto[]> GetPrintersWithCapabilitiesDtosAsync(Guid[]? ids, CancellationToken ct)
        {
            List<Printer> printers = await GetPrintersForExportAsync(ids, ct);
            List<Farm.Infrastructure.Domain.PrinterCapabilities> capabilities = await GetCapabilitiesListAsync(ids, ct);

            PrinterWithCapabilitiesDto[] results = printers.Select(p =>
            {
                Farm.Infrastructure.Domain.PrinterCapabilities? cap = capabilities.Find(c => c.PrinterId == p.Id);
                return new Farm.Web.Shared.PrinterWithCapabilitiesDto
                {
                    PrinterId = p.Id,
                    PrinterName = p.Name,
                    PrinterModel = p.Model != null ? p.Model.Name ?? string.Empty : string.Empty,
                    ManufacturerName = p.Manufacturer != null ? p.Manufacturer.Name : null,
                    Backend = p.Backend == 1 ? Farm.Web.Shared.PrinterBackend.PrusaLink : p.Backend == 2 ? Farm.Web.Shared.PrinterBackend.SDCP : Farm.Web.Shared.PrinterBackend.Moonraker,
                    IpAddress = p.IpAddress,
                    // Add import-friendly fields for re-importing
                    ServerUrl = p.ServerUrl,
                    ApiKey = p.ApiKey,
                    Notes = p.Notes,
                    Capabilities = cap == null ? null : new Farm.Web.Shared.PrinterCapabilitiesExportDto
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
            await foreach (Printer? p in query.AsAsyncEnumerable().WithCancellation(ct))
            {
                if (!first)
                {
                    await writer.WriteAsync(",");
                }

                first = false;
                _ = capabilities.TryGetValue(p.Id, out Farm.Infrastructure.Domain.PrinterCapabilities? cap);
                Dictionary<string, object?> dtoDict = BuildExportPrinterDictionary(p, cap);
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
                foreach (PropertyInfo prop in typeof(Farm.Infrastructure.Domain.PrinterCapabilities).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
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
                if (backend == 0) // Moonraker
                {
                    snapshotUrl = await _moon.GetCameraSnapshotUrlAsync(serverUrl, frontendPort, ct).ConfigureAwait(false);
                }
                else if (backend == 1) // PrusaLink
                {
                    snapshotUrl = await _prusa.GetCameraSnapshotUrlAsync(serverUrl, frontendPort, ct).ConfigureAwait(false);
                }
                else if (backend == 2) // SDCP
                {
                    snapshotUrl = await _sdcp.GetCameraSnapshotUrlAsync(serverUrl, ct).ConfigureAwait(false);
                }

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
                try
                {
                    ManufacturerDto created = await _catalogService.CreateManufacturerAsync(name, ct).ConfigureAwait(false);
                    manufacturerId = created.Id;
                }
                catch (Farm.Web.Api.Infrastructure.Exceptions.DuplicateEntityException ex)
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
                var createReq = new Farm.Web.Api.Controllers.Requests.CreateModelRequest(
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
                catch (Farm.Web.Api.Infrastructure.Exceptions.DuplicateEntityException ex)
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
                // Use provided BackendPort and FrontendPort from discovery, or leave null for defaults
                BackendPort = dto.BackendPort,
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

            // Delegate to backend-specific client for snapshot
            if (p.Backend == 0) // Moonraker
            {
                return await _moon.GetCameraSnapshotAsync(p.ServerUrl, ct).ConfigureAwait(false);
            }

            if (p.Backend == 1) // PrusaLink
            {
                string? snapshotUrl = await _prusa.GetCameraSnapshotUrlAsync(p.ServerUrl, p.FrontendPort, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(snapshotUrl))
                {
                    return null;
                }

                return await FetchBytesFromUrlAsync(snapshotUrl, p.ApiKey, ct).ConfigureAwait(false);
            }

            if (p.Backend == 2) // SDCP
            {
                string? snapshotUrl = await _sdcp.GetCameraSnapshotUrlAsync(p.ServerUrl, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(snapshotUrl))
                {
                    return null;
                }

                return await FetchBytesFromUrlAsync(snapshotUrl, null, ct).ConfigureAwait(false);
            }

            return null;
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

            // Delegate to backend-specific client for URL generation
            if (p.Backend == 0) // Moonraker
            {
                string? streamUrl = await _moon.GetCameraStreamUrlAsync(p.ServerUrl, p.FrontendPort, ct).ConfigureAwait(false);
                string? snapshotUrl = await _moon.GetCameraSnapshotUrlAsync(p.ServerUrl, p.FrontendPort, ct).ConfigureAwait(false);
                return (streamUrl, snapshotUrl);
            }

            if (p.Backend == 1) // PrusaLink
            {
                string? streamUrl = await _prusa.GetCameraStreamUrlAsync(p.ServerUrl, p.FrontendPort, ct).ConfigureAwait(false);
                string? snapshotUrl = await _prusa.GetCameraSnapshotUrlAsync(p.ServerUrl, p.FrontendPort, ct).ConfigureAwait(false);
                return (streamUrl, snapshotUrl);
            }

            if (p.Backend == 2) // SDCP
            {
                string? streamUrl = await _sdcp.GetCameraUrlAsync(p.ServerUrl, ct).ConfigureAwait(false);
                string? snapshotUrl = await _sdcp.GetCameraSnapshotUrlAsync(p.ServerUrl, ct).ConfigureAwait(false);
                return (streamUrl, snapshotUrl);
            }

            return (null, null);
        }

        public async Task<bool> SendHomeAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await _moon.SendHomeAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> HomeXYAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await _moon.HomeXYAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> HomeZAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await _moon.HomeZAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> SetTempsAsync(Guid id, double? hotend, double? bed, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await _moon.SetTempsAsync(moonrakerUrl, hotend, bed, ct).ConfigureAwait(false);
        }

        public async Task<bool> MoveAsync(Guid id, double? x, double? y, double? z, double? f, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await _moon.MoveAsync(moonrakerUrl, x, y, z, f, ct).ConfigureAwait(false);
        }

        public async Task<bool> MoveToAsync(Guid id, double? x, double? y, double? z, double? f, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await _moon.MoveToAsync(moonrakerUrl, x, y, z, f, ct).ConfigureAwait(false);
        }

        public async Task<bool> PauseAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == 2)
            {
                return await _sdcp.PausePrintAsync(p.ServerUrl, ct).ConfigureAwait(false);
            }
            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await _moon.PauseAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> ResumeAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == 2)
            {
                return await _sdcp.ResumePrintAsync(p.ServerUrl, ct).ConfigureAwait(false);
            }
            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await _moon.ResumeAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }

        public async Task<bool> EmergencyStopAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            if (p.Backend == 2)
            {
                return await _sdcp.CancelPrintAsync(p.ServerUrl, ct).ConfigureAwait(false);
            }
            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await _moon.EmergencyStopAsync(moonrakerUrl, ct).ConfigureAwait(false);
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

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await _moon.FirmwareRestartAsync(moonrakerUrl, ct).ConfigureAwait(false);
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

            if (p.Backend == 0)
            {
                string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                return await _moon.UploadGcodeAsync(moonrakerUrl, filename, stream, ct).ConfigureAwait(false);
            }
            return p.Backend switch
            {
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
                0 => await _moon.GetFileListAsync(BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort), ct).ConfigureAwait(false),
                1 => await _prusa.GetFileListAsync(p.ServerUrl, p.ApiKey, ct).ConfigureAwait(false),
                2 => await _sdcp.GetFileListAsync(p.ServerUrl, ct).ConfigureAwait(false),
                _ => Array.Empty<string>()
            };
        }

        public async Task<Farm.Web.Shared.ResolveHostnameResponse> ResolveHostnameAsync(string serverUrl, Farm.Web.Shared.PrinterBackend backend, CancellationToken ct)
        {
            // First normalize with port for internal operations (URL comparison, parsing)
            int defaultPort = backend == Farm.Web.Shared.PrinterBackend.PrusaLink ? 80 : backend == Farm.Web.Shared.PrinterBackend.SDCP ? 80 : 7125;
            string normalizedWithPort = NormalizeServerUrl(serverUrl, defaultPort);

            string? resolvedIp = null;
            string resolvedBase = normalizedWithPort;
            try
            {
                Uri uri = new(normalizedWithPort);
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

            // ServerUrl is already normalized without explicit port by NormalizeServerUrl()
            // Port is managed separately via FrontendPort field
            return new Farm.Web.Shared.ResolveHostnameResponse(normalizedWithPort, resolvedIp, resolvedBase);
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

            var createdPrinters = new List<PrinterDto>();
            var errorResults = new Dictionary<int, string>();
            var skippedCount = 0;
            var results = new List<dynamic>();

            for (int i = 0; i < printers.Length; i++)
            {
                try
                {
                    var printerDto = printers[i];
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
                            var allPrinters = await GetAllAsync(ct);
                            var existing = allPrinters.FirstOrDefault(p =>
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
                    var errorMessage = $"Failed to create printer: {ex.Message}";
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
        public async Task<Farm.Web.Shared.PrintJobStatusDto?> GetPrintJobStatusAsync(Guid id, CancellationToken ct)
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
                    case 0: // Moonraker
                    {
                        string moonrakerUrl = BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort);
                        var job = await _moon.GetJobAsync(moonrakerUrl, ct).ConfigureAwait(false);
                        if (job != null)
                        {
                            return new Farm.Web.Shared.PrintJobStatusDto
                            {
                                State = job.PrintState,
                                Progress = job.Progress,
                                JobName = job.JobName,
                                ThumbnailUrl = job.ThumbnailUrl
                            };
                        }
                        return null;
                    }

                    case 2: // PrusaLink (OctoPrint-like API)
                    {
                        // Note: PrusaLink client may not have GetJobStatusAsync yet
                        // Fallback to null for now - implementation can be added later
                        _logger.LogInformation($"[PrintJobStatus] PrusaLink print job status not yet implemented");
                        return null;
                    }

                    case 3: // SDCP (Elegoo)
                    {
                        var job = await _sdcp.GetJobAsync(printer.ServerUrl, ct).ConfigureAwait(false);
                        if (job != null)
                        {
                            return new Farm.Web.Shared.PrintJobStatusDto
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

            var fileExtension = System.IO.Path.GetExtension(file.FileName).ToLowerInvariant();
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
                var result = await BulkCreatePrintersAsync(printers, duplicateHandling, ct);
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
            var printers = new List<CreatePrinterDto>();
            var errors = new List<string>();

            try
            {
                using (var reader = new System.IO.StreamReader(file.OpenReadStream()))
                {
                    string? headerLine = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrWhiteSpace(headerLine))
                    {
                        throw new InvalidOperationException("CSV file is empty or has no header");
                    }

                    // Parse header
                    var headers = CsvImportParser.SplitCsvLine(headerLine).Select(h => h.Trim().ToLowerInvariant()).ToArray();
                    var nameIdx = Array.IndexOf(headers, "name");
                    var ipAddressIdx = Array.IndexOf(headers, "ipaddress");
                    var backendIdx = Array.IndexOf(headers, "backend");
                    var notesIdx = Array.IndexOf(headers, "notes");
                    var manufacturerNameIdx = Array.IndexOf(headers, "manufacturername");
                    var modelNameIdx = Array.IndexOf(headers, "modelname");
                    var apiKeyIdx = Array.IndexOf(headers, "apikey");
                    var isEnabledIdx = Array.IndexOf(headers, "isenabled");
                    var backendPortIdx = Array.IndexOf(headers, "backendport");
                    var frontendPortIdx = Array.IndexOf(headers, "frontendport");
                    var cameraStreamIdx = Array.IndexOf(headers, "camerastreamurl");
                    var cameraSnapshotIdx = Array.IndexOf(headers, "camerasnapshoturl");
                    var dateAcquiredIdx = Array.IndexOf(headers, "dateacquired");

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
                            var values = CsvImportParser.SplitCsvLine(line).Select(v => v.Trim()).ToArray();

                            if (values.Length < 3)
                            {
                                errors.Add($"Line {lineNumber}: Insufficient columns (need at least Name, IpAddress, Backend)");
                                continue;
                            }

                            // Validate backend
                            if (!Enum.TryParse<PrinterBackend>(values[backendIdx], true, out var backendEnum))
                            {
                                errors.Add($"Line {lineNumber}: Invalid backend '{values[backendIdx]}' (must be Moonraker, PrusaLink, or SDCP)");
                                continue;
                            }

                            // Build ServerUrl from IpAddress and backend
                            string ipAddress = values[ipAddressIdx];
                            int defaultPort = backendEnum == PrinterBackend.PrusaLink ? 80 : backendEnum == PrinterBackend.SDCP ? 80 : 7125;
                            string serverUrl = $"http://{ipAddress}:{defaultPort}";

                            var printer = new CreatePrinterDto
                            {
                                Name = values[nameIdx],
                                ServerUrl = serverUrl,
                                Backend = backendEnum,
                                NewManufacturerName = manufacturerNameIdx >= 0 && manufacturerNameIdx < values.Length && !string.IsNullOrWhiteSpace(values[manufacturerNameIdx]) ? values[manufacturerNameIdx] : null,
                                NewModelName = modelNameIdx >= 0 && modelNameIdx < values.Length && !string.IsNullOrWhiteSpace(values[modelNameIdx]) ? values[modelNameIdx] : null,
                                ApiKey = apiKeyIdx >= 0 && apiKeyIdx < values.Length ? values[apiKeyIdx] : null,
                                Notes = notesIdx >= 0 && notesIdx < values.Length ? values[notesIdx] : null,
                                IsEnabled = isEnabledIdx >= 0 && isEnabledIdx < values.Length && bool.TryParse(values[isEnabledIdx], out var ie) ? ie : true,
                                BackendPort = backendPortIdx >= 0 && backendPortIdx < values.Length && int.TryParse(values[backendPortIdx], out var bp) ? bp : null,
                                FrontendPort = frontendPortIdx >= 0 && frontendPortIdx < values.Length && int.TryParse(values[frontendPortIdx], out var fp) ? fp : null,
                                CameraStreamUrl = cameraStreamIdx >= 0 && cameraStreamIdx < values.Length ? values[cameraStreamIdx] : null,
                                CameraSnapshotUrl = cameraSnapshotIdx >= 0 && cameraSnapshotIdx < values.Length ? values[cameraSnapshotIdx] : null,
                                DateAcquired = dateAcquiredIdx >= 0 && dateAcquiredIdx < values.Length && DateTime.TryParse(values[dateAcquiredIdx], out var da) ? da : null
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
                using (var reader = new System.IO.StreamReader(file.OpenReadStream()))
                {
                    var content = await reader.ReadToEndAsync(ct);

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        throw new InvalidOperationException("JSON file is empty");
                    }

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        WriteIndented = false,
                        TypeInfoResolver = new Farm.Web.Api.Serialization.ImportExportTypeInfoResolver()
                    };

                    var printers = JsonSerializer.Deserialize<CreatePrinterDto[]>(content, options);

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
    }
}
