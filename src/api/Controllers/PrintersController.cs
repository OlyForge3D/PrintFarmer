using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Controllers.Responses;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/printers")]
public class PrintersController(
    IUnifiedLoggingService logger,
    Services.Printers.IPrintersService printersService,
    Services.Catalog.ICatalogService catalogService,
    IDefaultCatalogService defaultCatalogService,
    IValidator<CreatePrinterDto> validator,
    Services.Interfaces.IDiscoveryProxyService discoveryProxyService)
    : ControllerBase
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly Services.Printers.IPrintersService _printersService = printersService;
    private readonly Services.Catalog.ICatalogService _catalogService = catalogService;
    private readonly IDefaultCatalogService defaultCatalog = defaultCatalogService;
    private readonly IValidator<CreatePrinterDto> _validator = validator;
    private readonly Services.Interfaces.IDiscoveryProxyService _discoveryProxyService = discoveryProxyService;

    /// <summary>
    /// Retrieves camera URLs for all printers without making external API calls.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>A lightweight list of all printers with their configured camera URLs</returns>
    /// <response code="200">Returns the list of printers with camera URL information</response>
    [HttpGet("camera-urls")]
    [ProducesResponseType(typeof(IEnumerable<PrinterCameraUrlsDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<PrinterCameraUrlsDto>>> GetCameraUrlsAsync(CancellationToken ct)
    {
        try
        {
            PrinterCameraUrlsDto[] dtos = await _printersService.GetCameraUrlsAsync(ct);
            return Ok(dtos.ToList());
        }
        catch (Exception ex) when (IsTransientStartupDbException(ex))
        {
            _logger.LogWarning($"[CAMERA-URLS] Startup DB exception in /api/printers/camera-urls. TraceId={HttpContext.TraceIdentifier}, Exception={ex.Message}");
            return Ok(Array.Empty<PrinterCameraUrlsDto>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[FATAL] Unhandled exception in /api/printers/camera-urls. TraceId={HttpContext.TraceIdentifier}, User={User?.Identity?.Name ?? "anonymous"}, Exception={ex.Message}\n{ex.StackTrace}");
            return StatusCode(StatusCodes.Status500InternalServerError, $"Internal Server Error: {ex.Message}");
        }
    }

    private static bool IsTransientStartupDbException(Exception ex)
    {
        // SQLite "no such table" or other typical init race messages
        string msg = ex.GetBaseException().Message;
        return msg.Contains("no such table", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("database is locked", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Retrieves a lightweight list of all printers with minimal data for quick loading.
    /// This is the default GET endpoint for the printers resource.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <param name="includeDisabled">Return disabled printers as well (admin-only)</param>
    /// <returns>A lightweight list of all printers with basic information</returns>
    /// <response code="200">Returns the list of lightweight printer data</response>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PrinterFastDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<PrinterFastDto>>> GetAsync(CancellationToken ct, [FromQuery] bool includeDisabled = false)
    {
        try
        {
            PrinterFastDto[] dtos = await _printersService.GetAllFastDtosAsync(ct);
            bool isAdmin = User.IsInRole("farm_admin");
            if (isAdmin)
            {
                return Ok(dtos);
            }

            if (includeDisabled)
            {
                return Forbid();
            }

            // Filter to only enabled printers for normal users
            List<PrinterFastDto> enabledDtos = dtos.Where(p => p.IsEnabled).ToList();
            return Ok(enabledDtos);
        }
        catch (Exception ex) when (IsTransientStartupDbException(ex))
        {
            _logger.LogWarning($"[GET] Startup DB exception in /api/printers. TraceId={HttpContext.TraceIdentifier}, Exception={ex.Message}");
            return Ok(Array.Empty<PrinterFastDto>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[FATAL] Unhandled exception in /api/printers. TraceId={HttpContext.TraceIdentifier}, User={User?.Identity?.Name ?? "anonymous"}, Exception={ex.Message}\n{ex.StackTrace}");
            return StatusCode(StatusCodes.Status500InternalServerError, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates multiple printers in a bulk operation.
    /// </summary>
    /// <param name="printers">Array of printer configurations to create</param>
    /// <param name="duplicateHandling">How to handle duplicate printers: 'skip' (default), 'overwrite', or 'error'</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Result of bulk import operation including created printers and errors</returns>
    /// <response code="200">Returns bulk import results with created printers and any errors</response>
    /// <response code="400">If the printer data is invalid</response>
    /// <response code="500">If there was an error creating printers</response>
    [HttpPost("bulk")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> BulkCreateAsync(
        [FromBody] CreatePrinterDto[] printers,
        [FromQuery] string? duplicateHandling = "skip",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(printers);

        if (printers.Length == 0)
        {
            return BadRequest(new { message = "At least one printer configuration is required" });
        }

        // Validate all printers first before delegating to service
        Dictionary<int, List<string>> validationErrors = new Dictionary<int, List<string>>();
        for (int i = 0; i < printers.Length; i++)
        {
            ValidationResult result = await _validator.ValidateAsync(printers[i], ct);
            if (!result.IsValid)
            {
                validationErrors[i] = result.Errors.Select(e => e.ErrorMessage).ToList();
            }
        }

        // If all printers failed validation, return error
        if (validationErrors.Count == printers.Length)
        {
            string errorMessage = string.Join("; ", validationErrors.SelectMany(kvp =>
                kvp.Value.Select(err => $"[Printer {kvp.Key}] {err}")));
            _logger.LogWarning($"[BulkCreate] Validation failed for all printers: {errorMessage}");
            return BadRequest(new { message = "All printers failed validation", errors = validationErrors });
        }

        try
        {
            // Delegate to service for actual creation logic
            object result = await _printersService.BulkCreatePrintersAsync(printers, duplicateHandling ?? "skip", ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[BulkCreate] Bulk printer creation failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Bulk creation operation failed",
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Imports printers from an uploaded CSV or JSON file with optional duplicate handling.
    /// </summary>
    /// <param name="file">The CSV or JSON file containing printer configurations</param>
    /// <param name="duplicateHandling">How to handle duplicates: 'skip' (default), 'overwrite', or 'error'</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Result of import operation including created printers and any errors</returns>
    /// <response code="200">Returns import results with created printers and errors</response>
    /// <response code="400">If the file is invalid or missing</response>
    /// <response code="500">If there was an error importing printers</response>
    [HttpPost("import")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "farm_admin")]
    public async Task<IActionResult> ImportFromFileAsync(
        [FromForm] IFormFile file,
        [FromQuery] string? duplicateHandling = "skip",
        CancellationToken ct = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No file provided or file is empty" });
        }

        string fileExtension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (fileExtension != ".csv" && fileExtension != ".json")
        {
            return BadRequest(new { message = "File must be CSV or JSON format" });
        }

        if (file.Length > 10 * 1024 * 1024) // 10MB limit
        {
            return BadRequest(new { message = "File is too large (max 10MB)" });
        }

        try
        {
            _logger.LogInformation($"[Import] Starting import from file: {file.FileName}");
            object result = await _printersService.ImportFromFileAsync(file, duplicateHandling ?? "skip", ct);
            _logger.LogInformation($"[Import] Successfully imported from file: {file.FileName}");
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning($"[Import] Validation error: {ex.Message}");
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning($"[Import] Invalid data error: {ex.Message}");
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[Import] Import operation failed: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Import operation failed",
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Gets the current print job status for a specific printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The current print job status if a job is running, otherwise null</returns>
    /// <response code="200">Returns the print job status or null if no job running</response>
    /// <response code="404">If the printer with the specified ID was not found</response>
    /// <response code="500">If there was an error retrieving job status</response>
    [HttpGet("{id:guid}/printjob")]
    [ProducesResponseType(typeof(PrintJobStatusDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> GetPrintJobStatusAsync(Guid id, CancellationToken ct)
    {
        try
        {
            // Verify printer exists first
            Printer? printer = await _printersService.FindByIdWithIncludesAsync(id, ct);
            if (printer == null)
            {
                _logger.LogWarning($"[PrintJob] Printer {id} not found");
                return NotFound(new { message = $"Printer {id} not found" });
            }

            _logger.LogInformation($"[PrintJob] Getting print job status for printer {printer.Name}");

            // Delegate to service for actual retrieval logic
            PrintJobStatusDto? jobStatus = await _printersService.GetPrintJobStatusAsync(id, ct);

            // Return the status (may be null if no active job)
            return Ok(jobStatus);
        }
        catch (KeyNotFoundException)
        {
            _logger.LogWarning($"[PrintJob] Printer {id} not found");
            return NotFound(new { message = $"Printer {id} not found" });
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning($"[PrintJob] Timeout retrieving print job status for printer {id}");
            return Ok((object?)null); // Return null on timeout
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[PrintJob] Error getting print job status for printer {id}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Failed to retrieve print job status",
                error = ex.Message
            });
        }
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
        try
        {
            PrinterStatusDto dto = await _printersService.GetStatusDtoAsync(id, ct);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Error getting status for printer {id}: {ex.Message}");
            return new PrinterStatusDto(Id: id, IsOnline: false, State: null, Progress: null, JobName: null, ThumbnailUrl: null, CameraStreamUrl: null, CameraSnapshotUrl: null, SpoolInfo: null);
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
        try
        {
            PrinterDto dto = await _printersService.GetPrinterDtoAsync(id, ct);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get printer {id}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get printer");
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
        Printer? p = await _printersService.FindByIdWithIncludesAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        PrinterCapabilitiesDto? capabilitiesDto = null;
        if (p.Capabilities != null)
        {
            capabilitiesDto = new PrinterCapabilitiesDto(
                p.Capabilities.Id,
                p.Capabilities.PrinterId,
                p.Name,
                p.Capabilities.NozzleDiameter,
                p.Capabilities.SupportedMaterials,
                p.Capabilities.MaxBuildVolumeX,
                p.Capabilities.MaxBuildVolumeY,
                p.Capabilities.MaxBuildVolumeZ,
                p.Capabilities.HasHeatedBed,
                p.Capabilities.HasEnclosure,
                p.Capabilities.MultiMaterial,
                p.Capabilities.SupportsAutoLeveling,
                p.Capabilities.NumberOfExtruders,
                p.Capabilities.MinHotendTemp,
                p.Capabilities.MaxHotendTemp,
                p.Capabilities.MinBedTemp,
                p.Capabilities.MaxBedTemp,
                p.Capabilities.CurrentMaterial,
                p.Capabilities.CurrentSpoolId,
                p.Capabilities.IsAvailable,
                p.Capabilities.LastUpdated
            );
        }

        return new PrinterDetailsDto(
            p.Id,
            p.Name,
            p.ServerUrl,
            p.Notes,
            p.ManufacturerId,
            p.Manufacturer?.Name,
            p.ModelId,
            p.Model?.Name,
            p.Model?.MotionType != null ? (MotionType)p.Model.MotionType.Value : (MotionType?)null,
            p.Model?.MaxX,
            p.Model?.MaxY,
            p.Model?.MaxZ,
            p.DateAcquired,
            (PrinterBackend)p.Backend,
            p.ApiKey,
            null, // CameraStreamUrl (not available here)
            null, // CameraSnapshotUrl (not available here)
            p.OriginalServerUrl,
            p.IpAddress,
            p.BackendPort,
            p.FrontendPort,
            capabilitiesDto
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
        ValidationResult validationResult = await _validator.ValidateAsync(dto, ct);
        if (!validationResult.IsValid)
        {
            _logger.LogWarning($"Printer creation validation failed: {string.Join("; ", validationResult.Errors.Select(e => e.ErrorMessage))}");

            foreach (ValidationFailure? error in validationResult.Errors)
            {
                ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
            }

            return BadRequest(ModelState);
        }

        _logger.LogInformation($"Creating new printer: {dto.Name} ({dto.Backend})");

        // Delegate creation/business logic to the service
        PrinterDto created = await _printersService.CreatePrinterFromDtoAsync(dto, ct);
        return CreatedAtRoute("GetPrinterById", new { id = created.Id }, created);
    }

    /// <summary>
    /// Register printers discovered by the network discovery service.
    /// Accepts both single printers and arrays for backward compatibility.
    /// </summary>
    /// <param name="discoveredPrinters">Discovered printer(s) to register</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of registered printers</returns>
    /// <response code="200">Successfully registered discovered printer(s)</response>
    /// <response code="400">Invalid printer data</response>
    /// <response code="500">Server error</response>
    [HttpPost("discovered")]
    [ProducesResponseType(typeof(IEnumerable<PrinterDto>), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<PrinterDto>>> RegisterDiscoveredAsync(
        [FromBody] object? discoveredPrinters,
        CancellationToken ct)
    {
        if (discoveredPrinters == null)
        {
            return BadRequest("No printers provided");
        }

        // Parse input - could be single DiscoveredPrinterDto or array
        List<DiscoveredPrinterDto> printers = new();

        if (discoveredPrinters is List<DiscoveredPrinterDto> list)
        {
            printers.AddRange(list);
        }
        else if (discoveredPrinters is DiscoveredPrinterDto single)
        {
            printers.Add(single);
        }
        else if (discoveredPrinters is JsonElement jsonElem)
        {
            // Handle JSON deserialization
            try
            {
                JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                if (jsonElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    DiscoveredPrinterDto[]? array = JsonSerializer.Deserialize<DiscoveredPrinterDto[]>(jsonElem.GetRawText(), options);
                    if (array != null)
                    {
                        printers.AddRange(array);
                    }
                }
                else
                {
                    DiscoveredPrinterDto? obj = JsonSerializer.Deserialize<DiscoveredPrinterDto>(jsonElem.GetRawText(), options);
                    if (obj != null)
                    {
                        printers.Add(obj);
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse discovered printers JSON");
                return BadRequest("Invalid printer data format");
            }
        }

        if (!printers.Any())
        {
            return BadRequest("No valid printers provided");
        }

        List<PrinterDto> registered = new List<PrinterDto>();

        foreach (DiscoveredPrinterDto discovered in printers)
        {
            try
            {
                _logger.LogInformation(
                    $"Processing discovered printer: {discovered.Name} " +
                    $"({discovered.IpAddress}:{discovered.BackendPort ?? 80}) - Backend: {discovered.Backend}");

                // Check if printer already exists by normalized server URL
                string normalizedUrl = _printersService.NormalizeServerUrl(discovered.ServerUrl, discovered.BackendPort ?? 80);
                Printer? existing = (await _printersService.GetAllAsync(ct))
                    .FirstOrDefault(p => _printersService.NormalizeServerUrl(p.ServerUrl, 80) == normalizedUrl);

                if (existing != null)
                {
                    _logger.LogInformation($"Printer already registered: {existing.Name}");
                    PrinterDto existingDto = await _printersService.GetPrinterDtoAsync(existing.Id, ct);
                    if (existingDto != null)
                    {
                        registered.Add(existingDto);
                    }
                    continue;
                }

                // Create new printer from discovered data, preserving all discovered metadata
                CreatePrinterDto createDto = CreatePrinterDto.FromDiscovered(discovered);

                ValidationResult validationResult = await _validator.ValidateAsync(createDto, ct);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning(
                        $"Discovered printer validation failed: {string.Join("; ", validationResult.Errors.Select(e => e.ErrorMessage))}");
                    continue;
                }

                // Create the printer
                PrinterDto created = await _printersService.CreatePrinterFromDtoAsync(createDto, ct);
                registered.Add(created);

                _logger.LogInformation($"Successfully registered discovered printer: {created.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to register discovered printer: {discovered.Name}");
                // Continue with next printer on error
            }
        }

        return Ok(registered);
    }

    /// <summary>
    /// Sets the maintenance mode for a printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer</param>
    /// <param name="inMaintenance">True to enable maintenance mode, false to disable</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The updated printer DTO</returns>
    /// <response code="200">Returns the updated printer</response>
    /// <response code="404">If the printer with the specified ID was not found</response>
    /// <response code="500">If there was an error updating the printer</response>
    [HttpPut("{id:guid}/maintenance")]
    [ProducesResponseType(typeof(PrinterDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterDto>> SetMaintenanceModeAsync(Guid id, [FromBody] bool inMaintenance, CancellationToken ct)
    {
        Printer? printer = await _printersService.FindByIdAsync(id, ct);
        if (printer is null)
        {
            return NotFound();
        }
        printer.InMaintenance = inMaintenance;
        await _printersService.SaveChangesAsync(ct);

        // Optionally, you may want to return the updated DTO with more info
        string? manufacturerName = null;
        string? modelName = null;
        if (printer.ManufacturerId != Guid.Empty)
        {
            ManufacturerDto? man = await _catalogService.GetManufacturerByIdAsync(printer.ManufacturerId, ct);
            manufacturerName = man?.Name;
        }
        if (printer.ModelId != Guid.Empty)
        {
            PrinterModelDto? mod = await _catalogService.GetModelByIdAsync(printer.ModelId, ct);
            modelName = mod?.Name;
        }
        PrinterDto dto = new(
            Id: printer.Id,
            Name: printer.Name,
            ServerUrl: printer.ServerUrl,
            Notes: printer.Notes,
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
            Backend: (PrinterBackend)printer.Backend,
            ApiKey: printer.ApiKey,
            OriginalServerUrl: printer.OriginalServerUrl,
            IpAddress: printer.IpAddress
        );
        return Ok(dto);
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
        Printer? p = await _printersService.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }
        // resolve or create manufacturer/model
        Guid manufacturerId = dto.ManufacturerId ?? p.ManufacturerId;
        if (dto.ManufacturerId is null && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            string name = dto.NewManufacturerName!.Trim();
            // ICatalogRepository does not expose GetManufacturerByName; create via CatalogService
            ManufacturerDto created = await _catalogService.CreateManufacturerAsync(name, ct);
            manufacturerId = created.Id;
        }

        Guid modelId = dto.ModelId ?? p.ModelId;
        if ((dto.ModelId is null && !string.IsNullOrWhiteSpace(dto.NewModelName)) && manufacturerId != Guid.Empty)
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
                SupportedFilamentTypeIds: Array.Empty<Guid>());
            PrinterModelDto createdModel = await _catalogService.CreateModelAsync(createReq, ct);
            modelId = createdModel.Id;
        }

        // Use default catalog entries if manufacturer or model are still empty
        if (manufacturerId == Guid.Empty || modelId == Guid.Empty)
        {
            (Guid defaultManufacturerId, Guid defaultModelId) = await defaultCatalog.GetDefaultCatalogIdsAsync();
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
        int defaultPort = dto.Backend.HasValue ?
            (dto.Backend.Value == PrinterBackend.PrusaLink ? 80 :
             dto.Backend.Value == PrinterBackend.SDCP ? 80 :
             dto.Backend.Value == PrinterBackend.OctoPrint ? 5000 : 7125) :
            (p.Backend == (int)Farm.Infrastructure.PrinterBackend.PrusaLink ? 80 :
             p.Backend == (int)Farm.Infrastructure.PrinterBackend.SDCP ? 80 :
             p.Backend == (int)Farm.Infrastructure.PrinterBackend.OctoPrint ? 5000 : 7125);

        // Delegate normalization and optional hostname resolution to the PrintersService
        PrinterBackend backendForResolve = dto.Backend ?? (PrinterBackend)p.Backend;
        ResolveHostnameResponse resolveResp = await _printersService.ResolveHostnameAsync(dto.ServerUrl, backendForResolve, ct);
        p.ServerUrl = resolveResp.ResolvedBaseUrl ?? resolveResp.NormalizedInputUrl;
        p.OriginalServerUrl = resolveResp.NormalizedInputUrl;
        p.IpAddress = resolveResp.ResolvedIp;
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

        // Update port settings
        if (dto.BackendPort.HasValue)
        {
            p.BackendPort = dto.BackendPort.Value;
        }
        if (dto.FrontendPort.HasValue)
        {
            p.FrontendPort = dto.FrontendPort.Value;
        }

        // Update IsEnabled if provided
        if (dto.IsEnabled.HasValue)
        {
            p.IsEnabled = dto.IsEnabled.Value;
        }

        // Update or create printer capabilities
        PrinterCapabilities? capabilities = await _printersService.GetCapabilitiesByPrinterIdAsync(id, ct);
        if (capabilities == null)
        {
            capabilities = new PrinterCapabilities
            {
                Id = Guid.NewGuid(),
                PrinterId = id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        // Update capability fields from DTO
        capabilities.NozzleDiameter = dto.NozzleDiameter;
        capabilities.SupportedMaterials = dto.SupportedMaterials;
        capabilities.MaxBuildVolumeX = dto.MaxBuildVolumeX;
        capabilities.MaxBuildVolumeY = dto.MaxBuildVolumeY;
        capabilities.MaxBuildVolumeZ = dto.MaxBuildVolumeZ;
        capabilities.HasHeatedBed = dto.HasHeatedBed ?? true;
        capabilities.HasEnclosure = dto.HasEnclosure ?? false;
        capabilities.MultiMaterial = dto.MultiMaterial ?? false;
        capabilities.NumberOfExtruders = dto.NumberOfExtruders ?? 1;
        capabilities.MinHotendTemp = dto.MinHotendTemp;
        capabilities.MaxHotendTemp = dto.MaxHotendTemp;
        capabilities.MinBedTemp = dto.MinBedTemp;
        capabilities.MaxBedTemp = dto.MaxBedTemp;
        capabilities.SupportsAutoLeveling = dto.SupportsAutoLeveling ?? false;
        capabilities.MaxPrintSpeed = dto.MaxPrintSpeed;
        capabilities.LastUpdated = DateTime.UtcNow;
        capabilities.UpdatedAt = DateTime.UtcNow;

        await _printersService.SaveCapabilitiesAsync(capabilities, ct);

        // Build updated manufacturer/model names
        string? manufacturerName = null;
        string? modelName = null;
        if (p.ManufacturerId != Guid.Empty)
        {
            ManufacturerDto? man = await _catalogService.GetManufacturerByIdAsync(p.ManufacturerId, ct);
            manufacturerName = man?.Name;
        }
        if (p.ModelId != Guid.Empty)
        {
            PrinterModelDto? mod = await _catalogService.GetModelByIdAsync(p.ModelId, ct);
            modelName = mod?.Name;
        }

        PrinterDto dtoResponse = new(
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
    [ProducesResponseType(typeof(ResolveHostnameResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<ResolveHostnameResponse>> ResolveHostAsync([FromBody] ResolveHostnameRequest body, CancellationToken ct)
    {
        if (body is null)
        {
            return BadRequest("Request body is required.");
        }
        // Delegate hostname normalization and resolution to the service
        int defaultPort = body.Backend == Farm.Infrastructure.PrinterBackend.PrusaLink ? 80 :
                         body.Backend == Farm.Infrastructure.PrinterBackend.SDCP ? 80 : 7125;
        try
        {
            ResolveHostnameResponse resp = await _printersService.ResolveHostnameAsync(body.ServerUrl, body.Backend, ct);
            return Ok(resp);
        }
        catch (ArgumentException)
        {
            return BadRequest("Invalid URL");
        }
    }

    /// <summary>
    /// Gets the default capabilities for a printer model.
    /// </summary>
    /// <param name="modelId">The unique identifier of the printer model</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Default printer capabilities based on the model</returns>
    /// <response code="200">Returns the default capabilities for the model</response>
    /// <response code="404">If the model with the specified ID was not found</response>
    /// <response code="204">If no default capabilities are available for the model</response>
    /// <response code="500">If there was an error retrieving the capabilities</response>
    [HttpGet("model/{modelId:guid}/default-capabilities")]
    [ProducesResponseType(typeof(PrinterCapabilitiesDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(204)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterCapabilitiesDto>> GetModelDefaultCapabilitiesAsync(Guid modelId, CancellationToken ct)
    {
        // Load model to verify it exists and get capability data
        PrinterModelDto? modelDto = await _catalogService.GetModelByIdAsync(modelId, ct);
        if (modelDto == null)
        {
            return NotFound($"Printer model with ID {modelId} not found");
        }

        // Map modelDto to a lightweight shape consistent with previous behavior
        // Note: modelDto contains ManufacturerName, SupportedFilamentTypes and capability fields

        try
        {
            bool hasCapabilityData = modelDto.MaxX.HasValue || modelDto.MaxY.HasValue || modelDto.MaxZ.HasValue ||
                                     modelDto.DefaultNozzleDiameter.HasValue || modelDto.MaxHotendTemp.HasValue ||
                                     modelDto.MaxBedTemp.HasValue || (modelDto.SupportedFilamentTypes != null && modelDto.SupportedFilamentTypes.Length > 0);

            if (!hasCapabilityData)
            {
                return NoContent();
            }

            PrinterCapabilitiesDto dto = new(
                Id: Guid.Empty,
                PrinterId: Guid.Empty,
                PrinterName: modelDto.Name,
                NozzleDiameter: modelDto.DefaultNozzleDiameter,
                    SupportedMaterials: modelDto.SupportedFilamentTypes ?? Array.Empty<string>(),
                MaxBuildVolumeX: modelDto.MaxX,
                MaxBuildVolumeY: modelDto.MaxY,
                MaxBuildVolumeZ: modelDto.MaxZ,
                HasHeatedBed: modelDto.HasHeatedBed,
                HasEnclosure: modelDto.HasEnclosure,
                MultiMaterial: modelDto.MultiMaterial,
                NumberOfExtruders: modelDto.NumberOfExtruders,
                MinHotendTemp: modelDto.MinHotendTemp,
                MaxHotendTemp: modelDto.MaxHotendTemp,
                MinBedTemp: modelDto.MinBedTemp,
                MaxBedTemp: modelDto.MaxBedTemp,
                CurrentMaterial: null,
                CurrentSpoolId: null,
                IsAvailable: true,
                LastUpdated: DateTime.UtcNow
            );

            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error retrieving default capabilities for model {modelId}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve model capabilities");
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
        Printer? p = await _printersService.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }
        await _printersService.RemoveAsync(p, ct);
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
        byte[]? bytes = await _printersService.GetCameraSnapshotAsync(id, ct);
        return bytes is null ? NotFound() : File(bytes, "image/jpeg");
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
        bool ok = await _printersService.SendHomeAsync(id, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
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
        bool ok = await _printersService.HomeXYAsync(id, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
    }

    [HttpPost("{id:guid}/homez")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> HomeZAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.HomeZAsync(id, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
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
        bool ok = await _printersService.SetTempsAsync(id, targets.Hotend, targets.Bed, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
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
        bool ok = await _printersService.MoveAsync(id, req.X, req.Y, req.Z, req.F, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
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
        bool ok = await _printersService.MoveToAsync(id, req.X, req.Y, req.Z, req.F, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
    }

    [HttpPost("{id:guid}/pause")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> PauseAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.PauseAsync(id, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
    }

    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> ResumeAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.ResumeAsync(id, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
    }

    [HttpPost("{id:guid}/emergency-stop")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> EmergencyStopAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.EmergencyStopAsync(id, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
    }

    [HttpPost("{id:guid}/stop")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> StopAsync(Guid id, CancellationToken ct)
    {
        // Alias for emergency-stop for compatibility with frontend
        return await EmergencyStopAsync(id, ct);
    }

    [HttpPost("{id:guid}/firmware-restart")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> FirmwareRestartAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.FirmwareRestartAsync(id, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
    }

    [HttpPost("{id:guid}/disable-motors")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> DisableMotorsAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.DisableMotorsAsync(id, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
    }

    // Camera control endpoints
    [HttpPost("{id:guid}/camera/enable")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> EnableCameraAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.EnableCameraAsync(id, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
    }

    [HttpPost("{id:guid}/camera/disable")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> DisableCameraAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.DisableCameraAsync(id, ct);
        if (!ok)
        {
            return NotFound();
        }
        return new CommandResult(true, null);
    }

    [HttpGet("{id:guid}/camera/url")]
    [ProducesResponseType(typeof(CameraUrlResult), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CameraUrlResult>> GetCameraUrlAsync(Guid id, CancellationToken ct)
    {
        (string? streamUrl, string? snapshotUrl) = await _printersService.GetCameraUrlsForPrinterAsync(id, ct);
        if (streamUrl == null && snapshotUrl == null)
        {
            return NotFound();
        }
        return new CameraUrlResult(streamUrl, snapshotUrl);
    }

    [HttpPost("{id:guid}/files/upload")]
    [ProducesResponseType(typeof(UploadGcodeResultDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<UploadGcodeResultDto>> UploadGcodeAsync(Guid id, [FromForm] IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("No file provided");
        }

        if (!file.FileName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("File must be a .gcode file");
        }

        try
        {
            await using Stream fileStream = file.OpenReadStream();
            bool success = await _printersService.UploadGcodeAsync(id, file.FileName, fileStream, ct);

            if (!success)
            {
                return NotFound();
            }

            return Ok(new UploadGcodeResultDto("File uploaded successfully", file.FileName));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Upload failed: {ex.Message}");
        }
    }

    [HttpGet("{id:guid}/files")]
    [ProducesResponseType(typeof(PrinterFileDto[]), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterFileDto[]>> GetFileListAsync(Guid id, CancellationToken ct)
    {
        try
        {
            PrinterFileDto[] files = await _printersService.GetFileListAsync(id, ct);
            return Ok(files);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Failed to get file list: {ex.Message}");
        }
    }

    // File operations with body-based parameters (handles special characters in filenames)
    [HttpPost("{id:guid}/print")]
    [ProducesResponseType(typeof(StartPrintResultDto), 200)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 500)]
    public async Task<ActionResult<CommandResult>> StartPrintAsync(Guid id, [FromBody] FileOperationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request?.FileName))
        {
            return BadRequest(new CommandResult(false, "fileName is required"));
        }

        try
        {
            bool success = await _printersService.StartPrintFromFileAsync(id, request.FileName, ct);
            if (!success)
            {
                return Ok(new CommandResult(false, $"Printer not found or unable to start print for file: {request.FileName}"));
            }
            return Ok(new CommandResult(true, "Print started successfully"));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new CommandResult(false, $"Failed to start print: {ex.Message}"));
        }
    }

    [HttpDelete("{id:guid}/files")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 500)]
    public async Task<ActionResult<CommandResult>> DeleteFileAsync(Guid id, [FromBody] FileOperationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request?.FileName))
        {
            return BadRequest(new CommandResult(false, "fileName is required"));
        }

        try
        {
            bool success = await _printersService.DeletePrinterFileAsync(id, request.FileName, ct);
            if (!success)
            {
                return Ok(new CommandResult(false, $"Printer not found or unable to delete file: {request.FileName}"));
            }
            return Ok(new CommandResult(true, "File deleted successfully"));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new CommandResult(false, $"Failed to delete file: {ex.Message}"));
        }
    }

    // ===== HISTORY ENDPOINTS =====

    [HttpGet("{id}/history")]
    [ProducesResponseType(typeof(HistoryListResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<HistoryListResponse>> GetHistoryAsync(Guid id, [FromQuery] int? limit = null, [FromQuery] int? start = null, [FromQuery] DateTime? since = null, [FromQuery] DateTime? before = null, [FromQuery] string? order = null, CancellationToken ct = default)
    {
        try
        {
            HistoryListResponse resp = await _printersService.GetHistoryListAsync(id, limit, start, since, before, order, ct);
            return Ok(resp);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get history for printer {id}: {ex.Message}");
            return new HistoryListResponse { Count = 0, Jobs = Array.Empty<HistoryJob>() };
        }
    }

    [HttpGet("{id}/history/{jobId}")]
    [ProducesResponseType(typeof(HistoryJob), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(408)]
    [ProducesResponseType(502)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<HistoryJob>> GetHistoryJobAsync(Guid id, string jobId, CancellationToken ct = default)
    {
        try
        {
            HistoryJob job = await _printersService.GetHistoryJobAsync(id, jobId, ct);
            return Ok(job);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning($"GetHistoryJob called with null or empty jobId for printer {id}");
            return BadRequest("Job ID is required");
        }
        catch (KeyNotFoundException)
        {
            _logger.LogInformation($"History job {jobId} not found for printer {id}");
            return NotFound($"History job {jobId} not found");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, $"History requested for non-Moonraker printer {id}");
            return BadRequest("History is only available for Moonraker printers");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"Network error retrieving history job {jobId} for printer {id}: {ex.Message}");
            return StatusCode(StatusCodes.Status502BadGateway, "Unable to connect to printer");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning($"Timeout retrieving history job {jobId} for printer {id}: {ex.Message}");
            return StatusCode(StatusCodes.Status408RequestTimeout, "Request timeout");
        }
    }

    [HttpGet("{id}/history/totals")]
    [ProducesResponseType(typeof(HistoryTotals), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<HistoryTotals>> GetHistoryTotalsAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            HistoryTotals totals = await _printersService.GetHistoryTotalsAsync(id, ct);
            return Ok(totals);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get history totals for printer {id}: {ex.Message}");
            return new HistoryTotals { JobTotals = new JobTotals() };
        }
    }

    [HttpDelete("{id}/history/{jobId}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteHistoryJobAsync(Guid id, string jobId, CancellationToken ct = default)
    {
        try
        {
            bool success = await _printersService.DeleteHistoryJobAsync(id, jobId, ct);
            return success ? Ok() : StatusCode(StatusCodes.Status500InternalServerError, "Failed to delete history job");
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, $"History deletion requested for non-Moonraker printer {id}");
            return BadRequest("History deletion is only available for Moonraker printers");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to delete history job {jobId} for printer {id}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to delete history job");
        }
    }

    [HttpGet("export")]
    [ProducesResponseType(typeof(FileContentResult), 200)]
    [ProducesResponseType(500)]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "farm_admin")]
    public async Task<IActionResult> ExportPrintersAsync(CancellationToken ct)
    {
        byte[] bytes = await _printersService.BuildExportCsvAsync(null, ct);
        return File(bytes, "text/csv", $"printers-export-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.csv");
    }

    /// <summary>
    /// Exports printers selected by ID and includes their capabilities.
    /// Accepts an array of printer IDs in the request body. If no IDs are provided,
    /// all printers will be exported. Returns JSON array of printers with capability objects.
    /// </summary>
    [HttpPost("export")]
    [ProducesResponseType(typeof(PrinterWithCapabilitiesDto[]), 200)]
    [ProducesResponseType(500)]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "farm_admin")]
    public async Task<IActionResult> ExportPrintersByIdsAsync([FromBody] Guid[]? ids, CancellationToken ct)
    {
        try
        {
            PrinterWithCapabilitiesDto[] results = await _printersService.GetPrintersWithCapabilitiesDtosAsync(ids, ct);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export printers by ids");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to export printers");
        }
    }

    /// <summary>
    /// Streams an export file (CSV or JSON) for the selected printer IDs. This avoids building
    /// the entire payload in memory for large fleets. Query param 'format' may be 'csv' or 'json'.
    /// </summary>
    [HttpPost("export/file")]
    [ProducesResponseType(200)]
    [ProducesResponseType(500)]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "farm_admin")]
    public async Task<IActionResult> StreamExportAsync([FromBody] Guid[]? ids, [FromQuery] string format = "csv", CancellationToken ct = default)
    {
        try
        {
            // Delegate streaming export to service which will write directly to the response
            await _printersService.StreamExportToResponseAsync(ids, format, Response, ct);
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stream export");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to stream export");
        }
    }

    // Export helpers moved to PrintersService

    // Thumbnail extraction delegated to PrintersService

    [HttpGet("test")]
    [ProducesResponseType(typeof(object), 200)]
    public IActionResult SimpleTest()
    {
        _logger.LogError($"=== SIMPLE TEST ENDPOINT CALLED ===");
        return Ok(new { message = "Simple test works!", timestamp = DateTime.UtcNow });
    }

    // ===== PHASE 4: PRINTER CONFIGURATION ENDPOINTS =====

    /// <summary>
    /// Gets the current configuration for a specific printer.
    /// Returns all editable printer properties including API key, camera URLs, maintenance mode, etc.
    /// </summary>
    /// <param name="id">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Printer configuration details</returns>
    /// <response code="200">Returns the printer configuration</response>
    /// <response code="404">If the printer does not exist</response>
    /// <response code="500">If there was an error retrieving the configuration</response>
    [HttpGet("{id:guid}/config")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> GetPrinterConfigAsync(Guid id, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation($"[Config] Getting printer configuration for {id}");
            Printer? printer = await _printersService.FindByIdWithIncludesAsync(id, ct);

            if (printer == null)
            {
                _logger.LogWarning($"[Config] Printer {id} not found");
                return NotFound(new { message = $"Printer {id} not found" });
            }

            // Return printer configuration as JSON object
            var config = new
            {
                id = printer.Id,
                name = printer.Name,
                serverUrl = printer.ServerUrl,
                originalServerUrl = printer.OriginalServerUrl,
                ipAddress = printer.IpAddress,
                backend = printer.Backend,
                apiKey = printer.ApiKey,
                cameraStreamUrl = printer.CameraStreamUrl,
                cameraSnapshotUrl = printer.CameraSnapshotUrl,
                backendPort = printer.BackendPort,
                frontendPort = printer.FrontendPort,
                notes = printer.Notes,
                manufacturerId = printer.ManufacturerId,
                modelId = printer.ModelId,
                dateAcquired = printer.DateAcquired,
                inMaintenance = printer.InMaintenance
            };

            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[Config] Failed to get printer configuration for {id}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Failed to retrieve printer configuration",
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Updates the configuration for a specific printer.
    /// Allows updating API key, camera URLs, maintenance mode, and other editable properties.
    /// </summary>
    /// <param name="id">The unique identifier of the printer</param>
    /// <param name="config">The updated configuration properties</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Updated printer configuration</returns>
    /// <response code="200">Returns the updated configuration</response>
    /// <response code="400">If the configuration data is invalid</response>
    /// <response code="404">If the printer does not exist</response>
    /// <response code="500">If there was an error updating the configuration</response>
    [HttpPut("{id:guid}/config")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "farm_admin")]
    public async Task<IActionResult> UpdatePrinterConfigAsync(
        Guid id,
        [FromBody] object? config,
        CancellationToken ct)
    {
        if (config == null)
        {
            return BadRequest(new { message = "Configuration data is required" });
        }

        try
        {
            _logger.LogInformation($"[Config] Updating printer configuration for {id}");

            Printer? printer = await _printersService.FindByIdWithIncludesAsync(id, ct);
            if (printer == null)
            {
                _logger.LogWarning($"[Config] Printer {id} not found for update");
                return NotFound(new { message = $"Printer {id} not found" });
            }

            // Parse configuration updates from JSON
            if (config is JsonElement jsonElement)
            {
                Dictionary<string, object>? configDict = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText());
                if (configDict == null)
                {
                    return BadRequest(new { message = "Invalid configuration format" });
                }

                // Update fields if present in the request
                if (configDict.TryGetValue("name", out object? nameVal) && nameVal != null)
                {
                    printer.Name = nameVal.ToString() ?? printer.Name;
                }

                if (configDict.TryGetValue("apiKey", out object? apiKeyVal))
                {
                    printer.ApiKey = apiKeyVal?.ToString();
                }

                if (configDict.TryGetValue("cameraStreamUrl", out object? streamVal))
                {
                    printer.CameraStreamUrl = streamVal?.ToString();
                }

                if (configDict.TryGetValue("cameraSnapshotUrl", out object? snapshotVal))
                {
                    printer.CameraSnapshotUrl = snapshotVal?.ToString();
                }

                if (configDict.TryGetValue("notes", out object? notesVal))
                {
                    printer.Notes = notesVal?.ToString();
                }

                if (configDict.TryGetValue("inMaintenance", out object? maintenanceVal) && bool.TryParse(maintenanceVal?.ToString(), out bool maintValue))
                {
                    printer.InMaintenance = maintValue;
                }

                if (configDict.TryGetValue("backendPort", out object? bpVal) && int.TryParse(bpVal?.ToString(), out int bp))
                {
                    printer.BackendPort = bp;
                }

                if (configDict.TryGetValue("frontendPort", out object? fpVal) && int.TryParse(fpVal?.ToString(), out int fp))
                {
                    printer.FrontendPort = fp;
                }

                _logger.LogInformation($"[Config] Updating printer: {printer.Name} with new configuration");
                await _printersService.SaveChangesAsync(ct);
                _logger.LogInformation($"[Config] Successfully updated printer configuration for {id}");

                // Return updated configuration
                var updatedConfig = new
                {
                    id = printer.Id,
                    name = printer.Name,
                    serverUrl = printer.ServerUrl,
                    originalServerUrl = printer.OriginalServerUrl,
                    ipAddress = printer.IpAddress,
                    backend = printer.Backend,
                    apiKey = printer.ApiKey,
                    cameraStreamUrl = printer.CameraStreamUrl,
                    cameraSnapshotUrl = printer.CameraSnapshotUrl,
                    backendPort = printer.BackendPort,
                    frontendPort = printer.FrontendPort,
                    notes = printer.Notes,
                    manufacturerId = printer.ManufacturerId,
                    modelId = printer.ModelId,
                    dateAcquired = printer.DateAcquired,
                    inMaintenance = printer.InMaintenance,
                    message = "Configuration updated successfully"
                };

                return Ok(updatedConfig);
            }

            return BadRequest(new { message = "Configuration must be a JSON object" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[Config] Failed to update printer configuration for {id}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Failed to update printer configuration",
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Gets the capabilities and specifications for a specific printer.
    /// Returns detailed hardware capabilities like build volume, temperature limits, materials support, etc.
    /// </summary>
    /// <param name="id">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>Printer capabilities and specifications</returns>
    /// <response code="200">Returns the printer capabilities</response>
    /// <response code="404">If the printer does not exist or has no capabilities</response>
    /// <response code="500">If there was an error retrieving capabilities</response>
    [HttpGet("{id:guid}/capabilities")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> GetPrinterCapabilitiesAsync(Guid id, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation($"[Capabilities] Getting printer capabilities for {id}");

            PrinterCapabilities? capabilities = await _printersService.GetCapabilitiesByPrinterIdAsync(id, ct);

            if (capabilities == null)
            {
                _logger.LogWarning($"[Capabilities] No capabilities found for printer {id}");
                return NotFound(new { message = $"Capabilities not found for printer {id}" });
            }

            // Return capabilities as JSON object
            var capabilitiesObj = new
            {
                id = capabilities.Id,
                printerId = capabilities.PrinterId,
                nozzleDiameter = capabilities.NozzleDiameter,
                supportedMaterials = capabilities.SupportedMaterials,
                maxBuildVolumeX = capabilities.MaxBuildVolumeX,
                maxBuildVolumeY = capabilities.MaxBuildVolumeY,
                maxBuildVolumeZ = capabilities.MaxBuildVolumeZ,
                hasHeatedBed = capabilities.HasHeatedBed,
                hasEnclosure = capabilities.HasEnclosure,
                multiMaterial = capabilities.MultiMaterial,
                supportsAutoLeveling = capabilities.SupportsAutoLeveling,
                numberOfExtruders = capabilities.NumberOfExtruders,
                minHotendTemp = capabilities.MinHotendTemp,
                maxHotendTemp = capabilities.MaxHotendTemp,
                minBedTemp = capabilities.MinBedTemp,
                maxBedTemp = capabilities.MaxBedTemp,
                maxPrintSpeed = capabilities.MaxPrintSpeed,
                currentMaterial = capabilities.CurrentMaterial,
                currentSpoolId = capabilities.CurrentSpoolId,
                isAvailable = capabilities.IsAvailable,
                lastUpdated = capabilities.LastUpdated
            };

            return Ok(capabilitiesObj);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[Capabilities] Failed to get printer capabilities for {id}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Failed to retrieve printer capabilities",
                error = ex.Message
            });
        }
    }

    #region Discovery Stream Endpoints

    /// <summary>
    /// Start a network discovery stream to find printers on the local network.
    /// Returns a session ID that can be used to receive discovery progress via SignalR.
    /// </summary>
    /// <param name="request">Optional request with backend filters</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Session ID for tracking discovery progress</returns>
    /// <response code="200">Discovery started successfully</response>
    /// <response code="500">Failed to start discovery</response>
    [HttpPost("discover/stream")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> StartDiscoveryStreamAsync(
        [FromBody] DiscoveryStreamRequest? request,
        CancellationToken ct)
    {
        try
        {
            bool autoRegister = request?.AutoRegister ?? false;
            _logger.LogInformation($"[DISCOVERY] Starting discovery stream via API endpoint (autoRegister={autoRegister})");

            IReadOnlyList<PrinterBackend>? backends = request?.Backends?.ToList();
            Services.Interfaces.DiscoveryStreamResponse result = await _discoveryProxyService.StartDiscoveryStreamAsync(
                backends: backends,
                autoRegister: autoRegister,
                cancellationToken: ct);

            return Ok(new { sessionId = result.SessionId, message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DISCOVERY] Failed to start discovery stream");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Failed to start discovery",
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Cancel an active discovery stream.
    /// </summary>
    /// <param name="sessionId">The session ID to cancel</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Cancellation confirmation</returns>
    /// <response code="200">Discovery cancelled successfully</response>
    /// <response code="500">Failed to cancel discovery</response>
    [HttpPost("discover/{sessionId}/cancel")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> CancelDiscoveryStreamAsync(
        [FromRoute] string sessionId,
        CancellationToken ct)
    {
        try
        {
            _logger.LogInformation($"[DISCOVERY] Cancelling discovery stream {sessionId}");

            Services.Interfaces.DiscoveryCancelResponse result = await _discoveryProxyService.CancelDiscoveryStreamAsync(sessionId, ct);

            return Ok(new { message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[DISCOVERY] Failed to cancel discovery stream {sessionId}");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Failed to cancel discovery",
                error = ex.Message
            });
        }
    }

    #endregion

}
