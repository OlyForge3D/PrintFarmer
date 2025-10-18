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
using Farm.Web.Shared;
using Farm.Web.Shared.Annotations;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing 3D printers and their operations.
/// Supports Moonraker, PrusaLink, and SDCP printer backends.
/// </summary>
[ApiController]
[Route("api/printers")]
[Tags("Printers")]
public class PrintersController(Farm.Infrastructure.Repositories.Printers.IPrintersRepository printersRepo, Farm.Infrastructure.Repositories.Catalog.ICatalogRepository catalogRepo, IMoonrakerClient moon, IPrusaLinkClient prusa, ISdcpClient sdcp, INetworkDiscoveryService networkDiscovery, IUnifiedLoggingService logger, IValidator<CreatePrinterDto> validator, IPrinterCapabilityDiscoveryService capabilityDiscovery, IDefaultCatalogService defaultCatalog, Farm.Importing.Services.Import.IImportParserService importParser, Farm.Importing.Services.Import.IImportProcessorService importProcessor, Farm.Web.Api.Services.Printers.IPrintersService printersService) : ControllerBase
{

    private readonly Farm.Infrastructure.Repositories.Printers.IPrintersRepository _printersRepo = printersRepo;
    private readonly Farm.Infrastructure.Repositories.Catalog.ICatalogRepository _catalogRepo = catalogRepo;
    private readonly IMoonrakerClient moon = moon;
    private readonly IPrusaLinkClient prusa = prusa;
    private readonly ISdcpClient sdcp = sdcp;
    private readonly INetworkDiscoveryService networkDiscovery = networkDiscovery;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IValidator<CreatePrinterDto> validator = validator;
    private readonly IPrinterCapabilityDiscoveryService capabilityDiscovery = capabilityDiscovery;
    private readonly IDefaultCatalogService defaultCatalog = defaultCatalog;
    private readonly Farm.Importing.Services.Import.IImportParserService _importParser = importParser;
    private readonly Farm.Importing.Services.Import.IImportProcessorService _importProcessor = importProcessor;
    private readonly Farm.Web.Api.Services.Printers.IPrintersService _printersService = printersService;

    // Export options using a TypeInfoResolver that honors ImportExportAttribute
    private static readonly JsonSerializerOptions _exportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new Farm.Web.Api.Serialization.ImportExportTypeInfoResolver(),
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // NOTE: import deserialization now lives in Farm.Importing; controller no longer needs cached import JsonSerializerOptions

    // Feature flag: when enabled we swallow transient startup DB errors for /fast endpoint and return empty list.
    private static readonly bool FastEndpointDefensive =
        (Environment.GetEnvironmentVariable("PF_FAST_ENDPOINT_DEFENSIVE") ?? "true")
            .Equals("true", StringComparison.OrdinalIgnoreCase);
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

    private static string NormalizeServerUrl(string? input, int defaultPort)
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

    // Helper to write a single CSV row to the provided PipeWriter. Extracted to reduce nesting and improve testability.
    private static async Task WriteCsvRowAsync(System.IO.Pipelines.PipeWriter pipeWriter, string[] prefixFields, List<System.Reflection.PropertyInfo> capPropInfos, Farm.Infrastructure.Domain.PrinterCapabilities? cap, CancellationToken ct)
    {
        // Write a string value to the pipe as UTF8 without allocating intermediate strings when possible.
        static void WriteBytes(System.IO.Pipelines.PipeWriter pw, ReadOnlySpan<char> value)
        {
            if (value.Length == 0)
            {
                // write nothing (empty field)
                return;
            }

            // Reserve a span and copy as UTF8
            int maxBytes = Math.Max(1, value.Length * 4);
            Span<byte> span = pw.GetSpan(maxBytes);
            int written = Encoding.UTF8.GetBytes(value, span);
            pw.Advance(written);
        }

        // Helper to write a field followed by a delimiter
        static void WriteField(System.IO.Pipelines.PipeWriter pw, string value, byte delimiter)
        {
            WriteBytes(pw, value.AsSpan());
            Span<byte> dspan = pw.GetSpan(1);
            dspan[0] = delimiter;
            pw.Advance(1);
        }

        // Write prefix fields (already escaped by caller)
        for (int i = 0; i < prefixFields.Length; i++)
        {
            string val = prefixFields[i] ?? string.Empty;
            WriteField(pipeWriter, val, (byte)',');
        }

        // Write capability properties
        for (int i = 0; i < capPropInfos.Count; i++)
        {
            System.Reflection.PropertyInfo prop = capPropInfos[i];
            string? raw = cap == null ? null : prop.GetValue(cap)?.ToString();
            string escaped = EscapeCsvValue(raw);
            // If last capability field, terminate with newline
            byte delim = (i == capPropInfos.Count - 1) ? (byte)'\n' : (byte)',';
            WriteField(pipeWriter, escaped, delim);
        }

        // If there are no capability properties, ensure we end the line
        if (capPropInfos.Count == 0)
        {
            Span<byte> n = pipeWriter.GetSpan(1);
            n[0] = (byte)'\n';
            pipeWriter.Advance(1);
        }

        await pipeWriter.FlushAsync(ct);
    }

