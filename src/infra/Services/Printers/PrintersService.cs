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
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Printers;

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
/// <remarks>
/// Initializes a new instance of the PrintersService with all required dependencies.
/// </remarks>
/// <param name="unitOfWork">Unit of Work for database operations</param>
/// <param name="backendFactory">Factory for creating backend clients</param>
/// <param name="capabilityFactory">Factory for checking backend capabilities</param>
/// <param name="catalogService">Service for manufacturer/model lookups</param>
/// <param name="httpClientFactory">Factory for HTTP clients</param>
/// <param name="logger">Logging service for diagnostics</param>
/// <param name="broadcaster">SignalR broadcaster for real-time updates</param>
/// <param name="coordinator">Coordinator for parallel status queries</param>
/// <param name="statusClientFactory">Factory for backend-specific status clients</param>
/// <param name="statusCache">Cache reader for SignalR-updated status</param>
/// <param name="locationService">Service for location management</param>
/// <exception cref="ArgumentNullException">Thrown if any dependency is null</exception>
public class PrintersService(
    IUnitOfWork unitOfWork,
    IBackendClientFactory backendFactory,
    IBackendCapabilityFactory capabilityFactory,
    Catalog.ICatalogService catalogService,
    IHttpClientFactory httpClientFactory,
    Farm.Infrastructure.Telemetry.IUnifiedLoggingService logger,
    IPrinterStatusBroadcaster broadcaster,
    IMultiPrinterStatusCoordinator coordinator,
    IPrinterStatusClientFactory statusClientFactory,
    Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader statusCache,
    Farm.Infrastructure.Services.Locations.ILocationService locationService) : IPrintersService
{
    private readonly IUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    private readonly Catalog.ICatalogService _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
    private readonly IBackendClientFactory _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
    private readonly IBackendCapabilityFactory _capabilityFactory = capabilityFactory ?? throw new ArgumentNullException(nameof(capabilityFactory));
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly Farm.Infrastructure.Telemetry.IUnifiedLoggingService _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IPrinterStatusBroadcaster _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
    private readonly IMultiPrinterStatusCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly IPrinterStatusClientFactory _statusClientFactory = statusClientFactory ?? throw new ArgumentNullException(nameof(statusClientFactory));
    private readonly Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader _statusCache = statusCache ?? throw new ArgumentNullException(nameof(statusCache));
    private readonly Farm.Infrastructure.Services.Locations.ILocationService _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));

    /// <summary>
    /// Gets the appropriate backend client for a printer based on its backend type.
    /// Returns the generic IBackendClient which should be cast to capability interfaces as needed.
    /// </summary>
    private IBackendClient GetBackendClient(PrinterBackend backend)
    {
        return _backendFactory.GetClient(backend);
    }

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
        Printer? printer = await FindByIdAsync(printerId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException();

        try
        {
            var backend = (PrinterBackend)printer.Backend;

            // Use factory to get strongly-typed history client
            if (_capabilityFactory.TryGetHistoryClientTyped(backend, out ISupportsHistory? historyClient))
            {
                HistoryListResponse? response = await historyClient!.GetHistoryListAsync(printer.BackendUrl, limit, start, printer.ApiKey, ct).ConfigureAwait(false);
                if (response == null)
                {
                    _logger.LogWarning($"[History] No response from history API for printer {printerId}");
                    return new HistoryListResponse { Count = 0, Jobs = Array.Empty<HistoryJob>() };
                }

                _logger.LogInformation($"[History] Got {response.Count} jobs from {backend}");

                // Set ThumbnailUrl for each job
                foreach (HistoryJob job in response.Jobs)
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

        Printer? printer = await FindByIdAsync(printerId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException();

        try
        {
            var backend = (PrinterBackend)printer.Backend;

            if (!_capabilityFactory.TryGetHistoryClientTyped(backend, out ISupportsHistory? historyClient))
            {
                throw new InvalidOperationException("History is only available for backends that support it");
            }

            HistoryJob job = await historyClient!.GetHistoryJobAsync(printer!.BackendUrl, jobId, printer.ApiKey, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"History job {jobId} not found");

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

    /// <summary>
    /// Retrieves aggregate statistics for all print jobs in printer history.
    /// </summary>
    /// <param name="printerId">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Job totals including total count, time spent printing, filament used</returns>
    /// <exception cref="KeyNotFoundException">Thrown when printer not found</exception>
    /// <remarks>
    /// Calculates aggregate job statistics across all history entries.
    /// Falls back to calculating totals from full history if backend doesn't provide aggregates.
    /// Handles backends that don't support history gracefully (returns empty totals).
    /// </remarks>
    public async Task<HistoryTotals> GetHistoryTotalsAsync(Guid printerId, CancellationToken ct)
    {
        Printer? printer = await FindByIdAsync(printerId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException();

        try
        {
            var backend = (PrinterBackend)printer.Backend;

            if (_capabilityFactory.TryGetHistoryClientTyped(backend, out ISupportsHistory? historyClient))
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

    /// <summary>
    /// Deletes a specific print job from the printer's history.
    /// </summary>
    /// <param name="printerId">Unique printer identifier (GUID)</param>
    /// <param name="jobId">Backend-specific job identifier to delete</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if deletion succeeded, false if job not found or deletion failed</returns>
    /// <exception cref="KeyNotFoundException">Thrown when printer not found</exception>
    /// <exception cref="InvalidOperationException">Thrown when backend does not support history deletion</exception>
    /// <remarks>
    /// Permanently removes a job from the printer's history.
    /// Operation is immediate and non-reversible.
    /// Requires backend to support history capability.
    /// </remarks>
    public async Task<bool> DeleteHistoryJobAsync(Guid printerId, string jobId, CancellationToken ct)
    {
        Printer? printer = await FindByIdAsync(printerId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException();

        var backend = (PrinterBackend)printer.Backend;

        return !_capabilityFactory.TryGetHistoryClientTyped(backend, out ISupportsHistory? historyClient)
            ? throw new InvalidOperationException("History deletion is only available for backends that support it")
            : await historyClient!.DeleteHistoryJobAsync(printer!.BackendUrl, jobId, printer.ApiKey, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves all printers in the database.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of all printer entities</returns>
    /// <remarks>
    /// Returns basic printer entities without related navigation properties.
    /// Use GetAllWithIncludesAsync for scenarios requiring manufacturer, model, or location information.
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

    /// <summary>
    /// Retrieves all printers with Toolheads included, with tracking enabled for template updates.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of all printer entities with Toolheads, suitable for template application</returns>
    public async Task<List<Printer>> GetAllForTemplateUpdateAsync(CancellationToken ct)
    {
        return await _unitOfWork.Printers.GetAllForTemplateUpdateAsync(ct);
    }

    /// <summary>
    /// Retrieves a single printer with Toolheads included, with tracking enabled for template updates.
    /// </summary>
    /// <param name="id">The printer ID (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Printer entity with Toolheads if found; otherwise null</returns>
    public async Task<Printer?> FindByIdForTemplateUpdateAsync(Guid id, CancellationToken ct)
    {
        return await _unitOfWork.Printers.FindByIdForTemplateUpdateAsync(id, ct);
    }

    /// <summary>
    /// Retrieves a single printer by its unique identifier.
    /// </summary>
    /// <param name="id">The printer ID (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Printer entity if found; otherwise null</returns>
    /// <remarks>
    /// Returns basic printer entity without related navigation properties.
    /// Use FindByIdWithIncludesAsync for scenarios requiring manufacturer or model information.
    /// </remarks>
    public async Task<Printer?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        return await _unitOfWork.Printers.FindByIdAsync(id, ct);
    }

    /// <summary>
    /// Retrieves a single printer with related entities (Manufacturer, Model, Location).
    /// </summary>
    /// <param name="id">The printer ID (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Printer entity with eager-loaded relationships if found; otherwise null</returns>
    /// <remarks>
    /// Includes Manufacturer, Model, and Location navigation properties.
    /// Use for display scenarios requiring complete printer information.
    /// </remarks>
    public async Task<Printer?> FindByIdWithIncludesAsync(Guid id, CancellationToken ct)
    {
        return await _unitOfWork.Printers.FindByIdWithIncludesAsync(id, ct);
    }

    /// <summary>
    /// Adds a new printer to the database context (not committed until SaveChangesAsync is called).
    /// </summary>
    /// <param name="p">Printer entity to add</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <remarks>
    /// This method adds the printer to the EF Core context but does not commit the transaction.
    /// Call SaveChangesAsync to persist changes to the database.
    /// </remarks>
    public async Task AddAsync(Printer p, CancellationToken ct)
    {
        await _unitOfWork.Printers.AddAsync(p, ct);
    }

    /// <summary>
    /// Removes a printer from the database context (not committed until SaveChangesAsync is called).
    /// </summary>
    /// <param name="p">Printer entity to remove</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <remarks>
    /// This method marks the printer for deletion in the EF Core context but does not commit the transaction.
    /// Call SaveChangesAsync to persist the deletion to the database.
    /// </remarks>
    public async Task RemoveAsync(Printer p, CancellationToken ct)
    {
        await _unitOfWork.Printers.RemoveAsync(p, ct);
    }

    /// <summary>
    /// Persists all pending changes to the database.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <remarks>
    /// Commits any pending changes made via AddAsync, RemoveAsync, or direct entity modifications.
    /// This is the only method that actually writes changes to the database.
    /// </remarks>
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        await _unitOfWork.SaveChangesAsync(ct);
    }

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
#pragma warning disable CS8603
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
            IPrinterStatusClient statusClient = _statusClientFactory.GetStatusClient(p.Backend);
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

#pragma warning disable CS8603
    /// <summary>
    /// Retrieves real-time status for a printer including temperatures, position, and job progress.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID).</param>
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
        Printer? p = await _unitOfWork.Printers.FindByIdAsync(id, ct) ?? throw new KeyNotFoundException();

        try
        {
            // Delegate to the appropriate backend status client
            // Each backend client is responsible for creating the PrinterStatusDto
            _logger.LogDebug($"GetStatusDtoAsync: Getting status for printer {p.Id} ({p.Name}) with backend {p.Backend}");
            IPrinterStatusClient statusClient = _statusClientFactory.GetStatusClient(p.Backend);
            _logger.LogDebug($"GetStatusDtoAsync: Obtained status client {statusClient.GetType().Name} for printer {p.Id}");
            PrinterStatusDto result = await statusClient.GetPrinterStatusAsync(p, ct);
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

    /// <summary>
    /// Retrieves a printer DTO with full details including current real-time status.
    /// </summary>
    /// <param name="id">The printer ID (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Complete printer DTO with status information</returns>
    /// <exception cref="KeyNotFoundException">Thrown when printer not found</exception>
    /// <remarks>
    /// Delegates status retrieval to the appropriate backend status client based on printer backend type.
    /// Uses cached status when available for improved performance.
    /// </remarks>
    public async Task<PrinterDto> GetPrinterDtoAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await _unitOfWork.Printers.FindByIdWithIncludesAsync(id, ct) ?? throw new KeyNotFoundException();

        // Delegate to the appropriate backend status client
        // Each status client is responsible for retrieving typed status from its backend
        // and building the complete PrinterDto
        try
        {
            IPrinterStatusClient statusClient = _statusClientFactory.GetStatusClient(p.Backend);
            return await statusClient.GetPrinterDtoAsync(p, ct);
        }
        catch (Exception ex)
        {
            // Log and return an offline/fallback DTO so that write operations (assign/unassign)
            // don't surface transient backend errors as 500 to the client.
            _logger.LogWarning(ex, $"Failed to retrieve status for printer {p.Id}");
            return CreateOfflinePrinterDto(p);
        }
    }

    /// <summary>
    /// Retrieves camera URLs (stream and snapshot) for all printers that support camera functionality.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Array of DTOs containing printer camera URLs (may be null if camera not supported)</returns>
    /// <remarks>
    /// Queries each printer's backend to retrieve camera stream and snapshot URLs.
    /// Handles backends that don't support camera operations gracefully (returns null URLs).
    /// URLs are returned as-is without validation; frontend handles accessibility checks.
    /// </remarks>
    public async Task<PrinterCameraUrlsDto[]> GetCameraUrlsAsync(CancellationToken ct)
    {
        List<Printer> items = await _unitOfWork.Printers.GetAllAsync(ct);
        PrinterCameraUrlsDto[] dtos = await Task.WhenAll(items.Select(async p =>
        {
            string? streamUrl = null;
            string? snapshotUrl = null;

            var backend = (PrinterBackend)p.Backend;

            // Check if this backend supports camera operations
            BackendCapabilities backendCapabilities = _capabilityFactory.GetSupportedCapabilities(backend);
            if ((backendCapabilities & BackendCapabilities.Camera) == BackendCapabilities.Camera)
            {
                try
                {
                    // Use capability factory for polymorphic camera URL retrieval
                    // Note: We return URLs as-is without validation. The presence of a URL
                    // indicates camera support. Frontend can validate accessibility.
                    if (_capabilityFactory.TryGetCameraClientTyped(backend, out ISupportsCamera? cameraClient))
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

    /// <summary>
    /// Retrieves printers for bulk export operations.
    /// </summary>
    /// <param name="ids">Optional array of printer IDs to export; if null, exports all printers</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of printer entities for export with related data</returns>
    /// <remarks>
    /// Returns printers suitable for CSV or JSON export with all necessary related information.
    /// If ids array is provided, returns only those printers; otherwise returns all printers.
    /// </remarks>
    public async Task<List<Printer>> GetPrintersForExportAsync(Guid[]? ids, CancellationToken ct)
    {
        return await _unitOfWork.Printers.GetPrintersForExportAsync(ids, ct);
    }

    /// <summary>
    /// Checks if a printer with the given name or server URL already exists.
    /// </summary>
    /// <param name="name">The printer name to check</param>
    /// <param name="serverUrl">The server URL to check</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if a printer with the name or URL exists; otherwise false</returns>
    /// <remarks>
    /// Used during printer creation to prevent duplicate printer registrations.
    /// Checks for exact matches on either name or server URL.
    /// </remarks>
    public async Task<bool> ExistsByNameOrServerUrlAsync(string name, string serverUrl, CancellationToken ct)
    {
        return await _unitOfWork.Printers.ExistsByNameOrServerUrlAsync(name, serverUrl, ct);
    }

    /// <summary>
    /// Finds a printer by its ServerUrl.
    /// </summary>
    /// <param name="serverUrl">The server URL to search for</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Printer with matching ServerUrl if found; otherwise null</returns>
    /// <remarks>
    /// Uses efficient direct database query instead of loading all printers.
    /// Useful for discovering existing printers during network scanning.
    /// </remarks>
    public async Task<Printer?> FindByServerUrlAsync(string serverUrl, CancellationToken ct)
    {
        // Use the repository's efficient direct database query instead of loading all printers
        return await _unitOfWork.Printers.FindByServerUrlAsync(serverUrl, ct);
    }

    /// <summary>
    /// Retrieves all printers with their current status as fast DTOs (minimal payload).
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Array of fast DTOs containing printer info and current real-time status</returns>
    /// <remarks>
    /// Fast DTOs provide essential printer information and real-time status with minimal payload size.
    /// Used for dashboard and list views where full detail is not needed.
    /// Includes fallback to offline status if status retrieval fails for a printer.
    /// Camera URLs are retrieved from database (discovered at registration with frontend port).
    /// </remarks>
    public async Task<PrinterFastDto[]> GetAllFastDtosAsync(CancellationToken ct)
    {
        List<Printer> items = await _unitOfWork.Printers.GetAllWithIncludesAsync(ct);
        List<PrinterFastDto> dtos = [];

        foreach (Printer p in items)
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

    /// <summary>
    /// Retrieves all printers with complete status and hardware information (full detail DTOs).
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Array of complete DTOs with all printer and status information</returns>
    /// <remarks>
    /// Returns comprehensive printer details including real-time status, hardware specs,
    /// build volume, capabilities, and live temperature/position data.
    /// Uses cached status from SignalR updates for performance when available.
    /// Falls back to placeholder "Loading" status if not cached yet.
    /// Camera URLs prioritize database values (with correct frontend port) over status values.
    /// Includes fallback to offline status if DTO building fails for a printer.
    /// Used for detailed printer views and full-featured dashboards.
    /// </remarks>
    public async Task<CompletePrinterDto[]> GetAllCompleteDtosAsync(CancellationToken ct)
    {
        List<Printer> items = await _unitOfWork.Printers.GetAllWithIncludesAsync(ct);
        List<CompletePrinterDto> dtos = [];
        IReadOnlyDictionary<Guid, PrinterStatusDto> cachedStatuses = _statusCache.GetAllStatuses();

        foreach (Printer p in items)
        {
            try
            {
                // Try to get cached status first (from SignalR updates)
                // If not cached, create an offline placeholder
                PrinterStatusDto status = cachedStatuses.TryGetValue(p.Id, out PrinterStatusDto? cachedStatus)
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

                // Static configuration from database
                dtos.Add(new CompletePrinterDto(
                    Id: p.Id,
                    Name: p.Name,
                    Notes: p.Notes,
                    ManufacturerName: p.Manufacturer?.Name,
                    ModelName: p.Model?.Name,
                    Backend: MapBackendEnum(p.Backend),
                    ApiKey: p.ApiKey,
                    OriginalServerUrl: p.OriginalServerUrl,

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
                    FrontendUrl: p.FrontendUrl,
                    Location: p.Location == null ? null : new LocationSummaryDto(p.Location.Id, p.Location.Name, p.Location.Description)));
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
                    FrontendUrl: p.FrontendUrl,
                    Location: p.Location == null ? null : new LocationSummaryDto(p.Location.Id, p.Location.Name, p.Location.Description)));
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

    /// <summary>
    /// Builds a CSV export of printer configurations.
    /// </summary>
    /// <param name="ids">Optional array of printer IDs to export; if null, exports all printers</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>CSV content as UTF-8 encoded byte array</returns>
    /// <remarks>
    /// CSV format includes: Name, ServerUrl, Backend, BackendPort, FrontendPort, ManufacturerName, ModelName, Notes, ApiKey, IsEnabled, CameraStreamUrl, CameraSnapshotUrl, DateAcquired, LocationName.
    /// Format matches AdminCli CSV format for consistency across tools.
    /// Properly escapes CSV values to handle commas and quotes in string fields.
    /// </remarks>
    public async Task<byte[]> BuildExportCsvAsync(Guid[]? ids, CancellationToken ct)
    {
        // Delegate to StreamExportToResponseAsync using a memory stream wrapper
        using MemoryStream ms = new MemoryStream();
        using StreamWriter writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);

        List<Printer> printers = await GetPrintersForExportAsync(ids, ct);
        IQueryable<Printer> query = printers.AsQueryable();

        // Export fields matching discovery DTO format for consistency
        // Use IpAddress (not ServerUrl) to match discovery DTOs and be more user-friendly
        List<string> headerParts = new() { "Name", "IpAddress", "Backend", "BackendPort", "FrontendPort", "ManufacturerName", "ModelName", "Notes", "ApiKey", "IsEnabled", "CameraStreamUrl", "CameraSnapshotUrl", "DateAcquired", "LocationName" };

        await writer.WriteLineAsync(string.Join(',', headerParts));

        foreach (Printer p in query)
        {
            PrinterBackend backend = (PrinterBackend)p.Backend;
            string backendName = backend.ToString();

            // Extract IP address from ServerUrl (remove http:// prefix)
            string ipAddress = p.ServerUrl.Replace("http://", string.Empty).Replace("https://", string.Empty).TrimEnd('/');

            string backendPort = p.BackendPort.ToString();
            string frontendPort = p.FrontendPort?.ToString() ?? string.Empty;
            string apiKey = p.ApiKey ?? string.Empty;
            string cameraStreamUrl = p.CameraStreamUrl ?? string.Empty;
            string cameraSnapshotUrl = p.CameraSnapshotUrl ?? string.Empty;
            string dateAcquired = p.DateAcquired?.ToString("O") ?? string.Empty;
            string locationName = p.Location?.Name ?? string.Empty;
            string csvLine = $"{EscapeCsvValue(p.Name)},{EscapeCsvValue(ipAddress)},{backendName},{backendPort},{frontendPort},{EscapeCsvValue(p.Manufacturer?.Name)},{EscapeCsvValue(p.Model?.Name)},{EscapeCsvValue(p.Notes)},{EscapeCsvValue(apiKey)},{p.IsEnabled},{EscapeCsvValue(cameraStreamUrl)},{EscapeCsvValue(cameraSnapshotUrl)},{dateAcquired},{EscapeCsvValue(locationName)}";
            await writer.WriteLineAsync(csvLine);
        }

        await writer.FlushAsync();
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a JSON export of printer configurations.
    /// </summary>
    /// <param name="ids">Optional array of printer IDs to export; if null, exports all printers</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>JSON content as UTF-8 encoded byte array</returns>
    /// <remarks>
    /// Exports printers with all configuration details including toolhead information.
    /// Output is formatted as a JSON array suitable for re-import or documentation.
    /// Each printer includes: configuration, manufacturer/model info, camera URLs, locations.
    /// </remarks>
    /// <exception cref="JsonException">Thrown if JSON serialization fails</exception>
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

    /// <summary>
    /// Retrieves printers with complete capability information for capability-aware operations.
    /// </summary>
    /// <param name="ids">Optional array of printer IDs; if null, returns all printers with capabilities</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Array of DTOs containing printer configuration and capabilities</returns>
    /// <remarks>
    /// Includes hardware specifications (build volume, heated bed, enclosure, multi-material support).
    /// Includes material and spool tracking information.
    /// Used by UI components to show printer capabilities and supported operations.
    /// Toolhead information is populated from the printer model at creation time.
    /// </remarks>
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

                // Add import-friendly fields for re-importing
                ServerUrl = p.ServerUrl,
                BackendPort = p.BackendPort,
                FrontendPort = p.FrontendPort,
                ApiKey = p.ApiKey,
                Notes = p.Notes,

                // Export hardware specs from Printer instance (populated at creation time from PrinterModel)
                // NozzleDiameter and MaxHotendTemp are derived from the primary toolhead's component models
                Capabilities = new PrinterCapabilitiesExportDto
                {
                    Id = p.Id, // Use printer ID as capabilities ID
                    NozzleDiameter = p.Toolheads?.FirstOrDefault(t => t.IsPrimary)?.NozzleModel?.Diameter ?? 0.4,
                    SupportedMaterials = p.Toolheads?.FirstOrDefault(t => t.IsPrimary)?.SupportedMaterials,
                    MaxBuildVolumeX = p.MaxBuildVolumeX,
                    MaxBuildVolumeY = p.MaxBuildVolumeY,
                    MaxBuildVolumeZ = p.MaxBuildVolumeZ,
                    HasHeatedBed = p.HasHeatedBed,
                    HasEnclosure = p.HasEnclosure,
                    MultiMaterial = p.MultiMaterial,
                    SupportsAutoLeveling = p.SupportsAutoLeveling,
                    MaxHotendTemp = p.Toolheads?.FirstOrDefault(t => t.IsPrimary)?.HotendModel?.MaxTemp,
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

        string raw = value.Replace("\r", string.Empty).Replace("\n", " ");
        return raw.Contains(',') || raw.Contains('"') || raw.Contains('\n') ? '"' + raw.Replace("\"", "\"\"") + '"' : raw;
    }

    private static Dictionary<string, object?> BuildExportPrinterDictionary(Printer p)
    {
        Dictionary<string, object?> dict = new Dictionary<string, object?>
        {
            // Core configuration (always present)
            ["id"] = p.Id,
            ["name"] = p.Name,

            // ipAddress removed - use serverUrl
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
                ["nozzleDiameter"] = t.NozzleModel?.Diameter ?? 0.4,

                // Component model references - nozzle type comes from NozzleModel.NozzleType
                ["hotendModelId"] = t.HotendModelId,
                ["hotendModelName"] = t.HotendModel?.Name,
                ["extruderModelId"] = t.ExtruderModelId,
                ["extruderModelName"] = t.ExtruderModel?.Name,
                ["toolheadModelDefId"] = t.ToolheadModelDefId,
                ["toolheadModelDefName"] = t.ToolheadModelDef?.Name,
                ["nozzleModelId"] = t.NozzleModelId,
                ["nozzleModelName"] = t.NozzleModel?.Name,

                // Include nozzle type from the nozzle model for API compatibility
                ["nozzleType"] = t.NozzleModel?.NozzleType,
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

            BackendPort: p.BackendPort,
            FrontendPort: p.FrontendPort,
            SpoolInfo: null,
            BackendUrl: p.BackendUrl,
            FrontendUrl: p.FrontendUrl,
            Location: p.Location == null ? null : new LocationSummaryDto(p.Location.Id, p.Location.Name, p.Location.Description));
    }

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
    public async Task<PrinterDto> CreatePrinterFromDtoAsync(CreatePrinterFromDiscoveryDto dto, CancellationToken ct)
    {
        // Check for duplicate printer by IP address
        Printer? duplicate = await FindByServerUrlAsync(dto.ServerUrl, ct);

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
            (Guid unknownMfgId, Guid unknownModelId) = await _catalogService.GetDefaultCatalogIdsAsync(ct);

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
        catch
        {
        }

        // Port is managed separately via BackendPort field
        string serverUrlForStorage = resolvedBase;
        string originalUrlForStorage = inputUrl;

        // Load the PrinterModel template to copy default values from
        PrinterModelDto? modelTemplate = await _catalogService.GetModelByIdAsync(modelId, ct);
        _logger.LogDebug($"[CreatePrinterFromDto] Loaded PrinterModel template: {modelTemplate?.Name ?? "null"} for model ID {modelId}");

        Printer p = new()
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            ServerUrl = serverUrlForStorage,
            OriginalServerUrl = originalUrlForStorage,
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
            IsEnabled = dto.IsEnabled,

            // Copy hardware specifications from PrinterModel template
            MaxBuildVolumeX = modelTemplate?.MaxX,
            MaxBuildVolumeY = modelTemplate?.MaxY,
            MaxBuildVolumeZ = modelTemplate?.MaxZ,
            HasHeatedBed = modelTemplate?.HasHeatedBed ?? true,
            HasEnclosure = modelTemplate?.HasEnclosure ?? false,
            MultiMaterial = modelTemplate?.MultiMaterial ?? false,
            SupportsAutoLeveling = modelTemplate?.SupportsAutoLeveling ?? false,
            MaxPrintSpeed = modelTemplate?.MaxPrintSpeed,
            MaxBedTemp = modelTemplate?.MaxBedTemp
        };

        // Get default toolhead values from model's toolhead templates (nozzle diameter, max hotend temp, etc.)
        PrinterModelToolheadDto? primaryModelToolhead = modelTemplate?.Toolheads?.FirstOrDefault(t => t.IsPrimary) ?? modelTemplate?.Toolheads?.FirstOrDefault();

        // Create toolheads from import data or use defaults from template
        if (dto.Toolheads != null && dto.Toolheads.Count > 0)
        {
            // Import toolheads from JSON export
            foreach (CreateToolheadDto? toolheadDto in dto.Toolheads.OrderBy(t => t.Index))
            {
                Toolhead toolhead = new()
                {
                    Id = toolheadDto.Id ?? Guid.NewGuid(),
                    PrinterId = p.Id,
                    Name = toolheadDto.Name ?? $"Extruder {toolheadDto.Index + 1}",
                    Index = toolheadDto.Index,

                    // Component model references - nozzle type is derived from NozzleModelId
                    HotendModelId = toolheadDto.HotendModelId ?? primaryModelToolhead?.HotendModelId,
                    ExtruderModelId = toolheadDto.ExtruderModelId ?? primaryModelToolhead?.ExtruderModelId,
                    ToolheadModelDefId = toolheadDto.ToolheadModelDefId ?? primaryModelToolhead?.ToolheadModelDefId,
                    NozzleModelId = toolheadDto.NozzleModelId ?? primaryModelToolhead?.NozzleModelId,
                    SupportedMaterials = toolheadDto.SupportedMaterials ?? modelTemplate?.SupportedFilamentTypes,
                    IsPrimary = toolheadDto.IsPrimary
                };
                p.Toolheads.Add(toolhead);
            }

            _logger.LogInformation($"[CreatePrinterFromDto] Imported {dto.Toolheads.Count} toolhead(s) for printer {p.Name}");
        }
        else
        {
            // Create toolheads based on model template or defaults
            int numExtruders = modelTemplate?.Toolheads?.Length ?? 1;
            if (numExtruders < 1)
            {
                numExtruders = 1;
            }

            for (int i = 0; i < numExtruders; i++)
            {
                // Try to find a matching toolhead template by index, otherwise use primary
                PrinterModelToolheadDto? templateToolhead = modelTemplate?.Toolheads?.FirstOrDefault(t => t.Index == i) ?? primaryModelToolhead;

                Toolhead toolhead = new()
                {
                    Id = Guid.NewGuid(),
                    PrinterId = p.Id,
                    Name = templateToolhead?.Name ?? $"Extruder {i + 1}",
                    Index = i,
                    IsPrimary = templateToolhead?.IsPrimary ?? (i == 0),

                    // Component model references - nozzle type is derived from NozzleModelId
                    HotendModelId = templateToolhead?.HotendModelId,
                    ExtruderModelId = templateToolhead?.ExtruderModelId,
                    ToolheadModelDefId = templateToolhead?.ToolheadModelDefId,
                    NozzleModelId = templateToolhead?.NozzleModelId,
                    SupportedMaterials = templateToolhead?.SupportedMaterials ?? modelTemplate?.SupportedFilamentTypes
                };
                p.Toolheads.Add(toolhead);
            }

            _logger.LogInformation($"[CreatePrinterFromDto] Created {numExtruders} toolhead(s) from template for printer {p.Name}");
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

    /// <summary>
    /// Applies template defaults from the PrinterModel to an existing printer.
    /// Copies hardware specifications (build volume, max temps, supported materials, etc.)
    /// from the associated PrinterModel to the printer.
    /// </summary>
    /// <param name="printer">The printer entity to update (must include Toolheads if updating toolhead properties)</param>
    /// <param name="forceOverwrite">If true, overwrites all values from template. If false, only fills in null/unset values.</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if any values were updated, false if no changes were made</returns>
    public async Task<bool> ApplyModelTemplateAsync(Printer printer, bool forceOverwrite, CancellationToken ct)
    {
        if (printer.ModelId == Guid.Empty)
        {
            _logger.LogDebug($"[ApplyModelTemplate] Printer {printer.Name} has no model assigned - skipping template application");
            return false;
        }

        PrinterModelDto? modelTemplate = await _catalogService.GetModelByIdAsync(printer.ModelId, ct);
        if (modelTemplate == null)
        {
            _logger.LogWarning($"[ApplyModelTemplate] PrinterModel {printer.ModelId} not found for printer {printer.Name}");
            return false;
        }

        bool updated = false;

        // Apply hardware specifications from template
        if (modelTemplate.MaxX != null && (forceOverwrite || printer.MaxBuildVolumeX == null))
        {
            printer.MaxBuildVolumeX = modelTemplate.MaxX;
            updated = true;
        }

        if (modelTemplate.MaxY != null && (forceOverwrite || printer.MaxBuildVolumeY == null))
        {
            printer.MaxBuildVolumeY = modelTemplate.MaxY;
            updated = true;
        }

        if (modelTemplate.MaxZ != null && (forceOverwrite || printer.MaxBuildVolumeZ == null))
        {
            printer.MaxBuildVolumeZ = modelTemplate.MaxZ;
            updated = true;
        }

        if (modelTemplate.MaxPrintSpeed != null && (forceOverwrite || printer.MaxPrintSpeed == null))
        {
            printer.MaxPrintSpeed = modelTemplate.MaxPrintSpeed;
            updated = true;
        }

        if (modelTemplate.MaxBedTemp != null && (forceOverwrite || printer.MaxBedTemp == null))
        {
            printer.MaxBedTemp = modelTemplate.MaxBedTemp;
            updated = true;
        }

        // Apply boolean capabilities
        if (forceOverwrite || (!printer.HasEnclosure && modelTemplate.HasEnclosure))
        {
            printer.HasEnclosure = modelTemplate.HasEnclosure;
            updated = true;
        }

        if (forceOverwrite || (!printer.MultiMaterial && modelTemplate.MultiMaterial))
        {
            printer.MultiMaterial = modelTemplate.MultiMaterial;
            updated = true;
        }

        if (forceOverwrite || (!printer.SupportsAutoLeveling && modelTemplate.SupportsAutoLeveling))
        {
            printer.SupportsAutoLeveling = modelTemplate.SupportsAutoLeveling;
            updated = true;
        }

        if (forceOverwrite || (!printer.HasHeatedBed && modelTemplate.HasHeatedBed))
        {
            printer.HasHeatedBed = modelTemplate.HasHeatedBed;
            updated = true;
        }

        // Get default toolhead values from model's toolhead templates
        PrinterModelToolheadDto? defaultModelToolhead = modelTemplate.Toolheads?.FirstOrDefault(t => t.IsPrimary) ?? modelTemplate.Toolheads?.FirstOrDefault();

        // Apply toolhead defaults from model template
        if (printer.Toolheads?.Count > 0)
        {
            foreach (Toolhead toolhead in printer.Toolheads)
            {
                // Find matching toolhead template by index, otherwise use default
                PrinterModelToolheadDto? matchingTemplate = modelTemplate.Toolheads?.FirstOrDefault(t => t.Index == toolhead.Index) ?? defaultModelToolhead;

                // Apply NozzleModelId from template (nozzle diameter is derived from the nozzle model)
                if (matchingTemplate?.NozzleModelId != null && (forceOverwrite || toolhead.NozzleModelId == null))
                {
                    toolhead.NozzleModelId = matchingTemplate.NozzleModelId;
                    toolhead.UpdatedAt = DateTime.UtcNow;
                    updated = true;
                }

                if (modelTemplate.SupportedFilamentTypes?.Length > 0 && (forceOverwrite || toolhead.SupportedMaterials == null || toolhead.SupportedMaterials.Length == 0))
                {
                    toolhead.SupportedMaterials = modelTemplate.SupportedFilamentTypes;
                    toolhead.UpdatedAt = DateTime.UtcNow;
                    updated = true;
                }
            }
        }

        if (updated)
        {
            printer.LastCapabilityUpdate = DateTime.UtcNow;
            _logger.LogInformation($"[ApplyModelTemplate] Applied template defaults from model '{modelTemplate.Name}' to printer '{printer.Name}'");
        }
        else
        {
            _logger.LogDebug($"[ApplyModelTemplate] Printer '{printer.Name}' already has all values set - no changes needed");
        }

        return updated;
    }

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
            if (_capabilityFactory.TryGetCameraClientTyped(backendEnum, out ISupportsCamera? cameraClient) && cameraClient != null)
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
            return !resp.IsSuccessStatusCode ? null : await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Failed to fetch snapshot from {url}: {ex.Message}");
            return null;
        }
    }

    public async Task<(string? StreamUrl, string? SnapshotUrl)> GetCameraUrlsForPrinterAsync(Guid id, CancellationToken ct)
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
            if (_capabilityFactory.TryGetCameraClientTyped(backend, out ISupportsCamera? cameraClient))
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
            IBackendClient client = GetBackendClient(backend);

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

    /// <summary>
    /// Sends a home command for X and Y axes only (horizontal movement).
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if command sent successfully, false if operation not supported or printer not found</returns>
    /// <remarks>
    /// Homes the X and Y axes while leaving Z axis in current position.
    /// Useful for nozzle centering without raising the bed.
    /// Requires backend to implement ISupportsMovement capability.
    /// Returns false if printer not found or backend doesn't support movement.
    /// </remarks>
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
            IBackendClient client = GetBackendClient(backend);

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

    /// <summary>
    /// Sends a home command for Z axis only (vertical movement).
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if command sent successfully, false if operation not supported or printer not found</returns>
    /// <remarks>
    /// Homes the Z axis independently, useful for bed leveling or nozzle adjustment.
    /// Does not affect X/Y axis position.
    /// Requires backend to implement ISupportsMovement capability.
    /// Returns false if printer not found or backend doesn't support movement.
    /// </remarks>
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
            IBackendClient client = GetBackendClient(backend);

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
            IBackendClient client = GetBackendClient(backend);

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

    /// <summary>
    /// Moves the print head by specified offsets from current position.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="x">X-axis offset in millimeters, or null to skip X movement</param>
    /// <param name="y">Y-axis offset in millimeters, or null to skip Y movement</param>
    /// <param name="z">Z-axis offset in millimeters, or null to skip Z movement</param>
    /// <param name="f">Feedrate in mm/min, or null to use backend default</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if movement command succeeded, false if printer not found or backend unavailable</returns>
    /// <remarks>
    /// This is a relative movement command (moves from current position by specified amount).
    /// Requires backend to implement ISupportsMovement capability.
    /// At least one axis parameter should be provided; null values are ignored.
    /// Movement is queued and executed immediately by the printer.
    /// </remarks>
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
            IBackendClient client = GetBackendClient(backend);

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

    /// <summary>
    /// Moves the print head to specified absolute position coordinates.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="x">Target X-axis position in millimeters, or null to skip X movement</param>
    /// <param name="y">Target Y-axis position in millimeters, or null to skip Y movement</param>
    /// <param name="z">Target Z-axis position in millimeters, or null to skip Z movement</param>
    /// <param name="f">Feedrate in mm/min, or null to use backend default</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if positioning command succeeded, false if printer not found or backend unavailable</returns>
    /// <remarks>
    /// This is an absolute movement command (moves to exact coordinate, unlike MoveAsync which is relative).
    /// Requires backend to implement ISupportsMovement capability.
    /// At least one axis parameter should be provided; null values are ignored.
    /// Coordinates are typically limited to printer build volume (e.g., 0-250mm for Prusa).
    /// Movement is queued and executed immediately by the printer.
    /// </remarks>
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
            IBackendClient client = GetBackendClient(backend);

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

    /// <summary>
    /// Pauses the currently running print job.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if pause command succeeded, false if printer not found or backend unavailable</returns>
    /// <remarks>
    /// Pauses the current print job without canceling it.
    /// The job can be resumed with ResumeAsync.
    /// Requires backend to support print control capability.
    /// Print head and heaters remain active during pause.
    /// Useful for inspecting part quality, clearing jams, or adjusting nozzle height.
    /// </remarks>
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
            return _capabilityFactory.TryGetControlOperationsClientTyped(backend, out ISupportsControlOperations? controlClient)
                ? await controlClient!.PauseAsync(p!.BackendUrl, p.ApiKey, ct).ConfigureAwait(false)
                : false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to pause print on printer {id}");
            return false;
        }
    }

    /// <summary>
    /// Resumes a paused print job.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if resume command succeeded, false if printer not found or backend unavailable</returns>
    /// <remarks>
    /// Continues a print that was previously paused.
    /// Only works if print is in paused state (use GetStatusAsync to verify state).
    /// Resumes from the exact point where print was paused.
    /// Print head positions and temperatures are maintained during pause/resume cycle.
    /// </remarks>
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
            return _capabilityFactory.TryGetControlOperationsClientTyped(backend, out ISupportsControlOperations? controlClient)
                ? await controlClient!.ResumeAsync(p.BackendUrl, p.ApiKey, ct).ConfigureAwait(false)
                : false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to resume print on printer {id}");
            return false;
        }
    }

    /// <summary>
    /// Immediately stops and cancels the currently running print job.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if stop command succeeded, false if printer not found or backend unavailable</returns>
    /// <remarks>
    /// Cancels the current print job completely (cannot be resumed).
    /// Print head stays at current position; heaters cool down after stop.
    /// This is an emergency stop - irreversible and immediate.
    /// Use PauseAsync if you want to resume later.
    /// Requires backend to support print control capability.
    /// </remarks>
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
            return _capabilityFactory.TryGetControlOperationsClientTyped(backend, out ISupportsControlOperations? controlClient)
                ? await controlClient!.CancelAsync(p.BackendUrl, p.ApiKey, ct).ConfigureAwait(false)
                : false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to emergency stop printer {id}");
            return false;
        }
    }

    /// <summary>
    /// Reboots the printer's microcontroller (MCU).
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if restart command succeeded, false if printer not found or backend unavailable</returns>
    /// <remarks>
    /// Restarts the printer's main microcontroller (MCU).
    /// Requires backend to support control capability.
    /// Useful after firmware updates or to clear MCU errors.
    /// Connection may be temporarily lost during restart.
    /// API will be unavailable for a few seconds while MCU reboots.
    /// </remarks>
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
            IBackendClient client = GetBackendClient(backend);

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

    /// <summary>
    /// Disables all stepper motors on the printer.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if disable command succeeded, false if printer not found or backend unavailable</returns>
    /// <remarks>
    /// Sends M84 gcode command to disable all stepper motors.
    /// Useful for manual bed leveling, nozzle cleaning, or manual adjustment when print is complete.
    /// Motors will be re-engaged with next movement command.
    /// Requires backend to support gcode execution capability.
    /// </remarks>
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
            IBackendClient client = GetBackendClient(backend);

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
            return _capabilityFactory.TryGetStartPrintClientTyped(backend, out ISupportsStartPrint? startPrintClient)
                ? await startPrintClient!.StartPrintAsync(p.BackendUrl, filename, p.ApiKey, ct).ConfigureAwait(false)
                : false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to start print from file {filename} on printer {id}");
            return false;
        }
    }

    /// <summary>
    /// Deletes a gcode file from the printer's storage.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="filename">Filename of gcode file to delete (backend-specific path format)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if file deletion succeeded, false if not currently supported</returns>
    /// <remarks>
    /// File deletion is not currently exposed through capability interfaces.
    /// This would require adding ISupportsFileDelete capability interface.
    /// Currently returns false regardless of input parameters.
    /// TODO: Implement file deletion capability when interface is available.
    /// </remarks>
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

    /// <summary>
    /// Enables camera for a printer.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if camera was enabled, false if not currently supported</returns>
    /// <remarks>
    /// Camera enable/disable is not currently supported via capability interfaces.
    /// This would need to be implemented as a new capability interface.
    /// Currently returns false regardless of input parameters.
    /// TODO: Implement camera control capability when interface is available.
    /// </remarks>
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
    /// Disables camera for a printer.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if camera was disabled, false if not currently supported</returns>
    /// <remarks>
    /// Camera enable/disable is not currently supported via capability interfaces.
    /// Currently delegates to EnableCameraAsync as placeholder implementation.
    /// TODO: Implement camera control capability when interface is available.
    /// </remarks>
    public Task<bool> DisableCameraAsync(Guid id, CancellationToken ct)
    {
        // Delegate to EnableCameraAsync as they have identical implementation
        // Both are placeholder methods pending capability interface implementation
        return EnableCameraAsync(id, ct);
    }

    /// <summary>
    /// Uploads a gcode file to the printer's storage.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="filename">Desired filename on printer storage (backend-specific path format)</param>
    /// <param name="stream">File stream to upload</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if upload succeeded, false if backend unavailable or unsupported</returns>
    /// <remarks>
    /// Requires backend to support file upload capability.
    /// Filename format varies by backend (Moonraker: "gcodes/file.gcode", PrusaLink: "file.gcode").
    /// Stream should be open and readable; method will not close the stream.
    /// Large files may take several seconds to upload depending on network speed.
    /// </remarks>
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
            return _capabilityFactory.TryGetFileUploadClientTyped(backend, out ISupportsFileUpload? uploadClient)
                ? await uploadClient!.UploadGcodeAsync(p.BackendUrl, filename, stream, p.ApiKey, ct).ConfigureAwait(false)
                : false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to upload file to printer {id}");
            return false;
        }
    }

    /// <summary>
    /// Retrieves the list of gcode files stored on the printer.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Array of PrinterFileDto objects representing files on printer storage, or empty array if none found</returns>
    /// <remarks>
    /// Requires backend to support file list capability.
    /// Returns empty array if printer not found, backend unavailable, or no files present.
    /// File paths and formats vary by backend (Moonraker: "gcodes/", PrusaLink: flat structure).
    /// File information includes name, size, modification timestamp, and thumbnail URL (if available).
    /// Backend implementations are responsible for retrieving complete metadata including thumbnails.
    /// </remarks>
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
            IBackendClient client = GetBackendClient(backend);

            // Check if backend supports file list
            if (client is not ISupportsFileList fileListClient)
            {
                return Array.Empty<PrinterFileDto>();
            }

            string baseUrl = backend == PrinterBackend.Moonraker
                ? BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort)
                : p.BackendUrl;

            // Get file list with standardized PrinterFileInfo objects
            // Backend clients are responsible for retrieving thumbnails and converting timestamps to Unix format
            List<PrinterFileInfo> fileInfos = await fileListClient.GetFileListAsync(baseUrl, p.ApiKey, ct).ConfigureAwait(false);

            if (fileInfos.Count == 0)
            {
                return Array.Empty<PrinterFileDto>();
            }

            // Simply convert PrinterFileInfo to PrinterFileDto - backend has already provided all metadata
            return fileInfos
                .Select(f => new PrinterFileDto(f.Name, f.ThumbnailUrl, f.Modified, f.Size))
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to get file list for printer {id}");
            return Array.Empty<PrinterFileDto>();
        }
    }

    /// <summary>
    /// Downloads a gcode file from the printer's storage.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="filename">Filename of gcode file to download (backend-specific path format)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Byte array containing file contents, or null if download failed or not supported</returns>
    /// <remarks>
    /// Requires backend to support file download capability.
    /// Returns null if printer not found, backend unavailable, or file not found.
    /// File path format varies by backend (Moonraker: "gcodes/file.gcode", PrusaLink: "file.gcode").
    /// Downloaded file contents are returned as byte array in memory.
    /// Suitable for small to medium files; large files may consume significant memory.
    /// </remarks>
    public async Task<byte[]?> DownloadPrinterFileAsync(Guid id, string filename, CancellationToken ct)
    {
        Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
        if (p == null)
        {
            return null;
        }

        try
        {
            var backend = (PrinterBackend)p.Backend;
            IBackendClient client = GetBackendClient(backend);

            // Check if backend supports file download
            if (client is not ISupportsFileDownload downloadClient)
            {
                _logger.LogWarning($"Backend {backend} does not support file downloads");
                return null;
            }

            string baseUrl = backend == PrinterBackend.Moonraker
                ? BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort)
                : p.BackendUrl;

            // Download the file
            byte[]? fileContent = await downloadClient.DownloadFileAsync(baseUrl, filename, ct).ConfigureAwait(false);
            return fileContent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to download file {filename} from printer {id}");
            return null;
        }
    }

    /// <summary>
    /// Resolves printer hostname to IP address and normalizes URLs for API access.
    /// </summary>
    /// <param name="serverUrl">Server URL with hostname (e.g., "http://printername.local:7125")</param>
    /// <param name="backend">Backend type (Moonraker, PrusaLink, OctoPrint, or SDCP)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>ResolveHostnameResponse containing normalized URL, resolved IP, and base URL with IP</returns>
    /// <remarks>
    /// Performs DNS resolution of hostname to IPv4 address.
    /// Adds ".local" suffix if hostname is not an IP (mDNS/Bonjour support).
    /// Returns both URL variants: original hostname-based and IP-based.
    /// Gracefully handles resolution failures; returns original URL if DNS fails.
    /// Port information should be handled separately via BackendPort field.
    /// </remarks>
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
        catch
        {
        }

        // Port is managed separately via BackendPort field
        return new ResolveHostnameResponse(normalizedInputUrl, resolvedIp, resolvedBase);
    }

    /// <summary>
    /// Extracts thumbnail URL from gcode file metadata.
    /// </summary>
    /// <param name="metadata">Metadata dictionary from gcode file (contains thumbnail paths)</param>
    /// <param name="printerServerUrl">Base server URL for constructing absolute thumbnail URL</param>
    /// <returns>Absolute thumbnail URL if found, or null if no thumbnail available</returns>
    /// <remarks>
    /// Searches metadata for common thumbnail keys: "thumbnail", "thumbnails", "gcode_thumbnail".
    /// Supports both string and JSON array thumbnail formats.
    /// For arrays, selects the largest thumbnail by resolution (width × height).
    /// Combines relative path with server base URL to create absolute URL.
    /// Returns null if metadata is null, no thumbnail found, or all thumbnails empty.
    /// </remarks>
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
        return string.IsNullOrWhiteSpace(host)
            ? host
            : IPAddress.TryParse(host, out _) ?
            host :
            host.Contains('.', StringComparison.Ordinal) ? host : host + ".local";
    }

    /// <summary>
    /// Creates multiple printers in bulk with configurable duplicate handling.
    /// </summary>
    /// <param name="printers">Array of printer DTOs to create</param>
    /// <param name="duplicateHandling">Duplicate handling strategy: "skip" (default), "overwrite", or "error"</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Object containing counts (imported, skipped, failures) and detailed result array with status per printer</returns>
    /// <remarks>
    /// Processes printers sequentially to avoid DbContext concurrency issues.
    /// Duplicate detection is based on IP address extracted from ServerUrl.
    /// Duplicate handling modes:
    /// - "skip": Skips duplicate printers and continues with next
    /// - "overwrite": Removes existing printer with same IP and creates new one
    /// - "error": Treats duplicates as errors and stops processing that printer
    /// Broadcasts progress updates via SignalR for each printer.
    /// Camera discovery deferred to next status poll to avoid threading issues.
    /// Returns comprehensive error details for failed printers.
    /// </remarks>
    public async Task<object> BulkCreatePrintersAsync(CreatePrinterFromDiscoveryDto[] printers, string duplicateHandling = "skip", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(printers);

        List<PrinterDto> createdPrinters = [];
        Dictionary<int, string> errorResults = new Dictionary<int, string>();
        int skippedCount = 0;
        List<dynamic> results = [];

        // Process each printer sequentially to avoid DbContext concurrency issues
        for (int i = 0; i < printers.Length; i++)
        {
            try
            {
                CreatePrinterFromDiscoveryDto printerDto = printers[i];
                string status = "Imported";
                string? reason = null;
                PrinterDto? createdDto = null;
                Guid? createdPrinterId = null;

                // Check for duplicates by IP address
                Printer? existingByIp = await FindByServerUrlAsync(printerDto.ServerUrl, ct);
                if (existingByIp != null)
                {
                    if ((duplicateHandling ?? "skip") == "skip")
                    {
                        _logger.LogInformation($"[BulkCreate] Skipping duplicate printer: {printerDto.Name} (ServerUrl: {existingByIp.ServerUrl})");
                        skippedCount++;
                        status = "Skipped";
                        reason = $"Printer with ServerUrl {existingByIp.ServerUrl} already exists";
                    }
                    else if ((duplicateHandling ?? "skip") == "overwrite")
                    {
                        _logger.LogInformation($"[BulkCreate] Removing duplicate printer: {existingByIp.Name} (ServerUrl: {existingByIp.ServerUrl})");
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
                        reason = $"Printer with ServerUrl {existingByIp.ServerUrl} already exists";
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
                if (createdPrinterId.HasValue && status == "Imported")
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
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
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
            IBackendClient client = GetBackendClient(backend);

            if (client is not ISupportsJobControl jobClient)
            {
                _logger.LogWarning($"[PrintJobStatus] Backend {backend} does not support job control");
                return null;
            }

            string url = backend == PrinterBackend.Moonraker
                ? BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort)
                : printer.BackendUrl;

            PrinterJob? job = await jobClient.GetJobAsync(url, printer.ApiKey, ct).ConfigureAwait(false);

            return job != null
                ? new PrintJobStatusDto
                {
                    State = job.PrintState,
                    Progress = job.Progress,
                    JobName = job.JobName,
                    ThumbnailUrl = job.ThumbnailUrl
                }
                : null;
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
    /// Imports printers from a CSV or JSON file stream.
    /// </summary>
    /// <param name="stream">The file stream containing printer data (CSV or JSON format)</param>
    /// <param name="fileName">The file name with extension (.csv or .json) for format detection</param>
    /// <param name="duplicateHandling">Duplicate handling strategy: "skip" (default), "overwrite", or "error"</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Object containing import results with counts and detailed per-printer status</returns>
    /// <remarks>
    /// Supports CSV and JSON import formats.
    /// CSV format requires columns: Name, ServerUrl, Backend (plus optional columns for other fields).
    /// JSON format requires array of printer objects matching CreatePrinterDto schema.
    /// IDs are not portable between systems; import uses names and IP addresses for matching.
    /// Delegates to BulkCreatePrintersAsync for actual printer creation and duplicate handling.
    /// Broadcasts progress updates via SignalR during import.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown if file extension is not .csv or .json, or stream is empty</exception>
    /// <exception cref="InvalidOperationException">Thrown if file contains no valid printer entries</exception>
    public async Task<object> ImportFromStreamAsync(Stream stream, string fileName, string duplicateHandling = "skip", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(fileName);

        if (stream.Length == 0)
        {
            throw new ArgumentException("Stream cannot be empty");
        }

        string fileExtension = Path.GetExtension(fileName).ToLowerInvariant();
        if (fileExtension is not ".csv" and not ".json")
        {
            throw new ArgumentException("File must be CSV or JSON format");
        }

        try
        {
            CreatePrinterFromDiscoveryDto[] printers;

            if (fileExtension == ".csv")
            {
                printers = await ParseCsvStreamAsync(stream, ct);
            }
            else
            {
                // JSON format
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
    /// Required columns: Name, ServerUrl, Backend
    /// Optional columns: Notes, ManufacturerName, ModelName, ApiKey, IsEnabled, BackendPort, FrontendPort, CameraStreamUrl, CameraSnapshotUrl
    /// IDs are not portable between systems; use names instead.
    /// </summary>
    private async Task<CreatePrinterFromDiscoveryDto[]> ParseCsvStreamAsync(Stream stream, CancellationToken ct)
    {
        List<CreatePrinterFromDiscoveryDto> printers = [];
        List<string> errors = [];

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
                            errors.Add($"Line {lineNumber}: Insufficient columns (need at least Name, ServerUrl, Backend)");
                            continue;
                        }

                        // Validate backend
                        if (!Enum.TryParse(values[backendIdx], true, out PrinterBackend backendEnum))
                        {
                            errors.Add($"Line {lineNumber}: Invalid backend '{values[backendIdx]}' (must be Moonraker, PrusaLink, or SDCP)");
                            continue;
                        }

                        // IpAddress is the required column for CSV import
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

                        CreatePrinterFromDiscoveryDto printer = new()
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
    private async Task<CreatePrinterFromDiscoveryDto[]> ParseJsonStreamAsync(Stream stream, CancellationToken ct)
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

                CreatePrinterFromDiscoveryDto[]? printers = JsonSerializer.Deserialize<CreatePrinterFromDiscoveryDto[]>(content, options);

                return printers == null || printers.Length == 0
                    ? throw new InvalidOperationException("JSON file contains no valid printer entries")
                    : printers;
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
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
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
            if (_capabilityFactory.TryGetConfiguredCameraDetectionClient(backend, out ISupportsConfiguredCameraDetection? detectionClient) && detectionClient != null)
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

                bool gotCameraClient = _capabilityFactory.TryGetCameraClientTyped(backend, out ISupportsCamera? cameraClient);
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
