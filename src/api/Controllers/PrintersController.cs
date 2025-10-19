using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers.Responses;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Farm.Web.Shared;
using FluentValidation;
using FluentValidation.Results;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/printers")]
public class PrintersController(
    IUnifiedLoggingService logger,
    Services.Printers.IPrintersService printersService,
    Services.Catalog.ICatalogService catalogService,
    INetworkDiscoveryService networkDiscoveryService,
    IDefaultCatalogService defaultCatalogService,
    IValidator<CreatePrinterDto> validator)
    : ControllerBase
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly Services.Printers.IPrintersService _printersService = printersService;
    private readonly Services.Catalog.ICatalogService _catalogService = catalogService;
    private readonly INetworkDiscoveryService networkDiscovery = networkDiscoveryService;
    private readonly IDefaultCatalogService defaultCatalog = defaultCatalogService;
    private readonly IValidator<CreatePrinterDto> _validator = validator;

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
            var dtos = await _printersService.GetCameraUrlsAsync(ct);
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
            var dto = await _printersService.GetStatusDtoAsync(id, ct);
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
            var dto = await _printersService.GetPrinterDtoAsync(id, ct);
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
        var created = await _printersService.CreatePrinterFromDtoAsync(dto, ct);
        return CreatedAtRoute("GetPrinterById", new { id = created.Id }, created);
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
            var man = await _catalogService.GetManufacturerByIdAsync(printer.ManufacturerId, ct);
            manufacturerName = man?.Name;
        }
        if (printer.ModelId != Guid.Empty)
        {
            var mod = await _catalogService.GetModelByIdAsync(printer.ModelId, ct);
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
            var created = await _catalogService.CreateManufacturerAsync(name, ct);
            manufacturerId = created.Id;
        }

        Guid modelId = dto.ModelId ?? p.ModelId;
        if ((dto.ModelId is null && !string.IsNullOrWhiteSpace(dto.NewModelName)) && manufacturerId != Guid.Empty)
        {
            string mname = dto.NewModelName!.Trim();
            var createReq = new Requests.CreateModelRequest(
                ManufacturerId: manufacturerId,
                Name: mname,
                Type: null,
                MaxX: null,
                MaxY: null,
                MaxZ: null,
                DefaultBackend: null,
                SupportedFilamentTypeIds: Array.Empty<Guid>());
            var createdModel = await _catalogService.CreateModelAsync(createReq, ct);
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
             dto.Backend.Value == PrinterBackend.SDCP ? 80 : 7125) :
            (p.Backend == 1 ? 80 : p.Backend == 2 ? 80 : 7125);

        // Delegate normalization and optional hostname resolution to the PrintersService
        var backendForResolve = dto.Backend ?? (PrinterBackend)p.Backend;
        var resolveResp = await _printersService.ResolveHostnameAsync(dto.ServerUrl, backendForResolve, ct);
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
            var man = await _catalogService.GetManufacturerByIdAsync(p.ManufacturerId, ct);
            manufacturerName = man?.Name;
        }
        if (p.ModelId != Guid.Empty)
        {
            var mod = await _catalogService.GetModelByIdAsync(p.ModelId, ct);
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
        int defaultPort = body.Backend == Farm.Web.Shared.PrinterBackend.PrusaLink ? 80 :
                         body.Backend == Farm.Web.Shared.PrinterBackend.SDCP ? 80 : 7125;
        try
        {
            var resp = await _printersService.ResolveHostnameAsync(body.ServerUrl, body.Backend, ct);
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
        var modelDto = await _catalogService.GetModelByIdAsync(modelId, ct);
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

    // Print job control
    [HttpPost("{id:guid}/print/start")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> StartPrintAsync(Guid id, [FromBody] Requests.StartPrintRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }
        bool ok = await _printersService.StartPrintAsync(id, request.Filename, ct);
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
        var (streamUrl, snapshotUrl) = await _printersService.GetCameraUrlsForPrinterAsync(id, ct);
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
    [ProducesResponseType(typeof(string[]), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<string[]>> GetFileListAsync(Guid id, CancellationToken ct)
    {
        try
        {
            string[] files = await _printersService.GetFileListAsync(id, ct);
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

    [HttpPost("{id:guid}/files/{fileName}/print")]
    [ProducesResponseType(typeof(StartPrintResultDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<StartPrintResultDto>> StartPrintFromFileAsync(Guid id, string fileName, CancellationToken ct)
    {
        try
        {
            bool success = await _printersService.StartPrintFromFileAsync(id, fileName, ct);

            if (!success)
            {
                return NotFound();
            }

            return Ok(new StartPrintResultDto("Print started successfully", fileName));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Failed to start print: {ex.Message}");
        }
    }

    // ===== HISTORY ENDPOINTS =====

    [HttpGet("{id}/history")]
    [ProducesResponseType(typeof(Shared.HistoryListResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Shared.HistoryListResponse>> GetHistoryAsync(Guid id, [FromQuery] int? limit = null, [FromQuery] int? start = null, [FromQuery] DateTime? since = null, [FromQuery] DateTime? before = null, [FromQuery] string? order = null, CancellationToken ct = default)
    {
        try
        {
            var resp = await _printersService.GetHistoryListAsync(id, limit, start, since, before, order, ct);
            return Ok(resp);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get history for printer {id}: {ex.Message}");
            return new Shared.HistoryListResponse { Count = 0, Jobs = Array.Empty<Shared.HistoryJob>() };
        }
    }

    [HttpGet("{id}/history/{jobId}")]
    [ProducesResponseType(typeof(Shared.HistoryJob), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(408)]
    [ProducesResponseType(502)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Shared.HistoryJob>> GetHistoryJobAsync(Guid id, string jobId, CancellationToken ct = default)
    {
        try
        {
            var job = await _printersService.GetHistoryJobAsync(id, jobId, ct);
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
    [ProducesResponseType(typeof(Shared.HistoryTotals), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Shared.HistoryTotals>> GetHistoryTotalsAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var totals = await _printersService.GetHistoryTotalsAsync(id, ct);
            return Ok(totals);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get history totals for printer {id}: {ex.Message}");
            return new Shared.HistoryTotals { JobTotals = new Shared.JobTotals() };
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
            var results = await _printersService.GetPrintersWithCapabilitiesDtosAsync(ids, ct);
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

    [HttpGet("discover")]
    [ProducesResponseType(typeof(IEnumerable<DiscoveredPrinterDto>), 200)]
    [ProducesResponseType(408)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<DiscoveredPrinterDto>>> DiscoverPrintersAsync([FromQuery] string? backends, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation($"Starting network printer discovery... Backends={backends}");

            // Set timeout for network discovery - with 100ms per IP, 254 IPs * 2 ports = ~51 seconds + overhead
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(15)); // 15 minute total timeout for full network scan

            // Parse optional backends query parameter (comma-separated names)
            List<PrinterBackend>? backendList = null;
            if (!string.IsNullOrWhiteSpace(backends))
            {
                string[] parts = backends.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                List<PrinterBackend> parsed = new();
                foreach (string p in parts)
                {
                    if (Enum.TryParse(p, true, out PrinterBackend b))
                    {
                        parsed.Add(b);
                    }
                }
                if (parsed.Count > 0)
                {
                    backendList = parsed;
                }
            }

            List<DiscoveredPrinterDto> discovered = await networkDiscovery.DiscoverPrintersAsync(timeoutCts.Token);

            // If backend filter provided, apply it at controller layer
            if (backendList != null && backendList.Count > 0)
            {
                discovered = discovered.Where(d => backendList.Contains(d.Backend)).ToList();
            }

            // Get existing normalized server URLs from the service to filter out duplicates
            HashSet<string> normalizedExistingUrls = await _printersService.GetAllNormalizedServerUrlsAsync(80, ct);

            // Filter out printers that already exist in the database using service-normalized URLs
            List<DiscoveredPrinterDto> newPrinters = discovered
                .Where(d => !normalizedExistingUrls.Contains(_printersService.NormalizeServerUrl(d.ServerUrl, 80)))
                .ToList();

            _logger.LogInformation($"Discovery completed. Found {discovered.Count} printers, {newPrinters.Count} are new");

            return Ok(newPrinters);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Printer discovery operation was canceled or timed out");
            return StatusCode(StatusCodes.Status408RequestTimeout, "Discovery timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover printers");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to discover printers");
        }

    }

}