    // Helper to write CSV header to the provided PipeWriter
    private static async Task WriteCsvHeaderAsync(System.IO.Pipelines.PipeWriter pipeWriter, List<string> headerParts, CancellationToken ct)
    {
        string headerLine = string.Join(',', headerParts) + "\n";
        Span<byte> span = pipeWriter.GetSpan(headerLine.Length * 4);
        int written = Encoding.UTF8.GetBytes(headerLine.AsSpan(), span);
        pipeWriter.Advance(written);
        await pipeWriter.FlushAsync(ct);
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
        var items = await _printersService.GetAllWithIncludesAsync(ct);
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
    /// <returns>A list of all printers with cached information without real-time status or camera URLs</returns>
    /// <response code="200">Returns the list of printers with cached information</response>
    [HttpGet("fast")]
    [ProducesResponseType(typeof(IEnumerable<PrinterFastDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<PrinterFastDto>>> GetAllFastAsync(CancellationToken ct)
    {
        try
        {
            var dtos = await _printersService.GetAllFastDtosAsync(ct);
            return Ok(dtos);
        }
        catch (Exception ex) when (FastEndpointDefensive && IsTransientStartupDbException(ex))
        {
            _logger.LogWarning($"[FAST] Startup DB exception in /api/printers/fast. TraceId={HttpContext.TraceIdentifier}, Exception={ex.Message}");
            return Ok(Array.Empty<PrinterFastDto>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[FATAL] Unhandled exception in /api/printers/fast. TraceId={HttpContext.TraceIdentifier}, User={User?.Identity?.Name ?? "anonymous"}, Exception={ex.Message}\n{ex.StackTrace}");
            return StatusCode(StatusCodes.Status500InternalServerError, $"Internal Server Error: {ex.Message}");
        }
    }

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
        catch (Exception ex) when (FastEndpointDefensive && IsTransientStartupDbException(ex))
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
        Printer? p = await _printersRepo.FindByIdWithIncludesAsync(id, ct);
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
        ValidationResult validationResult = await validator.ValidateAsync(dto, ct);
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

        // resolve or create manufacturer/model
        Guid manufacturerId = dto.ManufacturerId ?? Guid.Empty;
        if (manufacturerId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            string name = dto.NewManufacturerName!.Trim();
            var existing = await _catalogRepo.GetManufacturerByIdAsync(Guid.Empty, ct);
            // fallback: use catalog repo AddManufacturerAsync when name provided (catalog repo works by id/name)
            // Note: ICatalogRepository currently lacks GetManufacturerByName, so we will create new manufacturer via repo using a new Guid
            if (manufacturerId == Guid.Empty)
            {
                Guid newId = Guid.NewGuid();
                await _catalogRepo.AddManufacturerAsync(newId, name, ct);
                manufacturerId = newId;
            }
        }

        Guid modelId = dto.ModelId ?? Guid.Empty;
        if (modelId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewModelName) && manufacturerId != Guid.Empty)
        {
            string mname = dto.NewModelName!.Trim();
            var modelEntity = await _catalogRepo.GetModelEntityAsync(Guid.Empty, ct);
            if (modelId == Guid.Empty)
            {
                var newModel = new Farm.Infrastructure.Domain.PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturerId, Name = mname };
                await _catalogRepo.AddModelAsync(newModel, ct);
                modelId = newModel.Id;
            }
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

        // Resolve host to IP and persist the IP-based base URL; store original URL for future re-resolve
        int defaultPort = dto.Backend == PrinterBackend.PrusaLink ? 80 :
                         dto.Backend == PrinterBackend.SDCP ? 80 : 7125;
        string normalizedInput = NormalizeServerUrl(dto.ServerUrl, defaultPort);
        string resolvedBase = normalizedInput;
        string? resolvedIp = null;
        try
        {
            Uri uri = new(normalizedInput);
            if (!System.Net.IPAddress.TryParse(uri.Host, out _))
            {
                string hostToResolve = EnsureLocalSuffix(uri.Host);
                IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(hostToResolve, ct);
                IPAddress? firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? (addresses.Length > 0 ? addresses[0] : null);
                if (firstIp is not null)
                {
                    UriBuilder ub = new(uri)
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
            DateAcquired = dto.DateAcquired?.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dto.DateAcquired.Value, DateTimeKind.Utc)
                : dto.DateAcquired,
            Backend = (int)dto.Backend,
            ApiKey = dto.ApiKey
        };
        await _printersRepo.AddAsync(p, ct);

        _logger.LogInformation($"Successfully created printer: {p.Name} with ID {p.Id}");

        // Auto-discover capabilities for the newly created printer
        try
        {
            _logger.LogInformation($"Starting capability discovery for newly created printer: {p.Name} ({p.Id})");

            // Reload the printer with includes for proper discovery
            Printer? printerForDiscovery = await _printersRepo.FindByIdWithIncludesAsync(p.Id, ct);

            if (printerForDiscovery != null)
            {
                PrinterCapabilities? discoveredCapabilities = await capabilityDiscovery.DiscoverCapabilitiesAsync(printerForDiscovery, ct);
                if (discoveredCapabilities != null)
                {
                    _logger.LogInformation($"Successfully discovered and saved capabilities for printer: {p.Name} ({p.Id})");
                }
                else
                {
                    _logger.LogWarning($"Failed to discover capabilities for printer: {p.Name} ({p.Id}) - capabilities will need to be added manually");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error during capability discovery for newly created printer: {p.Name} ({p.Id}) - printer was created successfully but capabilities discovery failed. Exception: {ex.Message}");
            // Don't fail the printer creation if capability discovery fails - user can manually add capabilities or trigger discovery later
        }

        // Get manufacturer and model names for the response
        string? manufacturerName = null;
        string? modelName = null;

        if (manufacturerId != Guid.Empty)
        {
            var man = await _catalogRepo.GetManufacturerByIdAsync(manufacturerId, ct);
            manufacturerName = man?.Name;
        }

        if (modelId != Guid.Empty)
        {
            var mod = await _catalogRepo.GetModelByIdAsync(modelId, ct);
            modelName = mod?.Name;
        }

        // Return the created printer without attempting to fetch status
        // Status will be fetched later when needed (like in the printers list)
        PrinterDto printerDto = new(
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
            Backend: dto.Backend, // Use the requested backend (ensures OctoPrint is set correctly)
            ApiKey: p.ApiKey,
            OriginalServerUrl: p.OriginalServerUrl,
            IpAddress: p.IpAddress
        );

        return CreatedAtRoute("GetPrinterById", new { id = p.Id }, printerDto);
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
        Printer? printer = await _printersRepo.FindByIdAsync(id, ct);
        if (printer is null)
        {
            return NotFound();
        }
        printer.InMaintenance = inMaintenance;
        await _printersRepo.SaveChangesAsync(ct);

        // Optionally, you may want to return the updated DTO with more info
        string? manufacturerName = null;
        string? modelName = null;
        if (printer.ManufacturerId != Guid.Empty)
        {
            var man = await _catalogRepo.GetManufacturerByIdAsync(printer.ManufacturerId, ct);
            manufacturerName = man?.Name;
        }
        if (printer.ModelId != Guid.Empty)
        {
            var mod = await _catalogRepo.GetModelByIdAsync(printer.ModelId, ct);
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
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }
        // resolve or create manufacturer/model
        Guid manufacturerId = dto.ManufacturerId ?? p.ManufacturerId;
        if (dto.ManufacturerId is null && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            string name = dto.NewManufacturerName!.Trim();
            // ICatalogRepository does not expose GetManufacturerByName; use AddManufacturerAsync to create a new one
            Guid newManId = Guid.NewGuid();
            await _catalogRepo.AddManufacturerAsync(newManId, name, ct);
            manufacturerId = newManId;
        }

        Guid modelId = dto.ModelId ?? p.ModelId;
        if ((dto.ModelId is null && !string.IsNullOrWhiteSpace(dto.NewModelName)) && manufacturerId != Guid.Empty)
        {
            string mname = dto.NewModelName!.Trim();
            var newModel = new Farm.Infrastructure.Domain.PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturerId, Name = mname };
            await _catalogRepo.AddModelAsync(newModel, ct);
            modelId = newModel.Id;
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
        string normalizedInput = NormalizeServerUrl(dto.ServerUrl, defaultPort);
        string resolvedBase = normalizedInput;
        string? resolvedIp = null;
        try
        {
            Uri uri = new(normalizedInput);
            if (!System.Net.IPAddress.TryParse(uri.Host, out _))
            {
                string hostToResolve = EnsureLocalSuffix(uri.Host);
                IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(hostToResolve, ct);
                IPAddress? firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? (addresses.Length > 0 ? addresses[0] : null);
                if (firstIp is not null)
                {
                    UriBuilder ub = new(uri)
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

        // Update or create printer capabilities
        PrinterCapabilities? capabilities = await _printersRepo.GetCapabilitiesByPrinterIdAsync(id, ct);
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

        await _printersRepo.SaveCapabilitiesAsync(capabilities, ct);

        // Build updated manufacturer/model names
        string? manufacturerName = null;
        string? modelName = null;
        if (p.ManufacturerId != Guid.Empty)
        {
            var man = await _catalogRepo.GetManufacturerByIdAsync(p.ManufacturerId, ct);
            manufacturerName = man?.Name;
        }
        if (p.ModelId != Guid.Empty)
        {
            var mod = await _catalogRepo.GetModelByIdAsync(p.ModelId, ct);
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
    [ProducesResponseType(typeof(Farm.Web.Shared.ResolveHostnameResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Farm.Web.Shared.ResolveHostnameResponse>> ResolveHostAsync([FromBody] Farm.Web.Shared.ResolveHostnameRequest body, CancellationToken ct)
    {
        if (body is null)
        {
            return BadRequest("Request body is required.");
        }
        int defaultPort = body.Backend == Farm.Web.Shared.PrinterBackend.PrusaLink ? 80 :
                         body.Backend == Farm.Web.Shared.PrinterBackend.SDCP ? 80 : 7125;
        string normalized = NormalizeServerUrl(body.ServerUrl, defaultPort);
        try
        {
            Uri uri = new(normalized);
            string host = uri.Host;
            if (!System.Net.IPAddress.TryParse(host, out _))
            {
                host = EnsureLocalSuffix(host);
            }
            string? ip = null;
            try
            {
                if (!System.Net.IPAddress.TryParse(host, out _))
                {
                    IPAddress[] addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct);
                    IPAddress? firstIp = Array.Find(addrs, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? (addrs.Length > 0 ? addrs[0] : null);
                    ip = firstIp?.ToString();
                }
                else
                {
                    ip = host;
                }
            }
            catch { }

            UriBuilder ub = new(uri) { Host = ip ?? uri.Host };
            string baseUrl = ub.Uri.ToString().TrimEnd('/');
            return new Farm.Web.Shared.ResolveHostnameResponse(normalized, ip, baseUrl);
        }
        catch
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
        var modelDto = await _catalogRepo.GetModelWithFilamentNamesAsync(modelId, ct);
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
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }
        await _printersRepo.RemoveAsync(p, ct);
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
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        byte[]? bytes = await moon.GetCameraSnapshotAsync(p.ServerUrl, ct);
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
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }
        bool ok = await moon.SendHomeAsync(p.ServerUrl, ct);
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
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }
        bool ok = await moon.HomeXYAsync(p.ServerUrl, ct);
        return new CommandResult(ok, ok ? null : "Failed to home XY");
    }

    [HttpPost("{id:guid}/homez")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> HomeZAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }
        bool ok = await moon.HomeZAsync(p.ServerUrl, ct);
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
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }
        bool ok = await moon.SetTempsAsync(p.ServerUrl, targets.Hotend, targets.Bed, ct);
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
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }
        bool ok = await moon.MoveAsync(p.ServerUrl, req.X, req.Y, req.Z, req.F, ct);
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
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }
        bool ok = await moon.MoveToAsync(p.ServerUrl, req.X, req.Y, req.Z, req.F, ct);
        return new CommandResult(ok, ok ? null : "Failed to move to position");
    }

    [HttpPost("{id:guid}/pause")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> PauseAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        bool ok = p.Backend == 2 ? await sdcp.PausePrintAsync(p.ServerUrl, ct) : await moon.PauseAsync(p.ServerUrl, ct);

        return new CommandResult(ok, ok ? null : "Failed to pause");
    }

    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> ResumeAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        bool ok = p.Backend == 2 ? await sdcp.ResumePrintAsync(p.ServerUrl, ct) : await moon.ResumeAsync(p.ServerUrl, ct);

        return new CommandResult(ok, ok ? null : "Failed to resume");
    }

    [HttpPost("{id:guid}/emergency-stop")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> EmergencyStopAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        bool ok = p.Backend == 2 ? await sdcp.CancelPrintAsync(p.ServerUrl, ct) : await moon.EmergencyStopAsync(p.ServerUrl, ct);

        return new CommandResult(ok, ok ? null : "Failed to emergency stop");
    }

    [HttpPost("{id:guid}/firmware-restart")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> FirmwareRestartAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
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
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        if (p.Backend == 2) // SDCP
        {
            bool ok = await sdcp.StartPrintAsync(p.ServerUrl, request.Filename, ct);
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
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        if (p.Backend == 2) // SDCP
        {
            bool ok = await sdcp.EnableCameraAsync(p.ServerUrl, ct);
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
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        if (p.Backend == 2) // SDCP
        {
            bool ok = await sdcp.DisableCameraAsync(p.ServerUrl, ct);
            return new CommandResult(ok, ok ? null : "Failed to disable camera");
        }

        return new CommandResult(false, "Camera control not supported for this printer type");
    }

    [HttpGet("{id:guid}/camera/url")]
    [ProducesResponseType(typeof(CameraUrlResult), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CameraUrlResult>> GetCameraUrlAsync(Guid id, CancellationToken ct)
    {
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        if (p.Backend == 2) // SDCP
        {
            string? streamUrl = await sdcp.GetCameraUrlAsync(p.ServerUrl, ct);
            string? snapshotUrl = await sdcp.GetCameraSnapshotUrlAsync(p.ServerUrl, ct);
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

        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p == null)
        {
            return NotFound();
        }

        try
        {
            await using Stream fileStream = file.OpenReadStream();
            bool success = (PrinterBackend)p.Backend switch
            {
                PrinterBackend.Moonraker => await moon.UploadGcodeAsync(p.ServerUrl, file.FileName, fileStream, ct),
                PrinterBackend.PrusaLink => await prusa.UploadGcodeAsync(p.ServerUrl, file.FileName, fileStream, p.ApiKey, ct),
                PrinterBackend.SDCP => await sdcp.UploadGcodeAsync(p.ServerUrl, file.FileName, fileStream, ct),
                _ => false
            };

            return success
                ? Ok(new Farm.Web.Shared.UploadGcodeResultDto("File uploaded successfully", file.FileName))
                : StatusCode(StatusCodes.Status500InternalServerError, "Failed to upload file to printer");
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
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p == null)
        {
            return NotFound();
        }

        try
        {
            string[] files = (PrinterBackend)p.Backend switch
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
            return StatusCode(StatusCodes.Status500InternalServerError, $"Failed to get file list: {ex.Message}");
        }
    }

    [HttpPost("{id:guid}/files/{fileName}/print")]
    [ProducesResponseType(typeof(Farm.Web.Shared.StartPrintResultDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Farm.Web.Shared.StartPrintResultDto>> StartPrintFromFileAsync(Guid id, string fileName, CancellationToken ct)
    {
        Printer? p = await _printersRepo.FindByIdAsync(id, ct);
        if (p == null)
        {
            return NotFound();
        }

        try
        {
            bool success = ((PrinterBackend)p.Backend) switch
            {
                PrinterBackend.Moonraker => await moon.StartPrintAsync(p.ServerUrl, fileName, ct),
                PrinterBackend.PrusaLink => await prusa.StartPrintAsync(p.ServerUrl, fileName, p.ApiKey, ct),
                PrinterBackend.SDCP => await sdcp.StartPrintAsync(p.ServerUrl, fileName, ct),
                _ => false
            };

            return success
                ? Ok(new Farm.Web.Shared.StartPrintResultDto("Print started successfully", fileName))
                : StatusCode(StatusCodes.Status500InternalServerError, "Failed to start print");
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Failed to start print: {ex.Message}");
        }
    }

    // ===== HISTORY ENDPOINTS =====

    [HttpGet("{id}/history")]
    [ProducesResponseType(typeof(Farm.Web.Shared.HistoryListResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Farm.Web.Shared.HistoryListResponse>> GetHistoryAsync(Guid id, [FromQuery] int? limit = null, [FromQuery] int? start = null, [FromQuery] DateTime? since = null, [FromQuery] DateTime? before = null, [FromQuery] string? order = null, CancellationToken ct = default)
    {
        Printer? printer = await _printersRepo.FindByIdAsync(id, ct);
        if (printer == null)
        {
            return NotFound();
        }

        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            // For non-Moonraker printers, return empty history for now
            return new Farm.Web.Shared.HistoryListResponse { Count = 0, Jobs = Array.Empty<Farm.Web.Shared.HistoryJob>() };
        }

        try
        {
            Services.HistoryListResponse? moonrakerResponse = await moon.GetHistoryListAsync(printer.ServerUrl, limit, start, since, before, order, ct);
            if (moonrakerResponse == null)
            {
                return new Farm.Web.Shared.HistoryListResponse { Count = 0, Jobs = Array.Empty<Farm.Web.Shared.HistoryJob>() };
            }

            // Convert from Moonraker models to shared models
            Shared.HistoryJob[] jobs = moonrakerResponse.Jobs.Select(j => new Farm.Web.Shared.HistoryJob
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
            _logger.LogError(ex, $"Failed to get history for printer {id}: {ex.Message}");
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
            _logger.LogWarning($"GetHistoryJob called with null or empty jobId for printer {id}");
            return BadRequest("Job ID is required");
        }

        Printer? printer = await _printersRepo.FindByIdAsync(id, ct);
        if (printer == null)
        {
            _logger.LogWarning($"Printer {id} not found for history job request");
            throw new PrinterNotFoundException($"Printer {id} not found");
        }

        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            _logger.LogWarning($"History requested for non-Moonraker printer {id} (Backend={printer.Backend})");
            return BadRequest("History is only available for Moonraker printers");
        }

        _logger.LogDebug($"Fetching history job {jobId} for printer {id} ({printer.Name})");

        try
        {
            Services.HistoryJob? moonrakerJob = await moon.GetHistoryJobAsync(printer.ServerUrl, jobId, ct);
            if (moonrakerJob == null)
            {
                _logger.LogInformation($"History job {jobId} not found for printer {id}");
                return NotFound($"History job {jobId} not found");
            }

            // Convert from Moonraker model to shared model
            Shared.HistoryJob job = new()
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

            _logger.LogDebug($"Successfully retrieved history job {jobId} for printer {id}");
            return job;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"Network error retrieving history job {jobId} for printer {id} from {printer.ServerUrl}: {ex.Message}");
            return StatusCode(StatusCodes.Status502BadGateway, "Unable to connect to printer");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning($"Timeout retrieving history job {jobId} for printer {id}: {ex.Message}");
            return StatusCode(StatusCodes.Status408RequestTimeout, "Request timeout");
        }
        // Let global exception handler catch other exceptions for consistent error responses
    }

    [HttpGet("{id}/history/totals")]
    [ProducesResponseType(typeof(Farm.Web.Shared.HistoryTotals), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Farm.Web.Shared.HistoryTotals>> GetHistoryTotalsAsync(Guid id, CancellationToken ct = default)
    {
        Printer? printer = await _printersRepo.FindByIdAsync(id, ct);
        if (printer == null)
        {
            return NotFound();
        }

        _logger.LogDebug($"GetHistoryTotals called for printer {id} ({printer.Name}), backend: {printer.Backend}");

        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            _logger.LogInformation($"Printer {id} is not Moonraker backend, returning empty totals");
            // Return empty totals for non-Moonraker printers
            return new Farm.Web.Shared.HistoryTotals
            {
                JobTotals = new Farm.Web.Shared.JobTotals()
            };
        }

        try
        {
            _logger.LogDebug($"Calling Moonraker API for totals at: {printer.ServerUrl}");
            Services.HistoryTotals? moonrakerTotals = await moon.GetHistoryTotalsAsync(printer.ServerUrl, ct);
            if (moonrakerTotals == null)
            {
                _logger.LogWarning($"Moonraker API returned null totals");
                return new Farm.Web.Shared.HistoryTotals { JobTotals = new Farm.Web.Shared.JobTotals() };
            }

            _logger.LogDebug($"Moonraker totals received - Jobs: {moonrakerTotals.JobTotals.TotalJobs}, PrintTime: {moonrakerTotals.JobTotals.TotalPrintTime}, FilamentUsed: {moonrakerTotals.JobTotals.TotalFilamentUsed}");

            // Convert from Moonraker model to shared model
            Shared.HistoryTotals totals = new()
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

            _logger.LogDebug($"Returning converted totals - Jobs: {totals.JobTotals.TotalJobs}, PrintTime: {totals.JobTotals.TotalPrintTime}, FilamentUsed: {totals.JobTotals.TotalFilamentUsed}");
            return totals;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get history totals for printer {id}: {ex.Message}");
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
        Printer? printer = await _printersRepo.FindByIdAsync(id, ct);
        if (printer == null)
        {
            return NotFound();
        }

        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            return BadRequest("History deletion is only available for Moonraker printers");
        }

        try
        {
            bool success = await moon.DeleteHistoryJobAsync(printer.ServerUrl, jobId, ct);
            return success ? Ok() : StatusCode(StatusCodes.Status500InternalServerError, "Failed to delete history job");
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
        var printers = (await _printersRepo.GetPrintersForExportAsync(null, ct))
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
            .ToList();

        StringBuilder csv = new();
        _ = csv.AppendLine("Name,ServerUrl,OriginalServerUrl,Notes,ManufacturerName,ModelName,Backend,ApiKey,DateAcquired");

        foreach (var printer in printers)
        {
            _ = csv.AppendLine($"{EscapeCsvValue(printer.Name)}," +
                          $"{EscapeCsvValue(printer.ServerUrl)}," +
                          $"{EscapeCsvValue(printer.OriginalServerUrl)}," +
                          $"{EscapeCsvValue(printer.Notes)}," +
                          $"{EscapeCsvValue(printer.ManufacturerName)}," +
                          $"{EscapeCsvValue(printer.ModelName)}," +
                          $"{EscapeCsvValue(printer.Backend)}," +
                          $"{EscapeCsvValue(printer.ApiKey)}," +
                          $"{EscapeCsvValue(printer.DateAcquired?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))}");
        }

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
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
            List<Printer> printers = await _printersRepo.GetPrintersForExportAsync(ids, ct);
            List<PrinterCapabilities> capabilities = await _printersRepo.GetCapabilitiesListAsync(ids, ct);

            var results = printers.Select(p =>
            {
                PrinterCapabilities? cap = capabilities.Find(c => c.PrinterId == p.Id);
                return new PrinterWithCapabilitiesDto
                {
                    PrinterId = p.Id,
                    PrinterName = p.Name,
                    PrinterModel = p.Model != null ? p.Model.Name ?? string.Empty : string.Empty,
                    ManufacturerName = p.Manufacturer != null ? p.Manufacturer.Name : null,
                    Backend = (PrinterBackend?)p.Backend,
                    IpAddress = p.IpAddress,
                    Capabilities = cap == null ? null : new PrinterCapabilitiesDto(
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
        format = (format ?? "csv").ToLowerInvariant();
        IQueryable<Printer> query = (await _printersRepo.GetPrintersForExportAsync(ids, ct)).AsQueryable();
        Dictionary<Guid, PrinterCapabilities> capabilities = (await _printersRepo.GetCapabilitiesListAsync(ids, ct)).ToDictionary(c => c.PrinterId);

        // local helper removed; using class-level PropertySuppressedForExport method instead

        if (format == "json")
        {
            await StreamJsonExportAsync(query, capabilities, ct);
            return new EmptyResult();
        }

        // CSV streaming using pipe
        string filename = $"printers-export-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.csv";
        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = $"attachment; filename={filename}";

        // Write header (include capability fields for parity with JSON export). Exclude properties marked to be suppressed for export.
        List<string> headerParts = new List<string>() { "Name", "ServerUrl", "OriginalServerUrl", "Notes", "ManufacturerName", "ModelName", "Backend", "ApiKey", "DateAcquired" };
        // Build capability property lists and append to headerParts
        BuildCsvHeaderAndCapProps(ref headerParts, out List<string> capPropsForCsv, out List<System.Reflection.PropertyInfo> capPropInfos);
        // Map a few friendly names for CSV (NozzleDiameter -> NozzleDiameter, SupportedMaterials -> SupportedMaterials)
        headerParts.AddRange(capPropsForCsv);
        headerParts.Add("CapabilitiesLastUpdated");

        // Write header using PipeWriter to reduce temporary allocations
        System.IO.Pipelines.PipeWriter pipeWriter = Response.BodyWriter;
        await WriteCsvHeaderAsync(pipeWriter, headerParts, ct);

        await foreach (Printer p in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            capabilities.TryGetValue(p.Id, out PrinterCapabilities? cap);

            // Build row values in the same order as headerParts
            // Pre-calc small buffer for a typical line; we still use GetBytes into the PipeWriter span per field
            // to avoid building large intermediate strings.
            // Write fixed prefix fields
            string[] prefixFields = new[]
            {
            EscapeCsvValue(p.Name),
            EscapeCsvValue(p.ServerUrl),
            EscapeCsvValue(p.OriginalServerUrl),
            EscapeCsvValue(p.Notes),
            EscapeCsvValue(p.Manufacturer?.Name),
            EscapeCsvValue(p.Model?.Name),
            EscapeCsvValue(p.Backend.ToString()),
            EscapeCsvValue(p.ApiKey),
            EscapeCsvValue(p.DateAcquired?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
        };

            // Delegate CSV row writing to helper to reduce nesting and satisfy analyzers
            await WriteCsvRowAsync(pipeWriter, prefixFields, capPropInfos, cap, ct);
        }

        return new EmptyResult();
    }

    // Extracted JSON streaming export to reduce nesting and satisfy analyzers (S1199).
    private async Task StreamJsonExportAsync(IQueryable<Printer> query, Dictionary<Guid, PrinterCapabilities> capabilities, CancellationToken ct)
    {
        Response.ContentType = "application/json";
        Response.Headers["Content-Disposition"] = $"attachment; filename=printers-export-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.json";
        await using Utf8JsonWriter writer = new Utf8JsonWriter(Response.BodyWriter);
        writer.WriteStartArray();
        await foreach (Printer p in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            capabilities.TryGetValue(p.Id, out PrinterCapabilities? cap);
            Dictionary<string, object?> dtoDict = BuildExportPrinterDictionary(p, cap);
            JsonSerializer.Serialize(writer, dtoDict, _exportJsonOptions);
            await writer.FlushAsync(ct);
        }
        writer.WriteEndArray();
        await writer.FlushAsync(ct);
    }

    public enum DuplicateHandling { Skip, Error, Update }

    [HttpPost("import")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "farm_admin")]
    public async Task<IActionResult> ImportPrintersAsync(IFormFile file, [FromQuery] string duplicateHandling = "skip", CancellationToken ct = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("No file provided");
        }
        // parse requested duplicate handling (we keep the query-string value for processor; local enum not needed)
        Enum.TryParse<DuplicateHandling>(duplicateHandling, true, out _);

        if (file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            (CreatePrinterDto[] dtos, List<string> parseErrors) = await _importParser.ParseCsvAsync(file.OpenReadStream(), ct);
            if (parseErrors.Count > 0)
            {
                return BadRequest(string.Join(';', parseErrors));
            }

            List<(string Name, string Status, System.Guid? Id, string? Reason)> processed = await _importProcessor.ProcessAsync(dtos, duplicateHandling, ct);
            return Ok(new
            {
                ImportedCount = processed.Count(r => r.Status == "Imported"),
                SkippedCount = processed.Count(r => r.Status == "Skipped"),
                Results = processed,
                Errors = Array.Empty<string>()
            });
        }
        else if (file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            // Parse JSON into CreatePrinterDto[] and forward to BulkCreateAsync logic
            try
            {
                (CreatePrinterDto[] dtos, List<string> parseErrors) = await _importParser.ParseJsonAsync(file.OpenReadStream(), ct);
                if (parseErrors.Count > 0)
                {
                    return BadRequest(string.Join(';', parseErrors));
                }

                List<(string Name, string Status, System.Guid? Id, string? Reason)> processed = await _importProcessor.ProcessAsync(dtos, duplicateHandling, ct);
                return Ok(new
                {
                    ImportedCount = processed.Count(r => r.Status == "Imported"),
                    SkippedCount = processed.Count(r => r.Status == "Skipped"),
                    Results = processed
                });
            }
            catch (System.Text.Json.JsonException ex)
            {
                return BadRequest($"Invalid JSON file: {ex.Message}");
            }
        }
        else
        {
            return BadRequest("File must be a CSV or JSON file");
        }

        // CSV handling moved into ImportCsvAsync
    }

    /// <summary>
    /// Bulk create printers from a JSON array of CreatePrinterDto objects.
    /// Returns per-item import results including success/failure and reasons.
    /// </summary>
    [HttpPost("bulk")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "farm_admin")]
    public async Task<IActionResult> BulkCreateAsync([FromBody] CreatePrinterDto[] dtos, CancellationToken ct)
    {
        if (dtos == null || dtos.Length == 0)
        {
            return BadRequest("No printers provided");
        }

        List<BulkImportResultItem> results = new();

        foreach ((CreatePrinterDto dto, int idx) in dtos.Select((d, i) => (d, i)))
        {
            try
            {
                // Validate using the existing validator
                ValidationResult validationResult = await validator.ValidateAsync(dto, ct);
                if (!validationResult.IsValid)
                {
                    string reason = string.Join("; ", validationResult.Errors.Select(e => e.ErrorMessage));
                    results.Add(new BulkImportResultItem(idx, dto.Name, "Failed", null, reason));
                    continue;
                }

                // Simple existence check by name or server URL to avoid duplicates
                bool exists = await _printersRepo.ExistsByNameOrServerUrlAsync(dto.Name, dto.ServerUrl, ct);
                if (exists)
                {
                    results.Add(new BulkImportResultItem(idx, dto.Name, "Skipped", null, "Printer already exists"));
                    continue;
                }

                PrinterDto created = await CreatePrinterFromDtoAsync(dto, ct);
                results.Add(new BulkImportResultItem(idx, dto.Name, "Imported", created.Id, null));
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Bulk import item failed at index {idx}: {ex.Message}");
                results.Add(new BulkImportResultItem(idx, dto?.Name ?? string.Empty, "Failed", null, ex.Message));
            }
        }

        return Ok(new
        {
            ImportedCount = results.Count(r => r.Status == "Imported"),
            SkippedCount = results.Count(r => r.Status == "Skipped"),
            Results = results
        });
    }

    private async Task<PrinterDto> CreatePrinterFromDtoAsync(CreatePrinterDto dto, CancellationToken ct)
    {
        // resolve or create manufacturer/model
        Guid manufacturerId = dto.ManufacturerId ?? Guid.Empty;
        if (manufacturerId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            string name = dto.NewManufacturerName!.Trim();
            Guid newManId = Guid.NewGuid();
            await _catalogRepo.AddManufacturerAsync(newManId, name, ct);
            manufacturerId = newManId;
        }

        Guid modelId = dto.ModelId ?? Guid.Empty;
        if (modelId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewModelName) && manufacturerId != Guid.Empty)
        {
            string mname = dto.NewModelName!.Trim();
            var newModel = new Farm.Infrastructure.Domain.PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturerId, Name = mname };
            await _catalogRepo.AddModelAsync(newModel, ct);
            modelId = newModel.Id;
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

        // Resolve host to IP and persist the IP-based base URL; store original URL for future re-resolve
        int defaultPort = dto.Backend == PrinterBackend.PrusaLink ? 80 : dto.Backend == PrinterBackend.SDCP ? 80 : 7125;
        string normalizedInput = NormalizeServerUrl(dto.ServerUrl, defaultPort);
        string resolvedBase = normalizedInput;
        string? resolvedIp = null;
        try
        {
            Uri uri = new(normalizedInput);
            if (!System.Net.IPAddress.TryParse(uri.Host, out _))
            {
                string hostToResolve = EnsureLocalSuffix(uri.Host);
                IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(hostToResolve, ct);
                IPAddress? firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? (addresses.Length > 0 ? addresses[0] : null);
                if (firstIp is not null)
                {
                    UriBuilder ub = new(uri)
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
            DateAcquired = dto.DateAcquired?.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dto.DateAcquired.Value, DateTimeKind.Utc)
                : dto.DateAcquired,
            Backend = (int)dto.Backend,
            ApiKey = dto.ApiKey
        };
        await _printersRepo.AddAsync(p, ct);

        // Auto-discover capabilities for the newly created printer (import scenario)
        try
        {
            Printer? printerForDiscovery = await _printersRepo.FindByIdWithIncludesAsync(p.Id, ct);

            if (printerForDiscovery != null)
            {
                PrinterCapabilities? discoveredCapabilities = await capabilityDiscovery.DiscoverCapabilitiesAsync(printerForDiscovery, ct);
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

        return new PrinterDto(
            Id: p.Id,
            Name: p.Name,
            ServerUrl: p.ServerUrl,
            Notes: p.Notes,
            IsOnline: false,
            State: null,
            ManufacturerName: null,
            ModelName: null,
            Backend: dto.Backend,
            ApiKey: p.ApiKey,
            OriginalServerUrl: p.OriginalServerUrl,
            IpAddress: p.IpAddress
        );
    }

    // Simple result shape for bulk import responses
    private sealed record BulkImportResultItem(int Index, string Name, string Status, Guid? Id = null, string? Reason = null);

    private static string EscapeCsvValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
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

    // Helper to build CSV header parts and capability property infos for export
    private static void BuildCsvHeaderAndCapProps(ref List<string> headerParts, out List<string> capPropsForCsv, out List<System.Reflection.PropertyInfo> capPropInfos)
    {
        capPropsForCsv = new List<string>();
        capPropInfos = new List<System.Reflection.PropertyInfo>();

        try
        {
            Type capType = typeof(Farm.Infrastructure.Domain.PrinterCapabilities);
            var resolver = _exportJsonOptions?.TypeInfoResolver;
            if (resolver != null)
            {
                var ti = resolver.GetTypeInfo(capType, _exportJsonOptions!);
                if (ti != null)
                {
                    foreach (var jp in ti.Properties)
                    {
                        System.Reflection.PropertyInfo? pi = capType.GetProperty(jp.Name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (pi == null)
                        {
                            continue;
                        }

                        ImportExportAttribute? attr = pi.GetCustomAttribute<ImportExportAttribute>(inherit: true);
                        if (attr != null && (attr.IgnoreFor & ImportExportTargets.Export) != 0)
                        {
                            continue;
                        }

                        capPropsForCsv.Add(jp.Name);
                        capPropInfos.Add(pi);
                    }
                }
            }
        }
        catch
        {
            // ignore and fall back to reflection
        }

        if (capPropsForCsv.Count == 0)
        {
            var infos = typeof(Farm.Infrastructure.Domain.PrinterCapabilities).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(pi => !(pi.GetCustomAttribute<ImportExportAttribute>(inherit: true) is ImportExportAttribute a && (a.IgnoreFor & ImportExportTargets.Export) != 0))
                .ToArray();
            capPropsForCsv = infos.Select(pi => pi.Name).ToList();
            capPropInfos = infos.ToList();
        }

        headerParts.AddRange(capPropsForCsv);
        headerParts.Add("CapabilitiesLastUpdated");
    }

    private static Dictionary<string, object?> BuildExportPrinterDictionary(Printer p, PrinterCapabilities? cap)
    {
        Dictionary<string, object?> dtoDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        dtoDict["printerId"] = p.Id;
        dtoDict["printerName"] = p.Name;
        dtoDict["printerModel"] = p.Model?.Name ?? string.Empty;
        dtoDict["manufacturerName"] = p.Manufacturer?.Name;
        dtoDict["backend"] = (PrinterBackend?)p.Backend;
        dtoDict["ipAddress"] = p.IpAddress;

        if (cap != null)
        {
            Dictionary<string, object?> capDict = new Dictionary<string, object?>();
            System.Reflection.PropertyInfo[] capProps = typeof(Farm.Infrastructure.Domain.PrinterCapabilities).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            foreach (System.Reflection.PropertyInfo prop in capProps)
            {
                if (PropertySuppressedForExport(prop))
                {
                    continue;
                }

                object? val = prop.GetValue(cap);
                ReadOnlySpan<char> nameSpan = prop.Name.AsSpan();
                string camel = char.ToLowerInvariant(nameSpan[0]).ToString() + nameSpan.Slice(1).ToString();
                capDict[camel] = val;
            }
            dtoDict["capabilities"] = capDict;
        }

        return dtoDict;
    }

    // Helper method to extract thumbnail URL from metadata
    private static string? ExtractThumbnailUrl(Dictionary<string, object> metadata, string printerServerUrl)
    {
        if (metadata == null)
        {
            return null;
        }

        // Look for thumbnail in common metadata keys
        string[] thumbnailKeys = new[] { "thumbnail", "thumbnails", "gcode_thumbnail" };

        foreach (string? key in thumbnailKeys)
        {
            if (metadata.TryGetValue(key, out object? thumbnailValue))
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
                    List<JsonElement> array = jsonElement.EnumerateArray().ToList();
                    if (array.Count > 0)
                    {
                        // Handle array of strings (legacy format)
                        if (array[0].ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            string? thumbnailPath = array[0].GetString();
                            if (!string.IsNullOrEmpty(thumbnailPath))
                            {
                                return thumbnailPath.StartsWith("http") ? thumbnailPath : $"{printerServerUrl.TrimEnd('/')}/server/files/gcodes/{thumbnailPath}";
                            }
                        }
                        // Handle array of thumbnail objects with relative_path property
                        else if (array[0].ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            // Look for the largest thumbnail (prefer 400x300, then 300x300, then others)
                            JsonElement thumbnailObj = array
                                .Where(t => t.TryGetProperty("relative_path", out _))
                                .OrderByDescending(t =>
                                {
                                    int width = t.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0;
                                    int height = t.TryGetProperty("height", out JsonElement h) ? h.GetInt32() : 0;
                                    return width * height; // Prefer larger thumbnails
                                })
                                .FirstOrDefault();

                            if (thumbnailObj.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                thumbnailObj.TryGetProperty("relative_path", out JsonElement relativePathProp))
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
                    if (Enum.TryParse<PrinterBackend>(p, true, out PrinterBackend b))
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

            // Get existing printer ServerUrls to filter out duplicates
            List<string> existingUrls = (await _printersRepo.GetAllWithIncludesAsync(ct)).Select(p => p.ServerUrl).ToList();

            // Normalize both existing and discovered URLs for proper comparison
            HashSet<string> normalizedExistingUrls = existingUrls
                .Select(url => NormalizeServerUrl(url, 80))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Filter out printers that already exist in the database
            List<DiscoveredPrinterDto> newPrinters = discovered
                .Where(d => !normalizedExistingUrls.Contains(NormalizeServerUrl(d.ServerUrl, 80)))
                .ToList();

            _logger.LogInformation($"Discovery completed. Found {discovered.Count} printers, {newPrinters.Count} are new");

            return Ok(newPrinters);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning($"Printer discovery was cancelled");
            return StatusCode(StatusCodes.Status408RequestTimeout, "Discovery operation timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to discover printers on network: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to discover printers. Please try again.");
        }
    }

    [HttpPost("discover/stream")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(408)]
    [ProducesResponseType(500)]
    public ActionResult StartDiscoveryStream([FromBody] StartDiscoveryRequest? request, CancellationToken ct)
    {
        try
        {
            // Generate a unique session ID for this discovery session
            string sessionId = Guid.NewGuid().ToString();

            _logger.LogInformation($"Starting streaming network printer discovery with session ID: {sessionId}");

            // Start the discovery process in the background
            // The progress and results will be sent via SignalR
            _ = Task.Run(async () =>
            {
                try
                {
                    using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMinutes(15)); // 15 minute total timeout to allow for multiple networks and slow responses

                    await networkDiscovery.DiscoverPrintersWithProgressAsync(sessionId, request?.Backends, timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning($"Streaming printer discovery was cancelled for session {sessionId}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to discover printers in streaming mode for session {sessionId}: {ex.Message}");
                }
            }, ct);

            // Return the session ID immediately so client can join the SignalR group
            return Ok(new { sessionId, message = "Discovery started. Connect to SignalR hub to receive updates." });
        }

        catch (Exception ex)
        {
            _logger.LogError($"Failed to start streaming printer discovery: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to start discovery stream. Please try again.");
        }
    }

    [HttpGet("test-simple")]
    [ProducesResponseType(typeof(object), 200)]
    public ActionResult TestSimple()
    {
        _logger.LogError($"=== SIMPLE TEST CALLED ===");
        return Ok(new { message = "API is working!", timestamp = DateTime.UtcNow });
    }

    // Request models moved to top-level files
}
