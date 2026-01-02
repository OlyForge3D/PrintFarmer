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
using AutoMapper;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Printers
{
    /// <summary>
    /// Core service for managing 3D printers across multiple backend protocols with real-time status monitoring and control operations.
    /// </summary>
    /// <remarks>
    /// This service provides comprehensive printer management capabilities including:
    /// - Multi-backend support (Moonraker, PrusaLink, OctoPrint, SDCP) via plugin architecture
    /// - Real-time status monitoring with SignalR broadcasting
    /// - Printer control operations (home, move, temperature, print management)
    /// - History tracking and job management via backend-specific capabilities
    /// - CSV/JSON export for printer configurations
    /// - Bulk import with duplicate handling (skip, overwrite, error)
    /// - Camera integration with URL discovery and snapshot capture
    /// - Circuit breaker pattern for fault tolerance
    /// - Status caching and fallback for improved reliability
    /// Uses BackendClientFactory and BackendCapabilityFactory for polymorphic backend access.
    /// Coordinates with MultiPrinterStatusCoordinator for efficient status updates.
    /// </remarks>
    public class PrintersService : IPrintersService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly Catalog.ICatalogService _catalogService;
        private readonly IBackendClientFactory _backendFactory;
        private readonly IBackendCapabilityFactory _capabilityFactory;
        private readonly ICircuitBreakerService _circuitBreaker;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Farm.Infrastructure.Telemetry.IUnifiedLoggingService _logger;
        private readonly AutoMapper.IMapper _mapper;
        private readonly IPrinterStatusBroadcaster _broadcaster;
        private readonly IMultiPrinterStatusCoordinator _coordinator;
        private readonly IPrinterStatusFallbackService _fallbackService;
        private readonly IPrinterStatusClientFactory _statusClientFactory;
        private readonly Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader _statusCache;
        private readonly Farm.Infrastructure.Services.Locations.ILocationService _locationService;

        public PrintersService(
            IUnitOfWork unitOfWork,
            IBackendClientFactory backendFactory,
            IBackendCapabilityFactory capabilityFactory,
            ICircuitBreakerService circuitBreaker,
            Catalog.ICatalogService catalogService,
            IHttpClientFactory httpClientFactory,
            Farm.Infrastructure.Telemetry.IUnifiedLoggingService logger,
            AutoMapper.IMapper mapper,
            IPrinterStatusBroadcaster broadcaster,
            IMultiPrinterStatusCoordinator coordinator,
            IPrinterStatusFallbackService fallbackService,
            IPrinterStatusClientFactory statusClientFactory,
            Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader statusCache,
            Farm.Infrastructure.Services.Locations.ILocationService locationService)
        {
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
            _capabilityFactory = capabilityFactory ?? throw new ArgumentNullException(nameof(capabilityFactory));
            _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
            _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _statusCache = statusCache ?? throw new ArgumentNullException(nameof(statusCache));
            _fallbackService = fallbackService ?? throw new ArgumentNullException(nameof(fallbackService));
            _statusClientFactory = statusClientFactory ?? throw new ArgumentNullException(nameof(statusClientFactory));
            _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));
        }

        /// <summary>
        /// Gets the appropriate backend client for a printer based on its backend type.
        /// Returns the generic IBackendClient which should be cast to capability interfaces as needed.
        /// </summary>
        private IBackendClient GetBackendClient(PrinterBackend backend)
        {
            return _backendFactory.GetClient(backend);
        }

        // History helpers moved from controller: delegate to appropriate backend client
        /// <summary>
        /// Retrieves print job history for a printer from its backend API.
        /// </summary>
        /// <param name="printerId">Unique printer identifier (GUID)</param>
        /// <param name="limit">Maximum number of history entries to return</param>
        /// <param name="start">Starting index for pagination</param>
        /// <param name="since">Filter jobs since this timestamp</param>
        /// <param name="before">Filter jobs before this timestamp</param>
        /// <param name="order">Sort order ("asc" or "desc")</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>History list response with jobs and pagination metadata</returns>
        /// <exception cref="KeyNotFoundException">Thrown when printer not found</exception>
        /// <exception cref="NotSupportedException">Thrown when backend does not support history capability</exception>
        /// <remarks>
        /// Requires backend to implement IHistoryCapability interface.
        /// Currently supported by Moonraker and PrusaLink backends.
        /// Uses circuit breaker for fault tolerance against unavailable backends.
        /// </remarks>
        public async Task<HistoryListResponse> GetHistoryListAsync(Guid printerId, int? limit, int? start, DateTime? since, DateTime? before, string? order, CancellationToken ct)
        {
            Printer? printer = await FindByIdAsync(printerId, ct).ConfigureAwait(false);
            if (printer == null)
            {
                throw new KeyNotFoundException();
            }

            try
            {
                var backend = (PrinterBackend)printer.Backend;

                // Use factory to get strongly-typed history client
                if (_capabilityFactory.TryGetHistoryClientTyped(backend, out var historyClient))
                {
                    HistoryListResponse? response = await historyClient!.GetHistoryListAsync(printer.BackendUrl, limit, start, printer.ApiKey, ct).ConfigureAwait(false);
                    if (response == null)
                    {
                        _logger.LogWarning($"[History] No response from history API for printer {printerId}");
                        return new HistoryListResponse { Count = 0, Jobs = Array.Empty<HistoryJob>() };
                    }

                    _logger.LogInformation($"[History] Got {response.Count} jobs from {backend}");
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

        /// <summary>
        /// Retrieves detailed information for a specific print job from history.
        /// </summary>
        /// <param name="printerId">Unique printer identifier (GUID)</param>
        /// <param name="jobId">Backend-specific job identifier</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Detailed job information including status, timestamps, filament usage</returns>
        /// <exception cref="KeyNotFoundException">Thrown when printer not found</exception>
        /// <exception cref="NotSupportedException">Thrown when backend does not support history capability</exception>
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
                var backend = (PrinterBackend)printer.Backend;

                if (!_capabilityFactory.TryGetHistoryClientTyped(backend, out var historyClient))
                {
                    throw new InvalidOperationException("History is only available for backends that support it");
                }

                var job = await historyClient!.GetHistoryJobAsync(printer!.BackendUrl, jobId, printer.ApiKey, ct).ConfigureAwait(false);
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
                {
                    throw;
                }

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
                var backend = (PrinterBackend)printer.Backend;

                if (_capabilityFactory.TryGetHistoryClientTyped(backend, out var historyClient))
                {
                    HistoryTotals? totals = await historyClient!.GetHistoryTotalsAsync(printer!.BackendUrl, printer.ApiKey, ct).ConfigureAwait(false);
                    if (totals != null)
                    {
                        return totals;
                    }

                    // Fallback: get full history and calculate totals
                    HistoryListResponse? response = await historyClient.GetHistoryListAsync(printer.BackendUrl, 10000, 0, printer.ApiKey, ct).ConfigureAwait(false);
                    if (response != null)
                    {
                        return CalculateOctoPrintHistoryTotals(response.Jobs);
                    }
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

            var backend = (PrinterBackend)printer.Backend;

            if (!_capabilityFactory.TryGetHistoryClientTyped(backend, out var historyClient))
            {
                throw new InvalidOperationException("History deletion is only available for backends that support it");
            }

            return await historyClient!.DeleteHistoryJobAsync(printer!.BackendUrl, jobId, printer.ApiKey, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves all printers without related entities.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>List of all printer entities</returns>
        /// <remarks>
        /// Does not include related entities like Manufacturer or Model.
        /// Use GetAllWithIncludesAsync for complete printer data with relationships.
        /// </remarks>
        public async Task<List<Printer>> GetAllAsync(CancellationToken ct)
        {
            return await _unitOfWork.Printers.GetAllAsync(ct);
        }

        /// <summary>
        /// Retrieves all printers with related entities (Manufacturer, Model, Location).
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>List of all printer entities with eager-loaded relationships</returns>
        /// <remarks>
        /// Includes Manufacturer, Model, and Location navigation properties.
        /// Use for display scenarios requiring complete printer information.
        /// </remarks>
        public async Task<List<Printer>> GetAllWithIncludesAsync(CancellationToken ct)
        {
            return await _unitOfWork.Printers.GetAllWithIncludesAsync(ct);
        }

        public async Task<Printer?> FindByIdAsync(Guid id, CancellationToken ct)
        {
            return await _unitOfWork.Printers.FindByIdAsync(id, ct);
        }

        public async Task<Printer?> FindByIdWithIncludesAsync(Guid id, CancellationToken ct)
        {
            return await _unitOfWork.Printers.FindByIdWithIncludesAsync(id, ct);
        }

        public async Task AddAsync(Printer p, CancellationToken ct)
        {
            await _unitOfWork.Printers.AddAsync(p, ct);
        }

        public async Task RemoveAsync(Printer p, CancellationToken ct)
        {
            await _unitOfWork.Printers.RemoveAsync(p, ct);
        }

        public async Task SaveChangesAsync(CancellationToken ct)
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }

        // DEPRECATED: PrinterCapabilities methods removed - hardware specs now on Printer entity

        // ----- Orchestration methods -----
        public async Task<PrinterDto[]> GetAllWithStatusDtosAsync(CancellationToken ct)
        {
            List<Printer> items = await _unitOfWork.Printers.GetAllWithIncludesAsync(ct);

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
        /// <summary>
        /// Retrieves real-time status for a printer including temperatures, position, and job progress.
        /// </summary>
        /// <param name="id">Unique printer identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Comprehensive status DTO with real-time printer state</returns>
        /// <exception cref="KeyNotFoundException">Thrown when printer not found</exception>
        /// <remarks>
        /// Status includes:
        /// - Online/offline state
        /// - Current temperatures (hotend, bed)
        /// - Position (X, Y, Z coordinates)
        /// - Print job progress and time estimates
        /// - Firmware state (printing, idle, error)
        /// Uses status cache for improved performance; falls back to live backend query if cache miss.
        /// Returns offline status if backend unreachable or circuit breaker open.
        /// </remarks>
        public async Task<PrinterStatusDto> GetStatusDtoAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await _unitOfWork.Printers.FindByIdAsync(id, ct);
            if (p is null)
            {
                throw new KeyNotFoundException();
            }

            try
            {
                // Delegate to the appropriate backend status client
                // Each backend client is responsible for creating the PrinterStatusDto
                _logger.LogDebug($"GetStatusDtoAsync: Getting status for printer {p.Id} ({p.Name}) with backend {p.Backend}");
                var statusClient = _statusClientFactory.GetStatusClient(p.Backend);
                _logger.LogDebug($"GetStatusDtoAsync: Obtained status client {statusClient.GetType().Name} for printer {p.Id}");
                var result = await statusClient.GetPrinterStatusAsync(p, ct);
                _logger.LogDebug($"GetStatusDtoAsync: Got status for printer {p.Id}: IsOnline={result.IsOnline}, State={result.State}");
                return result;
            }
            catch (ArgumentException ex)
            {
                // Unsupported backend type
                _logger.LogWarning($"✗ Unsupported printer backend {p.Backend} for printer {p.Id} ({p.Name}): {ex.Message}");
                return new PrinterStatusDto(Id: p.Id, IsOnline: false, State: "Unsupported", Progress: null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"✗ Failed to get status for printer {p.Id} ({p.Name}): {ex.GetType().Name}: {ex.Message}");
                return new PrinterStatusDto(Id: p.Id, IsOnline: false, State: "Offline", Progress: null);
            }
        }
#pragma warning restore CS8603

        public async Task<PrinterDto> GetPrinterDtoAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await _unitOfWork.Printers.FindByIdWithIncludesAsync(id, ct);
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
            List<Printer> items = await _unitOfWork.Printers.GetAllAsync(ct);
            PrinterCameraUrlsDto[] dtos = await Task.WhenAll(items.Select(async p =>
            {
                string? streamUrl = null;
                string? snapshotUrl = null;

                var backend = (PrinterBackend)p.Backend;

                // Check if this backend supports camera operations
                var backendCapabilities = _capabilityFactory.GetSupportedCapabilities(backend);
                if ((backendCapabilities & BackendCapabilities.Camera) == BackendCapabilities.Camera)
                {
                    try
                    {
                        // Use capability factory for polymorphic camera URL retrieval
                        // Note: We return URLs as-is without validation. The presence of a URL
                        // indicates camera support. Frontend can validate accessibility.
                        if (_capabilityFactory.TryGetCameraClientTyped(backend, out var cameraClient))
                        {
                            streamUrl = await cameraClient!.GetCameraStreamUrlAsync(p!.BackendUrl, p.FrontendPort, p.ApiKey, ct).ConfigureAwait(false);
                            snapshotUrl = await cameraClient.GetCameraSnapshotUrlAsync(p.BackendUrl, p.FrontendPort, p.ApiKey, ct).ConfigureAwait(false);
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

        // DEPRECATED: SaveCapabilitiesAsync removed - hardware specs now on Printer entity

        public async Task<List<Printer>> GetPrintersForExportAsync(Guid[]? ids, CancellationToken ct)
        {
            return await _unitOfWork.Printers.GetPrintersForExportAsync(ids, ct);
        }

        public async Task<bool> ExistsByNameOrServerUrlAsync(string name, string serverUrl, CancellationToken ct)
        {
            return await _unitOfWork.Printers.ExistsByNameOrServerUrlAsync(name, serverUrl, ct);
        }

        /// <summary>
        /// Check if a printer with the same IP address already exists.
        /// Extracts IP from ServerUrl input and compares against stored IpAddress.
        /// </summary>
        public async Task<Printer?> FindByIpAddressAsync(string serverUrl, CancellationToken ct)
        {
            // Use the repository's efficient direct database query instead of loading all printers
            return await _unitOfWork.Printers.FindByIpAddressAsync(serverUrl, ct);
        }

        public async Task<PrinterFastDto[]> GetAllFastDtosAsync(CancellationToken ct)
        {
            List<Printer> items = await _unitOfWork.Printers.GetAllWithIncludesAsync(ct);
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
                        BackendUrl: p.BackendUrl,
                        FrontendUrl: p.FrontendUrl,
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
                        IsEnabled: p.IsEnabled,
                        // Camera URLs from database (discovered at registration)
                        CameraStreamUrl: p.CameraStreamUrl,
                        CameraSnapshotUrl: p.CameraSnapshotUrl));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to get status for printer {p.Id}: {ex.Message}. Using offline status.");
                    // Fallback to offline status if retrieval fails
                    dtos.Add(new PrinterFastDto(
                        Id: p.Id,
                        Name: p.Name,
                        BackendUrl: p.BackendUrl,
                        FrontendUrl: p.FrontendUrl,
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
                        IsEnabled: p.IsEnabled,
                        // Camera URLs from database (discovered at registration)
                        CameraStreamUrl: p.CameraStreamUrl,
                        CameraSnapshotUrl: p.CameraSnapshotUrl));
                }
            }

            return dtos.ToArray();
        }

        public async Task<CompletePrinterDto[]> GetAllCompleteDtosAsync(CancellationToken ct)
        {
            List<Printer> items = await _unitOfWork.Printers.GetAllWithIncludesAsync(ct);
            var dtos = new List<CompletePrinterDto>();
            var cachedStatuses = _statusCache.GetAllStatuses();

            foreach (var p in items)
            {
                try
                {
                    // Try to get cached status first (from SignalR updates)
                    // If not cached, create an offline placeholder
                    PrinterStatusDto status = cachedStatuses.TryGetValue(p.Id, out var cachedStatus)
                        ? cachedStatus
                        : new PrinterStatusDto(
                            Id: p.Id,
                            IsOnline: false,
                            State: "Loading",
                            Progress: null,
                            JobName: null,
                            ThumbnailUrl: null,
                            CameraStreamUrl: null,
                            CameraSnapshotUrl: null,
                            SpoolInfo: null);

                    // Use database camera URLs (discovered at registration) - they use frontend port
                    // Fall back to status camera URLs only if database URLs are not set
                    string? cameraStreamUrl = !string.IsNullOrEmpty(p.CameraStreamUrl)
                        ? p.CameraStreamUrl
                        : status.CameraStreamUrl;

                    dtos.Add(new CompletePrinterDto(
                        // Static configuration from database
                        Id: p.Id,
                        Name: p.Name,
                        Notes: p.Notes,
                        ManufacturerName: p.Manufacturer?.Name,
                        ModelName: p.Model?.Name,
                        Backend: MapBackendEnum(p.Backend),
                        ApiKey: p.ApiKey,
                        OriginalServerUrl: p.OriginalServerUrl,
                        IpAddress: p.IpAddress,
                        BackendPort: p.BackendPort,
                        FrontendPort: p.FrontendPort,
                        InMaintenance: p.InMaintenance,
                        IsEnabled: p.IsEnabled,

                        // Live status from cache (or placeholder if not cached yet)
                        IsOnline: status.IsOnline,
                        State: status.State,
                        Progress: status.Progress,
                        JobName: status.JobName,
                        ThumbnailUrl: status.ThumbnailUrl,
                        // Camera URL from database (correct frontend port) or fallback to status
                        CameraStreamUrl: cameraStreamUrl,
                        X: status.X,
                        Y: status.Y,
                        Z: status.Z,
                        HotendTemp: status.HotendTemp,
                        BedTemp: status.BedTemp,
                        HotendTarget: status.HotendTarget,
                        BedTarget: status.BedTarget,
                        HomedAxes: null, // Will be filled by PrinterStatusUpdate via SignalR
                        SpoolInfo: status.SpoolInfo,
                        BackendUrl: p.BackendUrl,
                        FrontendUrl: p.FrontendUrl
                    ));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to build complete DTO for printer {p.Id}: {ex.Message}. Using offline status.");
                    // Fallback to offline status if DTO building fails
                    dtos.Add(new CompletePrinterDto(
                        Id: p.Id,
                        Name: p.Name,
                        Notes: p.Notes,
                        ManufacturerName: p.Manufacturer?.Name,
                        ModelName: p.Model?.Name,
                        Backend: MapBackendEnum(p.Backend),
                        ApiKey: p.ApiKey,
                        OriginalServerUrl: p.OriginalServerUrl,
                        IpAddress: p.IpAddress,
                        BackendPort: p.BackendPort,
                        FrontendPort: p.FrontendPort,
                        InMaintenance: p.InMaintenance,
                        IsEnabled: p.IsEnabled,

                        // Offline status - but still include camera URL from database
                        IsOnline: false,
                        State: null,
                        Progress: null,
                        JobName: null,
                        ThumbnailUrl: null,
                        CameraStreamUrl: p.CameraStreamUrl, // From database
                        X: null,
                        Y: null,
                        Z: null,
                        HotendTemp: null,
                        BedTemp: null,
                        HotendTarget: null,
                        BedTarget: null,
                        HomedAxes: null,
                        SpoolInfo: null,
                        BackendUrl: p.BackendUrl,
                        FrontendUrl: p.FrontendUrl
                    ));
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
            WriteIndented = true,
        };

        public async Task<byte[]> BuildExportCsvAsync(Guid[]? ids, CancellationToken ct)
        {
            // Delegate to StreamExportToResponseAsync using a memory stream wrapper
            using MemoryStream ms = new MemoryStream();
            using StreamWriter writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);

            List<Printer> printers = await GetPrintersForExportAsync(ids, ct);
            IQueryable<Printer> query = printers.AsQueryable();

            // Export fields matching AdminCli CSV format for consistency
            List<string> headerParts = new() { "Name", "IpAddress", "Backend", "BackendPort", "FrontendPort", "ManufacturerName", "ModelName", "Notes", "ApiKey", "IsEnabled", "CameraStreamUrl", "CameraSnapshotUrl", "DateAcquired", "LocationName" };

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
                string locationName = p.Location?.Name ?? "";
                string csvLine = $"{EscapeCsvValue(p.Name)},{EscapeCsvValue(p.IpAddress)},{backendName},{backendPort},{frontendPort},{EscapeCsvValue(p.Manufacturer?.Name)},{EscapeCsvValue(p.Model?.Name)},{EscapeCsvValue(p.Notes)},{EscapeCsvValue(apiKey)},{p.IsEnabled},{EscapeCsvValue(cameraStreamUrl)},{EscapeCsvValue(cameraSnapshotUrl)},{dateAcquired},{EscapeCsvValue(locationName)}";
                await writer.WriteLineAsync(csvLine);
            }

            await writer.FlushAsync();
            return ms.ToArray();
        }

        /// <summary>
        /// Exports printers to JSON format and returns the bytes.
        /// HTTP response handling is done by the controller.
        /// </summary>
        public async Task<byte[]> BuildExportJsonAsync(Guid[]? ids, CancellationToken ct)
        {
            List<Printer> printers = await GetPrintersForExportAsync(ids, ct);

            using MemoryStream ms = new MemoryStream();
            await using StreamWriter writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);

            await writer.WriteLineAsync("[");
            bool first = true;
            // Process each printer and include toolheads data
            foreach (Printer? p in printers)
            {
                if (!first)
                {
                    await writer.WriteLineAsync(",");
                }

                first = false;
                Dictionary<string, object?> dtoDict = BuildExportPrinterDictionary(p);
                string json = JsonSerializer.Serialize(dtoDict, _exportJsonOptions);
                // Indent each printer object by 2 spaces
                string indentedJson = string.Join(Environment.NewLine, json.Split(Environment.NewLine).Select(line => "  " + line));
                await writer.WriteAsync(indentedJson);
                await writer.FlushAsync();
            }
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("]");
            await writer.FlushAsync();

            return ms.ToArray();
        }

        public async Task<PrinterWithCapabilitiesDto[]> GetPrintersWithCapabilitiesDtosAsync(Guid[]? ids, CancellationToken ct)
        {
            List<Printer> printers = await GetPrintersForExportAsync(ids, ct);

            PrinterWithCapabilitiesDto[] results = printers.Select(p =>
            {
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
                    BackendPort = p.BackendPort,
                    FrontendPort = p.FrontendPort,
                    ApiKey = p.ApiKey,
                    Notes = p.Notes,
                    // Export hardware specs from Printer instance (populated at creation time from PrinterModel)
                    Capabilities = new PrinterCapabilitiesExportDto
                    {
                        Id = p.Id, // Use printer ID as capabilities ID
                        NozzleDiameter = p.Toolheads?.FirstOrDefault(t => t.IsPrimary)?.NozzleDiameter,
                        SupportedMaterials = p.Toolheads?.FirstOrDefault(t => t.IsPrimary)?.SupportedMaterials,
                        MaxBuildVolumeX = p.MaxBuildVolumeX,
                        MaxBuildVolumeY = p.MaxBuildVolumeY,
                        MaxBuildVolumeZ = p.MaxBuildVolumeZ,
                        HasHeatedBed = p.HasHeatedBed,
                        HasEnclosure = p.HasEnclosure,
                        MultiMaterial = p.MultiMaterial,
                        SupportsAutoLeveling = p.SupportsAutoLeveling,
                        NumberOfExtruders = p.Toolheads?.Count ?? 1,
                        MaxHotendTemp = p.Toolheads?.FirstOrDefault(t => t.IsPrimary)?.MaxHotendTemp,
                        MaxBedTemp = p.MaxBedTemp,
                        CurrentMaterial = p.CurrentMaterial,
                        CurrentSpoolId = p.CurrentSpoolId,
                        IsAvailable = p.IsAvailable,
                        LastUpdated = p.LastCapabilityUpdate
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

        private static Dictionary<string, object?> BuildExportPrinterDictionary(Printer p)
        {
            Dictionary<string, object?> dict = new Dictionary<string, object?>
            {
                // Core configuration (always present)
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["ipAddress"] = p.IpAddress,
                ["serverUrl"] = p.ServerUrl,
                ["originalServerUrl"] = p.OriginalServerUrl,
                ["notes"] = p.Notes,
                ["manufacturer"] = p.Manufacturer?.Name,
                ["model"] = p.Model?.Name,
                ["locationName"] = p.Location?.Name,
                ["backend"] = p.Backend,
                ["backendPort"] = p.BackendPort,
                ["frontendPort"] = p.FrontendPort,
                ["apiKey"] = p.ApiKey,
                ["dateAcquired"] = p.DateAcquired,
                
                // Hardware specs from Printer instance (populated at creation time from PrinterModel)
                ["maxBuildVolumeX"] = p.MaxBuildVolumeX,
                ["maxBuildVolumeY"] = p.MaxBuildVolumeY,
                ["maxBuildVolumeZ"] = p.MaxBuildVolumeZ,
                ["hasHeatedBed"] = p.HasHeatedBed,
                ["hasEnclosure"] = p.HasEnclosure,
                ["multiMaterial"] = p.MultiMaterial,
                ["supportsAutoLeveling"] = p.SupportsAutoLeveling,
                
                // Material and job tracking
                ["currentMaterial"] = p.CurrentMaterial,
                ["currentSpoolId"] = p.CurrentSpoolId,
                ["isAvailable"] = p.IsAvailable,
                ["lastUpdated"] = p.LastCapabilityUpdate,
                ["maxBedTemp"] = p.MaxBedTemp,
                
                // All toolheads as array (supports multi-toolhead printers)
                ["toolheads"] = p.Toolheads?.Select(t => new Dictionary<string, object?>
                {
                    ["id"] = t.Id,
                    ["name"] = t.Name,
                    ["index"] = t.Index,
                    ["nozzleDiameter"] = t.NozzleDiameter,
                    ["maxHotendTemp"] = t.MaxHotendTemp,
                    ["supportedMaterials"] = t.SupportedMaterials,
                    ["isPrimary"] = t.IsPrimary
                }).ToList() ?? new List<Dictionary<string, object?>>()
            };

            return dict;
        }


        private static PrinterDto CreateOfflinePrinterDto(Printer p)
        {
            return new PrinterDto(
                Id: p.Id,
                Name: p.Name,
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
                BackendPort: p.BackendPort,
                FrontendPort: p.FrontendPort,
                SpoolInfo: null,
                BackendUrl: p.BackendUrl,
                FrontendUrl: p.FrontendUrl
            );
        }

        // Reuse controller's GetSpoolInfoAsync logic adapted for service

        /// <summary>
        /// Creates a new printer from DTO with automatic URL normalization, validation, and camera discovery.
        /// </summary>
        /// <param name="dto">Printer creation DTO with URLs, backend type, and configuration</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Created printer DTO with assigned ID and normalized URLs</returns>
        /// <exception cref="ArgumentNullException">Thrown when dto is null</exception>
        /// <exception cref="ArgumentException">Thrown when required fields (Name, Backend, ServerUrl) are missing</exception>
        /// <exception cref="InvalidOperationException">Thrown when printer with same name or IP already exists</exception>
        /// <remarks>
        /// Creation process:
        /// 1. Validates required fields and backend enum value
        /// 2. Normalizes ServerUrl (resolves hostname to IP, extracts ports)
        /// 3. Checks for duplicates by name and IP address
        /// 4. Resolves Manufacturer and Model from catalog (creates if not found)
        /// 5. Creates printer entity with normalized URLs
        /// 6. Initiates background camera discovery (non-blocking)
        /// 7. Broadcasts printer creation via SignalR
        /// Camera URLs discovered asynchronously and updated after initial creation.
        /// </remarks>
        public async Task<PrinterDto> CreatePrinterFromDtoAsync(CreatePrinterDto dto, CancellationToken ct)
        {
            // Check for duplicate printer by IP address
            Printer? duplicate = await FindByIpAddressAsync(dto.ServerUrl, ct);

            if (duplicate != null)
            {
                _logger.LogWarning($"Duplicate printer detected: {dto.Name} at {dto.ServerUrl} - existing printer: {duplicate.Name} ({duplicate.Id})");
                throw new InvalidOperationException($"A printer already exists at this address: {duplicate.Name}");
            }

            // resolve manufacturer/model - use Unknown if not found
            // NOTE: We use CatalogService for all catalog lookups, which provides caching
            // to avoid repeated database queries during bulk operations like CSV import.
            // We don't create new manufacturers/models - if not found, we default to "Unknown" instead.
            Guid manufacturerId = dto.ManufacturerId ?? Guid.Empty;
            if (manufacturerId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
            {
                string name = dto.NewManufacturerName!.Trim();
                // Try to find existing manufacturer from catalog service (with caching), but don't create - use Unknown if not found
                ManufacturerDto? existingMfg = await _catalogService.FindManufacturerByNameAsync(name, ct);
                if (existingMfg != null)
                {
                    manufacturerId = existingMfg.Id;
                    _logger.LogInformation($"[Import] Found existing manufacturer '{name}' with ID {manufacturerId}");
                }
                else
                {
                    _logger.LogInformation($"[Import] Manufacturer '{name}' not found - will use Unknown manufacturer");
                    // Fall through to default catalog logic below
                }
            }

            Guid modelId = dto.ModelId ?? Guid.Empty;
            if (modelId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewModelName) && manufacturerId != Guid.Empty)
            {
                string mname = dto.NewModelName!.Trim();
                // Try to find existing model from catalog service (with caching), but don't create - use Unknown if not found
                PrinterModelDto? existingModel = await _catalogService.FindModelByNameAsync(mname, manufacturerId, ct);
                if (existingModel != null)
                {
                    modelId = existingModel.Id;
                    _logger.LogInformation($"[Import] Found existing model '{mname}' with ID {modelId}");
                }
                else
                {
                    _logger.LogInformation($"[Import] Model '{mname}' not found - will use Unknown model");
                    // Fall through to default catalog logic below
                }
            }

            // Use Unknown manufacturer/model as fallback
            if (manufacturerId == Guid.Empty || modelId == Guid.Empty)
            {
                var (unknownMfgId, unknownModelId) = await _catalogService.GetDefaultCatalogIdsAsync(ct);

                if (manufacturerId == Guid.Empty)
                {
                    manufacturerId = unknownMfgId;
                    _logger.LogInformation($"[Import] Using Unknown manufacturer (ID {manufacturerId})");
                }

                if (modelId == Guid.Empty)
                {
                    modelId = unknownModelId;
                    _logger.LogInformation($"[Import] Using Unknown model (ID {modelId})");
                }
            }

            // Use ServerUrl directly - it should already be in http://host format
            string inputUrl = dto.ServerUrl;
            string resolvedBase = inputUrl;
            string? resolvedIp = null;
            try
            {
                Uri uri = new(inputUrl);
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

            // Port is managed separately via BackendPort field
            string serverUrlForStorage = resolvedBase;
            string originalUrlForStorage = inputUrl;

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
                DateAcquired = dto.DateAcquired?.Kind switch
                {
                    DateTimeKind.Utc => dto.DateAcquired,
                    DateTimeKind.Unspecified => DateTime.SpecifyKind(dto.DateAcquired.Value, DateTimeKind.Utc),
                    DateTimeKind.Local => dto.DateAcquired.Value.ToUniversalTime(),
                    _ => null
                },
                Backend = (int)dto.Backend,
                ApiKey = dto.ApiKey,
                // BackendPort MUST be set by discovery probes (always includes actual port, even if standard)
                BackendPort = dto.BackendPort ?? throw new InvalidOperationException($"BackendPort is required - discovery probes must always set it for backend {dto.Backend}"),
                FrontendPort = dto.FrontendPort,
                // Use provided camera URLs from discovery, or leave null
                CameraStreamUrl = dto.CameraStreamUrl,
                CameraSnapshotUrl = dto.CameraSnapshotUrl,
                IsEnabled = dto.IsEnabled
            };

            // Create toolheads from import data or use defaults
            if (dto.Toolheads != null && dto.Toolheads.Count > 0)
            {
                // Import toolheads from JSON export
                foreach (var toolheadDto in dto.Toolheads.OrderBy(t => t.Index))
                {
                    Toolhead toolhead = new()
                    {
                        Id = toolheadDto.Id ?? Guid.NewGuid(),
                        PrinterId = p.Id,
                        Name = toolheadDto.Name ?? $"Extruder {toolheadDto.Index + 1}",
                        Index = toolheadDto.Index,
                        NozzleDiameter = toolheadDto.NozzleDiameter ?? 0.4,
                        MaxHotendTemp = toolheadDto.MaxHotendTemp,
                        SupportedMaterials = toolheadDto.SupportedMaterials,
                        IsPrimary = toolheadDto.IsPrimary
                    };
                    p.Toolheads.Add(toolhead);
                }
                _logger.LogInformation($"[CreatePrinterFromDto] Imported {dto.Toolheads.Count} toolhead(s) for printer {p.Name}");
            }
            else
            {
                // Create a default single Toolhead for single-toolhead printers
                Toolhead defaultToolhead = new()
                {
                    Id = Guid.NewGuid(),
                    PrinterId = p.Id,
                    Name = "Extruder 1",
                    Index = 0,
                    IsPrimary = true,
                    NozzleDiameter = 0.4 // Standard default nozzle size
                };
                p.Toolheads.Add(defaultToolhead);
            }

            // Assign location if provided
            if (!string.IsNullOrWhiteSpace(dto.LocationName))
            {
                Location? location = await _locationService.FindByNameAsync(dto.LocationName.Trim(), ct);
                if (location != null)
                {
                    p.LocationId = location.Id;
                    _logger.LogInformation($"[CreatePrinterFromDto] Assigned printer {p.Name} to location {location.Name}");
                }
                else
                {
                    _logger.LogWarning($"[CreatePrinterFromDto] Location '{dto.LocationName}' not found for printer {p.Name} - printer will have no location");
                }
            }

            await AddAsync(p, ct);

            // Return offline DTO for newly imported printer (hasn't fetched status yet)
            return CreateOfflinePrinterDto(p);
        }

        // Camera discovery methods removed - handled by RefreshCameraUrlsAsync called from status polling services

        /// <summary>
        /// Retrieves a camera snapshot image from the printer.
        /// </summary>
        /// <param name="id">Unique printer identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Snapshot image as byte array (JPEG format), or null if camera unavailable or snapshot fails</returns>
        /// <exception cref="KeyNotFoundException">Thrown when printer not found</exception>
        /// <remarks>
        /// Attempts to fetch snapshot from printer's camera URL (CameraSnapshotUrl from database).
        /// Returns null if:
        /// - Camera URL not configured
        /// - Camera unreachable or returns error
        /// - Backend does not support camera capability
        /// Image format typically JPEG; client responsible for rendering.
        /// </remarks>
        public async Task<byte[]?> GetCameraSnapshotAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return null;
            }

            try
            {
                // Use capability factory for polymorphic camera snapshot retrieval
                var backendEnum = (PrinterBackend)p.Backend;
                if (_capabilityFactory.TryGetCameraClientTyped(backendEnum, out var cameraClient) && cameraClient != null)
                {
                    // Try to get camera snapshot URL from the client
                    string snapUrl = backendEnum == PrinterBackend.Moonraker
                        ? BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort)
                        : p.BackendUrl;

                    // Get camera snapshot URL using capability interface
                    string? snapshotUrl = await cameraClient.GetCameraSnapshotUrlAsync(snapUrl, p.FrontendPort, p.ApiKey, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(snapshotUrl))
                    {
                        return await FetchBytesFromUrlAsync(snapshotUrl, p.ApiKey, ct).ConfigureAwait(false);
                    }
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
                // Use capability factory for polymorphic camera URL retrieval
                var backend = (PrinterBackend)p.Backend;
                if (_capabilityFactory.TryGetCameraClientTyped(backend, out var cameraClient))
                {
                    string? streamUrl = await cameraClient!.GetCameraStreamUrlAsync(p!.BackendUrl, p.FrontendPort, p.ApiKey, ct).ConfigureAwait(false);
                    string? snapshotUrl = await cameraClient.GetCameraSnapshotUrlAsync(p.BackendUrl, p.FrontendPort, p.ApiKey, ct).ConfigureAwait(false);
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

        /// <summary>
        /// Sends printer to home position for all axes (X, Y, Z).
        /// </summary>
        /// <param name="id">Unique printer identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>True if command succeeded, false if backend unavailable or command failed</returns>
        /// <exception cref="KeyNotFoundException">Thrown when printer not found</exception>
        /// <exception cref="NotSupportedException">Thrown when backend does not support movement capability</exception>
        /// <remarks>
        /// Requires backend to implement IMovementCapability interface.
        /// Command execution depends on printer firmware state (must be idle or ready).
        /// Uses circuit breaker for fault tolerance.
        /// </remarks>
        public async Task<bool> SendHomeAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;
                var client = GetBackendClient(backend);

                if (client is not ISupportsMovement movement)
                {
                    return false;
                }

                // Use capability interface for movement
                if (backend == PrinterBackend.OctoPrint)
                {
                    return await movement.HomeAsync(p.BackendUrl, p.ApiKey).ConfigureAwait(false);
                }

                string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                return await movement.SendHomeAsync(moonrakerUrl, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to send home command to printer {id}");
                return false;
            }
        }

        public async Task<bool> HomeXYAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;
                var client = GetBackendClient(backend);

                if (client is ISupportsMovement movement)
                {
                    string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                    return await movement.HomeXYAsync(moonrakerUrl, ct).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to home XY on printer {id}");
                return false;
            }
        }

        public async Task<bool> HomeZAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;
                var client = GetBackendClient(backend);

                if (client is ISupportsMovement movement)
                {
                    string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                    return await movement.HomeZAsync(moonrakerUrl, ct).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to home Z on printer {id}");
                return false;
            }
        }

        /// <summary>
        /// Sets target temperatures for hotend and/or bed heaters.
        /// </summary>
        /// <param name="id">Unique printer identifier (GUID)</param>
        /// <param name="hotend">Target hotend temperature in Celsius, or null to leave unchanged</param>
        /// <param name="bed">Target bed temperature in Celsius, or null to leave unchanged</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>True if command succeeded, false if backend unavailable or command failed</returns>
        /// <exception cref="KeyNotFoundException">Thrown when printer not found</exception>
        /// <exception cref="NotSupportedException">Thrown when backend does not support temperature capability</exception>
        /// <remarks>
        /// Requires backend to implement ITemperatureCapability interface.
        /// Pass null for heater to skip temperature change (e.g., hotend=210, bed=null sets only hotend).
        /// Temperatures clamped to safe ranges by backend firmware (typically 0-300°C hotend, 0-120°C bed).
        /// </remarks>
        public async Task<bool> SetTempsAsync(Guid id, double? hotend, double? bed, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;
                var client = GetBackendClient(backend);

                if (backend == PrinterBackend.OctoPrint)
                {
                    // OctoPrint: Use backend-specific temperature API
                    if (client is ISupportsOctoPrintTemperature octoPrintTemp)
                    {
                        bool success = true;

                        if (bed.HasValue)
                        {
                            bool bedSuccess = await octoPrintTemp.SetBedTempAsync(p.BackendUrl, p.ApiKey ?? string.Empty, bed.Value, ct).ConfigureAwait(false);
#pragma warning disable S2589 // Boolean expression always evaluates to true
                            success = success && bedSuccess;
#pragma warning restore S2589
                        }

                        if (hotend.HasValue)
                        {
                            bool hotendSuccess = await octoPrintTemp.SetHotendTempAsync(p.BackendUrl, p.ApiKey ?? string.Empty, hotend.Value, "tool0", ct).ConfigureAwait(false);
                            success = success && hotendSuccess;
                        }

                        return success;
                    }

                    return false;
                }

                // Moonraker, PrusaLink, SDCP: use generic temperature control
                if (client is ISupportsTemperatureControl tempControl)
                {
                    string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                    return await tempControl.SetTemperaturesAsync(moonrakerUrl, hotend, bed, p.ApiKey, ct).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to set temperatures on printer {id}");
                return false;
            }
        }

        public async Task<bool> MoveAsync(Guid id, double? x, double? y, double? z, double? f, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;
                var client = GetBackendClient(backend);

                if (client is ISupportsMovement movement)
                {
                    string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                    return await movement.MoveAsync(moonrakerUrl, x, y, z, f, ct: ct).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to move printer {id}");
                return false;
            }
        }

        public async Task<bool> MoveToAsync(Guid id, double? x, double? y, double? z, double? f, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;
                var client = GetBackendClient(backend);

                if (client is ISupportsMovement movement)
                {
                    string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                    return await movement.MoveToAsync(moonrakerUrl, x, y, z, f, ct).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to move to position on printer {id}");
                return false;
            }
        }

        public async Task<bool> PauseAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;

                // Try print job control capability
                if (_capabilityFactory.TryGetControlOperationsClientTyped(backend, out var controlClient))
                {
                    return await controlClient!.PauseAsync(p!.BackendUrl, p.ApiKey, ct).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to pause print on printer {id}");
                return false;
            }
        }

        public async Task<bool> ResumeAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;

                // Try print job control capability
                if (_capabilityFactory.TryGetControlOperationsClientTyped(backend, out var controlClient))
                {
                    return await controlClient!.ResumeAsync(p.BackendUrl, p.ApiKey, ct).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to resume print on printer {id}");
                return false;
            }
        }

        public async Task<bool> EmergencyStopAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;

                // Try print job control capability
                if (_capabilityFactory.TryGetControlOperationsClientTyped(backend, out var controlClient))
                {
                    return await controlClient!.CancelAsync(p.BackendUrl, p.ApiKey, ct).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to emergency stop printer {id}");
                return false;
            }
        }

        public async Task<bool> FirmwareRestartAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;
                var client = GetBackendClient(backend);

                if (client is ISupportsControlRestart controlRestart)
                {
                    string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                    return await controlRestart.FirmwareRestartAsync(moonrakerUrl, ct).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to firmware restart printer {id}");
                return false;
            }
        }

        public async Task<bool> DisableMotorsAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;
                var client = GetBackendClient(backend);

                if (client is ISupportsGcodeExecution gcodeClient)
                {
                    string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                    return await gcodeClient.SendGcodeAsync(moonrakerUrl, "M84", ct).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to disable motors on printer {id}");
                return false;
            }
        }

        /// <summary>
        /// Starts printing a gcode file that exists on the printer's storage.
        /// </summary>
        /// <param name="id">Unique printer identifier (GUID)</param>
        /// <param name="filename">Filename of gcode file on printer (backend-specific path format)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>True if print started successfully, false if backend unavailable or file not found</returns>
        /// <exception cref="KeyNotFoundException">Thrown when printer not found</exception>
        /// <exception cref="NotSupportedException">Thrown when backend does not support print management capability</exception>
        /// <remarks>
        /// Requires backend to implement IPrintManagementCapability interface.
        /// File must already exist on printer's storage (uploaded via backend or SD card).
        /// Filename format varies by backend (Moonraker: "gcodes/file.gcode", PrusaLink: "file.gcode").
        /// </remarks>
        public async Task<bool> StartPrintFromFileAsync(Guid id, string filename, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;

                // Try start print capability
                if (_capabilityFactory.TryGetStartPrintClientTyped(backend, out var startPrintClient))
                {
                    return await startPrintClient!.StartPrintAsync(p.BackendUrl, filename, p.ApiKey, ct).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to start print from file {filename} on printer {id}");
                return false;
            }
        }



        public async Task<bool> DeletePrinterFileAsync(Guid id, string filename, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            // File deletion is not currently exposed through capability interfaces
            // This would require adding ISupportsFileDelete capability interface
            return false;
        }

        public async Task<bool> EnableCameraAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            // Camera enable/disable is not currently supported via capability interfaces
            // This would need to be implemented as a new capability interface
            return false;
        }

        /// <summary>
        /// Disables camera for a printer. Currently not supported via capability interfaces.
        /// Delegates to the same logic as EnableCameraAsync pending implementation.
        /// </summary>
        public Task<bool> DisableCameraAsync(Guid id, CancellationToken ct)
        {
            // Delegate to EnableCameraAsync as they have identical implementation
            // Both are placeholder methods pending capability interface implementation
            return EnableCameraAsync(id, ct);
        }

        public async Task<bool> UploadGcodeAsync(Guid id, string filename, Stream stream, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return false;
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;
                if (_capabilityFactory.TryGetFileUploadClientTyped(backend, out var uploadClient))
                {
                    return await uploadClient!.UploadGcodeAsync(p.BackendUrl, filename, stream, p.ApiKey, ct).ConfigureAwait(false);
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to upload file to printer {id}");
                return false;
            }
        }

        public async Task<PrinterFileDto[]> GetFileListAsync(Guid id, CancellationToken ct)
        {
            Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
            if (p == null)
            {
                return Array.Empty<PrinterFileDto>();
            }

            try
            {
                var backend = (PrinterBackend)p.Backend;
                var client = GetBackendClient(backend);

                // Check if backend supports file list
                if (client is not ISupportsFileList fileListClient)
                {
                    return Array.Empty<PrinterFileDto>();
                }

                string baseUrl = backend == PrinterBackend.Moonraker
                    ? BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort)
                    : p.BackendUrl;

                // Get file list with standardized PrinterFileInfo objects
                List<PrinterFileInfo> fileInfos = await fileListClient.GetFileListAsync(baseUrl, p.ApiKey, ct).ConfigureAwait(false);

                if (fileInfos.Count == 0)
                {
                    return Array.Empty<PrinterFileDto>();
                }

                // For Moonraker, try to get additional metadata
                if (backend == PrinterBackend.Moonraker && client is ISupportsFileMetadata metadataClient)
                {
                    List<PrinterFileDto> result = new();
                    foreach (var fileInfo in fileInfos)
                    {
                        // For now, return file info without metadata details
                        // TODO: Extend PrinterFileMetadata or ISupportsFileMetadata to support thumbnails
                        result.Add(new PrinterFileDto(fileInfo.Name, null, fileInfo.Modified?.Ticks, fileInfo.Size));
                    }

                    return result.ToArray();
                }

                // For other backends, convert PrinterFileInfo to PrinterFileDto
                return fileInfos
                    .Select(f => new PrinterFileDto(f.Name, null, f.Modified?.Ticks, f.Size))
                    .ToArray();

                // For PrusaLink and SDCP, no metadata available currently
                // PrusaLink: thumbnail retrieval requires Digest Authentication
                // According to PrusaLink OpenAPI spec, v1 endpoints (/api/v1/files/{storage}/{path})
                // require Digest Auth, NOT X-Api-Key header authentication.
                // The legacy /api/files endpoint (OctoPrint compatible) works with X-Api-Key
                // but doesn't include thumbnail metadata in the response.
                // TODO: Implement Digest Authentication support for PrusaLink to enable v1 API access
                // SDCP: no metadata API currently exposed
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to get file list for printer {id}");
                return Array.Empty<PrinterFileDto>();
            }
        }

        public async Task<ResolveHostnameResponse> ResolveHostnameAsync(string serverUrl, PrinterBackend backend, CancellationToken ct)
        {
            // Normalize the input: ensure scheme and remove port (port is stored separately in BackendPort)
            Uri uri = new(serverUrl);
            string normalizedInputUrl = $"{uri.Scheme}://{uri.Host}";

            // Parse ServerUrl to resolve hostname and extract IP
            string? resolvedIp = null;
            string resolvedBase = normalizedInputUrl;
            try
            {
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

            // Port is managed separately via BackendPort field
            return new ResolveHostnameResponse(normalizedInputUrl, resolvedIp, resolvedBase);
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
                        return UrlNormalizer.CombineUrlSmart(printerServerUrl, $"/server/files/gcodes/{thumbnailStr}");
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
                                    return UrlNormalizer.CombineUrlSmart(printerServerUrl, $"/server/files/gcodes/{thumbnailPath}");
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
                                        return UrlNormalizer.CombineUrlSmart(printerServerUrl, $"/server/files/gcodes/{relativePath}");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return null;
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

            // Process each printer sequentially to avoid DbContext concurrency issues
            for (int i = 0; i < printers.Length; i++)
            {
                try
                {
                    CreatePrinterDto printerDto = printers[i];
                    string status = "Success";
                    string? reason = null;
                    PrinterDto? createdDto = null;
                    Guid? createdPrinterId = null;

                    // Check for duplicates by IP address
                    Printer? existingByIp = await FindByIpAddressAsync(printerDto.ServerUrl, ct);
                    if (existingByIp != null)
                    {
                        if ((duplicateHandling ?? "skip") == "skip")
                        {
                            _logger.LogInformation($"[BulkCreate] Skipping duplicate printer: {printerDto.Name} (IP: {existingByIp.IpAddress})");
                            skippedCount++;
                            status = "Skipped";
                            reason = $"Printer with IP {existingByIp.IpAddress} already exists";
                        }
                        else if ((duplicateHandling ?? "skip") == "overwrite")
                        {
                            _logger.LogInformation($"[BulkCreate] Removing duplicate printer: {existingByIp.Name} (IP: {existingByIp.IpAddress})");
                            await RemoveAsync(existingByIp, ct);
                            await SaveChangesAsync(ct);
                            // Load a fresh copy of the CSV printer data (not the one we're removing)
                            // This avoids EF Core tracking conflicts when creating the new printer
                            createdDto = await CreatePrinterFromDtoAsync(printerDto, ct);
                            createdPrinterId = Guid.Parse(createdDto.Id.ToString());
                            await SaveChangesAsync(ct);
                            createdPrinters.Add(createdDto);
                            _logger.LogInformation($"[BulkCreate] Successfully created printer: {createdDto.Name}");
                        }
                        else if ((duplicateHandling ?? "skip") == "error")
                        {
                            status = "Failed";
                            reason = $"Printer with IP {existingByIp.IpAddress} already exists";
                            errorResults[i] = reason;
                        }
                    }
                    else
                    {
                        // Create the printer
                        createdDto = await CreatePrinterFromDtoAsync(printerDto, ct);
                        createdPrinterId = Guid.Parse(createdDto.Id.ToString());
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

                    // Broadcast import progress update to all connected clients
                    await _broadcaster.BroadcastPrinterImportProgressAsync(result, ct);

                    // Queue background camera discovery for successfully imported printers
                    // This is done as fire-and-forget using ThreadPool to avoid blocking the import response
                    if (createdPrinterId.HasValue && status == "Success")
                    {
                        // Skip background camera discovery during bulk import to avoid DbContext threading issues
                        // Camera discovery will happen on the next status poll from the dashboard
                        _logger.LogDebug($"[BulkCreate] Skipping background camera discovery for {printerDto.Name} - will discover on next status poll");
                    }
                }
                catch (Exception ex)
                {
                    string errorMessage;

                    // Try to extract meaningful error from database exceptions
                    if (ex.Message.Contains("constraint failed", StringComparison.OrdinalIgnoreCase) ||
                        ex.InnerException?.Message.Contains("constraint", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var dbEx = Exceptions.DatabaseConstraintException.FromEfException(ex, "Printer");
                        errorMessage = dbEx.Message;
                        if (dbEx.ConstraintName != null)
                        {
                            errorMessage += $" ({dbEx.ConstraintName} on {dbEx.PropertyName})";
                        }
                    }
                    else if (ex.InnerException != null)
                    {
                        // Show inner exception if outer is generic EF message
                        errorMessage = $"Failed to create printer: {ex.InnerException.Message}";
                    }
                    else
                    {
                        errorMessage = $"Failed to create printer: {ex.Message}";
                    }

                    errorResults[i] = errorMessage;
                    _logger.LogWarning(ex, $"[BulkCreate] Error creating printer {printers[i].Name} at index {i}: {errorMessage}");

                    var result = new
                    {
                        index = i,
                        name = printers[i].Name,
                        status = "Failed",
                        id = (string?)null,
                        reason = errorMessage
                    };
                    results.Add(result);

                    // Broadcast import progress update for failure
                    await _broadcaster.BroadcastPrinterImportProgressAsync(result, ct);
                }
                finally
                {
                    // DbContext is automatically managed by Entity Framework
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

                var backend = (PrinterBackend)printer.Backend;
                var client = GetBackendClient(backend);

                if (client is not ISupportsJobControl jobClient)
                {
                    _logger.LogWarning($"[PrintJobStatus] Backend {backend} does not support job control");
                    return null;
                }

                string url = backend == PrinterBackend.Moonraker
                    ? BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort)
                    : printer.BackendUrl;

                PrinterJob? job = await jobClient.GetJobAsync(url, printer.ApiKey, ct).ConfigureAwait(false);

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
        /// Imports printers from a stream containing CSV or JSON data.
        /// This is the core import logic that works with any Stream, not just HTTP uploads.
        /// </summary>
        /// <param name="stream">The stream containing CSV or JSON data</param>
        /// <param name="fileName">The filename (used to detect format via extension)</param>
        /// <param name="duplicateHandling">How to handle duplicates: 'skip', 'overwrite', or 'error'</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Import result with counts and error details</returns>
        public async Task<object> ImportFromStreamAsync(Stream stream, string fileName, string duplicateHandling = "skip", CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(fileName);

            if (stream.Length == 0)
            {
                throw new ArgumentException("Stream cannot be empty");
            }

            string fileExtension = Path.GetExtension(fileName).ToLowerInvariant();
            if (fileExtension != ".csv" && fileExtension != ".json")
            {
                throw new ArgumentException("File must be CSV or JSON format");
            }

            try
            {
                CreatePrinterDto[] printers;

                if (fileExtension == ".csv")
                {
                    printers = await ParseCsvStreamAsync(stream, ct);
                }
                else // JSON
                {
                    printers = await ParseJsonStreamAsync(stream, ct);
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
                _logger.LogError(ex, $"[Import] Failed to import printers from stream: {ex.Message}");
                throw;
            }
        }


        /// <summary>
        /// Parses a CSV stream into printer DTOs.
        /// Required columns: Name, IpAddress, Backend
        /// Optional columns: Notes, ManufacturerName, ModelName, ApiKey, IsEnabled, BackendPort, FrontendPort, CameraStreamUrl, CameraSnapshotUrl
        /// IDs are not portable between systems; use names instead.
        /// </summary>
        private async Task<CreatePrinterDto[]> ParseCsvStreamAsync(Stream stream, CancellationToken ct)
        {
            List<CreatePrinterDto> printers = new List<CreatePrinterDto>();
            List<string> errors = new List<string>();

            try
            {
                using (StreamReader reader = new StreamReader(stream))
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
                    int locationNameIdx = Array.IndexOf(headers, "locationname");

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

                            // Build ServerUrl from IpAddress 
                            string ipAddress = values[ipAddressIdx];
                            // Get BackendPort from CSV - use default if not provided
                            int backendPort;
                            if (backendPortIdx >= 0 && backendPortIdx < values.Length && !string.IsNullOrWhiteSpace(values[backendPortIdx]) && int.TryParse(values[backendPortIdx], out int providedPort))
                            {
                                backendPort = providedPort;
                            }
                            else
                            {
                                // Use default port based on backend type
                                backendPort = backendEnum == PrinterBackend.Moonraker ? 7125 : 80;
                            }
                            string serverUrl = $"http://{ipAddress}";

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
                                BackendPort = backendPort,
                                FrontendPort = frontendPortIdx >= 0 && frontendPortIdx < values.Length && int.TryParse(values[frontendPortIdx], out int fp) ? fp : null,
                                CameraStreamUrl = cameraStreamIdx >= 0 && cameraStreamIdx < values.Length ? values[cameraStreamIdx] : null,
                                CameraSnapshotUrl = cameraSnapshotIdx >= 0 && cameraSnapshotIdx < values.Length ? values[cameraSnapshotIdx] : null,
#pragma warning disable S6580 // SonarSource: format provider is already specified (InvariantCulture)
                                DateAcquired = dateAcquiredIdx >= 0 && dateAcquiredIdx < values.Length && DateTime.TryParse(values[dateAcquiredIdx], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime da) ? da : null,
#pragma warning restore S6580
                                LocationName = locationNameIdx >= 0 && locationNameIdx < values.Length && !string.IsNullOrWhiteSpace(values[locationNameIdx]) ? values[locationNameIdx] : null
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
        /// Parses a JSON stream into printer DTOs.
        /// Expected format: Array of printer objects with Name, ServerUrl, Backend, etc.
        /// </summary>
        private async Task<CreatePrinterDto[]> ParseJsonStreamAsync(Stream stream, CancellationToken ct)
        {
            try
            {
                using (StreamReader reader = new StreamReader(stream))
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

        /// <summary>
        /// Refreshes camera URLs for a printer by querying the backend API.
        /// Updates the stored camera URLs in the database.
        /// For Moonraker: queries /server/webcams/list API for actual configured cameras
        /// For other backends: generates static URLs based on frontend port
        /// </summary>
        public async Task<PrinterDto?> RefreshCameraUrlsAsync(Guid id, CancellationToken ct)
        {
            _logger.LogInformation($"RefreshCameraUrlsAsync: Starting refresh for printer {id}");

            Printer? printer = await FindByIdWithIncludesAsync(id, ct).ConfigureAwait(false);
            if (printer == null)
            {
                _logger.LogWarning($"RefreshCameraUrlsAsync: Printer {id} not found");
                return null;
            }

            _logger.LogInformation($"RefreshCameraUrlsAsync: Found printer {printer.Name}, Backend={printer.Backend}, ServerUrl={printer.ServerUrl}, FrontendPort={printer.FrontendPort}");

            var backend = (PrinterBackend)printer.Backend;
            string? streamUrl = null;
            string? snapshotUrl = null;

            try
            {
                // Try to use the configured camera detection interface which queries actual cameras
                if (_capabilityFactory.TryGetConfiguredCameraDetectionClient(backend, out var detectionClient) && detectionClient != null)
                {
                    _logger.LogInformation($"RefreshCameraUrlsAsync: Using configured camera detection for backend {backend}");

                    // For Moonraker, use the frontend URL (not backend port 7125)
                    string baseUrlForCamera = backend == PrinterBackend.Moonraker
                        ? BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort)
                        : printer.BackendUrl;

                    _logger.LogInformation($"RefreshCameraUrlsAsync: Using baseUrlForCamera={baseUrlForCamera}");

                    // Call the detection method - it will ONLY return URLs if cameras actually exist
                    (streamUrl, snapshotUrl) = await detectionClient.DetectConfiguredCameraUrlsAsync(
                        baseUrlForCamera,
                        printer.FrontendPort,
                        printer.ApiKey,
                        ct).ConfigureAwait(false);

                    _logger.LogInformation($"RefreshCameraUrlsAsync: Got URLs from detection - stream={streamUrl}, snapshot={snapshotUrl}");
                }
                else
                {
                    // Fallback: Use standard camera client (may return default URLs even if cameras don't exist)
                    _logger.LogWarning($"RefreshCameraUrlsAsync: Configured camera detection not available for backend {backend}, falling back to standard interface");

                    bool gotCameraClient = _capabilityFactory.TryGetCameraClientTyped(backend, out var cameraClient);
                    if (gotCameraClient && cameraClient != null)
                    {
                        string baseUrlForCamera = backend == PrinterBackend.Moonraker
                            ? BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort)
                            : printer.BackendUrl;

                        streamUrl = await cameraClient.GetCameraStreamUrlAsync(baseUrlForCamera, printer.FrontendPort, printer.ApiKey, ct).ConfigureAwait(false);
                        snapshotUrl = await cameraClient.GetCameraSnapshotUrlAsync(baseUrlForCamera, printer.FrontendPort, printer.ApiKey, ct).ConfigureAwait(false);

                        _logger.LogInformation($"RefreshCameraUrlsAsync: Got URLs from standard interface - stream={streamUrl}, snapshot={snapshotUrl}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"RefreshCameraUrlsAsync: Failed to refresh camera URLs for printer {id}: {ex.Message}");
            }

            // Update printer in database - only set URLs if they are not null (i.e., cameras actually exist)
            _logger.LogInformation($"RefreshCameraUrlsAsync: Updating database for printer {printer.Name}: CameraStreamUrl={streamUrl}, CameraSnapshotUrl={snapshotUrl}");
            printer.CameraStreamUrl = streamUrl;
            printer.CameraSnapshotUrl = snapshotUrl;
            await SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation($"RefreshCameraUrlsAsync: SaveChangesAsync completed - URLs saved: stream={!string.IsNullOrEmpty(streamUrl)}, snapshot={!string.IsNullOrEmpty(snapshotUrl)}");

            // Return updated DTO
            return await GetPrinterDtoAsync(id, ct).ConfigureAwait(false);
        }
    }
}
