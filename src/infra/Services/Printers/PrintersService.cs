using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
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
using Farm.Infrastructure.Parsing;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
/// <param name="sensitiveDataProtector">Service for encrypting sensitive data</param>
/// <param name="spoolmanService">Service for Spoolman spool data retrieval</param>
/// <exception cref="ArgumentNullException">Thrown if any dependency is null</exception>
public class PrintersService(
    IUnitOfWork unitOfWork,
    IBackendClientFactory backendFactory,
    IBackendCapabilityFactory capabilityFactory,
    Catalog.ICatalogService catalogService,
    IHttpClientFactory httpClientFactory,
    ILogger<PrintersService> logger,
    IPrinterStatusBroadcaster broadcaster,
    IMultiPrinterStatusCoordinator coordinator,
    IPrinterStatusClientFactory statusClientFactory,
    Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader statusCache,
    Farm.Infrastructure.Services.Locations.ILocationService locationService,
    Farm.Infrastructure.Services.Security.ISensitiveDataProtector sensitiveDataProtector,
    Farm.Infrastructure.Services.Interfaces.ISpoolmanService spoolmanService) : IPrintersService
{
    private readonly IUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    private readonly Catalog.ICatalogService _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
    private readonly IBackendClientFactory _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
    private readonly IBackendCapabilityFactory _capabilityFactory = capabilityFactory ?? throw new ArgumentNullException(nameof(capabilityFactory));
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly ILogger<PrintersService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IPrinterStatusBroadcaster _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
    private readonly IMultiPrinterStatusCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly IPrinterStatusClientFactory _statusClientFactory = statusClientFactory ?? throw new ArgumentNullException(nameof(statusClientFactory));
    private readonly Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader _statusCache = statusCache ?? throw new ArgumentNullException(nameof(statusCache));
    private readonly Farm.Infrastructure.Services.Locations.ILocationService _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));
    private readonly Farm.Infrastructure.Services.Security.ISensitiveDataProtector _sensitiveDataProtector = sensitiveDataProtector ?? throw new ArgumentNullException(nameof(sensitiveDataProtector));
    private readonly Farm.Infrastructure.Services.Interfaces.ISpoolmanService _spoolmanService = spoolmanService ?? throw new ArgumentNullException(nameof(spoolmanService));

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
    /// The 'since' parameter enables incremental seeding - Moonraker supports server-side filtering,
    /// while OctoPrint requires client-side filtering.
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
                HistoryListResponse? response = await historyClient!.GetHistoryListAsync(printer.BackendUrl, limit, start, since, printer.Credential, ct).ConfigureAwait(false);
                if (response == null)
                {
                    _logger.LogWarning("[History] No response from history API for printer {PrinterId}", printerId);
                    return new HistoryListResponse { Count = 0, Jobs = Array.Empty<HistoryJob>() };
                }

                _logger.LogInformation("[History] Got {Count} jobs from {Backend}", response.Count, backend);

                // Set ThumbnailUrl for each job
                foreach (HistoryJob job in response.Jobs)
                {
                    job.ThumbnailUrl = ExtractThumbnailUrl(job.Metadata ?? new Dictionary<string, object>(), printer.ServerUrl);
                }

                return response;
            }
            else
            {
                _logger.LogWarning("[History] Printer {PrinterId} backend {PrinterBackend} does not support history", printerId, printer.Backend);
                return new HistoryListResponse { Count = 0, Jobs = Array.Empty<HistoryJob>() };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[History] Failed to retrieve history for printer {PrinterId}: {Message}", printerId, ex.Message);
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

            HistoryJob job = await historyClient!.GetHistoryJobAsync(printer!.BackendUrl, jobId, printer.Credential, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"History job {jobId} not found");

            // Set ThumbnailUrl
            job.ThumbnailUrl = ExtractThumbnailUrl(job.Metadata ?? new Dictionary<string, object>(), printer.ServerUrl);
            return job;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[History] Failed to retrieve job {JobId} for printer {PrinterId}: {Message}", jobId, printerId, ex.Message);
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
                HistoryTotals? totals = await historyClient!.GetHistoryTotalsAsync(printer!.BackendUrl, printer.Credential, ct).ConfigureAwait(false);
                if (totals != null)
                {
                    return totals;
                }

                // Fallback: get full history and calculate totals
                HistoryListResponse? response = await historyClient.GetHistoryListAsync(printer.BackendUrl, 10000, 0, since: null, printer.Credential, ct).ConfigureAwait(false);
                if (response != null)
                {
                    return CalculateOctoPrintHistoryTotals(response.Jobs);
                }
            }

            return new HistoryTotals { JobTotals = new JobTotals() };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[History] Failed to calculate totals for printer {PrinterId}: {Message}", printerId, ex.Message);
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
            : await historyClient!.DeleteHistoryJobAsync(printer!.BackendUrl, jobId, printer.Credential, ct).ConfigureAwait(false);
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
        // Repository already populates Credential property
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
        // Repository already populates Credential property
        return await _unitOfWork.Printers.GetAllWithIncludesAsync(ct);
    }

    /// <summary>
    /// Retrieves all printers with Toolheads included, with tracking enabled for template updates.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of all printer entities with Toolheads, suitable for template application</returns>
    public async Task<List<Printer>> GetAllForTemplateUpdateAsync(CancellationToken ct)
    {
        // Repository already populates Credential property
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
        // Repository already populates Credential property
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
        // Encrypt sensitive data before saving
        EncryptSensitiveData(p);
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

    /// <inheritdoc />
    public async Task SaveChangesWithRetryAsync(CancellationToken ct, int maxRetries = 5)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                await _unitOfWork.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxRetries)
            {
                attempt++;
                _logger.LogWarning(
                    "Concurrency conflict on save attempt {Attempt}/{MaxRetries}, refreshing entity values and retrying",
                    attempt, maxRetries);

                foreach (var entry in ex.Entries)
                {
                    var databaseValues = await entry.GetDatabaseValuesAsync(ct);
                    if (databaseValues is null)
                    {
                        throw; // Entity was deleted — cannot retry
                    }

                    // Accept the database's RowVersion (and any other original values)
                    // while keeping the caller's in-memory changes ("client wins").
                    entry.OriginalValues.SetValues(databaseValues);
                }

                // Exponential backoff with jitter to avoid colliding with background
                // services (AutoDispatch) that also write to the Printer row.
                int delayMs = (int)(Math.Pow(2, attempt) * 50) + RandomNumberGenerator.GetInt32(0, 50);
                await Task.Delay(delayMs, ct);
            }
        }
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
                    _logger.LogError("Error getting status for printer {PrinterName} ({PrinterId}): {Message}", printer.Name, printer.Id, ex.Message);
                    return CreateOfflinePrinterDto(printer);
                }
            },
            TimeSpan.FromSeconds(2),
            printer =>
            {
                // Timeout handler
                _logger.LogWarning("Fast timeout occurred for printer {PrinterName} ({PrinterId})", printer.Name, printer.Id);
            },
            (printer, ex) =>
            {
                // Error handler
                _logger.LogError("Error getting status for printer {PrinterName} ({PrinterId}): {Message}", printer.Name, printer.Id, ex.Message);
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
        PrinterDto dto;
        try
        {
            IPrinterStatusClient statusClient = _statusClientFactory.GetStatusClient(p.Backend);
            dto = await statusClient.GetPrinterDtoAsync(p, ct);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("Unsupported printer backend {PBackend} for printer {PId}", p.Backend, p.Id);
            dto = CreateOfflinePrinterDto(p);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to get DTO for printer {PId}: {Message}", p.Id, ex.Message);
            dto = CreateOfflinePrinterDto(p);
        }

        return dto;
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
            _logger.LogDebug("GetStatusDtoAsync: Getting status for printer {PId} ({PName}) with backend {PBackend}", p.Id, p.Name, p.Backend);
            IPrinterStatusClient statusClient = _statusClientFactory.GetStatusClient(p.Backend);
            _logger.LogDebug("GetStatusDtoAsync: Obtained status client {Name} for printer {PId}", statusClient.GetType().Name, p.Id);
            PrinterStatusDto result = await statusClient.GetPrinterStatusAsync(p, ct);
            _logger.LogDebug("GetStatusDtoAsync: Got status for printer {PId}: IsOnline={IsOnline}, State={State}", p.Id, result.IsOnline, result.State);
            return result;
        }
        catch (ArgumentException ex)
        {
            // Unsupported backend type
            _logger.LogWarning("✗ Unsupported printer backend {PBackend} for printer {PId} ({PName}): {Message}", p.Backend, p.Id, p.Name, ex.Message);
            return new PrinterStatusDto(Id: p.Id, IsOnline: false, State: "Unsupported", Progress: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("✗ Failed to get status for printer {PId} ({PName}): {Name}: {Message}", p.Id, p.Name, ex.GetType().Name, ex.Message);
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

        // Resolve camera URLs from Cameras table
        (string? camStream, string? camSnapshot) = await ResolveCameraUrlsFromTableAsync(id, ct);

        // Delegate to the appropriate backend status client
        // Each status client is responsible for retrieving typed status from its backend
        // and building the complete PrinterDto (including spool info via IManagedSpoolProvider)
        try
        {
            IPrinterStatusClient statusClient = _statusClientFactory.GetStatusClient(p.Backend);
            PrinterDto dto = await statusClient.GetPrinterDtoAsync(p, ct);

            // Override camera URLs from Cameras table when available
            if (!string.IsNullOrEmpty(camStream) || !string.IsNullOrEmpty(camSnapshot))
            {
                dto = dto with
                {
                    CameraStreamUrl = camStream ?? dto.CameraStreamUrl,
                    CameraSnapshotUrl = camSnapshot ?? dto.CameraSnapshotUrl,
                };
            }

            return dto;
        }
        catch (Exception ex)
        {
            // Log and return an offline/fallback DTO so that write operations (assign/unassign)
            // don't surface transient backend errors as 500 to the client.
            _logger.LogWarning(ex, "Failed to retrieve status for printer {PId}", p.Id);
            return CreateOfflinePrinterDto(p, camStream, camSnapshot);
        }
    }

    /// <summary>
    /// Retrieves camera URLs (stream and snapshot) for all printers from the Cameras table.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Array of DTOs containing printer camera URLs (may be null if no cameras configured)</returns>
    /// <remarks>
    /// Resolves camera URLs from the Cameras table (first enabled camera per printer).
    /// This avoids network calls to each backend and uses the persisted camera data.
    /// </remarks>
    public async Task<PrinterCameraUrlsDto[]> GetCameraUrlsAsync(CancellationToken ct)
    {
        List<Printer> items = await _unitOfWork.Printers.GetAllAsync(ct);
        PrinterCameraUrlsDto[] dtos = await Task.WhenAll(items.Select(async p =>
        {
            (string? streamUrl, string? snapshotUrl) = await ResolveCameraUrlsFromTableAsync(p.Id, ct).ConfigureAwait(false);
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
        Dictionary<Guid, (string? StreamUrl, string? SnapshotUrl)> cameraUrls = await BatchResolveCameraUrlsAsync(ct);
        List<PrinterFastDto> dtos = [];

        foreach (Printer p in items)
        {
            cameraUrls.TryGetValue(p.Id, out (string? StreamUrl, string? SnapshotUrl) cam);

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

                    CameraStreamUrl: cam.StreamUrl,
                    CameraSnapshotUrl: cam.SnapshotUrl));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to get status for printer {PId}: {Message}. Using offline status.", p.Id, ex.Message);

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

                    CameraStreamUrl: cam.StreamUrl,
                    CameraSnapshotUrl: cam.SnapshotUrl));
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
        Dictionary<Guid, (string? StreamUrl, string? SnapshotUrl)> cameraUrls = await BatchResolveCameraUrlsAsync(ct);
        List<CompletePrinterDto> dtos = [];
        IReadOnlyDictionary<Guid, PrinterStatusDto> cachedStatuses = _statusCache.GetAllStatuses();

        foreach (Printer p in items)
        {
            cameraUrls.TryGetValue(p.Id, out (string? StreamUrl, string? SnapshotUrl) cam);

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

                // Use camera URLs from Cameras table, fall back to status camera URLs
                string? cameraStreamUrl = !string.IsNullOrEmpty(cam.StreamUrl)
                    ? cam.StreamUrl
                    : status.CameraStreamUrl;

                // Static configuration from database
                dtos.Add(new CompletePrinterDto(
                    Id: p.Id,
                    Name: p.Name,
                    Notes: p.Notes,
                    ManufacturerId: p.ManufacturerId,
                    ManufacturerName: p.Manufacturer?.Name,
                    ModelId: p.ModelId,
                    ModelName: p.Model?.Name,
                    MotionType: p.Model?.MotionType.HasValue == true ? (MotionType)p.Model.MotionType.Value : null,
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
                    FileName: status.FileName ?? PrinterStatusDto.ExtractFileName(status.JobName),
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
                    SpoolInfo: status.SpoolInfo ?? await BuildDbSpoolInfoAsync(p, ct),
                    BackendUrl: p.BackendUrl,
                    FrontendUrl: p.FrontendUrl,
                    Location: p.Location == null ? null : new LocationSummaryDto(p.Location.Id, p.Location.Name, p.Location.Description),
                    ObicoEnabled: p.ObicoEnabled,
                    HasCatalogUpdate: p.Model != null && p.Model.UpdatedAt > (p.LastModelSyncAt ?? DateTime.MinValue)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to build complete DTO for printer {PId}: {Message}. Using offline status.", p.Id, ex.Message);

                // Fallback to offline status if DTO building fails
                dtos.Add(new CompletePrinterDto(
                    Id: p.Id,
                    Name: p.Name,
                    Notes: p.Notes,
                    ManufacturerId: p.ManufacturerId,
                    ManufacturerName: p.Manufacturer?.Name,
                    ModelId: p.ModelId,
                    ModelName: p.Model?.Name,
                    MotionType: p.Model?.MotionType.HasValue == true ? (MotionType)p.Model.MotionType.Value : null,
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
                    FileName: null,
                    ThumbnailUrl: null,
                    CameraStreamUrl: cam.StreamUrl, // From Cameras table
                    X: null,
                    Y: null,
                    Z: null,
                    HotendTemp: null,
                    BedTemp: null,
                    HotendTarget: null,
                    BedTarget: null,
                    HomedAxes: null,
                    SpoolInfo: await BuildDbSpoolInfoAsync(p, ct),
                    BackendUrl: p.BackendUrl,
                    FrontendUrl: p.FrontendUrl,
                    Location: p.Location == null ? null : new LocationSummaryDto(p.Location.Id, p.Location.Name, p.Location.Description),
                    ObicoEnabled: p.ObicoEnabled,
                    HasCatalogUpdate: p.Model != null && p.Model.UpdatedAt > (p.LastModelSyncAt ?? DateTime.MinValue)));
            }
        }

        return dtos.ToArray();
    }

    /// <summary>
    /// Maps an integer backend value to the PrinterBackend enum.
    /// </summary>
    private static PrinterBackend MapBackendEnum(int backendValue) => (PrinterBackend)backendValue;

    /// <summary>
    /// Encrypts sensitive data on a printer entity before saving to the database.
    /// Only encrypts ApiKey and Password fields if they are not already encrypted.
    /// </summary>
    private void EncryptSensitiveData(Printer p)
    {
        // Encrypt ApiKey if present and not already encrypted
        if (!string.IsNullOrEmpty(p.ApiKey) && !IsAlreadyEncrypted(p.ApiKey))
        {
            p.ApiKey = _sensitiveDataProtector.Protect(p.ApiKey);
        }

        // Encrypt Password if present and not already encrypted
        if (!string.IsNullOrEmpty(p.Password) && !IsAlreadyEncrypted(p.Password))
        {
            p.Password = _sensitiveDataProtector.Protect(p.Password);
        }
    }

    /// <summary>
    /// Simple heuristic to check if data is already encrypted.
    /// Data Protection produces Base64-encoded strings that are typically longer than plain text credentials.
    /// </summary>
    private static bool IsAlreadyEncrypted(string value)
    {
        // Data Protection output is Base64 and typically starts with "CfDJ8" for default configuration
        // It's also significantly longer than typical plaintext passwords/API keys
        return value.Length > 100 && value.StartsWith("CfDJ", StringComparison.Ordinal);
    }

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
    /// CSV format includes: Name, ServerUrl, Backend, BackendPort, FrontendPort, ManufacturerName, ModelName, Notes, ApiKey, Username, Password, IsEnabled, CameraStreamUrl, CameraSnapshotUrl, DateAcquired, LocationName.
    /// Format matches AdminCli CSV format for consistency across tools.
    /// Properly escapes CSV values to handle commas and quotes in string fields.
    /// </remarks>
    public async Task<byte[]> BuildExportCsvAsync(Guid[]? ids, CancellationToken ct)
    {
        // Delegate to StreamExportToResponseAsync using a memory stream wrapper
        using MemoryStream ms = new MemoryStream();
        using StreamWriter writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);

        List<Printer> printers = await GetPrintersForExportAsync(ids, ct);
        Dictionary<Guid, (string? StreamUrl, string? SnapshotUrl)> cameraBatch = await BatchResolveCameraUrlsAsync(ct);
        IQueryable<Printer> query = printers.AsQueryable();

        // Export fields matching discovery DTO format for consistency
        // Use IpAddress (not ServerUrl) to match discovery DTOs and be more user-friendly
        List<string> headerParts = new()
        {
            "Name",
            "IpAddress",
            "Backend",
            "BackendPort",
            "FrontendPort",
            "ManufacturerName",
            "ModelName",
            "Notes",
            "ApiKey",
            "Username",
            "Password",
            "IsEnabled",
            "CameraStreamUrl",
            "CameraSnapshotUrl",
            "DateAcquired",
            "LocationName"
        };

        await writer.WriteLineAsync(string.Join(',', headerParts));

        foreach (Printer p in query)
        {
            PrinterBackend backend = (PrinterBackend)p.Backend;
            string backendName = backend.ToString();

            // Extract IP address from ServerUrl (remove http:// prefix)
            string ipAddress = p.ServerUrl.Replace("http://", string.Empty).Replace("https://", string.Empty).TrimEnd('/');

            string backendPort = p.BackendPort.ToString();
            string frontendPort = p.FrontendPort?.ToString() ?? string.Empty;

            string apiKeyProtected = p.ApiKey ?? string.Empty;
            string apiKey = apiKeyProtected;
            string username = p.Username ?? string.Empty;
            string password = p.Password ?? string.Empty;

            // Decrypt for export (GetPrintersForExportAsync should already have decrypted,
            // but this is a safe extra pass to avoid exporting protected blobs).
            apiKey = string.IsNullOrWhiteSpace(apiKey) ? string.Empty : (_sensitiveDataProtector.Unprotect(apiKey) ?? apiKey);
            password = string.IsNullOrWhiteSpace(password) ? string.Empty : (_sensitiveDataProtector.Unprotect(password) ?? password);

            // PrusaLink uses digest auth (username/password). Older CSV imports sometimes placed the
            // password into ApiKey; normalize for export so re-import works.
            if (backend == PrinterBackend.PrusaLink)
            {
                // Legacy: password stored in ApiKey.
                // Only treat ApiKey as password when it looks like a protected blob, or when a username
                // is present (digest auth expected).
                if (string.IsNullOrWhiteSpace(password)
                    && !string.IsNullOrWhiteSpace(apiKey)
                    && (!string.IsNullOrWhiteSpace(username) || IsAlreadyEncrypted(apiKeyProtected)))
                {
                    password = apiKey;
                    apiKey = string.Empty;
                }

                bool hasDigestCreds = !string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password);
                if (hasDigestCreds)
                {
                    if (string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
                    {
                        username = "maker";
                    }

                    // Export digest auth only for PrusaLink (do not duplicate secrets across columns).
                    apiKey = string.Empty;
                }
            }

            cameraBatch.TryGetValue(p.Id, out (string? StreamUrl, string? SnapshotUrl) cam);
            string cameraStreamUrl = cam.StreamUrl ?? string.Empty;
            string cameraSnapshotUrl = cam.SnapshotUrl ?? string.Empty;
            string dateAcquired = p.DateAcquired?.ToString("O") ?? string.Empty;
            string locationName = p.Location?.Name ?? string.Empty;
            string csvLine = $"{EscapeCsvValue(p.Name)},{EscapeCsvValue(ipAddress)},{backendName},{backendPort},{frontendPort},{EscapeCsvValue(p.Manufacturer?.Name)},{EscapeCsvValue(p.Model?.Name)},{EscapeCsvValue(p.Notes)},{EscapeCsvValue(apiKey)},{EscapeCsvValue(username)},{EscapeCsvValue(password)},{p.IsEnabled},{EscapeCsvValue(cameraStreamUrl)},{EscapeCsvValue(cameraSnapshotUrl)},{dateAcquired},{EscapeCsvValue(locationName)}";
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
                // Identity (standard naming)
                Id = p.Id,
                Name = p.Name,
                Backend = MapBackendEnum(p.Backend),

                // Metadata (standard naming)
                ModelName = p.Model != null ? p.Model.Name ?? string.Empty : string.Empty,
                ManufacturerName = p.Manufacturer != null ? p.Manufacturer.Name : null,
                Notes = p.Notes,

                // Connection (IpAddress extracted from ServerUrl if needed)
                ServerUrl = p.ServerUrl,
                BackendPort = p.BackendPort,
                FrontendPort = p.FrontendPort,

                // Credentials
                ApiKey = p.ApiKey,
                Username = p.Username,
                Password = p.Password,

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

    private Dictionary<string, object?> BuildExportPrinterDictionary(Printer p)
    {
        var backend = (PrinterBackend)p.Backend;

        string? apiKeyProtected = p.ApiKey;
        string? apiKey = p.ApiKey;
        string? username = p.Username;
        string? password = p.Password;

        // Decrypt for export (GetPrintersForExportAsync should already have decrypted,
        // but this is a safe extra pass to avoid exporting protected blobs).
        apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : (_sensitiveDataProtector.Unprotect(apiKey) ?? apiKey);
        password = string.IsNullOrWhiteSpace(password) ? null : (_sensitiveDataProtector.Unprotect(password) ?? password);

        // Normalize legacy PrusaLink auth where password was stored in ApiKey.
        if (backend == PrinterBackend.PrusaLink)
        {
            if (string.IsNullOrWhiteSpace(password)
                && !string.IsNullOrWhiteSpace(apiKey)
                && (!string.IsNullOrWhiteSpace(username) || (!string.IsNullOrWhiteSpace(apiKeyProtected) && IsAlreadyEncrypted(apiKeyProtected))))
            {
                password = apiKey;
                apiKey = null;
            }

            bool hasDigestCreds = !string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password);
            if (hasDigestCreds)
            {
                if (string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
                {
                    username = "maker";
                }

                // Export digest auth only for PrusaLink (do not duplicate secrets across fields).
                apiKey = null;
            }
        }

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
            ["apiKey"] = apiKey,
            ["username"] = username,
            ["password"] = password,
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

    private static PrinterDto CreateOfflinePrinterDto(Printer p, string? cameraStreamUrl = null, string? cameraSnapshotUrl = null)
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
            FileName: null,
            ThumbnailUrl: null,
            CameraStreamUrl: cameraStreamUrl,
            CameraSnapshotUrl: cameraSnapshotUrl,
            X: null,
            Y: null,
            Z: null,
            HotendTemp: null,
            BedTemp: null,
            HotendTarget: null,
            BedTarget: null,
            Backend: MapBackendEnum(p.Backend),
            ApiKey: p.ApiKey,
            Username: p.Username,
            Password: p.Password,
            OriginalServerUrl: p.OriginalServerUrl,

            BackendPort: p.BackendPort,
            FrontendPort: p.FrontendPort,
            SpoolInfo: null,
            BackendUrl: p.BackendUrl,
            FrontendUrl: p.FrontendUrl,
            Location: p.Location == null ? null : new LocationSummaryDto(p.Location.Id, p.Location.Name, p.Location.Description),
            ObicoEnabled: p.ObicoEnabled,
            HasCatalogUpdate: p.Model != null && p.Model.UpdatedAt > (p.LastModelSyncAt ?? DateTime.MinValue));
    }

    /// <summary>
    /// Enriches a printer DTO with spool info from the DB when the backend didn't provide it.
    /// The database is the source of truth for spool assignments — backends may fail to sync.
    /// </summary>
    /// <summary>
    /// Builds a PrinterSpoolInfoDto from the DB's CurrentSpoolId by fetching spool details from Spoolman.
    /// Returns null if no spool is assigned or the fetch fails.
    /// Used by GetAllCompleteDtosAsync which reads from the status cache and needs DB-based spool fallback.
    /// </summary>
    private async Task<PrinterSpoolInfoDto?> BuildDbSpoolInfoAsync(Printer printer, CancellationToken ct)
    {
        if (printer.CurrentSpoolId is not { } spoolId)
        {
            return null;
        }

        try
        {
            SpoolmanSpoolDto? spool = await _spoolmanService.GetSpoolByIdAsync(spoolId, ct).ConfigureAwait(false);
            if (spool is null)
            {
                return new PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: spoolId);
            }

            return new PrinterSpoolInfoDto(
                HasActiveSpool: true,
                ActiveSpoolId: spoolId,
                SpoolName: spool.FilamentName,
                Material: spool.Material,
                ColorHex: spool.ColorHex != null ? (spool.ColorHex.StartsWith('#') ? spool.ColorHex : $"#{spool.ColorHex}") : null,
                FilamentName: spool.FilamentName,
                Vendor: spool.Vendor,
                RemainingWeightG: spool.RemainingWeightG);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to build spool info for printer {PId}, spool {SpoolId}", printer.Id, spoolId);
            return new PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: spoolId);
        }
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
            _logger.LogWarning("Duplicate printer detected: {DtoName} at {DtoServerUrl} - existing printer: {DuplicateName} ({DuplicateId})", dto.Name, dto.ServerUrl, duplicate.Name, duplicate.Id);
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
                _logger.LogInformation("[Import] Found existing manufacturer '{Name}' with ID {ManufacturerId}", name, manufacturerId);
            }
            else
            {
                _logger.LogInformation("[Import] Manufacturer '{Name}' not found - will use Unknown manufacturer", name);

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
                _logger.LogInformation("[Import] Found existing model '{Mname}' with ID {ModelId}", mname, modelId);
            }
            else
            {
                _logger.LogInformation("[Import] Model '{Mname}' not found - will use Unknown model", mname);

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
                _logger.LogInformation("[Import] Using Unknown manufacturer (ID {ManufacturerId})", manufacturerId);
            }

            if (modelId == Guid.Empty)
            {
                modelId = unknownModelId;
                _logger.LogInformation("[Import] Using Unknown model (ID {ModelId})", modelId);
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
        _logger.LogDebug("[CreatePrinterFromDto] Loaded PrinterModel template: {ModelTemplateName} for model ID {ModelId}", modelTemplate?.Name ?? "null", modelId);

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

            // Digest authentication credentials (primarily for PrusaLink)
            // Default username to "maker" for PrusaLink if password is provided but username is not
            Username = dto.Username ?? (dto.Backend == PrinterBackend.PrusaLink && !string.IsNullOrEmpty(dto.Password) ? "maker" : null),
            Password = dto.Password,

            // BackendPort MUST be set by discovery probes (always includes actual port, even if standard)
            BackendPort = dto.BackendPort ?? throw new InvalidOperationException($"BackendPort is required - discovery probes must always set it for backend {dto.Backend}"),
            FrontendPort = dto.FrontendPort,

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
            MaxBedTemp = modelTemplate?.MaxBedTemp,
            Wattage = dto.Wattage,
            MachineHourlyRate = dto.MachineHourlyRate
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

            _logger.LogInformation("[CreatePrinterFromDto] Imported {Count} toolhead(s) for printer {PName}", dto.Toolheads.Count, p.Name);
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

            _logger.LogInformation("[CreatePrinterFromDto] Created {NumExtruders} toolhead(s) from template for printer {PName}", numExtruders, p.Name);

            // Auto-create MMU virtual toolheads if MultiMaterial is enabled
            if (p.MultiMaterial)
            {
                SyncMmuVirtualToolheads(p, mmuGateCount: 4);
            }
        }

        // Assign location if provided
        if (!string.IsNullOrWhiteSpace(dto.LocationName))
        {
            Location? location = await _locationService.FindByNameAsync(dto.LocationName.Trim(), ct);
            if (location != null)
            {
                p.LocationId = location.Id;
                _logger.LogInformation("[CreatePrinterFromDto] Assigned printer {PName} to location {LocationName}", p.Name, location.Name);
            }
            else
            {
                _logger.LogWarning("[CreatePrinterFromDto] Location '{DtoLocationName}' not found for printer {PName} - printer will have no location", dto.LocationName, p.Name);
            }
        }

        await AddAsync(p, ct);

        // Create Camera entity if camera URLs were provided during discovery
        if (!string.IsNullOrEmpty(dto.CameraStreamUrl) || !string.IsNullOrEmpty(dto.CameraSnapshotUrl))
        {
            CameraSource source = MapBackendToCameraSource(dto.Backend);
            var camera = new Domain.Camera
            {
                Id = Guid.NewGuid(),
                PrinterId = p.Id,
                Name = $"{p.Name} Camera",
                StreamUrl = dto.CameraStreamUrl,
                SnapshotUrl = dto.CameraSnapshotUrl,
                IsEnabled = true,
                SortOrder = 0,
                Source = source,
                CameraType = CameraType.General,
                HealthStatus = CameraHealthStatus.Healthy,
                LastHealthCheck = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            };

            _unitOfWork.Cameras.Add(camera);
            await SaveChangesAsync(ct);
            _logger.LogInformation("[CreatePrinterFromDto] Created Camera {CameraId} for new printer {PrinterName}", camera.Id, p.Name);
        }

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
            _logger.LogDebug("[ApplyModelTemplate] Printer {PrinterName} has no model assigned - skipping template application", printer.Name);
            return false;
        }

        PrinterModelDto? modelTemplate = await _catalogService.GetModelByIdAsync(printer.ModelId, ct);
        if (modelTemplate == null)
        {
            _logger.LogWarning("[ApplyModelTemplate] PrinterModel {PrinterModelId} not found for printer {PrinterName}", printer.ModelId, printer.Name);
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

            // Auto-create MMU virtual toolheads when MultiMaterial is enabled
            if (modelTemplate.MultiMaterial)
            {
                SyncMmuVirtualToolheads(printer, mmuGateCount: 4);
            }
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

        // Always mark the sync as complete so HasCatalogUpdate clears,
        // even when the printer already has all template values.
        printer.LastModelSyncAt = DateTime.UtcNow;

        if (updated)
        {
            printer.LastCapabilityUpdate = DateTime.UtcNow;
            _logger.LogInformation("[ApplyModelTemplate] Applied template defaults from model '{ModelTemplateName}' to printer '{PrinterName}'", modelTemplate.Name, printer.Name);
        }
        else
        {
            _logger.LogDebug("[ApplyModelTemplate] Printer '{PrinterName}' already has all values set - synced without changes", printer.Name);
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
    /// Resolves the snapshot URL from the Cameras table (first enabled camera with a SnapshotUrl).
    /// Falls back to querying the backend if no camera record exists.
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
            // Resolve snapshot URL from Cameras table (source of truth)
            (_, string? snapshotUrl) = await ResolveCameraUrlsFromTableAsync(id, ct).ConfigureAwait(false);

            // Fallback to backend query if no camera record
            if (string.IsNullOrWhiteSpace(snapshotUrl))
            {
                var backendEnum = (PrinterBackend)p.Backend;
                if (_capabilityFactory.TryGetCameraClientTyped(backendEnum, out ISupportsCamera? cameraClient) && cameraClient != null)
                {
                    string snapUrl = backendEnum == PrinterBackend.Moonraker
                        ? BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort)
                        : p.BackendUrl;

                    snapshotUrl = await cameraClient.GetCameraSnapshotUrlAsync(snapUrl, p.FrontendPort, p.Credential, ct).ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrWhiteSpace(snapshotUrl))
            {
                return await FetchBytesFromUrlAsync(snapshotUrl, p.ApiKey, ct).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to get camera snapshot for printer {Id}: {Message}", id, ex.Message);
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
            _logger.LogDebug("Failed to fetch snapshot from {Url}: {Message}", url, ex.Message);
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

        // Resolve from Cameras table (source of truth)
        return await ResolveCameraUrlsFromTableAsync(id, ct).ConfigureAwait(false);
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
            if (backend == PrinterBackend.OctoPrint || backend == PrinterBackend.PrusaLink)
            {
                return await movement.HomeAsync(p.BackendUrl, p.Credential).ConfigureAwait(false);
            }

            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await movement.SendHomeAsync(moonrakerUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send home command to printer {Id}", id);
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

            if (client is not ISupportsMovement movement)
            {
                return false;
            }

            // PrusaLink and OctoPrint need credentials via apiKey parameter
            if (backend == PrinterBackend.OctoPrint || backend == PrinterBackend.PrusaLink)
            {
                return await movement.HomeXYAsync(p.BackendUrl, p.Credential, ct).ConfigureAwait(false);
            }

            // Moonraker doesn't need credentials
            string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            return await movement.HomeXYAsync(moonrakerUrl, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to home XY on printer {Id}", id);
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
                return await movement.HomeZAsync(moonrakerUrl, p.Credential, ct).ConfigureAwait(false);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to home Z on printer {Id}", id);
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
                return await tempControl.SetTemperaturesAsync(moonrakerUrl, hotend, bed, p.Credential, ct).ConfigureAwait(false);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set temperatures on printer {Id}", id);
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
            _logger.LogWarning(ex, "Failed to move printer {Id}", id);
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
                return await movement.MoveToAsync(moonrakerUrl, x, y, z, f, p.Credential, ct).ConfigureAwait(false);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to move to position on printer {Id}", id);
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
                ? await controlClient!.PauseAsync(p!.BackendUrl, p.Credential, ct).ConfigureAwait(false)
                : false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pause print on printer {Id}", id);
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
                ? await controlClient!.ResumeAsync(p.BackendUrl, p.Credential, ct).ConfigureAwait(false)
                : false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resume print on printer {Id}", id);
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
    /// Gracefully cancels the current print job (cannot be resumed).
    /// Uses CANCEL_PRINT macro on Moonraker, stop endpoint on PrusaLink, cancel command on OctoPrint.
    /// Print head stays at current position; heaters cool down after cancel.
    /// Use PauseAsync if you want to resume later.
    /// Requires backend to support print control capability.
    /// </remarks>
    public async Task<bool> CancelPrintAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
        if (p == null)
        {
            return false;
        }

        try
        {
            var backend = (PrinterBackend)p.Backend;

            // Try print job control capability - calls CancelAsync which routes to backend-specific cancel
            return _capabilityFactory.TryGetControlOperationsClientTyped(backend, out ISupportsControlOperations? controlClient)
                ? await controlClient!.CancelAsync(p.BackendUrl, p.Credential, ct).ConfigureAwait(false)
                : false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cancel print on printer {Id}", id);
            return false;
        }
    }

    /// <summary>
    /// Immediately stops the printer using emergency stop (M112).
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if emergency stop succeeded, false if printer not found or backend unavailable</returns>
    /// <remarks>
    /// This is more aggressive than CancelPrintAsync - sends M112 emergency stop command.
    /// Use only in emergencies when normal cancel is insufficient.
    /// May require firmware restart after use.
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
            IBackendClient client = GetBackendClient(backend);

            // Send M112 emergency stop via gcode execution capability
            if (client is ISupportsGcodeExecution gcodeClient)
            {
                string moonrakerUrl = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
                return await gcodeClient.SendGcodeAsync(moonrakerUrl, "M112", ct).ConfigureAwait(false);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emergency stop printer {Id}", id);
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
            _logger.LogWarning(ex, "Failed to firmware restart printer {Id}", id);
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
            _logger.LogWarning(ex, "Failed to disable motors on printer {Id}", id);
            return false;
        }
    }

    /// <summary>
    /// Sends an arbitrary G-code command to the printer firmware.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="gcode">The G-code command string to execute (e.g., "LOAD_FILAMENT", "M600")</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if command sent successfully, false if printer not found or backend unavailable</returns>
    /// <remarks>
    /// Sends raw G-code to the printer via the backend's gcode execution capability.
    /// Used for Klipper macros (LOAD_FILAMENT, UNLOAD_FILAMENT) and standard G-code commands (M600).
    /// Requires backend to support ISupportsGcodeExecution capability.
    /// </remarks>
    public async Task<bool> SendGcodeAsync(Guid id, string gcode, CancellationToken ct)
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
                return await gcodeClient.SendGcodeAsync(moonrakerUrl, gcode, ct).ConfigureAwait(false);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send G-code '{Gcode}' to printer {Id}", gcode, id);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<CommandResult> LoadFilamentAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
        if (p == null)
        {
            return new CommandResult(false, $"Printer {id} not found");
        }

        try
        {
            var backend = (PrinterBackend)p.Backend;
            IBackendClient client = GetBackendClient(backend);

            if (client is not ISupportsFilamentControl filamentClient)
            {
                return new CommandResult(false, $"Backend '{backend}' does not support filament control");
            }

            string url = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            bool result = await filamentClient.LoadFilamentAsync(url, ct).ConfigureAwait(false);
            return result
                ? new CommandResult(true, "Filament load initiated")
                : new CommandResult(false, "Printer rejected the filament load command");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load filament on printer {PName} ({Id})", p.Name, id);
            return new CommandResult(false, $"Failed to load filament: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<CommandResult> UnloadFilamentAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
        if (p == null)
        {
            return new CommandResult(false, $"Printer {id} not found");
        }

        try
        {
            var backend = (PrinterBackend)p.Backend;
            IBackendClient client = GetBackendClient(backend);

            if (client is not ISupportsFilamentControl filamentClient)
            {
                return new CommandResult(false, $"Backend '{backend}' does not support filament control");
            }

            string url = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            bool result = await filamentClient.UnloadFilamentAsync(url, ct).ConfigureAwait(false);
            return result
                ? new CommandResult(true, "Filament unload initiated")
                : new CommandResult(false, "Printer rejected the filament unload command");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unload filament on printer {PName} ({Id})", p.Name, id);
            return new CommandResult(false, $"Failed to unload filament: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<CommandResult> ChangeFilamentAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
        if (p == null)
        {
            return new CommandResult(false, $"Printer {id} not found");
        }

        try
        {
            var backend = (PrinterBackend)p.Backend;
            IBackendClient client = GetBackendClient(backend);

            if (client is not ISupportsFilamentControl filamentClient)
            {
                return new CommandResult(false, $"Backend '{backend}' does not support filament control");
            }

            string url = BuildMoonrakerUrl(p.ServerUrl, p.FrontendPort);
            bool result = await filamentClient.ChangeFilamentAsync(url, ct).ConfigureAwait(false);
            return result
                ? new CommandResult(true, "Filament change initiated")
                : new CommandResult(false, "Printer rejected the filament change command");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change filament on printer {PName} ({Id})", p.Name, id);
            return new CommandResult(false, $"Failed to change filament: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<CommandResult> SetActiveSpoolAsync(Guid id, int? spoolId, CancellationToken ct)
    {
        Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
        if (p is null)
        {
            return new CommandResult(false, $"Printer {id} not found");
        }

        // Spool exclusivity: check if another printer already has this spool loaded
        if (spoolId.HasValue)
        {
            Printer? conflicting = await _unitOfWork.Printers
                .FindByCurrentSpoolIdAsync(spoolId.Value, ct)
                .ConfigureAwait(false);

            if (conflicting is not null && conflicting.Id != id)
            {
                _logger.LogWarning(
                    "SetActiveSpoolAsync: Spool {SpoolId} is already assigned to printer {ConflictName} ({ConflictId})",
                    spoolId, conflicting.Name, conflicting.Id);
                return new CommandResult(
                    false,
                    $"Spool {spoolId} is already loaded on printer \"{conflicting.Name}\". Unload it there first.");
            }
        }

        try
        {
            // Always store spool assignment in PrintFarmer DB (works for all backends)
            p.CurrentSpoolId = spoolId;
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            // If backend supports native Spoolman, also sync to the printer
            var backend = (PrinterBackend)p.Backend;
            IBackendClient client = GetBackendClient(backend);

            if (client is ISupportsSpoolman spoolmanClient)
            {
                try
                {
                    bool synced = await spoolmanClient.SetSpoolmanActiveSpoolAsync(p.ServerUrl, spoolId, ct)
                        .ConfigureAwait(false);

                    if (!synced)
                    {
                        _logger.LogWarning(
                            "SetActiveSpoolAsync: Backend sync failed for printer {PName} ({Id}), but DB assignment succeeded",
                            p.Name, id);
                    }
                }
                catch (Exception ex)
                {
                    // Non-fatal: DB assignment is the source of truth
                    _logger.LogWarning(
                        ex,
                        "SetActiveSpoolAsync: Failed to sync spool to backend for printer {PName} ({Id})",
                        p.Name, id);
                }
            }

            // Broadcast spool change to connected clients immediately
            PrinterSpoolInfoDto? spoolInfo = spoolId.HasValue
                ? await BuildDbSpoolInfoAsync(p, ct).ConfigureAwait(false)
                : null;
            await _broadcaster.BroadcastSpoolChangeAsync(id, spoolInfo, ct).ConfigureAwait(false);

            return new CommandResult(true, spoolId.HasValue ? $"Active spool set to {spoolId}" : "Active spool cleared");
        }
        catch (Exception ex)
        {
            string action = spoolId.HasValue ? $"set active spool to {spoolId}" : "clear active spool";
            _logger.LogError(ex, "SetActiveSpoolAsync: Exception while attempting to {Action} on printer {PName} ({Id})", action, p.Name, id);
            return new CommandResult(false, $"Failed to {action}: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SpoolmanSpoolDto>?> ListPrinterSpoolsAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
        if (p is null)
        {
            return null;
        }

        try
        {
            var backend = (PrinterBackend)p.Backend;
            IBackendClient client = GetBackendClient(backend);

            if (client is not ISupportsSpoolman spoolmanClient)
            {
                return null;
            }

            string? json = await spoolmanClient.GetSpoolmanSpoolsAsync(p.ServerUrl, ct).ConfigureAwait(false);
            return SpoolmanJsonParser.ParseSpools(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListPrinterSpoolsAsync: Exception fetching spools for printer {PName} ({Id})", p.Name, id);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<CommandResult> SetToolheadSpoolAsync(Guid id, int toolheadIndex, int spoolId, CancellationToken ct)
    {
        // Load printer with toolheads collection
        Printer? p = await _unitOfWork.Printers
            .FindByIdWithToolheadsAsync(id, ct)
            .ConfigureAwait(false);

        if (p is null)
        {
            return new CommandResult(false, $"Printer {id} not found");
        }

        // Find the toolhead by index
        Toolhead? toolhead = p.Toolheads.FirstOrDefault(t => t.Index == toolheadIndex);

        // Auto-create MMU gates when the toolhead doesn't exist.
        // If the printer reports MMU gates via SignalR but MultiMaterial isn't set yet,
        // promote it and create the virtual gate rows so spool assignment works.
        if (toolhead is null)
        {
            if (!p.MultiMaterial && toolheadIndex > 0)
            {
                _logger.LogInformation(
                    "SetToolheadSpoolAsync: Promoting printer {PName} ({Id}) to MultiMaterial=true (requested toolhead T{Index})",
                    p.Name, id, toolheadIndex);
                p.MultiMaterial = true;
            }

            if (p.MultiMaterial)
            {
                int gateCount = Math.Max(4, toolheadIndex + 1);
                List<Toolhead> gates = CreateMmuVirtualToolheads(p, gateCount);
                if (gates.Count > 0)
                {
                    _unitOfWork.Printers.AddToolheads(gates);
                    await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                toolhead = gates.FirstOrDefault(t => t.Index == toolheadIndex)
                           ?? p.Toolheads.FirstOrDefault(t => t.Index == toolheadIndex);
            }
        }

        if (toolhead is null)
        {
            return new CommandResult(false, $"Toolhead with index {toolheadIndex} not found on printer \"{p.Name}\"");
        }

        try
        {
            // Fetch spool details from Spoolman to populate material and color
            SpoolmanSpoolDto? spool = await _spoolmanService.GetSpoolByIdAsync(spoolId, ct).ConfigureAwait(false);

            // Assign spool ID and denormalized info
            toolhead.CurrentSpoolId = spoolId;
            toolhead.CurrentMaterial = spool?.Material ?? null;
            toolhead.CurrentFilamentColor = spool?.ColorHex ?? null;
            toolhead.UpdatedAt = DateTime.UtcNow;

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "SetToolheadSpoolAsync: Assigned spool {SpoolId} to toolhead T{Index} on printer {PName} ({Id})",
                spoolId, toolheadIndex, p.Name, id);

            return new CommandResult(true, $"Spool {spoolId} assigned to toolhead T{toolheadIndex}");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SetToolheadSpoolAsync: Exception assigning spool {SpoolId} to toolhead T{Index} on printer {PName} ({Id})",
                spoolId, toolheadIndex, p.Name, id);
            return new CommandResult(false, $"Failed to assign spool: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<CommandResult> ClearToolheadSpoolAsync(Guid id, int toolheadIndex, CancellationToken ct)
    {
        // Load printer with toolheads collection
        Printer? p = await _unitOfWork.Printers
            .FindByIdWithToolheadsAsync(id, ct)
            .ConfigureAwait(false);

        if (p is null)
        {
            return new CommandResult(false, $"Printer {id} not found");
        }

        // Find the toolhead by index
        Toolhead? toolhead = p.Toolheads.FirstOrDefault(t => t.Index == toolheadIndex);

        // Auto-create MMU gates when the toolhead doesn't exist.
        if (toolhead is null)
        {
            if (!p.MultiMaterial && toolheadIndex > 0)
            {
                _logger.LogInformation(
                    "ClearToolheadSpoolAsync: Promoting printer {PName} ({Id}) to MultiMaterial=true (requested toolhead T{Index})",
                    p.Name, id, toolheadIndex);
                p.MultiMaterial = true;
            }

            if (p.MultiMaterial)
            {
                int gateCount = Math.Max(4, toolheadIndex + 1);
                List<Toolhead> gates = CreateMmuVirtualToolheads(p, gateCount);
                if (gates.Count > 0)
                {
                    _unitOfWork.Printers.AddToolheads(gates);
                    await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                toolhead = gates.FirstOrDefault(t => t.Index == toolheadIndex)
                           ?? p.Toolheads.FirstOrDefault(t => t.Index == toolheadIndex);
            }
        }

        if (toolhead is null)
        {
            return new CommandResult(false, $"Toolhead with index {toolheadIndex} not found on printer \"{p.Name}\"");
        }

        try
        {
            // Clear spool assignment
            toolhead.CurrentSpoolId = null;
            toolhead.CurrentMaterial = null;
            toolhead.CurrentFilamentColor = null;
            toolhead.UpdatedAt = DateTime.UtcNow;

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "ClearToolheadSpoolAsync: Cleared spool from toolhead T{Index} on printer {PName} ({Id})",
                toolheadIndex, p.Name, id);

            return new CommandResult(true, $"Spool cleared from toolhead T{toolheadIndex}");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ClearToolheadSpoolAsync: Exception clearing spool from toolhead T{Index} on printer {PName} ({Id})",
                toolheadIndex, p.Name, id);
            return new CommandResult(false, $"Failed to clear spool: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<CommandResult> EnsureMmuToolheadsAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await _unitOfWork.Printers
            .FindByIdWithToolheadsAsync(id, ct)
            .ConfigureAwait(false);

        if (p is null)
        {
            return new CommandResult(false, $"Printer {id} not found");
        }

        if (!p.MultiMaterial)
        {
            return new CommandResult(true, "Printer is not multi-material; no MMU gates needed");
        }

        int existingGates = p.Toolheads.Count(t => t.ToolheadType == ToolheadType.MmuGate);
        if (existingGates > 0)
        {
            return new CommandResult(true, $"Printer already has {existingGates} MMU gate(s)");
        }

        List<Toolhead> gates = CreateMmuVirtualToolheads(p);
        if (gates.Count > 0)
        {
            _unitOfWork.Printers.AddToolheads(gates);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new CommandResult(true, $"Created {gates.Count} MMU gate(s) for printer \"{p.Name}\"");
    }

    /// <inheritdoc />
    public void SyncMmuToolheadsOnEntity(Printer printer, bool wasMultiMaterial, int mmuGateCount = 4)
    {
        if (!wasMultiMaterial && printer.MultiMaterial)
        {
            // MultiMaterial enabled → create MmuGate toolheads
            SyncMmuVirtualToolheads(printer, mmuGateCount);
        }
        else if (wasMultiMaterial && !printer.MultiMaterial)
        {
            // MultiMaterial disabled → remove MmuGate toolheads
            List<Toolhead> gatesToRemove = printer.Toolheads
                .Where(t => t.ToolheadType == ToolheadType.MmuGate)
                .ToList();

            foreach (Toolhead gate in gatesToRemove)
            {
                printer.Toolheads.Remove(gate);
            }

            if (gatesToRemove.Count > 0)
            {
                _logger.LogInformation(
                    "SyncMmuToolheadsOnEntity: Removed {GateCount} MMU gate(s) from printer {PName} ({Id})",
                    gatesToRemove.Count, printer.Name, printer.Id);
            }
        }
    }

    /// <summary>
    /// Synchronizes MMU virtual toolheads by creating gates and adding them to the printer's
    /// navigation collection. Use for create/update flows where the Printer entity is already
    /// being saved (Added or Modified state).
    /// </summary>
    private void SyncMmuVirtualToolheads(Printer printer, int mmuGateCount = 4)
    {
        List<Toolhead> gates = CreateMmuVirtualToolheads(printer, mmuGateCount);
        foreach (Toolhead gate in gates)
        {
            printer.Toolheads.Add(gate);
        }
    }

    /// <summary>
    /// Creates MMU virtual toolhead entities for a multi-material printer.
    /// Returns the created gates WITHOUT adding them to the printer's navigation collection.
    /// </summary>
    /// <remarks>
    /// When the Printer was loaded from DB (Unchanged state), adding children via the navigation
    /// collection triggers a RowVersion concurrency check on the Printer UPDATE. To avoid this,
    /// callers for existing printers should use the repository's AddToolheads method
    /// to add gates directly to the DbContext instead.
    /// </remarks>
    private List<Toolhead> CreateMmuVirtualToolheads(Printer printer, int mmuGateCount = 4)
    {
        if (!printer.MultiMaterial)
        {
            return [];
        }

        int physicalToolheadCount = printer.Toolheads.Count(t => t.ToolheadType == ToolheadType.Physical);
        if (physicalToolheadCount > 1)
        {
            return [];
        }

        bool hasExistingGates = printer.Toolheads.Any(t => t.ToolheadType == ToolheadType.MmuGate);
        if (hasExistingGates)
        {
            return [];
        }

        Toolhead? primaryToolhead = printer.Toolheads.FirstOrDefault(t => t.ToolheadType == ToolheadType.Physical && t.IsPrimary)
                                    ?? printer.Toolheads.FirstOrDefault(t => t.ToolheadType == ToolheadType.Physical);

        _logger.LogInformation(
            "CreateMmuVirtualToolheads: Auto-creating {GateCount} MMU gates for printer {PName} ({Id})",
            mmuGateCount, printer.Name, printer.Id);

        var gates = new List<Toolhead>();
        for (int i = 1; i < mmuGateCount; i++)
        {
            gates.Add(new Toolhead
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Name = $"Gate {i}",
                Index = i,
                ToolheadType = ToolheadType.MmuGate,
                IsPrimary = false,
                HotendModelId = primaryToolhead?.HotendModelId,
                ExtruderModelId = primaryToolhead?.ExtruderModelId,
                ToolheadModelDefId = primaryToolhead?.ToolheadModelDefId,
                NozzleModelId = primaryToolhead?.NozzleModelId,
                SupportedMaterials = primaryToolhead?.SupportedMaterials,
                UpdatedAt = DateTime.UtcNow
            });
        }

        _logger.LogInformation(
            "CreateMmuVirtualToolheads: Created {GateCount} MMU gates for printer {PName} ({Id})",
            gates.Count, printer.Name, printer.Id);

        return gates;
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
                ? await startPrintClient!.StartPrintAsync(p.BackendUrl, filename, p.Credential, ct).ConfigureAwait(false)
                : false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start print from file {Filename} on printer {Id}", filename, id);
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
    public async Task<bool> DeletePrinterFileAsync(Guid id, string filename, CancellationToken ct)
    {
        Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
        if (p == null)
        {
            return false;
        }

        try
        {
            var backend = (PrinterBackend)p.Backend;

            return _capabilityFactory.TryGetFileDeleteClientTyped(backend, out ISupportsFileDelete? deleteClient)
                ? await deleteClient!.DeleteFileAsync(p.BackendUrl, filename, p.Credential, ct).ConfigureAwait(false)
                : false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete file {Filename} on printer {PrinterId}", filename, id);
            return false;
        }
    }

    /// <summary>
    /// Enables camera for a printer.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if camera was enabled, false if not currently supported</returns>
    /// <remarks>
    /// Camera enable/disable is not supported by printer firmware APIs (Moonraker, PrusaLink,
    /// OctoPrint, SDCP). These firmwares provide camera stream URLs and snapshots but have no
    /// concept of toggling cameras on/off at runtime. Most users run cameras via external
    /// systems (mjpg-streamer, crowsnest) that are outside PrintFarmer's control.
    /// See .squad/decisions/inbox/ for full architecture analysis.
    /// </remarks>
    public async Task<bool> EnableCameraAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
        if (p == null)
        {
            return false;
        }

        // Camera enable/disable is not supported by printer firmware APIs
        return false;
    }

    /// <summary>
    /// Disables camera for a printer.
    /// </summary>
    /// <param name="id">Unique printer identifier (GUID)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>True if camera was disabled, false if not currently supported</returns>
    /// <remarks>
    /// Camera enable/disable is not supported by printer firmware APIs.
    /// Delegates to EnableCameraAsync (both return false). See EnableCameraAsync remarks.
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
                ? await uploadClient!.UploadGcodeAsync(p.BackendUrl, filename, stream, p.Credential, ct).ConfigureAwait(false)
                : false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to upload file to printer {Id}", id);
            return false;
        }
    }

    /// <summary>
    /// Uploads a gcode file to the printer and starts printing it in a single backend operation.
    /// </summary>
    public async Task<UploadAndPrintResult> UploadAndStartPrintAsync(Guid id, string filename, Stream stream, IProgress<UploadAndPrintStage>? progress = null, CancellationToken ct = default)
    {
        Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
        if (p is null)
        {
            return UploadAndPrintResult.Fail(UploadAndPrintStage.Uploading, "Printer not found");
        }

        try
        {
            var backend = (PrinterBackend)p.Backend;
            if (!_capabilityFactory.TryGetUploadAndPrintClientTyped(backend, out ISupportsUploadAndPrint? client))
            {
                return UploadAndPrintResult.Fail(UploadAndPrintStage.Uploading, "Backend does not support upload and print");
            }

            return await client!.UploadAndStartPrintAsync(p.BackendUrl, filename, stream, p.Credential, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to upload and start print on printer {Id}", id);
            return UploadAndPrintResult.Fail(UploadAndPrintStage.Uploading, ex.Message);
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
            List<PrinterFileInfo> fileInfos = await fileListClient.GetFileListAsync(baseUrl, p.Credential, ct).ConfigureAwait(false);

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
            _logger.LogWarning(ex, "Failed to get file list for printer {Id}", id);
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
                _logger.LogWarning("Backend {Backend} does not support file downloads", backend);
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
            _logger.LogWarning(ex, "Failed to download file {Filename} from printer {Id}", filename, id);
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
                        _logger.LogInformation("[BulkCreate] Skipping duplicate printer: {PrinterDtoName} (ServerUrl: {ExistingByIpServerUrl})", printerDto.Name, existingByIp.ServerUrl);
                        skippedCount++;
                        status = "Skipped";
                        reason = $"Printer with ServerUrl {existingByIp.ServerUrl} already exists";
                    }
                    else if ((duplicateHandling ?? "skip") == "overwrite")
                    {
                        _logger.LogInformation("[BulkCreate] Removing duplicate printer: {ExistingByIpName} (ServerUrl: {ExistingByIpServerUrl})", existingByIp.Name, existingByIp.ServerUrl);
                        await RemoveAsync(existingByIp, ct);
                        await SaveChangesAsync(ct);

                        // Load a fresh copy of the CSV printer data (not the one we're removing)
                        // This avoids EF Core tracking conflicts when creating the new printer
                        createdDto = await CreatePrinterFromDtoAsync(printerDto, ct);
                        createdPrinterId = Guid.Parse(createdDto.Id.ToString());
                        await SaveChangesAsync(ct);
                        createdPrinters.Add(createdDto);
                        _logger.LogInformation("[BulkCreate] Successfully created printer: {CreatedDtoName}", createdDto.Name);
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
                    _logger.LogInformation("[BulkCreate] Successfully created printer: {CreatedDtoName}", createdDto.Name);
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
                    _logger.LogDebug("[BulkCreate] Skipping background camera discovery for {PrinterDtoName} - will discover on next status poll", printerDto.Name);
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
                _logger.LogWarning(ex, "[BulkCreate] Error creating printer {Value0} at index {I}: {ErrorMessage}", printers[i].Name, i, errorMessage);

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
                _logger.LogWarning("[PrintJobStatus] Printer {Id} not found", id);
                return null;
            }

            _logger.LogInformation("[PrintJobStatus] Getting print job status for printer {PrinterName} (Backend: {PrinterBackend})", printer.Name, printer.Backend);

            var backend = (PrinterBackend)printer.Backend;
            IBackendClient client = GetBackendClient(backend);

            if (client is not ISupportsJobControl jobClient)
            {
                _logger.LogWarning("[PrintJobStatus] Backend {Backend} does not support job control", backend);
                return null;
            }

            string url = backend == PrinterBackend.Moonraker
                ? BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort)
                : printer.BackendUrl;

            PrinterJob? job = await jobClient.GetJobAsync(url, printer.Credential, ct).ConfigureAwait(false);

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
            _logger.LogWarning("[PrintJobStatus] Timeout retrieving print job status for printer {Id}", id);
            return null; // Return null on timeout
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PrintJobStatus] Error getting print job status for printer {Id}: {Message}", id, ex.Message);
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

            _logger.LogInformation("[Import] Parsed {PrintersLength} printers from {FileExtension} file", printers.Length, fileExtension);

            // Use existing BulkCreatePrintersAsync for actual creation
            object result = await BulkCreatePrintersAsync(printers, duplicateHandling, ct);
            _logger.LogInformation($"[Import] Successfully imported printers from file");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Import] Failed to import printers from stream: {Message}", ex.Message);
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
                int usernameIdx = Array.IndexOf(headers, "username");
                int passwordIdx = Array.IndexOf(headers, "password");
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
                            Username = usernameIdx >= 0 && usernameIdx < values.Length ? values[usernameIdx] : null,
                            Password = passwordIdx >= 0 && passwordIdx < values.Length ? values[passwordIdx] : null,
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

                        // Backward compatibility: older CSV exports/imports used ApiKey for PrusaLink password.
                        if (printer.Backend == PrinterBackend.PrusaLink)
                        {
                            if (string.IsNullOrWhiteSpace(printer.Password) && !string.IsNullOrWhiteSpace(printer.ApiKey))
                            {
                                printer.Password = printer.ApiKey;
                                printer.ApiKey = null;
                            }

                            if (string.IsNullOrWhiteSpace(printer.Username) && !string.IsNullOrWhiteSpace(printer.Password))
                            {
                                printer.Username = "maker";
                            }
                        }

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
                _logger.LogWarning("[Import-CSV] Encountered {ErrorsCount} parsing errors while importing {PrintersCount} valid printers", errors.Count, printers.Count);
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
        _logger.LogInformation("RefreshCameraUrlsAsync: Starting refresh for printer {Id}", id);

        Printer? printer = await FindByIdWithIncludesAsync(id, ct).ConfigureAwait(false);
        if (printer == null)
        {
            _logger.LogWarning("RefreshCameraUrlsAsync: Printer {Id} not found", id);
            return null;
        }

        _logger.LogInformation("RefreshCameraUrlsAsync: Found printer {PrinterName}, Backend={PrinterBackend}, ServerUrl={PrinterServerUrl}, FrontendPort={PrinterFrontendPort}", printer.Name, printer.Backend, printer.ServerUrl, printer.FrontendPort);

        var backend = (PrinterBackend)printer.Backend;
        string? streamUrl = null;
        string? snapshotUrl = null;

        try
        {
            // Try to use the configured camera detection interface which queries actual cameras
            if (_capabilityFactory.TryGetConfiguredCameraDetectionClient(backend, out ISupportsConfiguredCameraDetection? detectionClient) && detectionClient != null)
            {
                _logger.LogInformation("RefreshCameraUrlsAsync: Using configured camera detection for backend {Backend}", backend);

                // For Moonraker, use the frontend URL (not backend port 7125)
                string baseUrlForCamera = backend == PrinterBackend.Moonraker
                    ? BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort)
                    : printer.BackendUrl;

                _logger.LogInformation("RefreshCameraUrlsAsync: Using baseUrlForCamera={BaseUrlForCamera}", baseUrlForCamera);

                // Call the detection method - it will ONLY return URLs if cameras actually exist
                (streamUrl, snapshotUrl) = await detectionClient.DetectConfiguredCameraUrlsAsync(
                    baseUrlForCamera,
                    printer.FrontendPort,
                    printer.Credential,
                    ct).ConfigureAwait(false);

                _logger.LogInformation("RefreshCameraUrlsAsync: Got URLs from detection - stream={StreamUrl}, snapshot={SnapshotUrl}", streamUrl, snapshotUrl);
            }
            else
            {
                // Fallback: Use standard camera client (may return default URLs even if cameras don't exist)
                _logger.LogWarning("RefreshCameraUrlsAsync: Configured camera detection not available for backend {Backend}, falling back to standard interface", backend);

                bool gotCameraClient = _capabilityFactory.TryGetCameraClientTyped(backend, out ISupportsCamera? cameraClient);
                if (gotCameraClient && cameraClient != null)
                {
                    string baseUrlForCamera = backend == PrinterBackend.Moonraker
                        ? BuildMoonrakerUrl(printer.ServerUrl, printer.FrontendPort)
                        : printer.BackendUrl;

                    streamUrl = await cameraClient.GetCameraStreamUrlAsync(baseUrlForCamera, printer.FrontendPort, printer.Credential, ct).ConfigureAwait(false);
                    snapshotUrl = await cameraClient.GetCameraSnapshotUrlAsync(baseUrlForCamera, printer.FrontendPort, printer.Credential, ct).ConfigureAwait(false);

                    _logger.LogInformation("RefreshCameraUrlsAsync: Got URLs from standard interface - stream={StreamUrl}, snapshot={SnapshotUrl}", streamUrl, snapshotUrl);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RefreshCameraUrlsAsync: Failed to refresh camera URLs for printer {Id}: {Message}", id, ex.Message);
        }

        // Upsert Camera entity — Cameras table is the sole source of truth
        _logger.LogInformation("RefreshCameraUrlsAsync: Updating cameras for printer {PrinterName}: StreamUrl={StreamUrl}, SnapshotUrl={SnapshotUrl}", printer.Name, streamUrl, snapshotUrl);
        if (!string.IsNullOrEmpty(streamUrl) || !string.IsNullOrEmpty(snapshotUrl))
        {
            await UpsertCameraForPrinterAsync(printer, backend, streamUrl, snapshotUrl, ct).ConfigureAwait(false);
        }

        await SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("RefreshCameraUrlsAsync: SaveChangesAsync completed - URLs saved: stream={StringIsNullOrEmpty}, snapshot={StringIsNullOrEmpty1}", !string.IsNullOrEmpty(streamUrl), !string.IsNullOrEmpty(snapshotUrl));

        // Return updated DTO
        return await GetPrinterDtoAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Upserts a Camera entity for a printer during camera URL refresh.
    /// Finds an existing camera by (printerId, source) and updates it, or creates a new one.
    /// </summary>
    /// <param name="printer">The printer entity.</param>
    /// <param name="backend">The printer backend type.</param>
    /// <param name="streamUrl">Detected camera stream URL.</param>
    /// <param name="snapshotUrl">Detected camera snapshot URL.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task UpsertCameraForPrinterAsync(Printer printer, PrinterBackend backend, string? streamUrl, string? snapshotUrl, CancellationToken ct)
    {
        CameraSource source = MapBackendToCameraSource(backend);

        Domain.Camera? existing = await _unitOfWork.Cameras.FindByPrinterIdAndSourceAsync(printer.Id, source, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            existing.StreamUrl = streamUrl;
            existing.SnapshotUrl = snapshotUrl;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.HealthStatus = CameraHealthStatus.Healthy;
            existing.LastHealthCheck = DateTime.UtcNow;
            existing.ConsecutiveFailures = 0;
            _logger.LogInformation("UpsertCameraForPrinterAsync: Updated existing Camera {CameraId} for printer {PrinterName}", existing.Id, printer.Name);
        }
        else
        {
            var camera = new Domain.Camera
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Name = $"{printer.Name} Camera",
                StreamUrl = streamUrl,
                SnapshotUrl = snapshotUrl,
                IsEnabled = true,
                SortOrder = 0,
                Source = source,
                CameraType = CameraType.General,
                HealthStatus = CameraHealthStatus.Healthy,
                LastHealthCheck = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            };

            _unitOfWork.Cameras.Add(camera);
            _logger.LogInformation("UpsertCameraForPrinterAsync: Created new Camera {CameraId} for printer {PrinterName}", camera.Id, printer.Name);
        }
    }

    /// <summary>
    /// Maps a PrinterBackend enum to the corresponding CameraSource enum.
    /// </summary>
    /// <param name="backend">The printer backend type.</param>
    private static CameraSource MapBackendToCameraSource(PrinterBackend backend) => backend switch
    {
        PrinterBackend.Moonraker => CameraSource.Moonraker,
        PrinterBackend.PrusaLink => CameraSource.PrusaLink,
        PrinterBackend.OctoPrint => CameraSource.OctoPrint,
        PrinterBackend.SDCP => CameraSource.SDCP,
        PrinterBackend.FlashForge => CameraSource.FlashForge,
        _ => CameraSource.Standalone,
    };

    /// <summary>
    /// Resolves camera URLs from the Cameras table for a given printer.
    /// Returns the first enabled camera ordered by SortOrder, preferring General type.
    /// This is the compatibility layer that allows printer DTOs to expose camera fields
    /// without reading from the deprecated Printer.CameraStreamUrl/CameraSnapshotUrl columns.
    /// </summary>
    /// <param name="printerId">The printer ID.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<(string? StreamUrl, string? SnapshotUrl)> ResolveCameraUrlsFromTableAsync(Guid printerId, CancellationToken ct)
    {
        List<Domain.Camera> cameras = await _unitOfWork.Cameras.GetByPrinterIdAsync(printerId, ct).ConfigureAwait(false);

        Domain.Camera? camera = cameras
            .Where(c => c.IsEnabled)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.CameraType == CameraType.General ? 0 : 1)
            .FirstOrDefault();

        return camera is not null
            ? (camera.StreamUrl, camera.SnapshotUrl)
            : (null, null);
    }

    /// <summary>
    /// Batch-loads camera URLs from the Cameras table for all printers.
    /// Returns a dictionary keyed by printer ID with the first enabled camera's URLs.
    /// Used by bulk DTO methods to avoid N+1 queries.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task<Dictionary<Guid, (string? StreamUrl, string? SnapshotUrl)>> BatchResolveCameraUrlsAsync(CancellationToken ct)
    {
        List<Domain.Camera> allCameras = await _unitOfWork.Cameras.GetEnabledAsync(ct).ConfigureAwait(false);

        return allCameras
            .Where(c => c.PrinterId.HasValue)
            .GroupBy(c => c.PrinterId!.Value)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    Domain.Camera? best = g
                        .OrderBy(c => c.SortOrder)
                        .ThenBy(c => c.CameraType == CameraType.General ? 0 : 1)
                        .FirstOrDefault();
                    return best is not null ? (best.StreamUrl, best.SnapshotUrl) : ((string?)null, (string?)null);
                });
    }
}
