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
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Discovery;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Controllers.Responses;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using IPrinterVersionCache = Farm.Infrastructure.Services.Printers.IPrinterVersionCache;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// API controller for managing printers and printer-related operations.
/// Provides endpoints for CRUD operations, printer status monitoring, file management,
/// job control, history tracking, and discovery of new printers.
/// Integrates with multiple printer backend types (Moonraker, PrusaLink, OctoPrint, SDCP).
/// </summary>
[ApiController]
[Route("api/printers")]
[Authorize]
public class PrintersController(
    ILogger<PrintersController> logger,
    Farm.Infrastructure.Services.Printers.IPrintersService printersService,
    Services.Catalog.ICatalogService catalogService,
    IValidator<CreatePrinterFromDiscoveryDto> validator,
    IDiscoveryProxyService discoveryProxyService,
    Farm.Infrastructure.Services.Printers.IPrinterBackendCapabilitiesService printerBackendCapabilitiesService,
    Farm.Infrastructure.Services.Printers.IBackendClientFactory backendClientFactory,
    IHttpClientFactory httpClientFactory,
    Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService obicoServerAssignment,
    ISettingsService settingsService,
    Farm.Infrastructure.Services.Printers.IPrinterSessionTimelineService printerSessionTimelineService,
    IPrintFarmerTelemetryService telemetryService,
    Farm.Infrastructure.Services.BedTypes.IBedTypeService bedTypeService,
    Farm.Infrastructure.Services.IProfileImportService? profileImportService = null,
    IPrinterVersionCache printerVersionCache = null!)
    : ControllerBase
{
    private readonly ILogger<PrintersController> _logger = logger;
    private readonly Farm.Infrastructure.Services.Printers.IPrintersService _printersService = printersService;
    private readonly Services.Catalog.ICatalogService _catalogService = catalogService;
    private readonly IValidator<CreatePrinterFromDiscoveryDto> _validator = validator;
    private readonly IDiscoveryProxyService _discoveryProxyService = discoveryProxyService;
    private readonly Farm.Infrastructure.Services.Printers.IPrinterBackendCapabilitiesService _printerBackendCapabilitiesService = printerBackendCapabilitiesService;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly Farm.Infrastructure.Services.IProfileImportService? _profileImportService = profileImportService;
    private readonly IPrinterVersionCache _printerVersionCache = printerVersionCache;
    private readonly Farm.Infrastructure.Services.Printers.IBackendClientFactory _backendClientFactory = backendClientFactory;
    private readonly Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService _obicoServerAssignment = obicoServerAssignment;
    private readonly ISettingsService _settingsService = settingsService;
    private readonly Farm.Infrastructure.Services.Printers.IPrinterSessionTimelineService _printerSessionTimelineService = printerSessionTimelineService;
    private readonly IPrintFarmerTelemetryService _telemetryService = telemetryService;
    private readonly Farm.Infrastructure.Services.BedTypes.IBedTypeService _bedTypeService = bedTypeService;

    /// <summary>
    /// Retrieves camera URLs for all printers without making external API calls.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A lightweight list of all printers with their configured camera URLs.</returns>
    /// <response code="200">Returns the list of printers with camera URL information.</response>
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
            _logger.LogWarning("[CAMERA-URLS] Startup DB exception in /api/printers/camera-urls. TraceId={HttpContextTraceIdentifier}, Exception={Message}", HttpContext.TraceIdentifier, ex.Message);
            return Ok(Array.Empty<PrinterCameraUrlsDto>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FATAL] Unhandled exception in /api/printers/camera-urls. TraceId={HttpContextTraceIdentifier}, User={Name}, Exception={Message}\n{StackTrace}", HttpContext.TraceIdentifier, User?.Identity?.Name ?? "anonymous", ex.Message, ex.StackTrace);
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
    /// Retrieves backend capabilities for all printers.
    /// Indicates which features each backend (Moonraker, PrusaLink, etc.) supports.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Backend capabilities for all printers.</returns>
    /// <response code="200">Returns backend capabilities for all printers.</response>
    [HttpGet("backend-capabilities")]
    [ProducesResponseType(typeof(IEnumerable<PrinterBackendCapabilitiesDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<PrinterBackendCapabilitiesDto>>> GetBackendCapabilitiesAsync(CancellationToken ct)
    {
        try
        {
            IEnumerable<PrinterBackendCapabilitiesDto> capabilities = await _printerBackendCapabilitiesService.GetAllAsync(ct);
            return Ok(capabilities);
        }
        catch (Exception ex) when (IsTransientStartupDbException(ex))
        {
            _logger.LogWarning("[BACKEND-CAPABILITIES] Startup DB exception in /api/printers/backend-capabilities. TraceId={HttpContextTraceIdentifier}, Exception={Message}", HttpContext.TraceIdentifier, ex.Message);
            return Ok(Array.Empty<PrinterBackendCapabilitiesDto>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FATAL] Unhandled exception in /api/printers/backend-capabilities. TraceId={HttpContextTraceIdentifier}, User={Name}, Exception={Message}\n{StackTrace}", HttpContext.TraceIdentifier, User?.Identity?.Name ?? "anonymous", ex.Message, ex.StackTrace);
            return StatusCode(StatusCodes.Status500InternalServerError, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Retrieves firmware/backend/API version information for a specific printer.
    /// Values are best-effort and may be null when not available.
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpGet("{printerId:guid}/version")]
    [ProducesResponseType(typeof(PrinterVersionInfoDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterVersionInfoDto>> GetPrinterVersionAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            PrinterVersionInfoDto? dto = await _printerVersionCache.GetAsync(printerId, ct);
            return dto == null ? NotFound($"Printer with ID {printerId} not found") : Ok(dto);
        }
        catch (Exception ex) when (IsTransientStartupDbException(ex))
        {
            _logger.LogWarning("[PRINTER-VERSION] Startup DB exception for printer {PrinterId}. TraceId={HttpContextTraceIdentifier}, Exception={Message}", printerId, HttpContext.TraceIdentifier, ex.Message);
            return NotFound($"Printer with ID {printerId} not found");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FATAL] Unhandled exception in /api/printers/{PrinterId}/version. TraceId={HttpContextTraceIdentifier}, User={Name}, Exception={Message}\n{StackTrace}", printerId, HttpContext.TraceIdentifier, User?.Identity?.Name ?? "anonymous", ex.Message, ex.StackTrace);
            return StatusCode(StatusCodes.Status500InternalServerError, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Retrieves backend capabilities for a specific printer.
    /// Indicates which features the printer's backend (Moonraker, PrusaLink, etc.) supports.
    /// </summary>
    /// <param name="printerId">The ID of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Backend capabilities for the specified printer.</returns>
    /// <response code="200">Returns backend capabilities for the printer.</response>
    /// <response code="404">Printer not found.</response>
    [HttpGet("{printerId}/backend-capabilities")]
    [ProducesResponseType(typeof(PrinterBackendCapabilitiesDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterBackendCapabilitiesDto>> GetPrinterBackendCapabilitiesAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            PrinterBackendCapabilitiesDto? capabilities = await _printerBackendCapabilitiesService.GetByPrinterIdAsync(printerId, ct);
            return capabilities == null ? NotFound($"Printer with ID {printerId} not found") : Ok(capabilities);
        }
        catch (Exception ex) when (IsTransientStartupDbException(ex))
        {
            _logger.LogWarning("[BACKEND-CAPABILITIES] Startup DB exception for printer {PrinterId}. TraceId={HttpContextTraceIdentifier}, Exception={Message}", printerId, HttpContext.TraceIdentifier, ex.Message);
            return NotFound($"Printer with ID {printerId} not found");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FATAL] Unhandled exception in /api/printers/{PrinterId}/backend-capabilities. TraceId={HttpContextTraceIdentifier}, User={Name}, Exception={Message}\n{StackTrace}", printerId, HttpContext.TraceIdentifier, User?.Identity?.Name ?? "anonymous", ex.Message, ex.StackTrace);
            return StatusCode(StatusCodes.Status500InternalServerError, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Tests connectivity to a printer backend before adding the printer.
    /// Validates that the provided URL and credentials can successfully connect to the printer.
    /// </summary>
    /// <param name="request">Connection test parameters including URL, backend type, and optional API key.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Connection test result with success status and optional message.</returns>
    /// <response code="200">Connection test completed (check success field for result).</response>
    /// <response code="400">Invalid request parameters.</response>
    [HttpPost("test-connection")]
    [ProducesResponseType(typeof(TestConnectionResponse), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<TestConnectionResponse>> TestConnectionAsync(
        [FromBody] TestConnectionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ServerUrl))
        {
            return BadRequest(new TestConnectionResponse { Success = false, Message = "Server URL is required" });
        }

        if (!Uri.TryCreate(request.ServerUrl, UriKind.Absolute, out Uri? serverUri))
        {
            return BadRequest(new TestConnectionResponse { Success = false, Message = "Invalid server URL format" });
        }

        _logger.LogInformation("Testing connection to {RequestServerUrl} with backend {RequestBackend}", request.ServerUrl, request.Backend);

        try
        {
            TestConnectionResponse result = await TestBackendConnectionAsync(serverUri, request.Backend, request.ApiKey, request.BackendPort, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Connection test failed: {Message}", ex.Message);
            return Ok(new TestConnectionResponse
            {
                Success = false,
                Message = $"Connection failed: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Tests connection to a printer backend based on the backend type.
    /// </summary>
    private async Task<TestConnectionResponse> TestBackendConnectionAsync(
        Uri serverUrl, PrinterBackend backend, string? apiKey, int? backendPort, CancellationToken ct)
    {
        using HttpClient httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(10);

        return backend switch
        {
            PrinterBackend.Moonraker => await TestMoonrakerConnectionAsync(httpClient, serverUrl, backendPort ?? 7125, ct),
            PrinterBackend.PrusaLink => await TestPrusaLinkConnectionAsync(serverUrl, apiKey, ct),
            PrinterBackend.OctoPrint => await TestOctoPrintConnectionAsync(httpClient, serverUrl, apiKey, ct),
            PrinterBackend.SDCP => await TestSdcpConnectionAsync(serverUrl, backendPort, ct),
            PrinterBackend.FlashForge => await TestFlashForgeConnectionAsync(serverUrl, backendPort, ct),
            _ => new TestConnectionResponse { Success = false, Message = $"Unsupported backend type: {backend}" }
        };
    }

    private async Task<TestConnectionResponse> TestSdcpConnectionAsync(Uri serverUrl, int? backendPort, CancellationToken ct)
    {
        Uri uriToTest = serverUrl;
        if (backendPort.HasValue)
        {
            uriToTest = new UriBuilder(serverUrl) { Port = backendPort.Value }.Uri;
        }

        try
        {
            Farm.Infrastructure.Contracts.Printers.IBackendClient client = _backendClientFactory.GetClient(PrinterBackend.SDCP);
            if (client is not Farm.Infrastructure.Services.Printers.ISupportsConnectionTest connectionTestClient)
            {
                return new TestConnectionResponse { Success = false, Message = "SDCP client is not available." };
            }

            bool ok = await connectionTestClient.TestConnectionAsync(uriToTest, ct);
            return ok
                ? new TestConnectionResponse { Success = true, Message = "Successfully connected to SDCP printer." }
                : new TestConnectionResponse { Success = false, Message = "SDCP endpoint did not respond." };
        }
        catch (OperationCanceledException)
        {
            return new TestConnectionResponse { Success = false, Message = "Connection timed out" };
        }
        catch (Exception ex)
        {
            return new TestConnectionResponse { Success = false, Message = $"Connection failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Tests FlashForge connection by performing a TCP handshake via the FlashForge client.
    /// </summary>
    private async Task<TestConnectionResponse> TestFlashForgeConnectionAsync(Uri serverUrl, int? backendPort, CancellationToken ct)
    {
        Uri uriToTest = serverUrl;
        if (backendPort.HasValue)
        {
            uriToTest = new UriBuilder(serverUrl) { Port = backendPort.Value }.Uri;
        }

        try
        {
            Farm.Infrastructure.Contracts.Printers.IBackendClient client = _backendClientFactory.GetClient(PrinterBackend.FlashForge);
            if (client is not Farm.Infrastructure.Services.Printers.ISupportsConnectionTest connectionTestClient)
            {
                return new TestConnectionResponse { Success = false, Message = "FlashForge client is not available." };
            }

            bool ok = await connectionTestClient.TestConnectionAsync(uriToTest, ct);
            return ok
                ? new TestConnectionResponse { Success = true, Message = "Successfully connected to FlashForge printer." }
                : new TestConnectionResponse { Success = false, Message = "FlashForge printer did not respond." };
        }
        catch (OperationCanceledException)
        {
            return new TestConnectionResponse { Success = false, Message = "Connection timed out" };
        }
        catch (Exception ex)
        {
            return new TestConnectionResponse { Success = false, Message = $"Connection failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Tests Moonraker connection by hitting /printer/info endpoint.
    /// </summary>
    private static async Task<TestConnectionResponse> TestMoonrakerConnectionAsync(
        HttpClient httpClient, Uri serverUrl, int backendPort, CancellationToken ct)
    {
        // Build URL with backend port (default 7125 for Moonraker API)
        var builder = new UriBuilder(serverUrl)
        {
            Port = backendPort,
            Path = "/printer/info"
        };

        var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);

        try
        {
            HttpResponseMessage response = await httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync(ct);

                // Moonraker responses are wrapped in "result"
                if (content.Contains("\"result\"") || content.Contains("hostname"))
                {
                    return new TestConnectionResponse
                    {
                        Success = true,
                        Message = "Successfully connected to Moonraker printer"
                    };
                }
            }

            return new TestConnectionResponse
            {
                Success = false,
                Message = $"Moonraker returned status {(int)response.StatusCode}: {response.ReasonPhrase}"
            };
        }
        catch (TaskCanceledException)
        {
            return new TestConnectionResponse { Success = false, Message = "Connection timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new TestConnectionResponse { Success = false, Message = $"Connection failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Tests PrusaLink connection by hitting /api/v1/status endpoint with Digest Authentication.
    /// </summary>
    private static async Task<TestConnectionResponse> TestPrusaLinkConnectionAsync(
        Uri serverUrl, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new TestConnectionResponse { Success = false, Message = "API Key is required for PrusaLink printers. Get it from printer Settings → Network → Credentials" };
        }

        // PrusaLink uses "maker" as the username with the API key as the password for digest auth
        const string username = "maker";

        var builder = new UriBuilder(serverUrl)
        {
            Path = "/api/v1/status"
        };

        // Create a new HttpClient with Digest auth handler for this test
        using var digestHandler = new DigestAuthHandler(username, apiKey);
        using var digestClient = new HttpClient(digestHandler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);

        try
        {
            HttpResponseMessage response = await digestClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                return new TestConnectionResponse
                {
                    Success = true,
                    Message = "Successfully connected to PrusaLink printer"
                };
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new TestConnectionResponse
                {
                    Success = false,
                    Message = "Invalid API key - authentication failed. Verify the API key from printer Settings → Network → Credentials."
                };
            }

            return new TestConnectionResponse
            {
                Success = false,
                Message = $"PrusaLink returned status {(int)response.StatusCode}: {response.ReasonPhrase}"
            };
        }
        catch (TaskCanceledException)
        {
            return new TestConnectionResponse { Success = false, Message = "Connection timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new TestConnectionResponse { Success = false, Message = $"Connection failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Tests OctoPrint connection by hitting /api/version endpoint with API key.
    /// </summary>
    private static async Task<TestConnectionResponse> TestOctoPrintConnectionAsync(
        HttpClient httpClient, Uri serverUrl, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new TestConnectionResponse { Success = false, Message = "API Key is required for OctoPrint printers" };
        }

        var builder = new UriBuilder(serverUrl)
        {
            Path = "/api/version"
        };

        var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            HttpResponseMessage response = await httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                return new TestConnectionResponse
                {
                    Success = true,
                    Message = "Successfully connected to OctoPrint server"
                };
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new TestConnectionResponse
                {
                    Success = false,
                    Message = "Invalid API key - authentication failed"
                };
            }

            return new TestConnectionResponse
            {
                Success = false,
                Message = $"OctoPrint returned status {(int)response.StatusCode}: {response.ReasonPhrase}"
            };
        }
        catch (TaskCanceledException)
        {
            return new TestConnectionResponse { Success = false, Message = "Connection timed out" };
        }
        catch (HttpRequestException ex)
        {
            return new TestConnectionResponse { Success = false, Message = $"Connection failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Retrieves a lightweight list of all printers with minimal data for quick loading.
    /// This is the default GET endpoint for the printers resource.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <param name="includeDisabled">Return disabled printers as well (admin-only).</param>
    /// <param name="doneWithinMinutes">Only return printers estimated to finish within this many minutes.</param>
    /// <param name="doneAfterMinutes">Only return printers estimated to finish after this many minutes.</param>
    /// <param name="bedTypeId">Filter printers by bed type ID.</param>
    /// <returns>A complete list of all printers with configuration and live status merged.</returns>
    /// <response code="200">Returns the list of complete printer data with live status.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<CompletePrinterDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<CompletePrinterDto>>> GetAsync(
        CancellationToken ct,
        [FromQuery] bool includeDisabled = false,
        [FromQuery] int? doneWithinMinutes = null,
        [FromQuery] int? doneAfterMinutes = null,
        [FromQuery] Guid? bedTypeId = null)
    {
        try
        {
            CompletePrinterDto[] dtos = await _printersService.GetAllCompleteDtosAsync(ct);
            bool isAdmin = User.IsInRole("farm_admin");
            IEnumerable<CompletePrinterDto> result = dtos;

            if (!isAdmin)
            {
                if (includeDisabled)
                {
                    return Forbid();
                }

                result = result.Where(p => p.IsEnabled);
            }

            // Time-based availability filters
            if (doneWithinMinutes.HasValue)
            {
                DateTime cutoff = DateTime.UtcNow.AddMinutes(doneWithinMinutes.Value);
                result = result.Where(p =>
                    !p.EstimatedCompletionTimeUtc.HasValue || p.EstimatedCompletionTimeUtc.Value <= cutoff);
            }

            if (doneAfterMinutes.HasValue)
            {
                DateTime cutoff = DateTime.UtcNow.AddMinutes(doneAfterMinutes.Value);
                result = result.Where(p =>
                    p.EstimatedCompletionTimeUtc.HasValue && p.EstimatedCompletionTimeUtc.Value > cutoff);
            }

            if (bedTypeId.HasValue)
            {
                result = result.Where(p => p.BedTypeId == bedTypeId.Value);
            }

            return Ok(result.ToList());
        }
        catch (Exception ex) when (IsTransientStartupDbException(ex))
        {
            _logger.LogWarning("[GET] Startup DB exception in /api/printers. TraceId={HttpContextTraceIdentifier}, Exception={Message}", HttpContext.TraceIdentifier, ex.Message);
            return Ok(Array.Empty<CompletePrinterDto>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FATAL] Unhandled exception in /api/printers. TraceId={HttpContextTraceIdentifier}, User={Name}, Exception={Message}\n{StackTrace}", HttpContext.TraceIdentifier, User?.Identity?.Name ?? "anonymous", ex.Message, ex.StackTrace);
            return StatusCode(StatusCodes.Status500InternalServerError, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates multiple printers in a bulk operation.
    /// </summary>
    /// <param name="printers">Array of printer configurations to create.</param>
    /// <param name="duplicateHandling">How to handle duplicate printers: 'skip' (default), 'overwrite', or 'error'.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result of bulk import operation including created printers and errors.</returns>
    /// <response code="200">Returns bulk import results with created printers and any errors.</response>
    /// <response code="400">If the printer data is invalid.</response>
    /// <response code="500">If there was an error creating printers.</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("bulk")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> BulkCreateAsync(
        [FromBody] CreatePrinterFromDiscoveryDto[] printers,
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
            _logger.LogWarning("[BulkCreate] Validation failed for all printers: {ErrorMessage}", errorMessage);
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
            _logger.LogError(ex, "[BulkCreate] Bulk printer creation failed: {Message}", ex.Message);
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
    /// <param name="file">The CSV or JSON file containing printer configurations.</param>
    /// <param name="duplicateHandling">How to handle duplicates: 'skip' (default), 'overwrite', or 'error'.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result of import operation including created printers and any errors.</returns>
    /// <response code="200">Returns import results with created printers and errors.</response>
    /// <response code="400">If the file is invalid or missing.</response>
    /// <response code="500">If there was an error importing printers.</response>
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

        // 10MB limit
        if (file.Length > 10 * 1024 * 1024)
        {
            return BadRequest(new { message = "File is too large (max 10MB)" });
        }

        try
        {
            _logger.LogInformation("[Import] Starting import from file: {FileFileName}", file.FileName);
            using (Stream stream = file.OpenReadStream())
            {
                object result = await _printersService.ImportFromStreamAsync(stream, file.FileName, duplicateHandling ?? "skip", ct);
                _logger.LogInformation("[Import] Successfully imported from file: {FileFileName}", file.FileName);
                return Ok(result);
            }
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("[Import] Validation error: {Message}", ex.Message);
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("[Import] Invalid data error: {Message}", ex.Message);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Import] Import operation failed: {Message}", ex.Message);
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
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The current print job status if a job is running, otherwise null.</returns>
    /// <response code="200">Returns the print job status or null if no job running.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error retrieving job status.</response>
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
                _logger.LogWarning("[PrintJob] Printer {Id} not found", id);
                return NotFound(new { message = $"Printer {id} not found" });
            }

            _logger.LogInformation("[PrintJob] Getting print job status for printer {PrinterName}", printer.Name);

            // Delegate to service for actual retrieval logic
            PrintJobStatusDto? jobStatus = await _printersService.GetPrintJobStatusAsync(id, ct);

            // Return the status (may be null if no active job)
            return Ok(jobStatus);
        }
        catch (KeyNotFoundException)
        {
            _logger.LogWarning("[PrintJob] Printer {Id} not found", id);
            return NotFound(new { message = $"Printer {id} not found" });
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[PrintJob] Timeout retrieving print job status for printer {Id}", id);
            return Ok((object?)null); // Return null on timeout
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PrintJob] Error getting print job status for printer {Id}: {Message}", id, ex.Message);
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
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The current status of the specified printer including print progress, temperatures, and position.</returns>
    /// <response code="200">Returns the printer's current status.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error communicating with the printer.</response>
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
            _logger.LogWarning("Error getting status for printer {Id}: {Message}", id, ex.Message);
            return new PrinterStatusDto(Id: id, IsOnline: false, State: null, Progress: null, JobName: null, ThumbnailUrl: null, CameraStreamUrl: null, CameraSnapshotUrl: null, SpoolInfo: null);
        }
    }

    /// <summary>
    /// Gets basic information about a specific printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Basic printer information including name, backend, connection status, and current state.</returns>
    /// <response code="200">Returns basic printer information.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
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
            _logger.LogError(ex, "Failed to get printer {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get printer");
        }
    }

    /// <summary>
    /// Gets detailed information about a specific printer including manufacturer, model, and configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Detailed printer information including manufacturer, model, purchase information, and settings.</returns>
    /// <response code="200">Returns detailed printer information.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
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

        // Get primary toolhead for capabilities DTO (backward compatibility)
        Toolhead? primaryToolhead = p.Toolheads?.FirstOrDefault(t => t.IsPrimary) ?? p.Toolheads?.FirstOrDefault();

        // Create capabilities DTO from Printer entity fields (merged from legacy PrinterCapabilities)
        // This provides backward compatibility while we transition to using Toolheads directly
        PrinterCapabilitiesDto? capabilitiesDto = new PrinterCapabilitiesDto(
            Guid.NewGuid(), // PrinterCapabilities.Id - generate a temporary ID since this entity is being phased out
            p.Id,
            p.Name,
            primaryToolhead?.NozzleModel?.Diameter ?? 0.4,  // Nozzle diameter from NozzleModel
            primaryToolhead?.SupportedMaterials,
            p.MaxBuildVolumeX,
            p.MaxBuildVolumeY,
            p.MaxBuildVolumeZ,
            p.HasHeatedBed,
            p.HasEnclosure,
            p.MultiMaterial,
            p.SupportsAutoLeveling,
            primaryToolhead?.HotendModel?.MaxTemp,  // MaxHotendTemp from primary toolhead's HotendModel
            p.MaxBedTemp,
            p.MaxPrintSpeed,
            p.CurrentMaterial,
            p.CurrentSpoolId,
            p.IsAvailable,
            p.ServiceState?.LastCapabilityUpdate ?? DateTime.UtcNow);

        // Map toolheads to DTOs with hardware tracking fields
        ToolheadDto[]? toolheadDtos = p.Toolheads?.OrderBy(t => t.Index).Select(t => new ToolheadDto(
            t.Id,
            t.Name,
            t.Index,
            t.NozzleModel?.Diameter,  // Nozzle diameter from NozzleModel
            t.NozzleModel?.NozzleType,  // Nozzle type from NozzleModel
            t.HotendModel?.MaxFlowRate,  // Max flow rate from HotendModel
            t.HotendModel?.MaxTemp,      // Max temp from HotendModel

            // Component model references - nozzle diameter comes from NozzleModel.Diameter
            t.HotendModelId,
            t.HotendModel?.Name,
            t.ExtruderModelId,
            t.ExtruderModel?.Name,
            t.ToolheadModelDefId,
            t.ToolheadModelDef?.Name,
            t.NozzleModelId,
            t.NozzleModel?.Name,
            t.SupportedMaterials,
            t.IsPrimary,
            t.UpdatedAt,
            t.ToolheadType,
            t.CurrentSpoolId,
            t.CurrentMaterial,
            t.CurrentFilamentColor)).ToArray();

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
            null, // CameraStreamUrl — resolved from Cameras table via DTO layer
            null, // CameraSnapshotUrl — resolved from Cameras table via DTO layer
            p.OriginalServerUrl,
            p.BackendPort,
            p.FrontendPort,
            capabilitiesDto,
            toolheadDtos,
            p.Username,
            p.Password,
            p.ObicoEnabled,
            p.ServiceState?.ObicoServer?.Name,
            p.Wattage,
            p.MachineHourlyRate,
            p.Model != null && p.ServiceState != null && p.Model.UpdatedAt > (p.ServiceState.LastModelSyncAt ?? DateTime.MinValue),
            p.ZOffsetMm,
            p.LastZOffsetCalibrationAt,
            p.UseModelDispatchDefaults,
            p.BuddyCameraIp,
            p.NozzleDiameter,
            p.HasMmu);
    }

    /// <summary>
    /// Creates a new printer configuration.
    /// </summary>
    /// <param name="dto">The printer data transfer object containing printer details.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The created printer with its assigned unique identifier.</returns>
    /// <response code="201">Returns the newly created printer.</response>
    /// <response code="400">If the printer data is invalid or validation fails.</response>
    /// <response code="409">If a printer with the same name and URL already exists.</response>
    /// <response code="500">If there was an error creating the printer.</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPost]
    [ProducesResponseType(typeof(PrinterDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterDto>> CreateAsync([FromBody] CreatePrinterFromDiscoveryDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        // Validate input using FluentValidation
        ValidationResult validationResult = await _validator.ValidateAsync(dto, ct);
        if (!validationResult.IsValid)
        {
            _logger.LogWarning("Printer creation validation failed: {Value0}", string.Join("; ", validationResult.Errors.Select(e => e.ErrorMessage)));

            foreach (ValidationFailure? error in validationResult.Errors)
            {
                ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
            }

            return BadRequest(ModelState);
        }

        _logger.LogInformation("Creating new printer: {DtoName} ({DtoBackend})", dto.Name, dto.Backend);

        // Delegate creation/business logic to the service
        PrinterDto created = await _printersService.CreatePrinterFromDtoAsync(dto, ct);

        // Import slicer profiles for this printer's model (pull-based, on-demand import)
        // Only imports if profiles don't already exist for this model
        // Use the input DTO since it has ModelId, and the result DTO only has names
        Guid? modelId = dto.ModelId;
        string modelName = dto.NewModelName ?? created.ModelName ?? "Unknown";
        string manufacturerName = dto.NewManufacturerName ?? created.ManufacturerName ?? "Unknown";

        if (modelId.HasValue && modelId.Value != Guid.Empty)
        {
            try
            {
                if (_profileImportService is not null)
                {
                    int imported = await _profileImportService.ImportProfilesForModelAsync(
                        modelId.Value,
                        modelName,
                        manufacturerName,
                        ct);

                    if (imported > 0)
                    {
                        _logger.LogInformation("Imported {Imported} slicer profiles for {ModelName}", imported, modelName);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail printer creation if profile import fails
                _logger.LogWarning("Failed to import profiles for {ModelName}: {Message}", modelName, ex.Message);
            }
        }

        return CreatedAtRoute("GetPrinterById", new { id = created.Id }, created);
    }

    /// <summary>
    /// Register printers discovered by the network discovery service.
    /// Accepts both single printers and arrays for backward compatibility.
    /// </summary>
    /// <param name="discoveredPrinters">Discovered printer(s) to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of registered printers.</returns>
    /// <response code="200">Successfully registered discovered printer(s).</response>
    /// <response code="400">Invalid printer data.</response>
    /// <response code="500">Server error.</response>
    [Authorize(Roles = "farm_admin")]
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

        List<PrinterDto> registered = [];

        foreach (DiscoveredPrinterDto discovered in printers)
        {
            try
            {
                _logger.LogInformation(
                    "Processing discovered printer: {DiscoveredName} ({IpAddress}:{Port}) - Backend: {Backend}",
                    discovered.Name, discovered.IpAddress, discovered.BackendPort, discovered.Backend);

                // Check if printer already exists by IP address
                Printer? existing = await _printersService.FindByServerUrlAsync(discovered.ServerUrl, ct);

                if (existing != null)
                {
                    _logger.LogInformation("Printer already registered: {ExistingName} (ServerUrl: {ExistingServerUrl})", existing.Name, existing.ServerUrl);
                    PrinterDto existingDto = await _printersService.GetPrinterDtoAsync(existing.Id, ct);
                    if (existingDto != null)
                    {
                        registered.Add(existingDto);
                    }

                    continue;
                }

                // Create new printer from discovered data, preserving all discovered metadata
                CreatePrinterFromDiscoveryDto createDto = CreatePrinterFromDiscoveryDto.FromDiscovered(discovered);

                ValidationResult validationResult = await _validator.ValidateAsync(createDto, ct);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning(
                        "Discovered printer validation failed: {Value0}", string.Join("; ", validationResult.Errors.Select(e => e.ErrorMessage)));
                    continue;
                }

                // Create the printer
                PrinterDto created = await _printersService.CreatePrinterFromDtoAsync(createDto, ct);
                registered.Add(created);

                _logger.LogInformation("Successfully registered discovered printer: {CreatedName}", created.Name);

                // Import slicer profiles for this printer's model (pull-based, on-demand import)
                if (_profileImportService is not null && createDto.ModelId.HasValue && createDto.ModelId.Value != Guid.Empty)
                {
                    try
                    {
                        int imported = await _profileImportService.ImportProfilesForModelAsync(
                            createDto.ModelId.Value,
                            createDto.NewModelName ?? created.ModelName ?? "Unknown",
                            createDto.NewManufacturerName ?? created.ManufacturerName ?? "Unknown",
                            ct);

                        if (imported > 0)
                        {
                            _logger.LogInformation("Imported {Imported} slicer profiles for {CreatedModelName}", imported, created.ModelName);
                        }
                    }
                    catch (Exception profileEx)
                    {
                        _logger.LogWarning("Failed to import profiles for {CreatedModelName}: {ProfileExMessage}", created.ModelName, profileEx.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register discovered printer: {DiscoveredName}", discovered.Name);

                // Continue with next printer on error
            }
        }

        return Ok(registered);
    }

    /// <summary>
    /// Sets the maintenance mode for a printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="inMaintenance">True to enable maintenance mode, false to disable.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The updated printer DTO.</returns>
    /// <response code="200">Returns the updated printer.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error updating the printer.</response>
    [Authorize(Roles = "farm_admin")]
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
            BackendPort: printer.BackendPort,
            FrontendPort: printer.FrontendPort,
            BackendUrl: printer.BackendUrl,
            FrontendUrl: printer.FrontendUrl);
        return Ok(dto);
    }

    /// <summary>
    /// Refreshes camera URLs for a printer by querying the backend API.
    /// Use this to update camera configuration after adding or modifying cameras on a printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The updated printer DTO with refreshed camera URLs.</returns>
    /// <response code="200">Returns the updated printer with camera URLs.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error refreshing camera URLs.</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("{id:guid}/refresh-cameras")]
    [ProducesResponseType(typeof(PrinterDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterDto>> RefreshCameraUrlsAsync(Guid id, CancellationToken ct)
    {
        try
        {
            PrinterDto? result = await _printersService.RefreshCameraUrlsAsync(id, ct);
            return result == null ? NotFound() : Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh camera URLs for printer {Id}", id);
            return StatusCode(500, "Failed to refresh camera URLs");
        }
    }

    /// <summary>
    /// Applies template defaults from the printer's associated model.
    /// Copies hardware specifications (build volume, max temps, supported materials, etc.)
    /// from the PrinterModel template to the printer, overwriting existing values.
    /// Useful for backfilling data on existing printers that were created before template support.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The updated printer with applied template values.</returns>
    /// <response code="200">Template applied successfully.</response>
    /// <response code="404">If the printer was not found.</response>
    /// <response code="500">If there was an error applying the template.</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("{id:guid}/apply-template")]
    [ProducesResponseType(typeof(PrinterDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterDto>> ApplyModelTemplateAsync(Guid id, CancellationToken ct)
    {
        try
        {
            // Use FindByIdForTemplateUpdateAsync to get printer with Toolheads and tracking enabled
            Printer? printer = await _printersService.FindByIdForTemplateUpdateAsync(id, ct);
            if (printer == null)
            {
                return NotFound();
            }

            bool updated = await _printersService.ApplyModelTemplateAsync(printer, forceOverwrite: true, ct);

            if (updated)
            {
                await _printersService.SaveChangesAsync(ct);
            }

            PrinterDto dto = await _printersService.GetPrinterDtoAsync(id, ct) ?? throw new InvalidOperationException("Printer not found after update");
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply model template for printer {Id}", id);
            return StatusCode(500, "Failed to apply model template");
        }
    }

    /// <summary>
    /// Applies template defaults from printer models to all printers.
    /// Overwrites existing values with template defaults.
    /// Useful for backfilling data on existing printers that were created before template support.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Summary of how many printers were updated.</returns>
    /// <response code="200">Templates applied successfully.</response>
    /// <response code="500">If there was an error applying templates.</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("apply-templates")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> ApplyModelTemplatesToAllAsync(CancellationToken ct)
    {
        try
        {
            // Use GetAllForTemplateUpdateAsync to get printers with Toolheads and tracking enabled
            List<Printer> allPrinters = await _printersService.GetAllForTemplateUpdateAsync(ct);
            int updatedCount = 0;
            int totalCount = 0;

            foreach (Printer printer in allPrinters)
            {
                totalCount++;
                bool updated = await _printersService.ApplyModelTemplateAsync(printer, forceOverwrite: true, ct);
                if (updated)
                {
                    updatedCount++;
                }
            }

            await _printersService.SaveChangesAsync(ct);

            _logger.LogInformation("Applied model templates to {UpdatedCount}/{TotalCount} printers", updatedCount, totalCount);
            return Ok(new { updated = updatedCount, total = totalCount, message = $"Applied templates to {updatedCount} printers" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply model templates to all printers");
            return StatusCode(500, "Failed to apply model templates");
        }
    }

    /// <summary>
    /// Updates an existing printer configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the printer to update.</param>
    /// <param name="dto">The updated printer data.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The updated printer.</returns>
    /// <response code="200">Returns the updated printer.</response>
    /// <response code="400">If the update data is invalid.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error updating the printer.</response>
    [Authorize(Roles = "farm_admin")]
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(PrinterDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterDto>> UpdateAsync(Guid id, [FromBody] UpdatePrinterDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        // Use FindByIdForTemplateUpdateAsync to load printer with Toolheads for updating
        Printer? p = await _printersService.FindByIdForTemplateUpdateAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        // Capture decrypted credentials BEFORE any modifications to avoid phantom changes
        // from PopulateCredential's decrypt → EncryptSensitiveFieldsOnTrackedEntities's re-encrypt cycle
        string? originalApiKey = p.ApiKey;
        string? originalPassword = p.Password;
        string? originalUsername = p.Username;

        // resolve or create manufacturer/model
        Guid manufacturerId = dto.ManufacturerId ?? p.ManufacturerId;
        if (dto.ManufacturerId is null && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            string name = dto.NewManufacturerName!.Trim();

            ManufacturerDto created = await _catalogService.CreateManufacturerAsync(name, null, null, ct);
            manufacturerId = created.Id;
        }

        Guid modelId = dto.ModelId ?? p.ModelId;
        if (dto.ModelId is null && !string.IsNullOrWhiteSpace(dto.NewModelName) && manufacturerId != Guid.Empty)
        {
            string mname = dto.NewModelName!.Trim();
            CreateModelRequest createReq = new CreateModelRequest(
                ManufacturerId: manufacturerId,
                Name: mname,
                MotionType: null,
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
            (Guid defaultManufacturerId, Guid defaultModelId) = await _catalogService.GetDefaultCatalogIdsAsync(ct);

            if (manufacturerId == Guid.Empty)
            {
                manufacturerId = defaultManufacturerId;
            }

            if (modelId == Guid.Empty)
            {
                modelId = defaultModelId;
            }
        }

        // Only update Name if provided and different
        if (!string.IsNullOrWhiteSpace(dto.Name) && dto.Name != p.Name)
        {
            p.Name = dto.Name;
        }

        // Only resolve hostname if a new ServerUrl is provided
        if (!string.IsNullOrWhiteSpace(dto.ServerUrl))
        {
            // Delegate normalization and optional hostname resolution to the PrintersService
            PrinterBackend backendForResolve = dto.Backend ?? (PrinterBackend)p.Backend;
            ResolveHostnameResponse resolveResp = await _printersService.ResolveHostnameAsync(dto.ServerUrl, backendForResolve, ct);
            p.ServerUrl = resolveResp.ResolvedBaseUrl ?? resolveResp.NormalizedInputUrl;
            p.OriginalServerUrl = resolveResp.NormalizedInputUrl;
        }

        // Track if model changed for template application
        bool modelChanged = modelId != p.ModelId;

        // Only update Notes if different
        if (dto.Notes != p.Notes)
        {
            p.Notes = dto.Notes;
        }

        // Only update manufacturer/model if changed
        if (manufacturerId != p.ManufacturerId)
        {
            p.ManufacturerId = manufacturerId;
        }

        if (modelId != p.ModelId)
        {
            p.ModelId = modelId;
        }

        // Only update DateAcquired if provided and different
        if (dto.DateAcquired.HasValue)
        {
            DateTime normalizedDate = dto.DateAcquired.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dto.DateAcquired.Value, DateTimeKind.Utc)
                : dto.DateAcquired.Value;

            if (normalizedDate != p.DateAcquired)
            {
                p.DateAcquired = normalizedDate;
            }
        }

        // Only update Backend if provided and different
        if (dto.Backend.HasValue && (int)dto.Backend.Value != p.Backend)
        {
            p.Backend = (int)dto.Backend.Value;
        }

        // If model changed, apply template defaults from the new model (don't overwrite user-customized values)
        if (modelChanged)
        {
            await _printersService.ApplyModelTemplateAsync(p, forceOverwrite: false, ct);
        }

        // Update credentials only if provided and different from decrypted originals
        if (!string.IsNullOrEmpty(dto.ApiKey) && dto.ApiKey != originalApiKey)
        {
            p.ApiKey = dto.ApiKey;
        }

        if (!string.IsNullOrEmpty(dto.Username) && dto.Username != originalUsername)
        {
            p.Username = dto.Username;
        }

        if (!string.IsNullOrEmpty(dto.Password) && dto.Password != originalPassword)
        {
            p.Password = dto.Password;
        }

        // Update port settings only if provided and different
        if (dto.BackendPort.HasValue && dto.BackendPort.Value != p.BackendPort)
        {
            p.BackendPort = dto.BackendPort.Value;
        }

        if (dto.FrontendPort.HasValue && dto.FrontendPort.Value != p.FrontendPort)
        {
            p.FrontendPort = dto.FrontendPort.Value;
        }

        // Update IsEnabled only if provided and different
        if (dto.IsEnabled.HasValue && dto.IsEnabled.Value != p.IsEnabled)
        {
            p.IsEnabled = dto.IsEnabled.Value;
        }

        // Update Obico monitoring opt-in (requires camera, auto-assigns server)
        if (dto.ObicoEnabled.HasValue && dto.ObicoEnabled.Value != p.ObicoEnabled)
        {
            if (dto.ObicoEnabled.Value)
            {
                // Enabling: validate camera exists
                bool hasCamera = p.Cameras != null && p.Cameras.Count != 0;
                if (!hasCamera)
                {
                    return BadRequest(new { error = "Obico monitoring requires at least one camera configured on the printer." });
                }

                p.ObicoEnabled = true;

                // Auto-assign to best available server
                ObicoServer? assigned = await _obicoServerAssignment.AssignServerAsync(p.Id, ct);
                if (assigned is null)
                {
                    ObicoSettings currentObicoSettings = _settingsService.Get<ObicoSettings>();
                    if (!currentObicoSettings.Enabled || string.IsNullOrWhiteSpace(currentObicoSettings.ObicoApiUrl))
                    {
                        return BadRequest(new
                        {
                            error = "No available Obico ML configuration. Open Settings > Obico Failure Detection to add a pooled Obico ML server or configure the global fallback."
                        });
                    }

                    _logger.LogInformation(
                        "[PRINTERS] No pooled Obico server available for printer {PrinterName}; using global Obico Failure Detection settings fallback",
                        p.Name);
                }
            }
            else
            {
                // Disabling: unassign server
                p.ObicoEnabled = false;
                await _obicoServerAssignment.UnassignServerAsync(p.Id, ct);
            }
        }

        // Track if any capability field changed to conditionally update LastCapabilityUpdate
        bool capabilityChanged = false;

        // Update hardware specs only if provided and different
        // Use epsilon comparison for floating-point values
        const double epsilon = 0.001;
        if (dto.MaxBuildVolumeX.HasValue && (!p.MaxBuildVolumeX.HasValue || Math.Abs(dto.MaxBuildVolumeX.Value - p.MaxBuildVolumeX.Value) > epsilon))
        {
            p.MaxBuildVolumeX = dto.MaxBuildVolumeX.Value;
            capabilityChanged = true;
        }

        if (dto.MaxBuildVolumeY.HasValue && (!p.MaxBuildVolumeY.HasValue || Math.Abs(dto.MaxBuildVolumeY.Value - p.MaxBuildVolumeY.Value) > epsilon))
        {
            p.MaxBuildVolumeY = dto.MaxBuildVolumeY.Value;
            capabilityChanged = true;
        }

        if (dto.MaxBuildVolumeZ.HasValue && (!p.MaxBuildVolumeZ.HasValue || Math.Abs(dto.MaxBuildVolumeZ.Value - p.MaxBuildVolumeZ.Value) > epsilon))
        {
            p.MaxBuildVolumeZ = dto.MaxBuildVolumeZ.Value;
            capabilityChanged = true;
        }

        if (dto.HasHeatedBed.HasValue && dto.HasHeatedBed.Value != p.HasHeatedBed)
        {
            p.HasHeatedBed = dto.HasHeatedBed.Value;
            capabilityChanged = true;
        }

        if (dto.HasEnclosure.HasValue && dto.HasEnclosure.Value != p.HasEnclosure)
        {
            p.HasEnclosure = dto.HasEnclosure.Value;
            capabilityChanged = true;
        }

        // Detect MultiMaterial toggle for MmuGate toolhead sync
        bool wasMultiMaterial = p.MultiMaterial;
        if (dto.MultiMaterial.HasValue && dto.MultiMaterial.Value != p.MultiMaterial)
        {
            p.MultiMaterial = dto.MultiMaterial.Value;
            capabilityChanged = true;
        }

        if (wasMultiMaterial != p.MultiMaterial)
        {
            _printersService.SyncMmuToolheadsOnEntity(p, wasMultiMaterial);
        }

        if (dto.SupportsAutoLeveling.HasValue && dto.SupportsAutoLeveling.Value != p.SupportsAutoLeveling)
        {
            p.SupportsAutoLeveling = dto.SupportsAutoLeveling.Value;
            capabilityChanged = true;
        }

        if (dto.MaxBedTemp.HasValue && dto.MaxBedTemp.Value != p.MaxBedTemp)
        {
            p.MaxBedTemp = dto.MaxBedTemp.Value;
            capabilityChanged = true;
        }

        if (dto.MaxPrintSpeed.HasValue && dto.MaxPrintSpeed.Value != p.MaxPrintSpeed)
        {
            p.MaxPrintSpeed = dto.MaxPrintSpeed.Value;
            capabilityChanged = true;
        }

        if (dto.Wattage.HasValue && dto.Wattage.Value != p.Wattage)
        {
            p.Wattage = dto.Wattage.Value;
            capabilityChanged = true;
        }

        if (dto.MachineHourlyRate.HasValue && dto.MachineHourlyRate.Value != p.MachineHourlyRate)
        {
            p.MachineHourlyRate = dto.MachineHourlyRate.Value;
        }

        if (dto.ZOffsetMm.HasValue && dto.ZOffsetMm.Value != p.ZOffsetMm)
        {
            p.ZOffsetMm = dto.ZOffsetMm.Value;
            p.LastZOffsetCalibrationAt = DateTime.UtcNow;
        }

        if (dto.BedTypeId.HasValue && dto.BedTypeId.Value != p.BedTypeId)
        {
            if (dto.BedTypeId.Value == Guid.Empty)
            {
                p.BedTypeId = null;
            }
            else
            {
                var bedType = await _bedTypeService.GetByIdAsync(dto.BedTypeId.Value, ct);
                if (bedType is null)
                {
                    return BadRequest(new { error = $"Bed type '{dto.BedTypeId.Value}' not found" });
                }

                p.BedTypeId = dto.BedTypeId.Value;
            }
        }

        // Only update LastCapabilityUpdate if capability fields actually changed
        if (capabilityChanged)
        {
            // Ensure ServiceState exists before updating LastCapabilityUpdate
            if (p.ServiceState == null)
            {
                p.ServiceState = new PrinterServiceState { PrinterId = p.Id };
            }

            p.ServiceState.LastCapabilityUpdate = DateTime.UtcNow;
        }

        if (dto.UseModelDispatchDefaults.HasValue)
        {
            p.UseModelDispatchDefaults = dto.UseModelDispatchDefaults.Value;
        }

        // Update toolheads if provided
        if (dto.Toolheads?.Length > 0 && p.Toolheads != null)
        {
            foreach (UpdateToolheadDto toolheadDto in dto.Toolheads)
            {
                Toolhead? toolhead = p.Toolheads.FirstOrDefault(t => t.Id == toolheadDto.Id);
                if (toolhead != null)
                {
                    bool toolheadChanged = false;

                    if (toolheadDto.Name != null && toolheadDto.Name != toolhead.Name)
                    {
                        toolhead.Name = toolheadDto.Name;
                        toolheadChanged = true;
                    }

                    if (toolheadDto.Index.HasValue && toolheadDto.Index.Value != toolhead.Index)
                    {
                        toolhead.Index = toolheadDto.Index.Value;
                        toolheadChanged = true;
                    }

                    // Component model references - only update if different
                    if (toolheadDto.HotendModelId.HasValue && toolheadDto.HotendModelId != toolhead.HotendModelId)
                    {
                        toolhead.HotendModelId = toolheadDto.HotendModelId;
                        toolheadChanged = true;
                    }

                    if (toolheadDto.ExtruderModelId.HasValue && toolheadDto.ExtruderModelId != toolhead.ExtruderModelId)
                    {
                        toolhead.ExtruderModelId = toolheadDto.ExtruderModelId;
                        toolheadChanged = true;
                    }

                    if (toolheadDto.ToolheadModelDefId.HasValue && toolheadDto.ToolheadModelDefId != toolhead.ToolheadModelDefId)
                    {
                        toolhead.ToolheadModelDefId = toolheadDto.ToolheadModelDefId;
                        toolheadChanged = true;
                    }

                    if (toolheadDto.NozzleModelId.HasValue && toolheadDto.NozzleModelId != toolhead.NozzleModelId)
                    {
                        toolhead.NozzleModelId = toolheadDto.NozzleModelId;
                        toolheadChanged = true;
                    }

                    if (toolheadDto.SupportedMaterials != null && toolheadDto.SupportedMaterials != toolhead.SupportedMaterials)
                    {
                        toolhead.SupportedMaterials = toolheadDto.SupportedMaterials;
                        toolheadChanged = true;
                    }

                    if (toolheadDto.IsPrimary.HasValue && toolheadDto.IsPrimary.Value != toolhead.IsPrimary)
                    {
                        toolhead.IsPrimary = toolheadDto.IsPrimary.Value;
                        toolheadChanged = true;
                    }

                    if (toolheadChanged)
                    {
                        toolhead.UpdatedAt = DateTime.UtcNow;
                    }
                }
            }
        }
        else
        {
            // Legacy: Update primary toolhead specs if no explicit toolheads array provided
            Toolhead? primaryToolhead = p.Toolheads?.FirstOrDefault(t => t.IsPrimary);
            if (primaryToolhead != null)
            {
                if (dto.SupportedMaterials != null && dto.SupportedMaterials != primaryToolhead.SupportedMaterials)
                {
                    primaryToolhead.SupportedMaterials = dto.SupportedMaterials;
                    primaryToolhead.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

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

        // Auto-create/update/remove Buddy camera when BuddyCameraIp changes
        if (dto.BuddyCameraIp != null)
        {
            string ip = dto.BuddyCameraIp.Trim();

            if (ip.Length == 0)
            {
                // Empty string = explicit clear request; skip validation and remove the camera.
                await _printersService.SyncBuddyCameraAsync(p, ip, ct);
            }
            else
            {
                // Validate: must be a plain hostname or IP with no embedded separators or control chars
                bool hasInvalidChar = ip.Any(c =>
                    c == ':' || c == '/' || c == '\\' || c == '@' || c == '?' || c == '#'
                    || char.IsControl(c) || char.IsWhiteSpace(c));

                bool isValidHost = !hasInvalidChar &&
                    (IPAddress.TryParse(ip, out _) ||
                     Uri.CheckHostName(ip) == UriHostNameType.Dns);

                if (!isValidHost)
                {
                    return BadRequest("Invalid BuddyCameraIp: must be a plain IP address or hostname.");
                }

                await _printersService.SyncBuddyCameraAsync(p, ip, ct);
            }
        }

        // Save all changes (printer + toolhead updates) with concurrency retry.
        // Background polling services may update the same printer row (e.g. status, temps),
        // which changes the RowVersion. The retry reloads the token and re-saves.
        await _printersService.SaveChangesWithRetryAsync(ct);

        PrinterDto dtoResponse = new(
            Id: p.Id,
            Name: p.Name,
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
            BackendPort: p.BackendPort,
            FrontendPort: p.FrontendPort,
            BackendUrl: p.BackendUrl,
            FrontendUrl: p.FrontendUrl,
            ObicoEnabled: p.ObicoEnabled,
            UseModelDispatchDefaults: p.UseModelDispatchDefaults);

        return Ok(dtoResponse);
    }

    /// <summary>
    /// Resolves a hostname to an IP address for printer configuration.
    /// </summary>
    /// <param name="body">The hostname resolution request containing the server URL and backend type.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The resolved IP address and normalized URL.</returns>
    /// <response code="200">Returns the resolved hostname information.</response>
    /// <response code="400">If the hostname resolution fails or URL is invalid.</response>
    /// <response code="500">If there was an error during hostname resolution.</response>
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
    /// <param name="modelId">The unique identifier of the printer model.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Default printer capabilities based on the model.</returns>
    /// <response code="200">Returns the default capabilities for the model.</response>
    /// <response code="404">If the model with the specified ID was not found.</response>
    /// <response code="204">If no default capabilities are available for the model.</response>
    /// <response code="500">If there was an error retrieving the capabilities.</response>
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
        // Nozzle diameter and max hotend temp are derived from toolhead component models
        try
        {
            // Get derived values from the primary toolhead (or first toolhead)
            PrinterModelToolheadDto? primaryToolhead = modelDto.Toolheads?.FirstOrDefault(t => t.IsPrimary) ?? modelDto.Toolheads?.FirstOrDefault();
            double? nozzleDiameter = primaryToolhead?.NozzleDiameter;
            int? maxHotendTemp = primaryToolhead?.MaxTemp;  // Derived from HotendModel

            bool hasCapabilityData = modelDto.MaxX.HasValue || modelDto.MaxY.HasValue || modelDto.MaxZ.HasValue ||
                                     nozzleDiameter.HasValue || maxHotendTemp.HasValue ||
                                     modelDto.MaxBedTemp.HasValue || (modelDto.SupportedFilamentTypes != null && modelDto.SupportedFilamentTypes.Length > 0);

            if (!hasCapabilityData)
            {
                return NoContent();
            }

            PrinterCapabilitiesDto dto = new(
                Id: Guid.Empty,
                PrinterId: Guid.Empty,
                PrinterName: modelDto.Name,
                NozzleDiameter: nozzleDiameter,
                SupportedMaterials: modelDto.SupportedFilamentTypes ?? Array.Empty<string>(),
                MaxBuildVolumeX: modelDto.MaxX,
                MaxBuildVolumeY: modelDto.MaxY,
                MaxBuildVolumeZ: modelDto.MaxZ,
                HasHeatedBed: modelDto.HasHeatedBed,
                HasEnclosure: modelDto.HasEnclosure,
                MultiMaterial: modelDto.MultiMaterial,
                MaxHotendTemp: maxHotendTemp,  // Derived from primary toolhead's HotendModel.MaxTemp
                MaxBedTemp: modelDto.MaxBedTemp,
                CurrentMaterial: null,
                CurrentSpoolId: null,
                IsAvailable: true,
                LastUpdated: DateTime.UtcNow);

            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving default capabilities for model {ModelId}", modelId);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve model capabilities");
        }
    }

    /// <summary>
    /// Deletes a printer configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the printer to delete.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>No content if successful.</returns>
    /// <response code="204">If the printer was successfully deleted.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error deleting the printer.</response>
    [Authorize(Roles = "farm_admin")]
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
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The camera snapshot as an image file.</returns>
    /// <response code="200">Returns the snapshot image.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="503">If the camera is not available or configured.</response>
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
    /// Homes all axes (X, Y, Z) of the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure of the homing operation.</returns>
    /// <response code="200">Returns the command execution result.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error executing the homing command.</response>
    [HttpPost("{id:guid}/home")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> HomeAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.SendHomeAsync(id, ct);
        _telemetryService.RecordPrinterOperation("home_all", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Homes the X and Y axes of the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure of the homing operation.</returns>
    /// <response code="200">Returns the command execution result.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error executing the homing command.</response>
    [HttpPost("{id:guid}/homexy")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> HomeXYAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.HomeXYAsync(id, ct);
        _telemetryService.RecordPrinterOperation("home_xy", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Homes the Z axis of the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure of the Z-axis homing operation.</returns>
    /// <response code="200">Returns the command execution result.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error executing the homing command.</response>
    [HttpPost("{id:guid}/homez")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> HomeZAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.HomeZAsync(id, ct);
        _telemetryService.RecordPrinterOperation("home_z", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    [HttpPost("{id:guid}/temps")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> SetTempsAsync(Guid id, [FromBody] Farm.Infrastructure.TempTargets targets, CancellationToken ct)
    {
        if (targets is null)
        {
            return BadRequest("Request body is required.");
        }

        bool ok = await _printersService.SetTempsAsync(id, targets.Hotend, targets.Bed, ct);
        _telemetryService.RecordPrinterOperation("set_temperature", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
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
        _telemetryService.RecordPrinterOperation("move", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
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
        _telemetryService.RecordPrinterOperation("move_to", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    [HttpPost("{id:guid}/pause")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> PauseAsync(Guid id, CancellationToken ct)
    {
        Printer? printer = await _printersService.FindByIdAsync(id, ct);
        if (printer is null)
        {
            return NotFound(new CommandResult(false, "Printer not found."));
        }

        bool ok = await _printersService.PauseAsync(id, ct);
        _telemetryService.RecordPrinterOperation("pause", id.ToString(), ok);

        return ok
            ? new CommandResult(true, null)
            : StatusCode(
                StatusCodes.Status502BadGateway,
                new CommandResult(false, "Pause failed. Printer may be offline or backend does not support pausing."));
    }

    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> ResumeAsync(Guid id, CancellationToken ct)
    {
        Printer? printer = await _printersService.FindByIdAsync(id, ct);
        if (printer is null)
        {
            return NotFound(new CommandResult(false, "Printer not found."));
        }

        bool ok = await _printersService.ResumeAsync(id, ct);
        _telemetryService.RecordPrinterOperation("resume", id.ToString(), ok);

        return ok
            ? new CommandResult(true, null)
            : StatusCode(
                StatusCodes.Status502BadGateway,
                new CommandResult(false, "Resume failed. Printer may be offline or backend does not support resuming."));
    }

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> CancelAsync(Guid id, CancellationToken ct)
    {
        Printer? printer = await _printersService.FindByIdAsync(id, ct);
        if (printer is null)
        {
            return NotFound(new CommandResult(false, "Printer not found."));
        }

        bool ok = await _printersService.CancelPrintAsync(id, ct);
        _telemetryService.RecordPrinterOperation("cancel", id.ToString(), ok);

        return ok
            ? new CommandResult(true, null)
            : StatusCode(
                StatusCodes.Status502BadGateway,
                new CommandResult(false, "Cancel failed. Printer may be offline or backend does not support cancel."));
    }

    [HttpPost("{id:guid}/emergency-stop")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> EmergencyStopAsync(Guid id, CancellationToken ct)
    {
        Printer? printer = await _printersService.FindByIdAsync(id, ct);
        if (printer is null)
        {
            return NotFound(new CommandResult(false, "Printer not found."));
        }

        bool ok = await _printersService.EmergencyStopAsync(id, ct);
        _telemetryService.RecordPrinterOperation("emergency_stop", id.ToString(), ok);

        return ok
            ? new CommandResult(true, null)
            : StatusCode(
                StatusCodes.Status502BadGateway,
                new CommandResult(false, "Emergency stop failed. Printer may be offline or backend does not support stop."));
    }

    /// <summary>
    /// Stops the print on the specified printer (alias for emergency-stop for frontend compatibility).
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure of the stop operation.</returns>
    /// <response code="200">Returns the command execution result.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error executing the stop command.</response>
    /// <remarks>
    /// This endpoint is an alias for /emergency-stop provided for frontend compatibility.
    /// Both endpoints execute the same emergency-stop operation.
    /// </remarks>
    [HttpPost("{id:guid}/stop")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> StopAsync(Guid id, CancellationToken ct)
    {
        // Alias for emergency-stop for compatibility with frontend
        return await EmergencyStopAsync(id, ct);
    }

    /// <summary>
    /// Restarts the firmware/MCU of the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure of the firmware restart operation.</returns>
    /// <response code="200">Returns the command execution result.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error executing the restart command.</response>
    /// <remarks>
    /// Restarts the printer's firmware/MCU without a full power cycle.
    /// This operation is typically used to recover from firmware issues.
    /// </remarks>
    [HttpPost("{id:guid}/firmware-restart")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> FirmwareRestartAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.FirmwareRestartAsync(id, ct);
        _telemetryService.RecordPrinterOperation("firmware_restart", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Disables the stepper motors of the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure of the disable motors operation.</returns>
    /// <response code="200">Returns the command execution result.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error executing the disable motors command.</response>
    /// <remarks>
    /// Disables all stepper motors, allowing manual movement of printer axes.
    /// Motors will remain disabled until explicitly re-enabled via homing or other operations.
    /// </remarks>
    [HttpPost("{id:guid}/disable-motors")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> DisableMotorsAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.DisableMotorsAsync(id, ct);
        _telemetryService.RecordPrinterOperation("disable_motors", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Sends a raw G-code command to the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="request">The G-code command request containing the script to execute.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure of the G-code command.</returns>
    /// <response code="200">Returns the command execution result.</response>
    /// <response code="400">If the request body is missing or the gcode string is empty.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error sending the G-code command.</response>
    /// <remarks>
    /// Sends arbitrary G-code commands to the printer firmware.
    /// Commonly used for Klipper macros (LOAD_FILAMENT, UNLOAD_FILAMENT) and standard commands (M600).
    /// Requires the backend to support G-code execution capability.
    /// </remarks>
    [HttpPost("{id:guid}/gcode")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> SendGcodeAsync(Guid id, [FromBody] GcodeCommandRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Command))
        {
            return BadRequest(new CommandResult(false, "G-code command is required."));
        }

        bool ok = await _printersService.SendGcodeAsync(id, request.Command.Trim(), ct);
        _telemetryService.RecordPrinterOperation("send_gcode", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    // Z-offset calibration endpoint

    /// <summary>
    /// Saves the calibrated Z-offset for a printer.
    /// Persists the value to the database and optionally sends save commands to the printer firmware.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="request">The Z-offset save request containing the offset value.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure.</returns>
    /// <response code="200">Z-offset saved successfully.</response>
    /// <response code="400">If the offset value is out of range.</response>
    /// <response code="404">If the printer was not found.</response>
    [HttpPost("{id:guid}/z-offset")]
    [Authorize(Roles = "farm_admin")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> SaveZOffsetAsync(Guid id, [FromBody] ZOffsetSaveRequest request, CancellationToken ct)
    {
        if (request.OffsetMm is null)
        {
            return BadRequest(new CommandResult(false, "offsetMm is required"));
        }

        decimal offsetMm = request.OffsetMm.Value;
        Printer? p = await _printersService.FindByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        // Send save commands to the printer firmware and verify success
        if (request.SaveToFirmware)
        {
            PrinterBackend backend = (PrinterBackend)p.Backend;
            string saveCommands = backend switch
            {
                PrinterBackend.Moonraker => $"SET_GCODE_OFFSET Z={offsetMm:F3}\nSAVE_CONFIG",
                _ => $"M851 Z{offsetMm:F3}\nM500"
            };

            foreach (string cmd in saveCommands.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                bool sent = await _printersService.SendGcodeAsync(id, cmd.Trim(), ct);
                if (!sent)
                {
                    _telemetryService.RecordPrinterOperation("save_z_offset", id.ToString(), false);
                    return BadRequest(new CommandResult(false, $"Firmware command failed: {cmd.Trim()}"));
                }
            }
        }

        // Persist the Z-offset to the database only after firmware success
        p.ZOffsetMm = offsetMm;
        p.LastZOffsetCalibrationAt = DateTime.UtcNow;
        await _printersService.SaveChangesAsync(ct);

        _telemetryService.RecordPrinterOperation("save_z_offset", id.ToString(), true);
        return new CommandResult(true, null);
    }

    // Filament control endpoints

    /// <summary>
    /// Loads filament into the extruder.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure with descriptive message.</returns>
    /// <response code="200">Filament load command sent successfully.</response>
    /// <response code="400">If the command failed (backend error, unsupported capability).</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    [HttpPost("{id:guid}/filament-load")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> LoadFilamentAsync(Guid id, CancellationToken ct)
    {
        CommandResult result = await _printersService.LoadFilamentAsync(id, ct);
        _telemetryService.RecordPrinterOperation("load_filament", id.ToString(), result.Success);
        return MapCommandResult(result);
    }

    /// <summary>
    /// Unloads filament from the extruder.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure with descriptive message.</returns>
    /// <response code="200">Filament unload command sent successfully.</response>
    /// <response code="400">If the command failed (backend error, unsupported capability).</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    [HttpPost("{id:guid}/filament-unload")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> UnloadFilamentAsync(Guid id, CancellationToken ct)
    {
        CommandResult result = await _printersService.UnloadFilamentAsync(id, ct);
        _telemetryService.RecordPrinterOperation("unload_filament", id.ToString(), result.Success);
        return MapCommandResult(result);
    }

    /// <summary>
    /// Initiates a filament change procedure (M600).
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure with descriptive message.</returns>
    /// <response code="200">Filament change command sent successfully.</response>
    /// <response code="400">If the command failed (backend error, unsupported capability).</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    [HttpPost("{id:guid}/filament-change")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> ChangeFilamentAsync(Guid id, CancellationToken ct)
    {
        CommandResult result = await _printersService.ChangeFilamentAsync(id, ct);
        _telemetryService.RecordPrinterOperation("change_filament", id.ToString(), result.Success);
        return MapCommandResult(result);
    }

    // ── MMU (Multi-Material Unit) control endpoints ──

    /// <summary>
    /// Selects and loads a specific MMU tool (gate) with filament change.
    /// Sends MMU_CHANGE_TOOL TOOL=N to the printer via Happy Hare.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="tool">The tool/gate index to select (0-based).</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <response code="200">MMU tool change command sent successfully.</response>
    /// <response code="400">If the command failed.</response>
    /// <response code="404">If the printer was not found.</response>
    [HttpPost("{id:guid}/mmu/change-tool/{tool:int}")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> MmuChangeToolAsync(Guid id, int tool, CancellationToken ct)
    {
        if (tool < 0 || tool > 16)
        {
            return BadRequest(new CommandResult(false, "Tool index must be between 0 and 16."));
        }

        bool ok = await _printersService.SendGcodeAsync(id, $"MMU_CHANGE_TOOL TOOL={tool}", ct);
        _telemetryService.RecordPrinterOperation("mmu_change_tool", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Ejects/unloads filament from the MMU.
    /// Sends MMU_EJECT to the printer via Happy Hare.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpPost("{id:guid}/mmu/eject")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> MmuEjectAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.SendGcodeAsync(id, "MMU_EJECT", ct);
        _telemetryService.RecordPrinterOperation("mmu_eject", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Loads filament from the currently selected MMU gate into the extruder.
    /// Sends MMU_LOAD to the printer via Happy Hare.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpPost("{id:guid}/mmu/load")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> MmuLoadAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.SendGcodeAsync(id, "MMU_LOAD", ct);
        _telemetryService.RecordPrinterOperation("mmu_load", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Homes the MMU unit.
    /// Sends MMU_HOME to the printer via Happy Hare.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpPost("{id:guid}/mmu/home")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> MmuHomeAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.SendGcodeAsync(id, "MMU_HOME", ct);
        _telemetryService.RecordPrinterOperation("mmu_home", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Pre-selects an MMU tool without loading filament.
    /// Sends MMU_SELECT_TOOL TOOL=N to the printer via Happy Hare.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="tool">The tool/gate index to pre-select (0-based).</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpPost("{id:guid}/mmu/select-tool/{tool:int}")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> MmuSelectToolAsync(Guid id, int tool, CancellationToken ct)
    {
        if (tool < 0 || tool > 16)
        {
            return BadRequest(new CommandResult(false, "Tool index must be between 0 and 16."));
        }

        bool ok = await _printersService.SendGcodeAsync(id, $"MMU_SELECT_TOOL TOOL={tool}", ct);
        _telemetryService.RecordPrinterOperation("mmu_select_tool", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Recovers the MMU from an error state.
    /// Sends MMU_RECOVER to the printer via Happy Hare.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpPost("{id:guid}/mmu/recover")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> MmuRecoverAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.SendGcodeAsync(id, "MMU_RECOVER", ct);
        _telemetryService.RecordPrinterOperation("mmu_recover", id.ToString(), ok);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Sets or clears the active Spoolman spool for a printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="request">The spool ID to set, or null/omitted to clear.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure with descriptive message.</returns>
    /// <response code="200">Spool was set or cleared successfully.</response>
    /// <response code="400">If the request failed (backend error, Spoolman not configured, invalid spool ID).</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    [HttpPost("{id:guid}/active-spool")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> SetActiveSpoolAsync(Guid id, [FromBody] SetActiveSpoolRequest? request, CancellationToken ct)
    {
        CommandResult result = await _printersService.SetActiveSpoolAsync(id, request?.SpoolId, ct);
        _telemetryService.RecordPrinterOperation("set_active_spool", id.ToString(), result.Success);
        return MapCommandResult(result);
    }

    /// <summary>
    /// Lists available spools from the Spoolman instance connected to a specific printer's backend.
    /// Routes through the printer's Moonraker proxy so results reflect that printer's Spoolman server,
    /// which may differ from the central Spoolman server configured in PrintFarmer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>List of available spools from the printer's Spoolman instance.</returns>
    /// <response code="200">Returns the list of spools.</response>
    /// <response code="404">If the printer was not found or does not support Spoolman.</response>
    [HttpGet("{id:guid}/spoolman/spools")]
    [ProducesResponseType(typeof(IEnumerable<SpoolmanSpoolDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IEnumerable<SpoolmanSpoolDto>>> GetPrinterSpoolsAsync(Guid id, CancellationToken ct)
    {
        IReadOnlyList<SpoolmanSpoolDto>? spools = await _printersService.ListPrinterSpoolsAsync(id, ct);
        if (spools is null)
        {
            return NotFound();
        }

        return Ok(spools);
    }

    /// <summary>
    /// Assigns a Spoolman spool to a specific toolhead (by index) on a printer.
    /// Fetches spool details from Spoolman to populate material and color information.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="toolheadIndex">Zero-based index of the toolhead (T0, T1, T2, etc.).</param>
    /// <param name="request">Request containing the spool ID to assign.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure with descriptive message.</returns>
    /// <response code="200">Spool was assigned successfully.</response>
    /// <response code="400">If the request failed (invalid spool ID, Spoolman not configured).</response>
    /// <response code="404">If the printer or toolhead was not found.</response>
    [HttpPut("{id:guid}/toolheads/{toolheadIndex:int}/spool")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> SetToolheadSpoolAsync(
        Guid id,
        int toolheadIndex,
        [FromBody] SetActiveSpoolRequest? request,
        CancellationToken ct)
    {
        if (request?.SpoolId is not { } spoolId)
        {
            return BadRequest(new CommandResult(false, "SpoolId is required"));
        }

        CommandResult result = await _printersService.SetToolheadSpoolAsync(id, toolheadIndex, spoolId, ct);
        _telemetryService.RecordPrinterOperation("set_toolhead_spool", id.ToString(), result.Success);
        return MapCommandResult(result);
    }

    /// <summary>
    /// Clears the spool assignment from a specific toolhead (by index) on a printer.
    /// Removes the spool ID, material, and color information.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="toolheadIndex">Zero-based index of the toolhead (T0, T1, T2, etc.).</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure with descriptive message.</returns>
    /// <response code="200">Spool was cleared successfully.</response>
    /// <response code="404">If the printer or toolhead was not found.</response>
    [HttpDelete("{id:guid}/toolheads/{toolheadIndex:int}/spool")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> ClearToolheadSpoolAsync(
        Guid id,
        int toolheadIndex,
        CancellationToken ct)
    {
        CommandResult result = await _printersService.ClearToolheadSpoolAsync(id, toolheadIndex, ct);
        _telemetryService.RecordPrinterOperation("clear_toolhead_spool", id.ToString(), result.Success);
        return MapCommandResult(result);
    }

    /// <summary>
    /// Ensures MMU virtual toolhead records exist for a multi-material printer.
    /// Creates missing MmuGate rows for legacy printers that predate the multi-toolhead feature.
    /// Idempotent — safe to call repeatedly.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating what happened.</returns>
    /// <response code="200">Sync completed (gates created or already present).</response>
    /// <response code="404">If the printer was not found.</response>
    [HttpPost("{id:guid}/toolheads/ensure-mmu")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> EnsureMmuToolheadsAsync(
        Guid id,
        CancellationToken ct)
    {
        CommandResult result = await _printersService.EnsureMmuToolheadsAsync(id, ct);
        return MapCommandResult(result);
    }

    // Camera control endpoints

    /// <summary>
    /// Enables camera functionality on the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure of the enable camera operation.</returns>
    /// <response code="200">Returns the command execution result.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error enabling the camera.</response>
    /// <remarks>
    /// Enables camera streaming and snapshot functionality on the printer.
    /// Camera must be physically connected and configured in the printer backend for this to work.
    /// </remarks>
    [HttpPost("{id:guid}/camera/enable")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> EnableCameraAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.EnableCameraAsync(id, ct);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Disables camera functionality on the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure of the disable camera operation.</returns>
    /// <response code="200">Returns the command execution result.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="500">If there was an error disabling the camera.</response>
    /// <remarks>
    /// Disables camera streaming and snapshot functionality on the printer.
    /// Can be used to reduce network load or disable camera access temporarily.
    /// </remarks>
    [HttpPost("{id:guid}/camera/disable")]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> DisableCameraAsync(Guid id, CancellationToken ct)
    {
        bool ok = await _printersService.DisableCameraAsync(id, ct);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Retrieves the camera stream and snapshot URLs for the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Object containing stream and snapshot URLs (may be null if camera not supported).</returns>
    /// <response code="200">Returns the camera URLs.</response>
    /// <response code="404">If the printer with the specified ID was not found or camera is not available.</response>
    /// <remarks>
    /// Returns the URLs for live camera streaming and snapshot capture.
    /// Either or both URLs may be null depending on printer capabilities and configuration.
    /// Frontend should validate URL accessibility before attempting to load.
    /// </remarks>
    [HttpGet("{id:guid}/camera/url")]
    [ProducesResponseType(typeof(CameraUrlResult), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CameraUrlResult>> GetCameraUrlAsync(Guid id, CancellationToken ct)
    {
        (string? streamUrl, string? snapshotUrl) = await _printersService.GetCameraUrlsForPrinterAsync(id, ct);
        return streamUrl == null && snapshotUrl == null ? NotFound() : new CameraUrlResult(streamUrl, snapshotUrl);
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

            return !success ? NotFound() : Ok(new UploadGcodeResultDto("File uploaded successfully", file.FileName));
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

    /// <summary>
    /// Downloads a file from a printer's storage.
    /// </summary>
    /// <param name="id">Printer ID.</param>
    /// <param name="filename">The filename to download (filename query parameter).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The requested file as a binary stream with appropriate content type.</returns>
    /// <response code="200">Returns the file content as a downloadable attachment.</response>
    /// <response code="400">The filename query parameter is missing or empty.</response>
    /// <response code="404">The printer with the specified ID was not found, or the file does not exist on the printer.</response>
    /// <response code="500">An error occurred while downloading the file from the printer.</response>
    [HttpGet("{id:guid}/files/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> DownloadFileAsync(Guid id, [FromQuery] string filename, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return BadRequest(new { error = "filename query parameter is required" });
        }

        try
        {
            byte[]? fileContent = await _printersService.DownloadPrinterFileAsync(id, filename, ct);
            if (fileContent == null)
            {
                return NotFound(new { error = $"File not found: {filename}" });
            }

            // Return the file with appropriate content type
            string contentType = "application/octet-stream";
            if (filename.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase) ||
                filename.EndsWith(".gco", StringComparison.OrdinalIgnoreCase))
            {
                contentType = "text/plain"; // Or "application/x-gcode" if needed
            }

            return File(fileContent, contentType, filename);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Printer not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file {Filename} from printer {Id}", filename, id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"Download failed: {ex.Message}" });
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
            return !success
                ? Ok(new CommandResult(false, $"Printer not found or unable to start print for file: {request.FileName}"))
                : Ok(new CommandResult(true, "Print started successfully"));
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
            return !success
                ? Ok(new CommandResult(false, $"Printer not found or unable to delete file: {request.FileName}"))
                : Ok(new CommandResult(true, "File deleted successfully"));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new CommandResult(false, $"Failed to delete file: {ex.Message}"));
        }
    }

    /// <summary>
    /// Retrieves the recent print-session timeline for a single printer.
    /// </summary>
    /// <param name="id">The printer identifier.</param>
    /// <param name="take">Maximum number of sessions to return.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Recent sessions composed from persisted jobs and failure incidents.</returns>
    [HttpGet("{id:guid}/session-timeline")]
    [ProducesResponseType(typeof(PrinterSessionTimelineDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterSessionTimelineDto>> GetSessionTimelineAsync(
        Guid id,
        [FromQuery] int take = Farm.Infrastructure.Services.Printers.PrinterSessionTimelineService.DefaultTake,
        CancellationToken ct = default)
    {
        try
        {
            PrinterSessionTimelineDto timeline = await _printerSessionTimelineService.GetRecentAsync(id, take, ct);
            return Ok(timeline);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get session timeline for printer {Id}: {Message}", id, ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to retrieve printer session timeline" });
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
            _logger.LogError(ex, "Failed to get history for printer {Id}: {Message}", id, ex.Message);
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
            _logger.LogWarning("GetHistoryJob called with null or empty jobId for printer {Id}", id);
            return BadRequest("Job ID is required");
        }
        catch (KeyNotFoundException)
        {
            _logger.LogInformation("History job {JobId} not found for printer {Id}", jobId, id);
            return NotFound($"History job {jobId} not found");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "History requested for non-Moonraker printer {Id}", id);
            return BadRequest("History is only available for Moonraker printers");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("Network error retrieving history job {JobId} for printer {Id}: {Message}", jobId, id, ex.Message);
            return StatusCode(StatusCodes.Status502BadGateway, "Unable to connect to printer");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning("Timeout retrieving history job {JobId} for printer {Id}: {Message}", jobId, id, ex.Message);
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
            _logger.LogError("Failed to get history totals for printer {Id}: {Message}", id, ex.Message);
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
            _logger.LogWarning(ex, "History deletion requested for non-Moonraker printer {Id}", id);
            return BadRequest("History deletion is only available for Moonraker printers");
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to delete history job {JobId} for printer {Id}: {Message}", jobId, id, ex.Message);
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
    /// <param name="ids">Optional array of printer IDs to export; if null, exports all printers.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
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
    /// <param name="ids">Optional array of printer IDs to export; if null, exports all printers.</param>
    /// <param name="format">Export format: 'csv' (default) or 'json'.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpPost("export/file")]
    [ProducesResponseType(200)]
    [ProducesResponseType(500)]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "farm_admin")]
    public async Task<IActionResult> StreamExportAsync([FromBody] Guid[]? ids, [FromQuery] string format = "csv", CancellationToken ct = default)
    {
        try
        {
            byte[] data;
            string contentType;
            string filename;

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                data = await _printersService.BuildExportJsonAsync(ids, ct);
                contentType = "application/json";
                filename = $"printers-export-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.json";
            }
            else
            {
                data = await _printersService.BuildExportCsvAsync(ids, ct);
                contentType = "text/csv";
                filename = $"printers-export-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.csv";
            }

            Response.ContentType = contentType;
            Response.Headers["Content-Disposition"] = $"attachment; filename={filename}";
            await Response.Body.WriteAsync(data.AsMemory(0, data.Length), ct);
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export printers");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to export printers");
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
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Printer configuration details.</returns>
    /// <response code="200">Returns the printer configuration.</response>
    /// <response code="404">If the printer does not exist.</response>
    /// <response code="500">If there was an error retrieving the configuration.</response>
    [HttpGet("{id:guid}/config")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> GetPrinterConfigAsync(Guid id, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[Config] Getting printer configuration for {Id}", id);
            Printer? printer = await _printersService.FindByIdWithIncludesAsync(id, ct);

            if (printer == null)
            {
                _logger.LogWarning("[Config] Printer {Id} not found", id);
                return NotFound(new { message = $"Printer {id} not found" });
            }

            // Return printer configuration as JSON object
            var config = new
            {
                id = printer.Id,
                name = printer.Name,
                serverUrl = printer.ServerUrl,
                originalServerUrl = printer.OriginalServerUrl,
                backend = printer.Backend,
                apiKey = printer.ApiKey,
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
            _logger.LogError(ex, "[Config] Failed to get printer configuration for {Id}: {Message}", id, ex.Message);
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
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="config">The updated configuration properties.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Updated printer configuration.</returns>
    /// <response code="200">Returns the updated configuration.</response>
    /// <response code="400">If the configuration data is invalid.</response>
    /// <response code="404">If the printer does not exist.</response>
    /// <response code="500">If there was an error updating the configuration.</response>
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
            _logger.LogInformation("[Config] Updating printer configuration for {Id}", id);

            // Use FindByIdAsync (with tracking) since this endpoint modifies and saves scalar fields.
            // FindByIdWithIncludesAsync is now AsNoTracking (read-only optimized).
            Printer? printer = await _printersService.FindByIdAsync(id, ct);
            if (printer == null)
            {
                _logger.LogWarning("[Config] Printer {Id} not found for update", id);
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

                _logger.LogInformation("[Config] Updating printer: {PrinterName} with new configuration", printer.Name);
                await _printersService.SaveChangesAsync(ct);
                _logger.LogInformation("[Config] Successfully updated printer configuration for {Id}", id);

                // Return updated configuration
                var updatedConfig = new
                {
                    id = printer.Id,
                    name = printer.Name,
                    serverUrl = printer.ServerUrl,
                    originalServerUrl = printer.OriginalServerUrl,
                    backend = printer.Backend,
                    apiKey = printer.ApiKey,
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
            _logger.LogError(ex, "[Config] Failed to update printer configuration for {Id}: {Message}", id, ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Failed to update printer configuration",
                error = ex.Message
            });
        }
    }

    #region Discovery Stream Endpoints

    /// <summary>
    /// Start a network discovery stream to find printers on the local network.
    /// Returns a session ID that can be used to receive discovery progress via SignalR.
    /// </summary>
    /// <param name="request">Optional request with backend filters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Session ID for tracking discovery progress.</returns>
    /// <response code="200">Discovery started successfully.</response>
    /// <response code="500">Failed to start discovery.</response>
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
            _logger.LogInformation("[DISCOVERY] Starting discovery stream via API endpoint (autoRegister={AutoRegister})", autoRegister);

            IReadOnlyList<PrinterBackend>? backends = request?.Backends?.ToList();
            DiscoveryStreamResponse result = await _discoveryProxyService.StartDiscoveryStreamAsync(
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
    /// <param name="sessionId">The session ID to cancel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cancellation confirmation.</returns>
    /// <response code="200">Discovery cancelled successfully.</response>
    /// <response code="500">Failed to cancel discovery.</response>
    [HttpPost("discover/{sessionId}/cancel")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> CancelDiscoveryStreamAsync(
        [FromRoute] string sessionId,
        CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[DISCOVERY] Cancelling discovery stream {SessionId}", sessionId);

            DiscoveryCancelResponse result = await _discoveryProxyService.CancelDiscoveryStreamAsync(sessionId, ct);

            return Ok(new { message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DISCOVERY] Failed to cancel discovery stream {SessionId}", sessionId);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Failed to cancel discovery",
                error = ex.Message
            });
        }
    }

    #endregion

    #region Printer Location Management

    /// <summary>
    /// Assign a printer to a location.
    /// </summary>
    /// <param name="id">The printer ID.</param>
    /// <param name="request">The location assignment request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated printer.</returns>
    /// <response code="200">Printer assigned to location successfully.</response>
    /// <response code="404">Printer or location not found.</response>
    /// <response code="500">Failed to assign printer to location.</response>
    [HttpPost("{id}/location")]
    [ProducesResponseType(typeof(PrinterDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> AssignPrinterToLocationAsync(
        [FromRoute] Guid id,
        [FromBody] AssignPrinterToLocationRequest request,
        CancellationToken ct)
    {
        try
        {
            if (request?.LocationId == null)
            {
                return BadRequest(new { message = "LocationId is required" });
            }

            Printer? printer = await _printersService.FindByIdAsync(id, ct);
            if (printer == null)
            {
                return NotFound(new { message = "Printer not found" });
            }

            // Update printer with location
            printer.LocationId = request.LocationId;
            await _printersService.SaveChangesAsync(ct);

            // Return authoritative updated DTO
            PrinterDto updated = await _printersService.GetPrinterDtoAsync(id, ct);
            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PrintersController] Failed to assign printer {Id} to location", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Remove a printer from its location (unassign).
    /// </summary>
    /// <param name="id">The printer ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>No content.</returns>
    /// <response code="204">Printer unassigned from location successfully.</response>
    /// <response code="404">Printer not found.</response>
    /// <response code="500">Failed to unassign printer from location.</response>
    [HttpDelete("{id}/location")]
    [ProducesResponseType(typeof(PrinterDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> UnassignPrinterFromLocationAsync(
        [FromRoute] Guid id,
        CancellationToken ct)
    {
        try
        {
            Printer? printer = await _printersService.FindByIdAsync(id, ct);
            if (printer == null)
            {
                return NotFound(new { message = "Printer not found" });
            }

            // Remove location from printer
            printer.LocationId = null;
            await _printersService.SaveChangesAsync(ct);

            // Return authoritative updated DTO
            PrinterDto updated = await _printersService.GetPrinterDtoAsync(id, ct);
            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PrintersController] Failed to unassign printer {Id} from location", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    #endregion

    /// <summary>
    /// Maps a CommandResult to the appropriate HTTP status code.
    /// Success → 200, "not found" → 404, other failures → 400.
    /// </summary>
    private ActionResult<CommandResult> MapCommandResult(CommandResult result)
    {
        if (result.Success)
        {
            return Ok(result);
        }

        if (result.Message?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
        {
            return NotFound(result);
        }

        return BadRequest(result);
    }
}
