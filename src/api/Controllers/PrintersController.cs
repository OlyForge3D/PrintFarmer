using System.Globalization;
using Farm.Web.Api.Controllers.Responses;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing 3D printers and their operations.
/// Supports Moonraker, PrusaLink, and SDCP printer backends.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Tags("Printers")]
public class PrintersController(AppDbContext db, IMoonrakerClient moon, IPrusaLinkClient prusa, ISdcpClient sdcp, INetworkDiscoveryService networkDiscovery, ILogger<PrintersController> logger, IValidator<CreatePrinterDto> validator, ICircuitBreakerService circuitBreaker, IPrinterCapabilityDiscoveryService capabilityDiscovery, IDefaultCatalogService defaultCatalog) : ControllerBase
{
    private static string EnsureLocalSuffix(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        { return host; }
        if (System.Net.IPAddress.TryParse(host, out _))
        { return host; }
        if (host.Contains('.', StringComparison.Ordinal))
        { return host; }
        return host + ".local";
    }
    private static string NormalizeServerUrl(string url, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(url))
        { return url; }
        var trimmed = url.Trim();
        // Ensure scheme
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }
        try
        {
            var ub = new UriBuilder(trimmed);
            if (ub.Port == -1)
            {
                ub.Port = defaultPort;
            }
            return ub.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            // If parsing fails, fall back to original input
            return url;
        }
    }

    /// <summary>
    /// Retrieves all printers with their current status information.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>A list of all printers with their current status, including online/offline state, print progress, and temperatures</returns>
    /// <response code="200">Returns the list of printers with status information</response>
    /// <response code="500">If there was an internal server error</response>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PrinterDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<PrinterDto>>> GetAllAsync(CancellationToken ct)
    {
        var items = await db.Printers.AsNoTracking().Include(p => p.Manufacturer).Include(p => p.Model).ToListAsync(ct);

        // Use aggressive timeouts and circuit breaker patterns for bulk status loading
        using var fastTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fastTimeoutCts.CancelAfter(TimeSpan.FromSeconds(2)); // Aggressive 2-second timeout for bulk operations

        var dtos = await Task.WhenAll(items.Select(async p =>
        {
            try
            {
                if (p.Backend == 1) // PrusaLink
                {
                    var breaker = circuitBreaker.GetCircuitBreaker($"prusalink-{p.Id}");
                    var status = await breaker.ExecuteAsync(async ct =>
                        await prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct), fastTimeoutCts.Token);
                    return new PrinterDto(
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
                    var breaker = circuitBreaker.GetCircuitBreaker($"sdcp-{p.Id}");
                    var status = await breaker.ExecuteAsync(async ct =>
                        await sdcp.GetCompositeStatusAsync(p.ServerUrl, ct), fastTimeoutCts.Token);
                    return new PrinterDto(
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
                else // Moonraker
                {
                    var breaker = circuitBreaker.GetCircuitBreaker($"moonraker-{p.Id}");
                    var status = await breaker.ExecuteAsync(async ct =>
                        await moon.GetCompositeStatusAsync(p.ServerUrl, ct), fastTimeoutCts.Token);
                    var spoolInfo = await GetSpoolInfoAsync(p.ServerUrl, fastTimeoutCts.Token);
                    return new PrinterDto(
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
                logger.FastTimeout(p.Name, p.Id);
                // Return offline printer for timeout cases
                return CreateOfflinePrinterDto(p);
            }
            catch (Exception ex)
            {
                logger.ErrorGettingStatus(ex, p.Name, p.Id);
                // Return offline printer for any error
                return CreateOfflinePrinterDto(p);
            }
        }));
        return Ok(dtos);
    }

    private static PrinterDto CreateOfflinePrinterDto(Domain.Printer p)
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
            Backend: p.Backend == 1 ? Farm.Web.Shared.PrinterBackend.PrusaLink :
                     p.Backend == 2 ? Farm.Web.Shared.PrinterBackend.SDCP :
                     Farm.Web.Shared.PrinterBackend.Moonraker,
            ApiKey: p.ApiKey,
            OriginalServerUrl: p.OriginalServerUrl,
            IpAddress: p.IpAddress
        );
    }

    /// <summary>
    /// Retrieves basic information for all printers without detailed status.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>A lightweight list of all printers with basic information only</returns>
    /// <response code="200">Returns the list of printers with basic information</response>
    [HttpGet("basic")]
    [ProducesResponseType(typeof(IEnumerable<PrinterBasicDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<PrinterBasicDto>>> GetBasicAsync(CancellationToken ct)
    {
        var items = await db.Printers.AsNoTracking().Include(p => p.Manufacturer).Include(p => p.Model).ToListAsync(ct);
        var dtos = items.Select(p => new PrinterBasicDto(
            Id: p.Id,
            Name: p.Name,
            ServerUrl: p.ServerUrl,
            Notes: p.Notes,
            ManufacturerName: p.Manufacturer?.Name,
            ModelName: p.Model?.Name,
            Backend: p.Backend == 1 ? Farm.Web.Shared.PrinterBackend.PrusaLink :
                     p.Backend == 2 ? Farm.Web.Shared.PrinterBackend.SDCP :
                     Farm.Web.Shared.PrinterBackend.Moonraker,
            ApiKey: p.ApiKey,
            OriginalServerUrl: p.OriginalServerUrl,
            IpAddress: p.IpAddress
        )).ToList();
        return Ok(dtos);
    }

    /// <summary>
    /// Retrieves all printers with cached information for fast loading.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>A list of all printers with cached information without real-time status</returns>
    /// <response code="200">Returns the list of printers with cached information</response>
    [HttpGet("fast")]
    [ProducesResponseType(typeof(IEnumerable<PrinterDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<PrinterDto>>> GetAllFastAsync(CancellationToken ct)
    {
        var items = await db.Printers.AsNoTracking().Include(p => p.Manufacturer).Include(p => p.Model).ToListAsync(ct);

        // Return all printers as offline initially - let the client load statuses progressively
        var dtos = items.Select(p => new PrinterDto(
            Id: p.Id,
            Name: p.Name,
            ServerUrl: p.ServerUrl,
            Notes: p.Notes,
            IsOnline: false, // Default to offline, client will update via individual status calls
            State: null,
            ManufacturerName: p.Manufacturer?.Name,
            ModelName: p.Model?.Name,
            Backend: p.Backend == 1 ? Farm.Web.Shared.PrinterBackend.PrusaLink :
                     p.Backend == 2 ? Farm.Web.Shared.PrinterBackend.SDCP :
                     Farm.Web.Shared.PrinterBackend.Moonraker,
            ApiKey: p.ApiKey,
            OriginalServerUrl: p.OriginalServerUrl,
            IpAddress: p.IpAddress
        )).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Gets the current status of a specific printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The current status of the specified printer including print progress, temperatures, and position</returns>
    /// <response code="200">Returns the printer's current status</response>
    /// <response code="404">If the printer with the specified ID was not found</response>
    /// <response code="500">If there was an error communicating with the printer</response>
    [HttpGet("{id:guid}/status")]
    [ProducesResponseType(typeof(PrinterStatusDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterStatusDto>> GetStatusAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        { return NotFound(); }

        // Use moderate timeout for individual status checks (balance between responsiveness and accuracy)
        using var statusCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        statusCts.CancelAfter(TimeSpan.FromSeconds(3)); // 3-second timeout for individual status

        try
        {
            if (p.Backend == 1) // PrusaLink
            {
                var breaker = circuitBreaker.GetCircuitBreaker($"prusalink-{p.Id}");
                var status = await breaker.ExecuteAsync(async ct =>
                    await prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct), statusCts.Token);
                return new PrinterStatusDto(
                    Id: p.Id,
                    IsOnline: status.IsOnline,
                    State: status.State,
                    Progress: status.Progress,
                    JobName: status.JobName,
                    ThumbnailUrl: status.ThumbnailUrl,
                    CameraStreamUrl: status.CameraStreamUrl,
                    CameraSnapshotUrl: status.CameraSnapshotUrl
                );
            }
            else if (p.Backend == 2) // SDCP
            {
                var breaker = circuitBreaker.GetCircuitBreaker($"sdcp-{p.Id}");
                var status = await breaker.ExecuteAsync(async ct =>
                    await sdcp.GetCompositeStatusAsync(p.ServerUrl, ct), statusCts.Token);
                return new PrinterStatusDto(
                    Id: p.Id,
                    IsOnline: status.IsOnline,
                    State: status.State,
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
                    BedTarget: status.BedTarget
                );
            }
            else // Moonraker
            {
                var breaker = circuitBreaker.GetCircuitBreaker($"moonraker-{p.Id}");
                var status = await breaker.ExecuteAsync(async ct =>
                    await moon.GetCompositeStatusAsync(p.ServerUrl, ct), statusCts.Token);
                var spoolInfo = await GetSpoolInfoAsync(p.ServerUrl, statusCts.Token);
                return new PrinterStatusDto(
                    Id: p.Id,
                    IsOnline: status.IsOnline,
                    State: status.State,
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
                    SpoolInfo: spoolInfo
                );
            }
        }
        catch (OperationCanceledException) when (statusCts.Token.IsCancellationRequested)
        {
            logger.StatusTimeout(p.Id);
            // Return offline status for timeout cases
            return new PrinterStatusDto(
                Id: p.Id,
                IsOnline: false,
                State: null,
                Progress: null,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null,
                SpoolInfo: null
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error getting status for printer {PrinterId}", p.Id);
            // Return offline status if there's any error
            return new PrinterStatusDto(
                Id: p.Id,
                IsOnline: false,
                State: null,
                Progress: null,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null,
                SpoolInfo: null
            );
        }
    }

    /// <summary>
    /// Gets basic information about a specific printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Basic printer information including name, backend, connection status, and current state</returns>
    /// <response code="200">Returns basic printer information</response>
    /// <response code="404">If the printer with the specified ID was not found</response>
    [HttpGet("{id:guid}", Name = "GetPrinterById")]
    [ProducesResponseType(typeof(PrinterDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.Include(x => x.Manufacturer).Include(x => x.Model).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        { return NotFound(); }
        if (p.Backend == 1) // PrusaLink
        {
            var status = await prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct);
            return new PrinterDto(
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
            var status = await sdcp.GetCompositeStatusAsync(p.ServerUrl, ct);
            return new PrinterDto(
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
        else // Moonraker
        {
            var status = await moon.GetCompositeStatusAsync(p.ServerUrl, ct);
            var spoolInfo = await GetSpoolInfoAsync(p.ServerUrl, ct);
            return new PrinterDto(
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

    /// <summary>
    /// Gets detailed information about a specific printer including manufacturer, model, and configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Detailed printer information including manufacturer, model, purchase information, and settings</returns>
    /// <response code="200">Returns detailed printer information</response>
    /// <response code="404">If the printer with the specified ID was not found</response>
    [HttpGet("{id:guid}/details")]
    [ProducesResponseType(typeof(PrinterDetailsDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterDetailsDto>> GetDetailsAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.AsNoTracking().Include(x => x.Manufacturer).Include(x => x.Model).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        { return NotFound(); }
        return new PrinterDetailsDto(
            p.Id,
            p.Name,
            p.ServerUrl,
            p.Notes,
        p.ManufacturerId,
        p.Manufacturer?.Name,
        p.ModelId,
        p.Model?.Name,
        p.Model?.MaxX,
        p.Model?.MaxY,
        p.Model?.MaxZ,
        p.DateAcquired,
        (PrinterBackend)p.Backend,
        p.ApiKey,
        p.OriginalServerUrl,
        p.IpAddress
    );
    }

    /// <summary>
    /// Creates a new printer configuration.
    /// </summary>
    /// <param name="dto">The printer data transfer object containing printer details</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The created printer with its assigned unique identifier</returns>
    /// <response code="201">Returns the newly created printer</response>
    /// <response code="400">If the printer data is invalid or validation fails</response>
    /// <response code="409">If a printer with the same name and URL already exists</response>
    /// <response code="500">If there was an error creating the printer</response>
    [HttpPost]
    [ProducesResponseType(typeof(PrinterDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterDto>> CreateAsync([FromBody] CreatePrinterDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);
        // Validate input using FluentValidation
        var validationResult = await validator.ValidateAsync(dto, ct);
        if (!validationResult.IsValid)
        {
            logger.LogWarning("Printer creation validation failed: {Errors}",
                string.Join("; ", validationResult.Errors.Select(e => e.ErrorMessage)));

            foreach (var error in validationResult.Errors)
            {
                ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
            }
            return BadRequest(ModelState);
        }

        logger.LogInformation("Creating new printer: {Name} ({Backend})", dto.Name, dto.Backend);

        // resolve or create manufacturer/model
        Guid manufacturerId = dto.ManufacturerId ?? Guid.Empty;
        if (manufacturerId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            var name = dto.NewManufacturerName!.Trim();
            var existing = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name == name, ct);
            if (existing is null)
            {
                existing = new Manufacturer { Id = Guid.NewGuid(), Name = name };
                db.Manufacturers.Add(existing);
                await db.SaveChangesAsync(ct);
            }
            manufacturerId = existing.Id;
        }

        Guid modelId = dto.ModelId ?? Guid.Empty;
        if (modelId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewModelName) && manufacturerId != Guid.Empty)
        {
            var mname = dto.NewModelName!.Trim();
            var existingModel = await db.Models.FirstOrDefaultAsync(m => m.ManufacturerId == manufacturerId && m.Name == mname, ct);
            if (existingModel is null)
            {
                existingModel = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturerId, Name = mname };
                db.Models.Add(existingModel);
                await db.SaveChangesAsync(ct);
            }
            modelId = existingModel.Id;
        }

        // Use default catalog entries if manufacturer or model are still empty
        if (manufacturerId == Guid.Empty || modelId == Guid.Empty)
        {
            var (defaultManufacturerId, defaultModelId) = await defaultCatalog.GetDefaultCatalogIdsAsync();
            if (manufacturerId == Guid.Empty)
            {
                manufacturerId = defaultManufacturerId;
            }
            if (modelId == Guid.Empty)
            {
                modelId = defaultModelId;
            }
        }

        // Resolve host to IP and persist the IP-based base URL; store original URL for future re-resolve
        var defaultPort = dto.Backend == PrinterBackend.PrusaLink ? 80 :
                         dto.Backend == PrinterBackend.SDCP ? 80 : 7125;
        var normalizedInput = NormalizeServerUrl(dto.ServerUrl, defaultPort);
        string resolvedBase = normalizedInput;
        string? resolvedIp = null;
        try
        {
            var uri = new Uri(normalizedInput);
            if (!System.Net.IPAddress.TryParse(uri.Host, out _))
            {
                var hostToResolve = EnsureLocalSuffix(uri.Host);
                var addresses = await System.Net.Dns.GetHostAddressesAsync(hostToResolve, ct);
                var firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (firstIp is not null)
                {
                    var ub = new UriBuilder(uri)
                    {
                        Host = firstIp.ToString()
                    };
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

        var p = new Printer
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            ServerUrl = resolvedBase,
            OriginalServerUrl = normalizedInput,
            IpAddress = resolvedIp,
            Notes = dto.Notes,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            DateAcquired = dto.DateAcquired?.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dto.DateAcquired.Value, DateTimeKind.Utc)
                : dto.DateAcquired,
            Backend = (int)dto.Backend,
            ApiKey = dto.ApiKey
        };
        db.Printers.Add(p);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Successfully created printer: {Name} with ID {Id}", p.Name, p.Id);

        // Auto-discover capabilities for the newly created printer
        try
        {
            logger.LogInformation("Starting capability discovery for newly created printer: {Name} ({Id})", p.Name, p.Id);
            
            // Reload the printer with includes for proper discovery
            var printerForDiscovery = await db.Printers
                .Include(pr => pr.Manufacturer)
                .Include(pr => pr.Model)
                .FirstOrDefaultAsync(pr => pr.Id == p.Id, ct);
                
            if (printerForDiscovery != null)
            {
                var discoveredCapabilities = await capabilityDiscovery.DiscoverCapabilitiesAsync(printerForDiscovery, ct);
                if (discoveredCapabilities != null)
                {
                    logger.LogInformation("Successfully discovered and saved capabilities for printer: {Name} ({Id})", p.Name, p.Id);
                }
                else
                {
                    logger.LogWarning("Failed to discover capabilities for printer: {Name} ({Id}) - capabilities will need to be added manually", p.Name, p.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during capability discovery for newly created printer: {Name} ({Id}) - printer was created successfully but capabilities discovery failed", p.Name, p.Id);
            // Don't fail the printer creation if capability discovery fails - user can manually add capabilities or trigger discovery later
        }

        // Get manufacturer and model names for the response
        string? manufacturerName = null;
        string? modelName = null;

        if (manufacturerId != Guid.Empty)
        {
            var manufacturer = await db.Manufacturers.FirstOrDefaultAsync(m => m.Id == manufacturerId, ct);
            manufacturerName = manufacturer?.Name;
        }

        if (modelId != Guid.Empty)
        {
            var model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct);
            modelName = model?.Name;
        }

        // Return the created printer without attempting to fetch status
        // Status will be fetched later when needed (like in the printers list)
        var printerDto = new PrinterDto(
            Id: p.Id,
            Name: p.Name,
            ServerUrl: p.ServerUrl,
            Notes: p.Notes,
            IsOnline: false, // Default to offline, will be updated by background services
            State: "Unknown",
            ManufacturerName: manufacturerName,
            ModelName: modelName,
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
            Backend: (PrinterBackend)p.Backend,
            ApiKey: p.ApiKey,
            OriginalServerUrl: p.OriginalServerUrl,
            IpAddress: p.IpAddress
        );

        return CreatedAtRoute("GetPrinterById", new { id = p.Id }, printerDto);
    }

    /// <summary>
    /// Updates an existing printer configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the printer to update</param>
    /// <param name="dto">The updated printer data</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The updated printer</returns>
    /// <response code="200">Returns the updated printer</response>
    /// <response code="400">If the update data is invalid</response>
    /// <response code="404">If the printer with the specified ID was not found</response>
    /// <response code="500">If there was an error updating the printer</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(PrinterDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterDto>> UpdateAsync(Guid id, [FromBody] UpdatePrinterDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        { return NotFound(); }
        // resolve or create manufacturer/model
        Guid manufacturerId = dto.ManufacturerId ?? p.ManufacturerId;
        if (dto.ManufacturerId is null && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            var name = dto.NewManufacturerName!.Trim();
            var existing = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name == name, ct);
            if (existing is null)
            {
                existing = new Manufacturer { Id = Guid.NewGuid(), Name = name };
                db.Manufacturers.Add(existing);
                await db.SaveChangesAsync(ct);
            }
            manufacturerId = existing.Id;
        }

        Guid modelId = dto.ModelId ?? p.ModelId;
        if ((dto.ModelId is null && !string.IsNullOrWhiteSpace(dto.NewModelName)) && manufacturerId != Guid.Empty)
        {
            var mname = dto.NewModelName!.Trim();
            var existingModel = await db.Models.FirstOrDefaultAsync(m => m.ManufacturerId == manufacturerId && m.Name == mname, ct);
            if (existingModel is null)
            {
                existingModel = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturerId, Name = mname };
                db.Models.Add(existingModel);
                await db.SaveChangesAsync(ct);
            }
            modelId = existingModel.Id;
        }

        // Use default catalog entries if manufacturer or model are still empty
        if (manufacturerId == Guid.Empty || modelId == Guid.Empty)
        {
            var (defaultManufacturerId, defaultModelId) = await defaultCatalog.GetDefaultCatalogIdsAsync();
            if (manufacturerId == Guid.Empty)
            {
                manufacturerId = defaultManufacturerId;
            }
            if (modelId == Guid.Empty)
            {
                modelId = defaultModelId;
            }
        }

        p.Name = dto.Name;
        var defaultPort = dto.Backend.HasValue ?
            (dto.Backend.Value == PrinterBackend.PrusaLink ? 80 :
             dto.Backend.Value == PrinterBackend.SDCP ? 80 : 7125) :
            (p.Backend == 1 ? 80 : p.Backend == 2 ? 80 : 7125);
        var normalizedInput = NormalizeServerUrl(dto.ServerUrl, defaultPort);
        string resolvedBase = normalizedInput;
        string? resolvedIp = null;
        try
        {
            var uri = new Uri(normalizedInput);
            if (!System.Net.IPAddress.TryParse(uri.Host, out _))
            {
                var hostToResolve = EnsureLocalSuffix(uri.Host);
                var addresses = await System.Net.Dns.GetHostAddressesAsync(hostToResolve, ct);
                var firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (firstIp is not null)
                {
                    var ub = new UriBuilder(uri)
                    {
                        Host = firstIp.ToString()
                    };
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
        p.ServerUrl = resolvedBase;
        p.OriginalServerUrl = normalizedInput;
        p.IpAddress = resolvedIp;
        p.Notes = dto.Notes;
        p.ManufacturerId = manufacturerId;
        p.ModelId = modelId;
        p.DateAcquired = dto.DateAcquired?.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dto.DateAcquired.Value, DateTimeKind.Utc)
            : dto.DateAcquired;
        if (dto.Backend.HasValue)
        {
            p.Backend = (int)dto.Backend.Value;
        }

        if (dto.ApiKey != null)
        {
            p.ApiKey = dto.ApiKey;
        }

        await db.SaveChangesAsync(ct);

        // Build updated manufacturer/model names
        string? manufacturerName = null;
        string? modelName = null;
        if (p.ManufacturerId != Guid.Empty)
        {
            var man = await db.Manufacturers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == p.ManufacturerId, ct);
            manufacturerName = man?.Name;
        }
        if (p.ModelId != Guid.Empty)
        {
            var mod = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == p.ModelId, ct);
            modelName = mod?.Name;
        }

        var dtoResponse = new PrinterDto(
            Id: p.Id,
            Name: p.Name,
            ServerUrl: p.ServerUrl,
            Notes: p.Notes,
            IsOnline: false,
            State: "Unknown",
            ManufacturerName: manufacturerName,
            ModelName: modelName,
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
            Backend: (PrinterBackend)p.Backend,
            ApiKey: p.ApiKey,
            OriginalServerUrl: p.OriginalServerUrl,
            IpAddress: p.IpAddress
        );

        return Ok(dtoResponse);
    }

    /// <summary>
    /// Resolves a hostname to an IP address for printer configuration.
    /// </summary>
    /// <param name="body">The hostname resolution request containing the server URL and backend type</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The resolved IP address and normalized URL</returns>
    /// <response code="200">Returns the resolved hostname information</response>
    /// <response code="400">If the hostname resolution fails or URL is invalid</response>
    /// <response code="500">If there was an error during hostname resolution</response>
    [HttpPost("resolve")]
    [ProducesResponseType(typeof(Farm.Web.Shared.ResolveHostnameResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Farm.Web.Shared.ResolveHostnameResponse>> ResolveHostAsync([FromBody] Farm.Web.Shared.ResolveHostnameRequest body, CancellationToken ct)
    {
        if (body is null)
        {
            return BadRequest("Request body is required.");
        }
        var defaultPort = body.Backend == Farm.Web.Shared.PrinterBackend.PrusaLink ? 80 :
                         body.Backend == Farm.Web.Shared.PrinterBackend.SDCP ? 80 : 7125;
        var normalized = NormalizeServerUrl(body.ServerUrl, defaultPort);
        try
        {
            var uri = new Uri(normalized);
            var host = uri.Host;
            if (!System.Net.IPAddress.TryParse(host, out _))
            {
                host = EnsureLocalSuffix(host);
            }
            string? ip = null;
            try
            {
                if (!System.Net.IPAddress.TryParse(host, out _))
                {
                    var addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct);
                    var firstIp = Array.Find(addrs, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
                    ip = firstIp?.ToString();
                }
                else
                {
                    ip = host;
                }
            }
            catch { }

            var ub = new UriBuilder(uri) { Host = ip ?? uri.Host };
            var baseUrl = ub.Uri.ToString().TrimEnd('/');
            return new Farm.Web.Shared.ResolveHostnameResponse(normalized, ip, baseUrl);
        }
        catch
        {
            return BadRequest("Invalid URL");
        }
    }

    /// <summary>
    /// Deletes a printer configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the printer to delete</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>No content if successful</returns>
    /// <response code="204">If the printer was successfully deleted</response>
    /// <response code="404">If the printer with the specified ID was not found</response>
    /// <response code="500">If there was an error deleting the printer</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        { return NotFound(); }
        db.Printers.Remove(p);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Gets a camera snapshot image from the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The camera snapshot as an image file</returns>
    /// <response code="200">Returns the snapshot image</response>
    /// <response code="404">If the printer with the specified ID was not found</response>
    /// <response code="503">If the camera is not available or configured</response>
    [HttpGet("{id:guid}/snapshot")]
    [ProducesResponseType(typeof(FileContentResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> GetSnapshotAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        {
            return NotFound();
        }

        var bytes = await moon.GetCameraSnapshotAsync(p.ServerUrl, ct);
        if (bytes is null)
        { return NotFound(); }
        return File(bytes, "image/jpeg");
    }

    /// <summary>
    /// Homes all axes of the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Result indicating success or failure of the homing operation</returns>
    /// <response code="200">Returns the command execution result</response>
    /// <response code="404">If the printer with the specified ID was not found</response>
    /// <response code="500">If there was an error executing the homing command</response>
    [HttpPost("{id:guid}/home")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> HomeAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        { return NotFound(); }
        var ok = await moon.SendHomeAsync(p.ServerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to send home command");
    }

    /// <summary>
    /// Homes the X and Y axes of the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Result indicating success or failure of the homing operation</returns>
    /// <response code="200">Returns the command execution result</response>
    /// <response code="404">If the printer with the specified ID was not found</response>
    /// <response code="500">If there was an error executing the homing command</response>
    [HttpPost("{id:guid}/homexy")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> HomeXYAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        { return NotFound(); }
        var ok = await moon.HomeXYAsync(p.ServerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to home XY");
    }

    [HttpPost("{id:guid}/homez")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> HomeZAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        { return NotFound(); }
        var ok = await moon.HomeZAsync(p.ServerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to home Z");
    }

    [HttpPost("{id:guid}/temps")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> SetTempsAsync(Guid id, [FromBody] TempTargets targets, CancellationToken ct)
    {
        if (targets is null)
        {
            return BadRequest("Request body is required.");
        }
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        { return NotFound(); }
        var ok = await moon.SetTempsAsync(p.ServerUrl, targets.Hotend, targets.Bed, ct);
        return new CommandResult(ok, ok ? null : "Failed to set temperatures");
    }

    [HttpPost("{id:guid}/move")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> MoveAsync(Guid id, [FromBody] MoveRequest req, CancellationToken ct)
    {
        if (req is null)
        {
            return BadRequest("Request body is required.");
        }
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        { return NotFound(); }
        var ok = await moon.MoveAsync(p.ServerUrl, req.X, req.Y, req.Z, req.F, ct);
        return new CommandResult(ok, ok ? null : "Failed to move");
    }

    [HttpPost("{id:guid}/moveto")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> MoveToAsync(Guid id, [FromBody] MoveRequest req, CancellationToken ct)
    {
        if (req is null)
        {
            return BadRequest("Request body is required.");
        }
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        { return NotFound(); }
        var ok = await moon.MoveToAsync(p.ServerUrl, req.X, req.Y, req.Z, req.F, ct);
        return new CommandResult(ok, ok ? null : "Failed to move to position");
    }

    [HttpPost("{id:guid}/pause")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> PauseAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        {
            return NotFound();
        }

        bool ok;
        if (p.Backend == 2) // SDCP
        {
            ok = await sdcp.PausePrintAsync(p.ServerUrl, ct);
        }
        else // Moonraker (and PrusaLink for now)
        {
            ok = await moon.PauseAsync(p.ServerUrl, ct);
        }

        return new CommandResult(ok, ok ? null : "Failed to pause");
    }

    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> ResumeAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        {
            return NotFound();
        }

        bool ok;
        if (p.Backend == 2) // SDCP
        {
            ok = await sdcp.ResumePrintAsync(p.ServerUrl, ct);
        }
        else // Moonraker (and PrusaLink for now)
        {
            ok = await moon.ResumeAsync(p.ServerUrl, ct);
        }

        return new CommandResult(ok, ok ? null : "Failed to resume");
    }

    [HttpPost("{id:guid}/emergency-stop")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> EmergencyStopAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        {
            return NotFound();
        }

        bool ok;
        if (p.Backend == 2) // SDCP
        {
            ok = await sdcp.CancelPrintAsync(p.ServerUrl, ct);
        }
        else // Moonraker (and PrusaLink for now)
        {
            ok = await moon.EmergencyStopAsync(p.ServerUrl, ct);
        }

        return new CommandResult(ok, ok ? null : "Failed to emergency stop");
    }

    [HttpPost("{id:guid}/firmware-restart")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> FirmwareRestartAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        {
            return NotFound();
        }

        // Only Moonraker/Klipper supports firmware restart
        if (p.Backend != 0) // Not Moonraker
        {
            return BadRequest("Firmware restart is only supported for Moonraker/Klipper printers");
        }

        bool ok = await moon.FirmwareRestartAsync(p.ServerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to restart firmware");
    }

    // Print job control
    [HttpPost("{id:guid}/print/start")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> StartPrintAsync(Guid id, [FromBody] Farm.Web.Api.Controllers.Requests.StartPrintRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        {
            return NotFound();
        }

        if (p.Backend == 2) // SDCP
        {
            var ok = await sdcp.StartPrintAsync(p.ServerUrl, request.Filename, ct);
            return new CommandResult(ok, ok ? null : "Failed to start print");
        }

        return new CommandResult(false, "Start print not implemented for this printer type");
    }

    // Camera control endpoints
    [HttpPost("{id:guid}/camera/enable")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> EnableCameraAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        {
            return NotFound();
        }

        if (p.Backend == 2) // SDCP
        {
            var ok = await sdcp.EnableCameraAsync(p.ServerUrl, ct);
            return new CommandResult(ok, ok ? null : "Failed to enable camera");
        }

        return new CommandResult(false, "Camera control not supported for this printer type");
    }

    [HttpPost("{id:guid}/camera/disable")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> DisableCameraAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        {
            return NotFound();
        }

        if (p.Backend == 2) // SDCP
        {
            var ok = await sdcp.DisableCameraAsync(p.ServerUrl, ct);
            return new CommandResult(ok, ok ? null : "Failed to disable camera");
        }

        return new CommandResult(false, "Camera control not supported for this printer type");
    }

    [HttpGet("{id:guid}/camera/url")]
    [ProducesResponseType(typeof(CameraUrlResult), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CameraUrlResult>> GetCameraUrlAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        {
            return NotFound();
        }

        if (p.Backend == 2) // SDCP
        {
            var streamUrl = await sdcp.GetCameraUrlAsync(p.ServerUrl, ct);
            var snapshotUrl = await sdcp.GetCameraSnapshotUrlAsync(p.ServerUrl, ct);
            return new CameraUrlResult(streamUrl, snapshotUrl);
        }

        return new CameraUrlResult(null, null);
    }

    [HttpPost("{id:guid}/files/upload")]
    [ProducesResponseType(typeof(Farm.Web.Shared.UploadGcodeResultDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Farm.Web.Shared.UploadGcodeResultDto>> UploadGcodeAsync(Guid id, [FromForm] IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("No file provided");
        }

        if (!file.FileName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("File must be a .gcode file");
        }

        var p = await db.Printers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null)
        { return NotFound(); }

        try
        {
            await using var fileStream = file.OpenReadStream();
            bool success = ((PrinterBackend)p.Backend) switch
            {
                PrinterBackend.Moonraker => await moon.UploadGcodeAsync(p.ServerUrl, file.FileName, fileStream, ct),
                PrinterBackend.PrusaLink => await prusa.UploadGcodeAsync(p.ServerUrl, file.FileName, fileStream, p.ApiKey, ct),
                PrinterBackend.SDCP => await sdcp.UploadGcodeAsync(p.ServerUrl, file.FileName, fileStream, ct),
                _ => false
            };

            if (success)
            {
                return Ok(new Farm.Web.Shared.UploadGcodeResultDto("File uploaded successfully", file.FileName));
            }
            else
            {
                return StatusCode(500, "Failed to upload file to printer");
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Upload failed: {ex.Message}");
        }
    }

    [HttpGet("{id:guid}/files")]
    [ProducesResponseType(typeof(string[]), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<string[]>> GetFileListAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Printers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null)
        { return NotFound(); }

        try
        {
            string[] files = ((PrinterBackend)p.Backend) switch
            {
                PrinterBackend.Moonraker => await moon.GetFileListAsync(p.ServerUrl, ct),
                PrinterBackend.PrusaLink => await prusa.GetFileListAsync(p.ServerUrl, p.ApiKey, ct),
                PrinterBackend.SDCP => await sdcp.GetFileListAsync(p.ServerUrl, ct),
                _ => []
            };

            return Ok(files);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to get file list: {ex.Message}");
        }
    }

    [HttpPost("{id:guid}/files/{fileName}/print")]
    [ProducesResponseType(typeof(Farm.Web.Shared.StartPrintResultDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Farm.Web.Shared.StartPrintResultDto>> StartPrintFromFileAsync(Guid id, string fileName, CancellationToken ct)
    {
        var p = await db.Printers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p == null)
        { return NotFound(); }

        try
        {
            bool success = ((PrinterBackend)p.Backend) switch
            {
                PrinterBackend.Moonraker => await moon.StartPrintAsync(p.ServerUrl, fileName, ct),
                PrinterBackend.PrusaLink => await prusa.StartPrintAsync(p.ServerUrl, fileName, p.ApiKey, ct),
                PrinterBackend.SDCP => await sdcp.StartPrintAsync(p.ServerUrl, fileName, ct),
                _ => false
            };

            if (success)
            {
                return Ok(new Farm.Web.Shared.StartPrintResultDto("Print started successfully", fileName));
            }
            else
            {
                return StatusCode(500, "Failed to start print");
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to start print: {ex.Message}");
        }
    }

    // Helper record moved to top-level file

    // Helper method to get spool information for Moonraker printers
    private async Task<PrinterSpoolInfoDto?> GetSpoolInfoAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            // Get the active spool ID from Moonraker
            var activeSpoolId = await moon.GetSpoolmanActiveSpoolAsync(serverUrl, ct);
            if (activeSpoolId == null)
            {
                return new PrinterSpoolInfoDto(HasActiveSpool: false);
            }

            // Get spool details from Spoolman via Moonraker
            var spoolDetailsJson = await moon.GetSpoolmanSpoolByIdAsync(serverUrl, activeSpoolId.Value, ct);
            if (string.IsNullOrWhiteSpace(spoolDetailsJson))
            {
                return new PrinterSpoolInfoDto(
                    HasActiveSpool: true,
                    ActiveSpoolId: activeSpoolId
                );
            }

            // Parse the JSON response to extract spool information
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(spoolDetailsJson);
                var root = doc.RootElement;

                var spoolName = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var material = root.TryGetProperty("material", out var matEl) ? matEl.GetString() : null;
                var colorHex = root.TryGetProperty("color_hex", out var colorEl) ? colorEl.GetString() : null;
                var remainingWeight = root.TryGetProperty("remaining_weight", out var weightEl) && weightEl.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? weightEl.GetDouble() : (double?)null;

                // Check if filament information is nested
                string? filamentName = null;
                string? vendor = null;
                if (root.TryGetProperty("filament", out var filamentEl) && filamentEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    filamentName = filamentEl.TryGetProperty("name", out var fnameEl) ? fnameEl.GetString() : null;
                    if (filamentEl.TryGetProperty("vendor", out var vendorEl) && vendorEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        vendor = vendorEl.TryGetProperty("name", out var vNameEl) ? vNameEl.GetString() : null;
                    }
                }

                return new PrinterSpoolInfoDto(
                    HasActiveSpool: true,
                    ActiveSpoolId: activeSpoolId,
                    SpoolName: spoolName,
                    Material: material,
                    ColorHex: colorHex,
                    FilamentName: filamentName,
                    Vendor: vendor,
                    RemainingWeightG: remainingWeight,
                    SpoolInUse: true
                );
            }
            catch
            {
                // If JSON parsing fails, return basic info
                return new PrinterSpoolInfoDto(
                    HasActiveSpool: true,
                    ActiveSpoolId: activeSpoolId
                );
            }
        }
        catch
        {
            // If any Spoolman operations fail, just return no spool info
            return new PrinterSpoolInfoDto(HasActiveSpool: false);
        }
    }

    // ===== HISTORY ENDPOINTS =====

    [HttpGet("{id}/history")]
    [ProducesResponseType(typeof(Farm.Web.Shared.HistoryListResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Farm.Web.Shared.HistoryListResponse>> GetHistoryAsync(Guid id, [FromQuery] int? limit = null, [FromQuery] int? start = null, [FromQuery] DateTime? since = null, [FromQuery] DateTime? before = null, [FromQuery] string? order = null, CancellationToken ct = default)
    {
        var printer = await db.Printers.FindAsync(new object?[] { id }, cancellationToken: ct);
        if (printer == null)
        { return NotFound(); }

        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            // For non-Moonraker printers, return empty history for now
            return new Farm.Web.Shared.HistoryListResponse { Count = 0, Jobs = Array.Empty<Farm.Web.Shared.HistoryJob>() };
        }

        try
        {
            var moonrakerResponse = await moon.GetHistoryListAsync(printer.ServerUrl, limit, start, since, before, order, ct);
            if (moonrakerResponse == null)
            {
                return new Farm.Web.Shared.HistoryListResponse { Count = 0, Jobs = Array.Empty<Farm.Web.Shared.HistoryJob>() };
            }

            // Convert from Moonraker models to shared models
            var jobs = moonrakerResponse.Jobs.Select(j => new Farm.Web.Shared.HistoryJob
            {
                JobId = j.JobId,
                Exists = j.Exists,
                EndTime = j.EndTime,
                FilamentUsed = j.FilamentUsed,
                Filename = j.Filename,
                Metadata = j.Metadata,
                PrintDuration = j.PrintDuration,
                Status = j.Status,
                StartTime = j.StartTime,
                TotalDuration = j.TotalDuration,
                User = j.User,
                AuxiliaryData = j.AuxiliaryData?.Select(a => new Farm.Web.Shared.AuxiliaryData
                {
                    Provider = a.Provider,
                    Name = a.Name,
                    Value = a.Value,
                    Description = a.Description,
                    Units = a.Units
                }).ToArray(),
                ThumbnailUrl = ExtractThumbnailUrl(j.Metadata, printer.ServerUrl)
            }).ToArray();

            return new Farm.Web.Shared.HistoryListResponse
            {
                Count = moonrakerResponse.Count,
                Jobs = jobs
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get history for printer {id}: {ex.Message}");
            return new Farm.Web.Shared.HistoryListResponse { Count = 0, Jobs = Array.Empty<Farm.Web.Shared.HistoryJob>() };
        }
    }

    [HttpGet("{id}/history/{jobId}")]
    [ProducesResponseType(typeof(Farm.Web.Shared.HistoryJob), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(408)]
    [ProducesResponseType(502)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Farm.Web.Shared.HistoryJob>> GetHistoryJobAsync(Guid id, string jobId, CancellationToken ct = default)
    {
        // Input validation
        if (string.IsNullOrWhiteSpace(jobId))
        {
            logger.LogWarning("GetHistoryJob called with null or empty jobId for printer {PrinterId}", id);
            return BadRequest("Job ID is required");
        }

        var printer = await db.Printers.FindAsync(new object?[] { id, ct }, cancellationToken: ct);
        if (printer == null)
        {
            logger.LogWarning("Printer {PrinterId} not found for history job request", id);
            throw new PrinterNotFoundException($"Printer {id} not found");
        }

        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            logger.LogWarning("History requested for non-Moonraker printer {PrinterId} (Backend={Backend})",
                id, printer.Backend);
            return BadRequest("History is only available for Moonraker printers");
        }

        logger.LogDebug("Fetching history job {JobId} for printer {PrinterId} ({PrinterName})",
            jobId, id, printer.Name);

        try
        {
            var moonrakerJob = await moon.GetHistoryJobAsync(printer.ServerUrl, jobId, ct);
            if (moonrakerJob == null)
            {
                logger.LogInformation("History job {JobId} not found for printer {PrinterId}", jobId, id);
                return NotFound($"History job {jobId} not found");
            }

            // Convert from Moonraker model to shared model
            var job = new Farm.Web.Shared.HistoryJob
            {
                JobId = moonrakerJob.JobId,
                Exists = moonrakerJob.Exists,
                EndTime = moonrakerJob.EndTime,
                FilamentUsed = moonrakerJob.FilamentUsed,
                Filename = moonrakerJob.Filename,
                Metadata = moonrakerJob.Metadata,
                PrintDuration = moonrakerJob.PrintDuration,
                Status = moonrakerJob.Status,
                StartTime = moonrakerJob.StartTime,
                TotalDuration = moonrakerJob.TotalDuration,
                User = moonrakerJob.User,
                AuxiliaryData = moonrakerJob.AuxiliaryData?.Select(a => new Farm.Web.Shared.AuxiliaryData
                {
                    Provider = a.Provider,
                    Name = a.Name,
                    Value = a.Value,
                    Description = a.Description,
                    Units = a.Units
                }).ToArray()
            };

            logger.LogDebug("Successfully retrieved history job {JobId} for printer {PrinterId}", jobId, id);
            return job;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error retrieving history job {JobId} for printer {PrinterId} from {ServerUrl}",
                jobId, id, printer.ServerUrl);
            return StatusCode(502, "Unable to connect to printer");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogWarning(ex, "Timeout retrieving history job {JobId} for printer {PrinterId}", jobId, id);
            return StatusCode(408, "Request timeout");
        }
        // Let global exception handler catch other exceptions for consistent error responses
    }

    [HttpGet("{id}/history/totals")]
    [ProducesResponseType(typeof(Farm.Web.Shared.HistoryTotals), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Farm.Web.Shared.HistoryTotals>> GetHistoryTotalsAsync(Guid id, CancellationToken ct = default)
    {
        var printer = await db.Printers.FindAsync(new object?[] { id }, cancellationToken: ct);
        if (printer == null)
        { return NotFound(); }

        logger.LogDebug("GetHistoryTotals called for printer {PrinterId} ({PrinterName}), backend: {Backend}", id, printer.Name, printer.Backend);

        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            logger.LogInformation("Printer {PrinterId} is not Moonraker backend, returning empty totals", id);
            // Return empty totals for non-Moonraker printers
            return new Farm.Web.Shared.HistoryTotals
            {
                JobTotals = new Farm.Web.Shared.JobTotals()
            };
        }

        try
        {
            logger.LogDebug("Calling Moonraker API for totals at: {ServerUrl}", printer.ServerUrl);
            var moonrakerTotals = await moon.GetHistoryTotalsAsync(printer.ServerUrl, ct);
            if (moonrakerTotals == null)
            {
                logger.LogWarning("Moonraker API returned null totals");
                return new Farm.Web.Shared.HistoryTotals { JobTotals = new Farm.Web.Shared.JobTotals() };
            }

            logger.LogDebug("Moonraker totals received - Jobs: {Jobs}, PrintTime: {PrintTime}, FilamentUsed: {Filament}", moonrakerTotals.JobTotals.TotalJobs, moonrakerTotals.JobTotals.TotalPrintTime, moonrakerTotals.JobTotals.TotalFilamentUsed);

            // Convert from Moonraker model to shared model
            var totals = new Farm.Web.Shared.HistoryTotals
            {
                JobTotals = new Farm.Web.Shared.JobTotals
                {
                    TotalJobs = (int)moonrakerTotals.JobTotals.TotalJobs,
                    TotalTime = moonrakerTotals.JobTotals.TotalTime,
                    TotalPrintTime = moonrakerTotals.JobTotals.TotalPrintTime,
                    TotalFilamentUsed = moonrakerTotals.JobTotals.TotalFilamentUsed,
                    LongestJob = moonrakerTotals.JobTotals.LongestJob,
                    LongestPrint = moonrakerTotals.JobTotals.LongestPrint
                },
                AuxiliaryTotals = moonrakerTotals.AuxiliaryTotals?.Select(a => new Farm.Web.Shared.AuxiliaryTotals
                {
                    Provider = a.Provider,
                    Field = a.Field,
                    Maximum = a.Maximum,
                    Total = a.Total
                }).ToArray()
            };

            logger.LogDebug("Returning converted totals - Jobs: {Jobs}, PrintTime: {PrintTime}, FilamentUsed: {Filament}", totals.JobTotals.TotalJobs, totals.JobTotals.TotalPrintTime, totals.JobTotals.TotalFilamentUsed);
            return totals;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get history totals for printer {PrinterId}", id);
            return new Farm.Web.Shared.HistoryTotals { JobTotals = new Farm.Web.Shared.JobTotals() };
        }
    }

    [HttpDelete("{id}/history/{jobId}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteHistoryJobAsync(Guid id, string jobId, CancellationToken ct = default)
    {
        var printer = await db.Printers.FindAsync(new object?[] { id }, cancellationToken: ct);
        if (printer == null)
        { return NotFound(); }

        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            return BadRequest("History deletion is only available for Moonraker printers");
        }

        try
        {
            var success = await moon.DeleteHistoryJobAsync(printer.ServerUrl, jobId, ct);
            return success ? Ok() : StatusCode(500, "Failed to delete history job");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete history job {JobId} for printer {PrinterId}", jobId, id);
            return StatusCode(500, "Failed to delete history job");
        }
    }

    [HttpGet("export")]
    [ProducesResponseType(typeof(FileContentResult), 200)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> ExportPrintersAsync(CancellationToken ct)
    {
        var printers = await db.Printers
            .Include(p => p.Manufacturer)
            .Include(p => p.Model)
            .Select(p => new
            {
                p.Name,
                p.ServerUrl,
                p.OriginalServerUrl,
                p.Notes,
                ManufacturerName = p.Manufacturer != null ? p.Manufacturer.Name : "",
                ModelName = p.Model != null ? p.Model.Name : "",
                Backend = p.Backend.ToString(),
                p.ApiKey,
                p.DateAcquired
            })
            .ToListAsync(ct);

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Name,ServerUrl,OriginalServerUrl,Notes,ManufacturerName,ModelName,Backend,ApiKey,DateAcquired");

        foreach (var printer in printers)
        {
            csv.AppendLine($"{EscapeCsvValue(printer.Name)}," +
                          $"{EscapeCsvValue(printer.ServerUrl)}," +
                          $"{EscapeCsvValue(printer.OriginalServerUrl)}," +
                          $"{EscapeCsvValue(printer.Notes)}," +
                          $"{EscapeCsvValue(printer.ManufacturerName)}," +
                          $"{EscapeCsvValue(printer.ModelName)}," +
                          $"{EscapeCsvValue(printer.Backend)}," +
                          $"{EscapeCsvValue(printer.ApiKey)}," +
                          $"{EscapeCsvValue(printer.DateAcquired?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))}");
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"printers-export-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.csv");
    }

    [HttpPost("import")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> ImportPrintersAsync(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("No file provided");
        }

        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("File must be a CSV file");
        }

        var results = new List<object>();
        var errors = new List<string>();

        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            var csvContent = await reader.ReadToEndAsync(ct);
            var lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length < 2)
            {
                return BadRequest("CSV file must contain at least a header row and one data row");
            }

            var header = lines[0].Split(',');
            var expectedHeaders = new[] { "Name", "ServerUrl", "OriginalServerUrl", "Notes", "ManufacturerName", "ModelName", "Backend", "ApiKey", "DateAcquired" };

            // Validate header
            for (int i = 0; i < expectedHeaders.Length; i++)
            {
                if (i >= header.Length || !header[i].Trim().Equals(expectedHeaders[i], StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Invalid header format. Expected: {string.Join(",", expectedHeaders)}");
                    break;
                }
            }

            if (errors.Count == 0)
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    try
                    {
                        var values = ParseCsvLine(lines[i]);
                        if (values.Length >= 9)
                        {
                            var createDto = new CreatePrinterDto
                            {
                                Name = values[0]?.Trim() ?? "",
                                ServerUrl = values[1]?.Trim() ?? "",
                                OriginalServerUrl = string.IsNullOrWhiteSpace(values[2]) ? null : values[2].Trim(),
                                Notes = string.IsNullOrWhiteSpace(values[3]) ? null : values[3].Trim(),
                                NewManufacturerName = string.IsNullOrWhiteSpace(values[4]) ? null : values[4].Trim(),
                                NewModelName = string.IsNullOrWhiteSpace(values[5]) ? null : values[5].Trim(),
                                Backend = Enum.TryParse<PrinterBackend>(values[6]?.Trim(), true, out var backend) ? backend : PrinterBackend.Moonraker,
                                ApiKey = string.IsNullOrWhiteSpace(values[7]) ? null : values[7].Trim(),
                                DateAcquired = DateTime.TryParse(values[8]?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null
                            };

                            // Validate required fields
                            if (string.IsNullOrWhiteSpace(createDto.Name))
                            {
                                errors.Add($"Row {i + 1}: Name is required");
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(createDto.ServerUrl))
                            {
                                errors.Add($"Row {i + 1}: ServerUrl is required");
                                continue;
                            }

                            // Check if printer already exists
                            var existingPrinter = await db.Printers
                                .FirstOrDefaultAsync(p => p.Name == createDto.Name, ct);

                            if (existingPrinter != null)
                            {
                                results.Add(new { Row = i + 1, Name = createDto.Name, Status = "Skipped", Reason = "Printer already exists" });
                                continue;
                            }

                            // Create the printer using existing logic
                            var result = await CreatePrinterFromDtoAsync(createDto, ct);
                            results.Add(new { Row = i + 1, Name = createDto.Name, Status = "Imported", Id = result.Id });
                        }
                        else
                        {
                            errors.Add($"Row {i + 1}: Invalid number of columns");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Row {i + 1}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return BadRequest($"Error processing file: {ex.Message}");
        }

        return Ok(new
        {
            ImportedCount = results.Count(r => ((dynamic)r).Status == "Imported"),
            SkippedCount = results.Count(r => ((dynamic)r).Status == "Skipped"),
            Results = results,
            Errors = errors
        });
    }

    private static string EscapeCsvValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        // Escape quotes and wrap in quotes if contains comma, quote, or newline
        if (value.Contains('"'))
        {
            value = value.Replace("\"", "\"\"");
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            value = $"\"{value}\"";
        }

        return value;
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        int index = 0;
        while (index < line.Length)
        {
            char c = line[index];

            if (c == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    // Escaped quote
                    current.Append('"');
                    index += 2; // Skip the escaped quote pair
                    continue;
                }
                else
                {
                    // Toggle quote state
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                // End of field
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }

            index++;
        }

        // Add the last field
        result.Add(current.ToString());

        return result.ToArray();
    }

    private async Task<PrinterDto> CreatePrinterFromDtoAsync(CreatePrinterDto dto, CancellationToken ct)
    {
        // resolve or create manufacturer/model
        Guid manufacturerId = dto.ManufacturerId ?? Guid.Empty;
        if (manufacturerId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            var name = dto.NewManufacturerName!.Trim();
            var existing = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name == name, ct);
            if (existing is null)
            {
                existing = new Manufacturer { Id = Guid.NewGuid(), Name = name };
                db.Manufacturers.Add(existing);
                await db.SaveChangesAsync(ct);
            }
            manufacturerId = existing.Id;
        }

        Guid modelId = dto.ModelId ?? Guid.Empty;
        if (modelId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewModelName) && manufacturerId != Guid.Empty)
        {
            var mname = dto.NewModelName!.Trim();
            var existingModel = await db.Models.FirstOrDefaultAsync(m => m.ManufacturerId == manufacturerId && m.Name == mname, ct);
            if (existingModel is null)
            {
                existingModel = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturerId, Name = mname };
                db.Models.Add(existingModel);
                await db.SaveChangesAsync(ct);
            }
            modelId = existingModel.Id;
        }

        // Use default catalog entries if manufacturer or model are still empty
        if (manufacturerId == Guid.Empty || modelId == Guid.Empty)
        {
            var (defaultManufacturerId, defaultModelId) = await defaultCatalog.GetDefaultCatalogIdsAsync();
            if (manufacturerId == Guid.Empty)
            {
                manufacturerId = defaultManufacturerId;
            }
            if (modelId == Guid.Empty)
            {
                modelId = defaultModelId;
            }
        }

        // Resolve host to IP and persist the IP-based base URL; store original URL for future re-resolve
        var defaultPort = dto.Backend == PrinterBackend.PrusaLink ? 80 :
                         dto.Backend == PrinterBackend.SDCP ? 80 : 7125;
        var normalizedInput = NormalizeServerUrl(dto.ServerUrl, defaultPort);
        string resolvedBase = normalizedInput;
        string? resolvedIp = null;
        try
        {
            var uri = new Uri(normalizedInput);
            if (!System.Net.IPAddress.TryParse(uri.Host, out _))
            {
                var hostToResolve = EnsureLocalSuffix(uri.Host);
                var addresses = await System.Net.Dns.GetHostAddressesAsync(hostToResolve, ct);
                var firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (firstIp is not null)
                {
                    var ub = new UriBuilder(uri)
                    {
                        Host = firstIp.ToString()
                    };
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

        var p = new Printer
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            ServerUrl = resolvedBase,
            OriginalServerUrl = normalizedInput,
            IpAddress = resolvedIp,
            Notes = dto.Notes,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            DateAcquired = dto.DateAcquired?.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dto.DateAcquired.Value, DateTimeKind.Utc)
                : dto.DateAcquired,
            Backend = (int)dto.Backend,
            ApiKey = dto.ApiKey
        };
        db.Printers.Add(p);
        await db.SaveChangesAsync(ct);

        // Auto-discover capabilities for the newly created printer (import scenario)
        try
        {
            // Reload the printer with includes for proper discovery
            var printerForDiscovery = await db.Printers
                .Include(pr => pr.Manufacturer)
                .Include(pr => pr.Model)
                .FirstOrDefaultAsync(pr => pr.Id == p.Id, ct);
                
            if (printerForDiscovery != null)
            {
                var discoveredCapabilities = await capabilityDiscovery.DiscoverCapabilitiesAsync(printerForDiscovery, ct);
                // For bulk import, don't log individual success/failures to avoid log spam
                if (discoveredCapabilities == null)
                {
                    logger.LogDebug("Could not discover capabilities for imported printer: {Name} ({Id})", p.Name, p.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error during capability discovery for imported printer: {Name} ({Id})", p.Name, p.Id);
            // Don't fail the import if capability discovery fails
        }

        // For import, we'll return a simplified PrinterDto without live status to avoid network delays
        return new PrinterDto(
            Id: p.Id,
            Name: p.Name,
            ServerUrl: p.ServerUrl,
            Notes: p.Notes,
            IsOnline: false, // Will be updated by background service
            State: null,
            ManufacturerName: null,
            ModelName: null,
            Backend: dto.Backend,
            ApiKey: p.ApiKey,
            OriginalServerUrl: p.OriginalServerUrl,
            IpAddress: p.IpAddress
        );
    }

    // Helper method to extract thumbnail URL from metadata
    private static string? ExtractThumbnailUrl(Dictionary<string, object> metadata, string printerServerUrl)
    {
        if (metadata == null)
        {
            return null;
        }

        // Look for thumbnail in common metadata keys
        var thumbnailKeys = new[] { "thumbnail", "thumbnails", "gcode_thumbnail" };

        foreach (var key in thumbnailKeys)
        {
            if (metadata.TryGetValue(key, out var thumbnailValue))
            {
                // Handle different thumbnail formats
                if (thumbnailValue is string thumbnailStr && !string.IsNullOrEmpty(thumbnailStr))
                {
                    // If it's already a full URL, return it
                    if (thumbnailStr.StartsWith("http://") || thumbnailStr.StartsWith("https://"))
                    {
                        return thumbnailStr;
                    }

                    // Otherwise, construct the full URL
                    return $"{printerServerUrl.TrimEnd('/')}/server/files/gcodes/{thumbnailStr}";
                }

                // Handle array of thumbnails - take the first one
                if (thumbnailValue is System.Text.Json.JsonElement jsonElement && jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var array = jsonElement.EnumerateArray().ToList();
                    if (array.Count > 0)
                    {
                        // Handle array of strings (legacy format)
                        if (array[0].ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var thumbnailPath = array[0].GetString();
                            if (!string.IsNullOrEmpty(thumbnailPath))
                            {
                                return thumbnailPath.StartsWith("http") ? thumbnailPath : $"{printerServerUrl.TrimEnd('/')}/server/files/gcodes/{thumbnailPath}";
                            }
                        }
                        // Handle array of thumbnail objects with relative_path property
                        else if (array[0].ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            // Look for the largest thumbnail (prefer 400x300, then 300x300, then others)
                            var thumbnailObj = array
                                .Where(t => t.TryGetProperty("relative_path", out _))
                                .OrderByDescending(t =>
                                {
                                    var width = t.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                                    var height = t.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                                    return width * height; // Prefer larger thumbnails
                                })
                                .FirstOrDefault();

                            if (thumbnailObj.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                thumbnailObj.TryGetProperty("relative_path", out var relativePathProp))
                            {
                                var relativePath = relativePathProp.GetString();
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

    [HttpGet("test")]
    [ProducesResponseType(typeof(object), 200)]
    public IActionResult SimpleTest()
    {
        logger.LogCritical("=== SIMPLE TEST ENDPOINT CALLED ===");
        return Ok(new { message = "Simple test works!", timestamp = DateTime.UtcNow });
    }

    [HttpGet("discover")]
    [ProducesResponseType(typeof(IEnumerable<DiscoveredPrinterDto>), 200)]
    [ProducesResponseType(408)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<DiscoveredPrinterDto>>> DiscoverPrintersAsync(CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Starting network printer discovery...");

            // Set timeout for network discovery - with 100ms per IP, 254 IPs * 2 ports = ~51 seconds + overhead
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(15)); // 15 minute total timeout for full network scan

            var discovered = await networkDiscovery.DiscoverPrintersAsync(timeoutCts.Token);

            // Get existing printer ServerUrls to filter out duplicates
            var existingUrls = await db.Printers
                .AsNoTracking()
                .Select(p => p.ServerUrl)
                .ToListAsync(ct);

            // Normalize both existing and discovered URLs for proper comparison
            var normalizedExistingUrls = existingUrls
                .Select(url => NormalizeServerUrl(url, 80))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Filter out printers that already exist in the database
            var newPrinters = discovered
                .Where(d => !normalizedExistingUrls.Contains(NormalizeServerUrl(d.ServerUrl, 80)))
                .ToList();

            logger.LogInformation("Discovery completed. Found {TotalCount} printers, {NewCount} are new",
                discovered.Count, newPrinters.Count);

            return Ok(newPrinters);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Printer discovery was cancelled");
            return StatusCode(408, "Discovery operation timed out");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to discover printers on network");
            return StatusCode(500, "Failed to discover printers. Please try again.");
        }
    }

    [HttpPost("discover/stream")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(408)]
    [ProducesResponseType(500)]
    public ActionResult StartDiscoveryStream(CancellationToken ct)
    {
        try
        {
            // Generate a unique session ID for this discovery session
            var sessionId = Guid.NewGuid().ToString();

            logger.LogInformation("Starting streaming network printer discovery with session ID: {SessionId}", sessionId);

            // Start the discovery process in the background
            // The progress and results will be sent via SignalR
            _ = Task.Run(async () =>
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMinutes(15)); // 15 minute total timeout to allow for multiple networks and slow responses

                    await networkDiscovery.DiscoverPrintersWithProgressAsync(sessionId, timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("Streaming printer discovery was cancelled for session {SessionId}", sessionId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to discover printers in streaming mode for session {SessionId}", sessionId);
                }
            }, ct);

            // Return the session ID immediately so client can join the SignalR group
            return Ok(new { sessionId, message = "Discovery started. Connect to SignalR hub to receive updates." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start streaming printer discovery");
            return StatusCode(500, "Failed to start discovery stream. Please try again.");
        }
    }

    [HttpGet("test-simple")]
    [ProducesResponseType(typeof(object), 200)]
    public ActionResult TestSimple()
    {
        logger.LogCritical("=== SIMPLE TEST CALLED ===");
        return Ok(new { message = "API is working!", timestamp = DateTime.UtcNow });
    }

    // Request models moved to top-level files
}
