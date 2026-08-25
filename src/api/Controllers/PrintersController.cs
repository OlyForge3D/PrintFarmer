using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Discovery;
using Farm.Infrastructure.Services.Idempotency;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Controllers.Responses;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Infrastructure.Idempotency;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IPrinterVersionCache = Farm.Infrastructure.Services.Printers.IPrinterVersionCache;
using MoonrakerEndpointResolution = Farm.Infrastructure.Services.Printers.MoonrakerEndpointResolution;
using MoonrakerOnboardingResolver = Farm.Infrastructure.Services.Printers.MoonrakerOnboardingResolver;
using PerToolAttributionCapability = Farm.Infrastructure.Services.Printers.PerToolAttributionCapability;

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
    IDiscoverySessionRegistry discoverySessions,
    Farm.Infrastructure.Services.Printers.IPrinterBackendCapabilitiesService printerBackendCapabilitiesService,
    Farm.Infrastructure.Services.Printers.IBackendClientFactory backendClientFactory,
    IHttpClientFactory httpClientFactory,
    IEgressGuard egressGuard,
    Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService obicoServerAssignment,
    ISettingsService settingsService,
    Farm.Infrastructure.Services.Printers.IPrinterSessionTimelineService printerSessionTimelineService,
    IPrintFarmerTelemetryService telemetryService,
    Farm.Infrastructure.Services.BedTypes.IBedTypeService bedTypeService,
    Farm.Infrastructure.Services.IProfileImportService? profileImportService = null,
    IPrinterVersionCache printerVersionCache = null!,
    Farm.Infrastructure.Services.Queue.Dispatch.IDispatchClaimService? dispatchClaimService = null,
    Farm.Infrastructure.Services.Queue.IQueueResourceAuthorizationService? queueResourceAuthorization = null,
    Farm.Infrastructure.Services.Queue.IPrinterPhysicalActuationService? physicalActuationService = null,
    AppDbContext? appDbContext = null,
    Farm.Infrastructure.Services.Printers.IPrinterCacheInvalidator? printerCacheInvalidator = null)
    : ControllerBase
{
    private const int MaxHistoryQueryEntries = 2000;

    private readonly Farm.Infrastructure.Services.Queue.Dispatch.IDispatchClaimService? _dispatchClaimService = dispatchClaimService;
    private readonly Farm.Infrastructure.Services.Queue.IQueueResourceAuthorizationService? _queueResourceAuthorization = queueResourceAuthorization;
    private readonly Farm.Infrastructure.Services.Queue.IPrinterPhysicalActuationService? _physicalActuationService = physicalActuationService;
    private readonly AppDbContext? _appDbContext = appDbContext;
    private readonly Farm.Infrastructure.Services.Printers.IPrinterCacheInvalidator? _printerCacheInvalidator = printerCacheInvalidator;
    private readonly ILogger<PrintersController> _logger = logger;
    private readonly Farm.Infrastructure.Services.Printers.IPrintersService _printersService = printersService;
    private readonly Services.Catalog.ICatalogService _catalogService = catalogService;
    private readonly IValidator<CreatePrinterFromDiscoveryDto> _validator = validator;
    private readonly IDiscoveryProxyService _discoveryProxyService = discoveryProxyService;
    private readonly IDiscoverySessionRegistry _discoverySessions = discoverySessions;
    private readonly Farm.Infrastructure.Services.Printers.IPrinterBackendCapabilitiesService _printerBackendCapabilitiesService = printerBackendCapabilitiesService;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IEgressGuard _egressGuard = egressGuard;
    private readonly Farm.Infrastructure.Services.IProfileImportService? _profileImportService = profileImportService;
    private readonly IPrinterVersionCache _printerVersionCache = printerVersionCache;
    private readonly Farm.Infrastructure.Services.Printers.IBackendClientFactory _backendClientFactory = backendClientFactory;
    private readonly Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService _obicoServerAssignment = obicoServerAssignment;
    private readonly ISettingsService _settingsService = settingsService;
    private readonly Farm.Infrastructure.Services.Printers.IPrinterSessionTimelineService _printerSessionTimelineService = printerSessionTimelineService;
    private readonly IPrintFarmerTelemetryService _telemetryService = telemetryService;
    private readonly Farm.Infrastructure.Services.BedTypes.IBedTypeService _bedTypeService = bedTypeService;

    /// <summary>
    /// Retrieves same-origin camera proxy URLs for enabled printers.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A lightweight list of enabled printers with authenticated proxy URLs.</returns>
    /// <response code="200">Returns the list of printers with camera URL information.</response>
    [HttpGet("camera-urls")]
    [ProducesResponseType(typeof(IEnumerable<PrinterCameraUrlsDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<PrinterCameraUrlsDto>>> GetCameraUrlsAsync(CancellationToken ct)
    {
        try
        {
            PrinterCameraUrlsDto[] dtos = await _printersService.GetCameraUrlsAsync(ct);
            dtos = await FilterAccessiblePrintersAsync(dtos, dto => dto.Id, ct);
            return Ok(dtos.Select(CreateSafeCameraUrls).ToList());
        }
        catch (Exception ex) when (IsTransientStartupDbException(ex))
        {
            _logger.LogWarning("[CAMERA-URLS] Startup DB exception in /api/printers/camera-urls. TraceId={HttpContextTraceIdentifier}, Exception={Message}", HttpContext.TraceIdentifier, ex.Message);
            return Ok(Array.Empty<PrinterCameraUrlsDto>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FATAL] Unhandled exception in /api/printers/camera-urls. TraceId={HttpContextTraceIdentifier}, User={Name}, Exception={Message}\n{StackTrace}", HttpContext.TraceIdentifier, User?.Identity?.Name ?? "anonymous", ex.Message, ex.StackTrace);
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Camera routes could not be read",
                type: "https://printfarmer.dev/problems/camera-routes-read-failed",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "camera_routes_read_failed",
                });
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
    /// Retrieves the minimal printer projection used by dashboard statistics and alerts.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <param name="includeDisabled">Return disabled printers as well (admin-only).</param>
    /// <returns>Visible printers with identity, maintenance, catalog-update, and cached status fields.</returns>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(IEnumerable<PrinterSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IEnumerable<PrinterSummaryDto>>> GetSummaryAsync(
        CancellationToken ct,
        [FromQuery] bool includeDisabled = false)
    {
        bool isAdmin = User.IsInRole("farm_admin");
        if (!isAdmin && includeDisabled)
        {
            return Forbid();
        }

        try
        {
            PrinterSummaryDto[] summaries = await _printersService.GetAllSummaryDtosAsync(ct);
            if (!includeDisabled)
            {
                summaries = summaries.Where(summary => summary.IsEnabled).ToArray();
            }

            summaries = await FilterAccessiblePrintersAsync(summaries, summary => summary.Id, ct);

            return Ok(summaries);
        }
        catch (Exception ex) when (IsTransientStartupDbException(ex))
        {
            _logger.LogWarning("[GET] Startup DB exception in /api/printers/summary. TraceId={HttpContextTraceIdentifier}, Exception={Message}", HttpContext.TraceIdentifier, ex.Message);
            return Ok(Array.Empty<PrinterSummaryDto>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FATAL] Unhandled exception in /api/printers/summary. TraceId={HttpContextTraceIdentifier}, User={Name}, Exception={Message}", HttpContext.TraceIdentifier, User?.Identity?.Name ?? "anonymous", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, "Internal Server Error");
        }
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
            PrinterBackendCapabilitiesDto[] capabilities = (await _printerBackendCapabilitiesService.GetAllAsync(ct)).ToArray();
            capabilities = await FilterAccessiblePrintersAsync(capabilities, dto => dto.PrinterId, ct);
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
    /// <param name="forceRefresh">
    /// When <c>true</c>, bypasses any cached version result (including a cached partial result
    /// recorded during a transient backend fault) and re-queries the backend live. Intended for
    /// the explicit "Refresh version info" operator action; automatic polling should omit this.
    /// </param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpGet("{printerId:guid}/version")]
    [ProducesResponseType(typeof(PrinterVersionInfoDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterVersionInfoDto>> GetPrinterVersionAsync(Guid printerId, [FromQuery] bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!await CanAccessPrinterAsync(printerId, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound($"Printer with ID {printerId} not found");
        }

        try
        {
            PrinterVersionInfoDto? dto = await _printerVersionCache.GetAsync(printerId, ct, forceRefresh);
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
        if (!await CanAccessPrinterAsync(printerId, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound($"Printer with ID {printerId} not found");
        }

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
    [RequirePermission("printers", "admin")]
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

        if (!PrinterClientUrl.IsSafeInput(request.ServerUrl) ||
            !Uri.TryCreate(request.ServerUrl, UriKind.Absolute, out Uri? serverUri))
        {
            return BadRequest(new TestConnectionResponse
            {
                Success = false,
                Message = "Server URL must be an HTTP/HTTPS URL without embedded credentials."
            });
        }

        EgressCheckResult egressCheck = await _egressGuard.CheckAsync(serverUri.ToString(), ct);
        if (!egressCheck.IsAllowed)
        {
            _logger.LogWarning(
                "Connection test denied by egress guard for host {Host}: {Reason}",
                LogSanitizer.Sanitize(serverUri.Host),
                LogSanitizer.Sanitize(egressCheck.DenyReason));
            return BadRequest(new TestConnectionResponse
            {
                Success = false,
                Message = "The requested server address is not allowed."
            });
        }

        _logger.LogInformation("Testing printer connection with backend {RequestBackend}", request.Backend);

        try
        {
            TestConnectionResponse result = await TestBackendConnectionAsync(
                serverUri,
                egressCheck,
                request.Backend,
                request.ApiKey,
                request.Username,
                request.Password,
                request.BackendPort,
                ct);
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
        Uri serverUrl,
        EgressCheckResult egressCheck,
        PrinterBackend backend,
        string? apiKey,
        string? username,
        string? password,
        int? backendPort,
        CancellationToken ct)
    {
        // Reuse the exact address the egress guard just vetted for the real connection instead
        // of letting each backend re-resolve the hostname independently — otherwise a
        // DNS-rebinding attacker could swap the record between the check above and the
        // connection made by the backend helpers below.
        Uri connectUri = egressCheck.ResolvedAddress is not null
            ? EgressGuard.CreatePinnedUri(serverUrl, egressCheck.ResolvedAddress)
            : serverUrl;
        string? hostHeader = serverUrl.IsDefaultPort ? serverUrl.Host : $"{serverUrl.Host}:{serverUrl.Port}";

        using HttpClient httpClient = _httpClientFactory.CreateClient("VettedEgress");
        httpClient.Timeout = TimeSpan.FromSeconds(10);
        if (connectUri != serverUrl)
        {
            httpClient.DefaultRequestHeaders.Host = hostHeader;
        }

        string? effectiveApiKey = apiKey ?? password;

        return backend switch
        {
            PrinterBackend.Moonraker => await TestMoonrakerConnectionAsync(httpClient, connectUri, backendPort, ct),
            PrinterBackend.PrusaLink => await TestPrusaLinkConnectionAsync(connectUri, apiKey, username, password, connectUri != serverUrl ? hostHeader : null, ct),
            PrinterBackend.OctoPrint => await TestOctoPrintConnectionAsync(httpClient, connectUri, effectiveApiKey, ct),
            PrinterBackend.SDCP => await TestSdcpConnectionAsync(connectUri, backendPort, ct),
            PrinterBackend.FlashForge => await TestFlashForgeConnectionAsync(connectUri, backendPort, ct),
            _ => new TestConnectionResponse { Success = false, Message = $"Unsupported backend type: {backend}" }
        };
    }

    private async Task<TestConnectionResponse> TestSdcpConnectionAsync(Uri serverUrl, int? backendPort, CancellationToken ct)
    {
        Uri uriToTest = serverUrl;
        if (backendPort.HasValue)
        {
            uriToTest = new UriBuilder(serverUrl) { Port = backendPort.Value }.Uri;

            EgressCheckResult rewriteCheck = await _egressGuard.CheckAsync(uriToTest.ToString(), ct);
            if (!rewriteCheck.IsAllowed)
            {
                _logger.LogWarning(
                    "Connection test denied by egress guard for rewritten host {Host}: {Reason}",
                    LogSanitizer.Sanitize(uriToTest.Host),
                    LogSanitizer.Sanitize(rewriteCheck.DenyReason));
                return new TestConnectionResponse
                {
                    Success = false,
                    Message = "The requested server address is not allowed."
                };
            }
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

            EgressCheckResult rewriteCheck = await _egressGuard.CheckAsync(uriToTest.ToString(), ct);
            if (!rewriteCheck.IsAllowed)
            {
                _logger.LogWarning(
                    "Connection test denied by egress guard for rewritten host {Host}: {Reason}",
                    LogSanitizer.Sanitize(uriToTest.Host),
                    LogSanitizer.Sanitize(rewriteCheck.DenyReason));
                return new TestConnectionResponse
                {
                    Success = false,
                    Message = "The requested server address is not allowed."
                };
            }
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
    /// Tests Moonraker connection by probing stock /printer/info and Snapmaker U1 /machine/system_info endpoints.
    /// </summary>
    private async Task<TestConnectionResponse> TestMoonrakerConnectionAsync(
        HttpClient httpClient, Uri serverUrl, int? backendPort, CancellationToken ct)
    {
        if (backendPort.HasValue)
        {
            // MoonrakerOnboardingResolver dials the caller-supplied backendPort as its
            // authoritative candidate before falling back to well-known ports. Re-vet the
            // rewritten (same-host, different-port) URI so the guard checks the URI actually
            // used to dial, consistent with the SDCP/FlashForge re-vet above.
            Uri rewrittenUri = MoonrakerOnboardingResolver.BuildEndpointUri(
                serverUrl, backendPort.Value, MoonrakerOnboardingResolver.PrinterInfoPath);
            EgressCheckResult rewriteCheck = await _egressGuard.CheckAsync(rewrittenUri.ToString(), ct);
            if (!rewriteCheck.IsAllowed)
            {
                _logger.LogWarning(
                    "Connection test denied by egress guard for rewritten host {Host}: {Reason}",
                    LogSanitizer.Sanitize(rewrittenUri.Host),
                    LogSanitizer.Sanitize(rewriteCheck.DenyReason));
                return new TestConnectionResponse
                {
                    Success = false,
                    Message = "The requested server address is not allowed."
                };
            }
        }

        try
        {
            MoonrakerEndpointResolution? resolution = await MoonrakerOnboardingResolver.ResolveAsync(httpClient, serverUrl, backendPort, ct);

            return resolution is not null
                ? new TestConnectionResponse
                {
                    Success = true,
                    Message = resolution.IsSnapmakerU1
                        ? $"Successfully connected to Snapmaker U1 Moonraker printer on port {resolution.BackendPort}"
                        : $"Successfully connected to Moonraker printer on port {resolution.BackendPort}"
                }
                : new TestConnectionResponse
                {
                    Success = false,
                    Message = "Moonraker did not respond on the standard 7125 endpoint or Snapmaker U1 port 80 endpoint"
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
        Uri serverUrl, string? apiKey, string? username, string? password, string? hostHeader, CancellationToken ct)
    {
        string? credentialSecret = !string.IsNullOrWhiteSpace(password) ? password : apiKey;
        if (string.IsNullOrWhiteSpace(credentialSecret))
        {
            return new TestConnectionResponse
            {
                Success = false,
                Message = "PrusaLink credentials are required. Use the printer's Network → Credentials password/API key."
            };
        }

        string effectiveUsername = !string.IsNullOrWhiteSpace(username) ? username : "maker";

        var builder = new UriBuilder(serverUrl)
        {
            Path = "/api/v1/status"
        };

        // Create a new HttpClient with Digest auth handler for this test. The inner
        // HttpClientHandler disables auto-redirect so an attacker-controlled destination
        // cannot use a redirect to launder a request into an egress-denied address after
        // the destination has already been vetted.
        using var digestHandler = new DigestAuthHandler(
            new HttpClientHandler { AllowAutoRedirect = false },
            effectiveUsername,
            credentialSecret);
        using var digestClient = new HttpClient(digestHandler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        if (!string.IsNullOrEmpty(hostHeader))
        {
            digestClient.DefaultRequestHeaders.Host = hostHeader;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);

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
                    Message = "Invalid PrusaLink credentials - authentication failed. Verify the password/API key from printer Settings → Network → Credentials."
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

        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
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

            CompletePrinterDto[] filtered = await FilterAccessiblePrintersAsync(
                result.ToArray(),
                dto => dto.Id,
                ct);

            return Ok(filtered.ToList());
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
    [RequirePermission("printers", "admin")]
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
    [RequirePermission("printers", "admin")]
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

            if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
            {
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
            return Ok(null); // Return null on timeout
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
    /// Lists object-exclusion metadata for the current print job.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The current job's objects and exclusion state.</returns>
    [HttpGet("{id:guid}/printjob/objects")]
    [ProducesResponseType(typeof(PrintJobObjectListDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<PrintJobObjectListDto>> GetPrintJobObjectsAsync(Guid id, CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound(new { message = $"Printer {id} not found" });
        }

        PrintJobObjectListDto? objects = await _printersService.GetPrintJobObjectsAsync(id, ct);
        if (objects is null)
        {
            return NotFound(new { message = $"Printer {id} not found" });
        }

        return Ok(objects);
    }

    /// <summary>
    /// Excludes a single object from the current print job.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="request">Object exclusion request.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Command execution result.</returns>
    [HttpPost("{id:guid}/printjob/objects/exclude")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> ExcludePrintJobObjectAsync(Guid id, [FromBody] ExcludePrintJobObjectRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new CommandResult(false, "Object name is required."));
        }

        return await ExecuteActiveCommandControlAsync(
            id,
            "exclude_object",
            "exclude_object",
            token => _printersService.ExcludePrintJobObjectAsync(id, request.Name, token),
            ct);
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
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

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
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

        try
        {
            PrinterDto dto = await _printersService.GetPrinterDtoAsync(id, ct);
            AppDbContext? revisionDb = ResolveAppDbContext();
            if (revisionDb is not null)
            {
                var revision = await revisionDb.Printers
                    .AsNoTracking()
                    .Where(printer => printer.Id == id)
                    .Select(printer => new
                    {
                        printer.RowVersion,
                        printer.ConfigurationRevision,
                    })
                    .SingleOrDefaultAsync(ct);
                if (revision is not null)
                {
                    string? encoded = EncodeRowVersion(revision.RowVersion);
                    dto = dto with
                    {
                        RowVersion = encoded,
                        ConfigurationRevision = revision.ConfigurationRevision,
                    };
                    if (encoded is not null)
                    {
                        Response.Headers.ETag = $"\"{encoded}\"";
                    }
                }
            }

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
    /// <param name="fallbackGroupService">Injected fallback-group service used to include per-printer fallback chains in the details payload.</param>
    /// <param name="featureGate">Operator feature gate consulted to decide whether multi-slot fallback chains are exposed (issue #711, FIX E).</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Detailed printer information including manufacturer, model, purchase information, settings, and configured fallback groups.</returns>
    /// <response code="200">Returns detailed printer information.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    [HttpGet("{id:guid}/details")]
    [ProducesResponseType(typeof(PrinterDetailsDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterDetailsDto>> GetDetailsAsync(
        Guid id,
        [FromServices] Farm.Infrastructure.Services.Printers.IFilamentFallbackGroupService fallbackGroupService,
        [FromServices] Farm.Infrastructure.Services.OperatorFeatures.IOperatorFeatureGate featureGate,
        CancellationToken ct)
    {
        Printer? p = await _printersService.FindByIdWithIncludesAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

        // Internal connection settings are editable configuration. Only printer
        // administrators receive the server URL or decrypted password.
        bool canAdministerPrinters = PrintFarmerPermissions.HasPermission(User, "printers:admin");
        string? effectivePassword = p.Password;
        if ((PrinterBackend)p.Backend == PrinterBackend.PrusaLink
            && string.IsNullOrWhiteSpace(effectivePassword)
            && !string.IsNullOrWhiteSpace(p.ApiKey))
        {
            effectivePassword = p.ApiKey;
        }

        string? clientServerUrl = canAdministerPrinters ? PrinterClientUrl.Create(p.ServerUrl) : null;
        string? clientPassword = canAdministerPrinters ? effectivePassword : null;

        // Get primary toolhead for capabilities DTO (backward compatibility)
        Toolhead? primaryToolhead = p.Toolheads?.FirstOrDefault(t => t.IsPrimary) ?? p.Toolheads?.FirstOrDefault();

        // Only expose per-tool attribution surface (SupportsPerToolAttribution flag and
        // per-toolhead CumulativePrintHours) when the multi-slot-fallback operator feature
        // is on AND the printer's persisted domain capability flag is true (issue #711, F6
        // backend). When either condition fails, both the capability flag and the odometer
        // values collapse to their unset defaults (false / null) so #719 UI consumers see a
        // deterministic "not applicable" shape rather than stale or fabricated wear.
        bool multiSlotFallbackEnabled = await featureGate.IsEnabledAsync(Farm.Infrastructure.Services.OperatorFeatures.OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false);
        bool perToolAttributionActive = multiSlotFallbackEnabled && p.SupportsPerToolAttribution;

        // Create capabilities DTO from Printer entity fields (merged from legacy PrinterCapabilities)
        // This provides backward compatibility while we transition to using Toolheads directly
        PrinterCapabilitiesDto? capabilitiesDto = new PrinterCapabilitiesDto(
            Guid.NewGuid(), // PrinterCapabilities.Id - generate a temporary ID since this entity is being phased out
            p.Id,
            p.Name,
            p.ServiceState?.LastCapabilityUpdate ?? DateTime.UtcNow,
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
            p.IsAvailable);

        // Map toolheads to DTOs with hardware tracking fields
        ToolheadDto[]? toolheadDtos = p.Toolheads?.OrderBy(t => t.Index).Select(t => new ToolheadDto(
            t.Id,
            t.Name,
            t.Index,
            t.NozzleModel?.Diameter,  // Nozzle diameter from NozzleModel
            t.NozzleModel?.NozzleMaterial?.Name ?? t.NozzleModel?.NozzleType.ToString(),  // Nozzle material name from NozzleModel (open string set)
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
            t.CurrentFilamentColor,

            // Only project the per-toolhead odometer when the capability is active for this
            // printer; otherwise emit explicit null so consumers can distinguish "no
            // attribution available" from "zero hours accrued" (a supported printer with a
            // fresh baseline still returns 0.0 here).
            perToolAttributionActive ? t.CumulativePrintHours : null)).ToArray();

        // Only expose fallback chains when the multi-slot-fallback operator feature is on
        // (issue #711, FIX E); otherwise return an empty list so gated-off clients never
        // see fallback config.
        IReadOnlyList<Farm.Infrastructure.Dtos.FilamentFallbackGroupDto> fallbackGroups =
            multiSlotFallbackEnabled
                ? await fallbackGroupService.ListForPrinterAsync(id, ct)
                : [];

        string? rowVersion = p.RowVersion is { Length: > 0 }
            ? Convert.ToBase64String(p.RowVersion)
            : null;
        Response.Headers.CacheControl = "no-store";
        return new PrinterDetailsDto(
            p.Id,
            p.Name,
            clientServerUrl,
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
            clientPassword,
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
            p.HasMmu,
            fallbackGroups,
            perToolAttributionActive,
            !string.IsNullOrWhiteSpace(p.ServerUrl),
            !string.IsNullOrWhiteSpace(p.ApiKey),
            !string.IsNullOrWhiteSpace(p.Username),
            !string.IsNullOrWhiteSpace(effectivePassword),
            false,
            false,
            rowVersion);
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
    [RequirePermission("printers", "admin")]
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
        PrinterDto created = await CreatePrinterAndImportProfilesAsync(dto, ct);

        return CreatedAtRoute("GetPrinterById", new { id = created.Id }, created);
    }

    private async Task<PrinterDto> CreatePrinterAndImportProfilesAsync(
        CreatePrinterFromDiscoveryDto dto,
        CancellationToken ct)
    {
        PrinterDto created = await _printersService.CreatePrinterFromDtoAsync(dto, ct);
        Guid? modelId = dto.ModelId;
        if (modelId is null || modelId == Guid.Empty || _profileImportService is null)
        {
            return created;
        }

        string modelName = dto.NewModelName ?? created.ModelName ?? "Unknown";
        string manufacturerName = dto.NewManufacturerName ?? created.ManufacturerName ?? "Unknown";
        try
        {
            int imported = await _profileImportService.ImportProfilesForModelAsync(
                modelId.Value,
                modelName,
                manufacturerName,
                ct);
            if (imported > 0)
            {
                _logger.LogInformation(
                    "Imported {Imported} slicer profiles for {ModelName}",
                    imported,
                    LogSanitizer.Sanitize(modelName));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to import profiles for {ModelName}: {Message}",
                LogSanitizer.Sanitize(modelName),
                LogSanitizer.Sanitize(ex.Message));
        }

        return created;
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
    [RequirePermission("printers", "admin")]
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
                    _logger.LogInformation("Printer already registered: {ExistingName} ({ExistingId})", existing.Name, existing.Id);

                    // Periodic firmware re-probe/refresh producer (#1618 / #1613 PR-5): reuses this
                    // existing discovery scan tick rather than a new scheduler, throttled internally
                    // to the configured Discovery:FirmwareReprobeIntervalHours cadence.
                    bool firmwareRefreshed = await _printersService.RefreshDetectedFirmwareIdentityAsync(existing.Id, discovered, ct);
                    if (firmwareRefreshed)
                    {
                        _logger.LogInformation("Refreshed firmware identity for printer {ExistingId} from periodic re-probe", existing.Id);
                    }

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
    [RequirePermission("printers", "admin")]
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

        if (BindPrinterIfMatch(printer) is { } precondition)
        {
            return precondition;
        }

        printer.InMaintenance = inMaintenance;
        try
        {
            await _printersService.SaveChangesAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return PrinterRevisionConflict();
        }

        WritePrinterEtag(printer);

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
            FrontendUrl: printer.FrontendUrl,
            RowVersion: EncodeRowVersion(printer.RowVersion),
            ConfigurationRevision: printer.ConfigurationRevision);
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
    [RequirePermission("printers", "admin")]
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
    [RequirePermission("printers", "admin")]
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
    [RequirePermission("printers", "admin")]
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
    [RequirePermission("printers", "admin")]
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

        if (BindPrinterIfMatch(p) is { } precondition)
        {
            return precondition;
        }

        // Validate toolhead metrology bounds up front so a rejected write never partially
        // mutates the printer (see CalibrationPrinterUpdateMapper.ValidateToolheadMetrology).
        if (dto.Toolheads?.Length > 0)
        {
            foreach (UpdateToolheadDto toolheadDto in dto.Toolheads)
            {
                if (Services.Calibration.CalibrationPrinterUpdateMapper.ValidateToolheadMetrology(toolheadDto) is { } problem)
                {
                    return BadRequest(problem);
                }
            }
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
            if (!PrinterClientUrl.IsSafeInput(dto.ServerUrl))
            {
                return BadRequest("Server URL must be an HTTP/HTTPS URL without embedded credentials.");
            }

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
                        LogSanitizer.Sanitize(p.Name));
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
            await _printersService.SyncMmuToolheadsOnEntityAsync(p, wasMultiMaterial, ct: ct);
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

        capabilityChanged |= Services.Calibration.CalibrationPrinterUpdateMapper.ApplyPrinter(p, dto);

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

                    toolheadChanged |= Services.Calibration.CalibrationPrinterUpdateMapper.ApplyToolhead(
                        toolhead,
                        toolheadDto);

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
            if (primaryToolhead != null && dto.SupportedMaterials != null && dto.SupportedMaterials != primaryToolhead.SupportedMaterials)
            {
                primaryToolhead.SupportedMaterials = dto.SupportedMaterials;
                primaryToolhead.UpdatedAt = DateTime.UtcNow;
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
                // Accept valid IPv4 or IPv6 addresses (including bracketed IPv6 like [fe80::1]).
                // Only apply the hostname char-blacklist for input that is not a parseable IP.
                string candidateForParse = ip.StartsWith('[') && ip.EndsWith(']')
                    ? ip[1..^1]
                    : ip;

                bool isValidHost;
                if (IPAddress.TryParse(candidateForParse, out IPAddress? parsedIp))
                {
                    // Brackets are only valid around an IPv6 address (RFC 3986 §3.2.2).
                    // Reject [v4-addr] — it passes TryParse but breaks URL construction
                    // and is never a valid user intent (admin sees 200, camera never works).
                    if (ip.StartsWith('[') && parsedIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        return BadRequest("Invalid BuddyCameraIp: brackets are only valid around an IPv6 address.");
                    }

                    // Valid IP address; downstream SSRF validation handles range checks.
                    isValidHost = true;
                }
                else
                {
                    // Not a parseable IP; treat as hostname and reject injection chars.
                    bool hasInvalidChar = ip.Any(c =>
                        c == ':' || c == '/' || c == '\\' || c == '@' || c == '?' || c == '#'
                        || char.IsControl(c) || char.IsWhiteSpace(c));

                    isValidHost = !hasInvalidChar &&
                        Uri.CheckHostName(ip) == UriHostNameType.Dns;
                }

                if (!isValidHost)
                {
                    return BadRequest("Invalid BuddyCameraIp: must be a plain IP address or hostname.");
                }

                // TODO(#428 follow-up): extract a shared CameraHostNormalizer helper that
                // validates + canonicalizes BuddyCameraIp in one place, removing the split
                // between controller validation and service URL construction.
                await _printersService.SyncBuddyCameraAsync(p, ip, ct);
            }
        }

        // Backend, multi-material, and topology edits all converge on one equality-guarded
        // capability derivation before the unit of work commits.
        _ = PerToolAttributionCapability.Refresh(p);

        // The endpoint is revision-guarded (BindPrinterIfMatch above). A concurrency
        // conflict must surface as a typed 412 to honor the If-Match contract rather than
        // silently retrying, so callers can refetch and re-issue against the new revision.
        try
        {
            await _printersService.SaveChangesAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return PrinterRevisionConflict();
        }

        // The edit is now durably committed - tell every backend polling service to drop any
        // cached copy of this printer so the very next poll tick re-reads the row (with fresh
        // credentials/URL/backend) instead of polling stale data for up to 30 seconds (#1763).
        _printerCacheInvalidator?.Invalidate(p.Id);

        WritePrinterEtag(p);

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
            UseModelDispatchDefaults: p.UseModelDispatchDefaults,
            RowVersion: EncodeRowVersion(p.RowVersion),
            ConfigurationRevision: p.ConfigurationRevision);

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
    [RequirePermission("printers", "admin")]
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
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

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
    /// <response code="409">If the printer is currently busy (e.g., printing).</response>
    /// <response code="500">If there was an error executing the homing command.</response>
    [HttpPost("{id:guid}/home")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> HomeAsync(Guid id, CancellationToken ct)
    {
        return await ExecuteDirectBooleanControlAsync(
            id,
            "home",
            "home_all",
            token => _printersService.SendHomeAsync(id, token),
            ct);
    }

    /// <summary>
    /// Homes the X and Y axes of the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure of the homing operation.</returns>
    /// <response code="200">Returns the command execution result.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="409">If the printer is currently busy (e.g., printing).</response>
    /// <response code="500">If there was an error executing the homing command.</response>
    [HttpPost("{id:guid}/homexy")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> HomeXYAsync(Guid id, CancellationToken ct)
    {
        return await ExecuteDirectBooleanControlAsync(
            id,
            "home_xy",
            "home_xy",
            token => _printersService.HomeXYAsync(id, token),
            ct);
    }

    /// <summary>
    /// Homes the Z axis of the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure of the Z-axis homing operation.</returns>
    /// <response code="200">Returns the command execution result.</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    /// <response code="409">If the printer is currently busy (e.g., printing).</response>
    /// <response code="500">If there was an error executing the homing command.</response>
    [HttpPost("{id:guid}/homez")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> HomeZAsync(Guid id, CancellationToken ct)
    {
        return await ExecuteDirectBooleanControlAsync(
            id,
            "home_z",
            "home_z",
            token => _printersService.HomeZAsync(id, token),
            ct);
    }

    [HttpPost("{id:guid}/temps")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [ProducesResponseType(502)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> SetTempsAsync(Guid id, [FromBody] Farm.Infrastructure.TempTargets targets, CancellationToken ct)
    {
        if (targets is null)
        {
            return BadRequest("Request body is required.");
        }

        return await ExecuteDirectOutcomeControlAsync(
            id,
            "set_temperature",
            "set_temperature",
            token => _printersService.SetTempsAsync(id, targets.Hotend, targets.Bed, token),
            ct);
    }

    [HttpPost("{id:guid}/move")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [ProducesResponseType(502)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> MoveAsync(Guid id, [FromBody] MoveRequest req, CancellationToken ct)
    {
        if (req is null)
        {
            return BadRequest("Request body is required.");
        }

        return await ExecuteDirectOutcomeControlAsync(
            id,
            "move",
            "move",
            token => _printersService.MoveAsync(id, req.X, req.Y, req.Z, req.F, token),
            ct);
    }

    [HttpPost("{id:guid}/moveto")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    [ProducesResponseType(502)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> MoveToAsync(Guid id, [FromBody] MoveRequest req, CancellationToken ct)
    {
        if (req is null)
        {
            return BadRequest("Request body is required.");
        }

        return await ExecuteDirectOutcomeControlAsync(
            id,
            "move_to",
            "move_to",
            token => _printersService.MoveToAsync(id, req.X, req.Y, req.Z, req.F, token),
            ct);
    }

    private async Task<ActionResult<CommandResult>> ExecuteDirectBooleanControlAsync(
        Guid printerId,
        string operation,
        string telemetryOperation,
        Func<CancellationToken, Task<bool>> backendCall,
        CancellationToken ct,
        PrinterActuationResult? acquired = null)
    {
        PrinterActuationResult begin = acquired ?? await BeginPhysicalControlAsync(
            printerId,
            operation,
            ct);
        if (!begin.Success || begin.Lease is null)
        {
            return MapActuationDenial(begin);
        }

        try
        {
            bool accepted = await backendCall(ct);
            _telemetryService.RecordPrinterOperation(
                telemetryOperation,
                printerId.ToString(),
                accepted);
            if (accepted)
            {
                await _physicalActuationService!.CompleteDirectAsync(
                    begin.Lease,
                    accepted: true,
                    ct: ct);
                return new CommandResult(true, null);
            }

            await _physicalActuationService!.MarkDirectUnknownAsync(
                begin.Lease,
                "backend_control_outcome_unknown",
                CancellationToken.None);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new CommandResult(
                    false,
                    "The backend did not prove whether the physical command was applied; reconciliation is required."));
        }
        catch (OperationCanceledException)
        {
            await _physicalActuationService!.MarkDirectUnknownAsync(
                begin.Lease,
                "backend_control_cancelled_after_send",
                CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await _physicalActuationService!.MarkDirectUnknownAsync(
                begin.Lease,
                "backend_control_exception",
                CancellationToken.None);
            _logger.LogWarning(
                exception,
                "Physical operation {Operation} has an unknown outcome on printer {PrinterId}",
                LogSanitizer.Sanitize(operation),
                printerId);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new CommandResult(
                    false,
                    "The physical command outcome is unknown; reconciliation is required."));
        }
    }

    private async Task<ActionResult<CommandResult>> ExecuteDirectOutcomeControlAsync(
        Guid printerId,
        string operation,
        string telemetryOperation,
        Func<CancellationToken, Task<Farm.Infrastructure.Services.Printers.PrinterControlOutcome>> backendCall,
        CancellationToken ct)
    {
        PrinterActuationResult begin = await BeginPhysicalControlAsync(
            printerId,
            operation,
            ct);
        if (!begin.Success || begin.Lease is null)
        {
            return MapActuationDenial(begin);
        }

        try
        {
            Farm.Infrastructure.Services.Printers.PrinterControlOutcome outcome =
                await backendCall(ct);
            bool accepted =
                outcome == Farm.Infrastructure.Services.Printers.PrinterControlOutcome.Ok;
            _telemetryService.RecordPrinterOperation(
                telemetryOperation,
                printerId.ToString(),
                accepted);
            if (outcome == Farm.Infrastructure.Services.Printers.PrinterControlOutcome.BackendUnreachable)
            {
                await _physicalActuationService!.MarkDirectUnknownAsync(
                    begin.Lease,
                    "backend_unreachable",
                    CancellationToken.None);
            }
            else
            {
                await _physicalActuationService!.CompleteDirectAsync(
                    begin.Lease,
                    accepted,
                    accepted ? null : outcome.ToString(),
                    ct);
            }

            return MapControlOutcome(outcome);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await _physicalActuationService!.MarkDirectUnknownAsync(
                begin.Lease,
                "backend_control_exception",
                CancellationToken.None);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new CommandResult(
                    false,
                    "The physical command outcome is unknown; reconciliation is required."));
        }
    }

    private async Task<ActionResult<CommandResult>> ExecuteDirectCommandControlAsync(
        Guid printerId,
        string operation,
        string telemetryOperation,
        Func<CancellationToken, Task<CommandResult>> backendCall,
        CancellationToken ct)
    {
        PrinterActuationResult begin = await BeginPhysicalControlAsync(
            printerId,
            operation,
            ct);
        if (!begin.Success || begin.Lease is null)
        {
            return MapActuationDenial(begin);
        }

        try
        {
            CommandResult result = await backendCall(ct);
            _telemetryService.RecordPrinterOperation(
                telemetryOperation,
                printerId.ToString(),
                result.Success);
            if (result.Success)
            {
                await _physicalActuationService!.CompleteDirectAsync(
                    begin.Lease,
                    accepted: true,
                    ct: ct);
            }
            else
            {
                await _physicalActuationService!.MarkDirectUnknownAsync(
                    begin.Lease,
                    "backend_control_outcome_unknown",
                    CancellationToken.None);
            }

            return MapCommandResult(result);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await _physicalActuationService!.MarkDirectUnknownAsync(
                begin.Lease,
                "backend_control_exception",
                CancellationToken.None);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new CommandResult(
                    false,
                    "The physical command outcome is unknown; reconciliation is required."));
        }
    }

    private async Task<ActionResult<CommandResult>> ExecuteActiveCommandControlAsync(
        Guid printerId,
        string operation,
        string telemetryOperation,
        Func<CancellationToken, Task<CommandResult>> backendCall,
        CancellationToken ct)
    {
        if (_physicalActuationService is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new CommandResult(false, "The physical actuation service is unavailable."));
        }

        PrinterActuationResult begin = await _physicalActuationService.AcquireActiveAsync(
            printerId,
            QueueActorIdentity.Resolve(User),
            operation,
            ct);
        if (!begin.Success || begin.Lease is null)
        {
            return MapActuationDenial(begin);
        }

        try
        {
            CommandResult result = await backendCall(ct);
            _telemetryService.RecordPrinterOperation(
                telemetryOperation,
                printerId.ToString(),
                result.Success);
            if (result.Success)
            {
                await _physicalActuationService.CompleteDirectAsync(
                    begin.Lease,
                    accepted: true,
                    ct: ct);
            }
            else
            {
                await _physicalActuationService.MarkDirectUnknownAsync(
                    begin.Lease,
                    "backend_control_outcome_unknown",
                    CancellationToken.None);
            }

            return MapCommandResult(result);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await _physicalActuationService.MarkDirectUnknownAsync(
                begin.Lease,
                "backend_control_exception",
                CancellationToken.None);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new CommandResult(
                    false,
                    "The physical command outcome is unknown; reconciliation is required."));
        }
    }

    private async Task<PrinterActuationResult> BeginPhysicalControlAsync(
        Guid printerId,
        string operation,
        CancellationToken ct)
    {
        if (_physicalActuationService is null)
        {
            return new PrinterActuationResult(
                PrinterActuationResultCode.FenceConflict,
                Detail: "The physical actuation service is unavailable.");
        }

        return await _physicalActuationService.AcquireDirectAsync(
            printerId,
            QueueActorIdentity.Resolve(User),
            operation,
            ct);
    }

    private async Task<ActionResult<CommandResult>> QueueLifecycleControlAsync(
        Guid printerId,
        string operation,
        string telemetryOperation,
        CancellationToken ct)
    {
        if (_physicalActuationService is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new CommandResult(false, "The physical actuation service is unavailable."));
        }

        PrinterActuationResult queued = await _physicalActuationService.QueueLifecycleAsync(
            printerId,
            QueueActorIdentity.Resolve(User),
            operation,
            ct);
        _telemetryService.RecordPrinterOperation(
            telemetryOperation,
            printerId.ToString(),
            queued.Success);
        return queued.Success
            ? Accepted(new CommandResult(true, "Attempt-bound control command queued."))
            : MapActuationDenial(queued);
    }

    private ActionResult<CommandResult> MapActuationDenial(PrinterActuationResult result) =>
        result.Code switch
        {
            PrinterActuationResultCode.PrinterNotFound =>
                NotFound(new CommandResult(false, "Printer not found.")),
            PrinterActuationResultCode.PrinterBusy or
                PrinterActuationResultCode.FenceConflict or
                PrinterActuationResultCode.ConcurrencyConflict =>
                Conflict(new CommandResult(false, result.Detail ?? "Printer is busy.")),
            _ => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new CommandResult(false, result.Detail ?? "Physical actuation is unavailable.")),
        };

    private ActionResult? BindPrinterIfMatch(Printer printer)
    {
        AppDbContext? db = ResolveAppDbContext();
        if (db is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "printer_revision_service_unavailable" });
        }

        string? supplied = Request.Headers.IfMatch.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new { error = "precondition_required", detail = "If-Match is required." });
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(
                supplied.Trim().TrimStart('W', '/').Trim('"'));
        }
        catch (FormatException)
        {
            return BadRequest(new { error = "If-Match must be a base-64 encoded ETag." });
        }

        if (printer.RowVersion is not { Length: > 0 } actual ||
            !expected.SequenceEqual(actual))
        {
            return PrinterRevisionConflict();
        }

        db.Entry(printer)
            .Property(candidate => candidate.Revision)
            .OriginalValue = RevisionETag.Decode(expected);
        return null;
    }

    private AppDbContext? ResolveAppDbContext() =>
        _appDbContext ??
        HttpContext.RequestServices.GetService<AppDbContext>();

    private ObjectResult PrinterRevisionConflict() =>
        StatusCode(
            StatusCodes.Status412PreconditionFailed,
            new { error = "printer_revision_conflict" });

    private void WritePrinterEtag(Printer printer)
    {
        string? encoded = EncodeRowVersion(printer.RowVersion);
        if (encoded is not null)
        {
            Response.Headers.ETag = $"\"{encoded}\"";
        }
    }

    private static string? EncodeRowVersion(byte[]? rowVersion) =>
        rowVersion is { Length: > 0 } ? Convert.ToBase64String(rowVersion) : null;

    private ActionResult<CommandResult> MapControlOutcome(Farm.Infrastructure.Services.Printers.PrinterControlOutcome outcome)
    {
        return outcome switch
        {
            Farm.Infrastructure.Services.Printers.PrinterControlOutcome.Ok =>
                new CommandResult(true, null),
            Farm.Infrastructure.Services.Printers.PrinterControlOutcome.NotFound =>
                NotFound(new CommandResult(false, "Printer not found.")),
            Farm.Infrastructure.Services.Printers.PrinterControlOutcome.BackendBusy =>
                Conflict(new CommandResult(false, "Printer firmware refused the command (busy).")),
            Farm.Infrastructure.Services.Printers.PrinterControlOutcome.BackendUnsupported =>
                StatusCode(StatusCodes.Status502BadGateway, new CommandResult(false, "Backend does not support this command.")),
            Farm.Infrastructure.Services.Printers.PrinterControlOutcome.BackendUnreachable =>
                StatusCode(StatusCodes.Status502BadGateway, new CommandResult(false, "Backend unreachable or returned an error.")),
            _ => StatusCode(StatusCodes.Status502BadGateway, new CommandResult(false, "Command failed.")),
        };
    }

    [HttpPost("{id:guid}/pause")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> PauseAsync(Guid id, CancellationToken ct)
    {
        return await QueueLifecycleControlAsync(id, "pause", "pause", ct);
    }

    [HttpPost("{id:guid}/resume")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> ResumeAsync(Guid id, CancellationToken ct)
    {
        return await QueueLifecycleControlAsync(id, "resume", "resume", ct);
    }

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> CancelAsync(Guid id, CancellationToken ct)
    {
        return await QueueLifecycleControlAsync(id, "cancel", "cancel", ct);
    }

    [HttpPost("{id:guid}/emergency-stop")]
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> EmergencyStopAsync(Guid id, CancellationToken ct)
    {
        PrinterActuationResult direct = await BeginPhysicalControlAsync(
            id,
            "emergencystop",
            ct);
        if (direct.Code == PrinterActuationResultCode.PrinterBusy)
        {
            return await QueueLifecycleControlAsync(
                id,
                "emergencystop",
                "emergency_stop",
                ct);
        }

        return await ExecuteDirectBooleanControlAsync(
            id,
            "emergencystop",
            "emergency_stop",
            token => _printersService.EmergencyStopAsync(id, token),
            ct,
            direct);
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
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
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
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> FirmwareRestartAsync(Guid id, CancellationToken ct)
    {
        return await ExecuteDirectBooleanControlAsync(
            id,
            "firmware_restart",
            "firmware_restart",
            token => _printersService.FirmwareRestartAsync(id, token),
            ct);
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
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> DisableMotorsAsync(Guid id, CancellationToken ct)
    {
        return await ExecuteDirectBooleanControlAsync(
            id,
            "disable_motors",
            "disable_motors",
            token => _printersService.DisableMotorsAsync(id, token),
            ct);
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
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
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

        if (BindPrinterIfMatch(p) is { } precondition)
        {
            return precondition;
        }

        // Send save commands to the printer firmware and verify success
        PrinterActuationLease? physicalLease = null;
        if (request.SaveToFirmware)
        {
            PrinterActuationResult begin = await BeginPhysicalControlAsync(
                id,
                "save_z_offset",
                ct);
            if (!begin.Success || begin.Lease is null)
            {
                return MapActuationDenial(begin);
            }

            physicalLease = begin.Lease;

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
                    await _physicalActuationService!.MarkDirectUnknownAsync(
                        physicalLease,
                        "z_offset_firmware_outcome_unknown",
                        CancellationToken.None);
                    _telemetryService.RecordPrinterOperation("save_z_offset", id.ToString(), false);
                    return StatusCode(
                        StatusCodes.Status503ServiceUnavailable,
                        new CommandResult(
                            false,
                            "The firmware did not prove whether the Z-offset command was applied."));
                }
            }
        }

        // Persist the Z-offset to the database only after firmware success
        p.ZOffsetMm = offsetMm;
        p.LastZOffsetCalibrationAt = DateTime.UtcNow;
        try
        {
            await _printersService.SaveChangesAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            if (physicalLease is not null)
            {
                await _physicalActuationService!.MarkDirectUnknownAsync(
                    physicalLease,
                    "printer_revision_conflict_after_physical_control",
                    CancellationToken.None);
            }

            return PrinterRevisionConflict();
        }

        if (physicalLease is not null)
        {
            await _physicalActuationService!.CompleteDirectAsync(
                physicalLease,
                accepted: true,
                ct: ct);
        }

        WritePrinterEtag(p);

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
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> LoadFilamentAsync(Guid id, CancellationToken ct)
    {
        return await ExecuteDirectCommandControlAsync(
            id,
            "filament_load",
            "load_filament",
            token => _printersService.LoadFilamentAsync(id, token),
            ct);
    }

    /// <summary>
    /// Unloads filament from the extruder and returns residual weight of the outgoing spool.
    /// The residual weight is captured from Spoolman before the unload command is sent so the
    /// operator's "return to shelf" workflow can log inventory without extra client round-trips.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="toolheadIndex">
    /// Optional zero-based toolhead / MMU-gate / U1-lane index whose spool is being unloaded.
    /// When omitted, the outgoing spool defaults to <c>Printer.CurrentSpoolId</c> falling
    /// back to the primary toolhead's <c>CurrentSpoolId</c> — the legacy single-tool path.
    /// Guided swap flow supplies the target lane on multi-slot printers.
    /// </param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result including success/failure, message, spool ID, material, and residual weight (g).</returns>
    /// <response code="200">Filament unload command sent successfully.</response>
    /// <response code="400">If the command failed (backend error, unsupported capability, unknown toolhead index).</response>
    /// <response code="404">If the printer with the specified ID was not found.</response>
    [HttpPost("{id:guid}/filament-unload")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(FilamentUnloadResult), 200)]
    [ProducesResponseType(typeof(FilamentUnloadResult), 400)]
    [ProducesResponseType(typeof(FilamentUnloadResult), 404)]
    public async Task<ActionResult<FilamentUnloadResult>> UnloadFilamentAsync(
        Guid id,
        [FromQuery] int? toolheadIndex,
        CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.Submit, ct))
        {
            return NotFound();
        }

        FilamentUnloadResult result = await _printersService.UnloadFilamentAsync(id, toolheadIndex, ct);
        _telemetryService.RecordPrinterOperation("unload_filament", id.ToString(), result.Success);
        return MapFilamentUnloadResult(result);
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
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> ChangeFilamentAsync(Guid id, CancellationToken ct)
    {
        return await ExecuteDirectCommandControlAsync(
            id,
            "filament_change",
            "change_filament",
            token => _printersService.ChangeFilamentAsync(id, token),
            ct);
    }

    /// <summary>Performs a bounded relative extrusion or retraction for maintenance.</summary>
    [HttpPost("{id:guid}/extrude")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CommandResult), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CommandResult>> ExtrudeFilamentAsync(
        Guid id,
        [FromBody] ExtrudeFilamentRequest request,
        CancellationToken ct)
    {
        if (!double.IsFinite(request.DistanceMm) ||
            request.DistanceMm == 0 ||
            Math.Abs(request.DistanceMm) > 100)
        {
            return BadRequest(new CommandResult(
                false,
                "Extrusion distance must be between -100 and 100 mm and cannot be zero."));
        }

        if (request.FeedrateMmPerMinute is < 1 or > 6000)
        {
            return BadRequest(new CommandResult(
                false,
                "Extrusion feedrate must be between 1 and 6000 mm/min."));
        }

        string distance = request.DistanceMm.ToString(
            "0.###",
            CultureInfo.InvariantCulture);
        string command =
            $"M83\nG1 E{distance} F{request.FeedrateMmPerMinute}\nM82";
        return await ExecuteDirectBooleanControlAsync(
            id,
            "extrude_filament",
            "extrude_filament",
            token => _printersService.SendGcodeAsync(id, command, token),
            ct);
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
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> MmuChangeToolAsync(Guid id, int tool, CancellationToken ct)
    {
        if (tool < 0 || tool > 16)
        {
            return BadRequest(new CommandResult(false, "Tool index must be between 0 and 16."));
        }

        return await ExecuteDirectBooleanControlAsync(
            id,
            "mmu_change_tool",
            "mmu_change_tool",
            token => _printersService.SendGcodeAsync(id, $"MMU_CHANGE_TOOL TOOL={tool}", token),
            ct);
    }

    /// <summary>
    /// Ejects/unloads filament from the MMU.
    /// Sends MMU_EJECT to the printer via Happy Hare.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpPost("{id:guid}/mmu/eject")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> MmuEjectAsync(Guid id, CancellationToken ct)
    {
        return await ExecuteDirectBooleanControlAsync(
            id,
            "mmu_eject",
            "mmu_eject",
            token => _printersService.SendGcodeAsync(id, "MMU_EJECT", token),
            ct);
    }

    /// <summary>
    /// Loads filament from the currently selected MMU gate into the extruder.
    /// Sends MMU_LOAD to the printer via Happy Hare.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpPost("{id:guid}/mmu/load")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> MmuLoadAsync(Guid id, CancellationToken ct)
    {
        return await ExecuteDirectBooleanControlAsync(
            id,
            "mmu_load",
            "mmu_load",
            token => _printersService.SendGcodeAsync(id, "MMU_LOAD", token),
            ct);
    }

    /// <summary>
    /// Homes the MMU unit.
    /// Sends MMU_HOME to the printer via Happy Hare.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpPost("{id:guid}/mmu/home")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> MmuHomeAsync(Guid id, CancellationToken ct)
    {
        return await ExecuteDirectBooleanControlAsync(
            id,
            "mmu_home",
            "mmu_home",
            token => _printersService.SendGcodeAsync(id, "MMU_HOME", token),
            ct);
    }

    /// <summary>
    /// Pre-selects an MMU tool without loading filament.
    /// Sends MMU_SELECT_TOOL TOOL=N to the printer via Happy Hare.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="tool">The tool/gate index to pre-select (0-based).</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpPost("{id:guid}/mmu/select-tool/{tool:int}")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> MmuSelectToolAsync(Guid id, int tool, CancellationToken ct)
    {
        if (tool < 0 || tool > 16)
        {
            return BadRequest(new CommandResult(false, "Tool index must be between 0 and 16."));
        }

        return await ExecuteDirectBooleanControlAsync(
            id,
            "mmu_select_tool",
            "mmu_select_tool",
            token => _printersService.SendGcodeAsync(id, $"MMU_SELECT_TOOL TOOL={tool}", token),
            ct);
    }

    /// <summary>
    /// Recovers the MMU from an error state.
    /// Sends MMU_RECOVER to the printer via Happy Hare.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    [HttpPost("{id:guid}/mmu/recover")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> MmuRecoverAsync(Guid id, CancellationToken ct)
    {
        return await ExecuteDirectBooleanControlAsync(
            id,
            "mmu_recover",
            "mmu_recover",
            token => _printersService.SendGcodeAsync(id, "MMU_RECOVER", token),
            ct);
    }

    /// <summary>
    /// Executes a bounded Qidibox or AFC gate action. The client selects typed fields; only
    /// server-generated allowlisted macros can reach the backend.
    /// </summary>
    [HttpPost("{id:guid}/mmu/gate-action")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(CommandResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CommandResult), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CommandResult>> MmuGateActionAsync(
        Guid id,
        [FromBody] MmuGateActionRequest request,
        CancellationToken ct)
    {
        string protocol = request.Protocol.Trim().ToLowerInvariant();
        string action = request.Action.Trim().ToLowerInvariant();
        string? command = null;
        if (protocol == "qidibox" &&
            request.GateIndex is >= 0 and <= 16)
        {
            command = action switch
            {
                "load" => $"T{request.GateIndex}",
                "unload" => $"UNLOAD_T{request.GateIndex}",
                "eject" => $"EJECT_T{request.GateIndex}",
                _ => null,
            };
        }
        else if (protocol == "afc" &&
                 request.LaneName is { Length: > 0 } laneName &&
                 laneName.Length <= 64 &&
                 Regex.IsMatch(
                     laneName,
                     "^[A-Za-z0-9_-]+$",
                     RegexOptions.CultureInvariant))
        {
            command = action switch
            {
                "load" => $"CHANGE_TOOL LANE={laneName}",
                "unload" => $"TOOL_UNLOAD LANE={laneName}",
                _ => null,
            };
        }

        if (command is null)
        {
            return BadRequest(new CommandResult(
                false,
                "Unsupported or invalid MMU gate action."));
        }

        return await ExecuteDirectBooleanControlAsync(
            id,
            $"mmu_{protocol}_{action}",
            "mmu_gate_action",
            token => _printersService.SendGcodeAsync(id, command, token),
            ct);
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
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> SetActiveSpoolAsync(Guid id, [FromBody] SetActiveSpoolRequest? request, CancellationToken ct)
    {
        Printer? printer = await _printersService.FindByIdAsync(id, ct);
        if (printer is null)
        {
            return NotFound(new CommandResult(false, "Printer not found."));
        }

        if (BindPrinterIfMatch(printer) is { } precondition)
        {
            return precondition;
        }

        ActionResult<CommandResult> result = await ExecuteDirectCommandControlAsync(
            id,
            "set_active_spool",
            "set_active_spool",
            token => _printersService.SetActiveSpoolAsync(id, request?.SpoolId, token),
            ct);
        if (result.Result is OkObjectResult)
        {
            WritePrinterEtag(printer);
        }

        return result;
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
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

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
    /// <para>
    /// The server enforces the guided-swap material check here: if the scanned spool
    /// does not match the expected material for this toolhead (per active/queued jobs)
    /// and the request does not carry an explicit override, the assignment is rejected
    /// with <c>409 Conflict</c> and a typed <see cref="Farm.Infrastructure.Services.Printers.SwapValidationResultDto"/>
    /// body. This makes the hard-stop authoritative — thin clients cannot bypass it by
    /// skipping the pre-flight validation endpoint.
    /// </para>
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="toolheadIndex">Zero-based index of the toolhead (T0, T1, T2, etc.).</param>
    /// <param name="request">Request containing the spool ID to assign and optional override flag.</param>
    /// <param name="validator">Injected swap validator (server-enforced material check).</param>
    /// <param name="featureGate">Injected operator-feature gate (#725) controlling the guided-swap path.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Result indicating success or failure with descriptive message.</returns>
    /// <response code="200">Spool was assigned successfully.</response>
    /// <response code="400">If the request failed (invalid spool ID, Spoolman not configured).</response>
    /// <response code="404">If the printer or toolhead was not found.</response>
    /// <response code="409">Material mismatch and no valid override — body carries the SwapValidationResultDto.</response>
    /// <remarks>
    /// Operator-feature gate integration (issue OlyForge3D/PrintFarmer#725): the
    /// server-enforced validation / override-audit path is wrapped in a
    /// <c>guidedSwapEnabled</c> check. This binding endpoint itself stays available even
    /// when the guided flow is disabled (it is a direct capability-gated control per the
    /// #710 acceptance addendum); when disabled it reverts to the pre-#710 blind
    /// assignment (no pre-flight validation 409, no override log/telemetry).
    /// </remarks>
    [HttpPut("{id:guid}/toolheads/{toolheadIndex:int}/spool")]
    [Idempotent(IdempotencyRouteKeys.PrinterToolheadSpoolBind)]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(typeof(Farm.Infrastructure.Services.Printers.SwapValidationResultDto), 409)]
    public async Task<ActionResult<CommandResult>> SetToolheadSpoolAsync(
        Guid id,
        int toolheadIndex,
        [FromBody] SetActiveSpoolRequest? request,
        [FromServices] Farm.Infrastructure.Services.Printers.IPrinterToolheadSwapValidator validator,
        [FromServices] Farm.Infrastructure.Services.OperatorFeatures.IOperatorFeatureGate featureGate,
        CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.Submit, ct))
        {
            return NotFound(new CommandResult(false, "Printer not found."));
        }

        if (request?.SpoolId is not { } spoolId)
        {
            return BadRequest(new CommandResult(false, "SpoolId is required"));
        }

        Printer? printer = await _printersService.FindByIdAsync(id, ct);
        if (printer is null)
        {
            return NotFound(new CommandResult(false, "Printer not found."));
        }

        // #900 revision guard: this binding endpoint is If-Match protected. Verify the
        // supplied precondition against the current printer revision before any mutation.
        if (BindPrinterIfMatch(printer) is { } precondition)
        {
            return precondition;
        }

        // An override is only honoured when the operator both set the flag AND supplied a
        // non-empty reason (issue #710 contract: mismatch overrides are recorded with a
        // reason). A flag without a reason is NOT a valid override.
        bool hasOverrideIntent = request.OverrideMismatch
            && !string.IsNullOrWhiteSpace(request.OverrideReason);

        // Guided-swap gate (#725): the server-enforced material check and override audit
        // only apply when guidedSwapEnabled is on. When disabled, revert to the pre-#710
        // blind assignment so the direct spool-binding control remains usable.
        bool guidedSwapEnabled = await featureGate.IsEnabledAsync(
            Farm.Infrastructure.Services.OperatorFeatures.OperatorFeature.GuidedSwap, ct).ConfigureAwait(false);

        // Audit context is built ONLY for an authorized mismatch override and passed to the
        // service so the durable record commits atomically with the binding (B6). Null on
        // every other path (ok / disabled / unknown / not-found).
        Farm.Infrastructure.Services.Printers.FilamentSwapOverrideContext? overrideAudit = null;

        if (guidedSwapEnabled)
        {
            // B1: ALWAYS validate before any binding — even when an override flag/reason is
            // present. The override can only be honoured for a genuine mismatch (below).
            Farm.Infrastructure.Services.Printers.SwapValidationResult validation =
                await validator.ValidateAsync(id, toolheadIndex, spoolId, ct).ConfigureAwait(false);

            // B2: an invalid / unresolved lane must NEVER fall through to a blind bind.
            switch (validation.Outcome)
            {
                case Farm.Infrastructure.Services.Printers.SwapValidationOutcome.PrinterNotFound:
                    return NotFound(new CommandResult(false, $"Printer {id} not found"));
                case Farm.Infrastructure.Services.Printers.SwapValidationOutcome.ToolheadNotFound:
                    return NotFound(new CommandResult(false, $"Toolhead index {toolheadIndex} not found on printer {id}"));
                case Farm.Infrastructure.Services.Printers.SwapValidationOutcome.ToolheadOutOfRange:
                    return BadRequest(new CommandResult(false, $"Toolhead index {toolheadIndex} is out of range"));
            }

            Farm.Infrastructure.Services.Printers.SwapValidationResultDto body = validation.Result!;

            switch (body.Status)
            {
                case Farm.Infrastructure.Services.Printers.SwapValidationStatus.Ok:
                    // Normal write permitted; no override, no audit.
                    break;

                case Farm.Infrastructure.Services.Printers.SwapValidationStatus.Mismatch:
                    // Override permitted ONLY for a real mismatch AND explicit flag AND reason.
                    if (!hasOverrideIntent)
                    {
                        return Conflict(body);
                    }

                    overrideAudit = new Farm.Infrastructure.Services.Printers.FilamentSwapOverrideContext(
                        UserId: User?.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                        UserName: User?.Identity?.Name,
                        Reason: request.OverrideReason!.Trim(),
                        ExpectedMaterial: body.Expected,
                        ScannedMaterial: body.Scanned,
                        AffectedJobIds: body.AffectedJobs.Select(j => j.JobId).ToList());
                    break;

                case Farm.Infrastructure.Services.Printers.SwapValidationStatus.Unknown:
                default:
                    // B7: never override unknown — no write, no audit.
                    return Conflict(body);
            }
        }

        // C1: the guided binding contract (fail closed on a null commit-time re-resolution)
        // applies only when the guided-swap feature is on. When disabled we pass Direct so the
        // generic/legacy direct binding semantics are preserved unchanged. Guided mode is NOT
        // inferred from overrideAudit — a normal guided `ok` bind has no audit but must still
        // fail closed.
        Farm.Infrastructure.Services.Printers.SpoolBindPolicy bindPolicy = guidedSwapEnabled
            ? Farm.Infrastructure.Services.Printers.SpoolBindPolicy.Guided
            : Farm.Infrastructure.Services.Printers.SpoolBindPolicy.Direct;

        CommandResult result = await _printersService
            .SetToolheadSpoolAsync(id, toolheadIndex, spoolId, overrideAudit, bindPolicy, ct)
            .ConfigureAwait(false);
        _telemetryService.RecordPrinterOperation("set_toolhead_spool", id.ToString(), result.Success);

        // Emit override telemetry ONLY after an authorized-override assignment succeeded. The
        // durable audit row is written atomically inside the service; this is best-effort
        // observability on top. A failed write leaves neither audit row nor telemetry.
        if (overrideAudit is not null && result.Success)
        {
            _logger.LogWarning(
                "Toolhead spool override: user {User} loaded spool {SpoolId} on printer {PrinterId} toolhead T{ToolheadIndex} despite mismatch. Reason: {Reason}",
                LogSanitizer.Sanitize(overrideAudit.UserName ?? overrideAudit.UserId ?? "(unknown)"),
                spoolId,
                id,
                toolheadIndex,
                LogSanitizer.Sanitize(overrideAudit.Reason));
            _telemetryService.RecordPrinterOperation("set_toolhead_spool_override", id.ToString(), true);
        }

        if (result is ToolheadSpoolBindResult
            {
                FailureKind: ToolheadSpoolBindFailureKind.TopologyConflict,
            })
        {
            return Conflict(result);
        }

        // Advance and surface the printer revision only on a committed binding so callers
        // can chain subsequent If-Match requests against the new ETag.
        if (result.Success)
        {
            WritePrinterEtag(printer);
        }

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
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> ClearToolheadSpoolAsync(
        Guid id,
        int toolheadIndex,
        CancellationToken ct)
    {
        Printer? printer = await _printersService.FindByIdAsync(id, ct);
        if (printer is null)
        {
            return NotFound(new CommandResult(false, "Printer not found."));
        }

        if (BindPrinterIfMatch(printer) is { } precondition)
        {
            return precondition;
        }

        ActionResult<CommandResult> result = await ExecuteDirectCommandControlAsync(
            id,
            "clear_toolhead_spool",
            "clear_toolhead_spool",
            token => _printersService.ClearToolheadSpoolAsync(
                id,
                toolheadIndex,
                token),
            ct);
        if (result.Result is OkObjectResult)
        {
            WritePrinterEtag(printer);
        }

        return result;
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
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CommandResult>> EnsureMmuToolheadsAsync(
        Guid id,
        CancellationToken ct)
    {
        Printer? printer = await _printersService.FindByIdAsync(id, ct);
        if (printer is null)
        {
            return NotFound(new CommandResult(false, "Printer not found."));
        }

        if (BindPrinterIfMatch(printer) is { } precondition)
        {
            return precondition;
        }

        ActionResult<CommandResult> result = await ExecuteDirectCommandControlAsync(
            id,
            "ensure_mmu_toolheads",
            "ensure_mmu_toolheads",
            token => _printersService.EnsureMmuToolheadsAsync(id, token),
            ct);
        if (result.Result is OkObjectResult)
        {
            WritePrinterEtag(printer);
        }

        return result;
    }

    /// <summary>
    /// Validates a scanned Spoolman spool against the expected material for a specific
    /// toolhead on the given printer. Thin endpoint that backs the guided filament swap flow.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="toolheadIndex">Zero-based toolhead index (T0, T1, T2, ...).</param>
    /// <param name="spoolId">Spoolman spool identifier being scanned.</param>
    /// <param name="validator">Injected swap validator service.</param>
    /// <param name="featureGate">Injected operator-feature gate (#725).</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Typed validation result describing ok/mismatch, expected/scanned material, and affected jobs.</returns>
    /// <response code="200">Validation completed (ok or mismatch result in body).</response>
    /// <response code="400">If the query is missing or invalid (e.g., spoolId missing).</response>
    /// <response code="404">If the printer or toolhead was not found, or the guided-swap feature is disabled (ProblemDetails code=featureDisabled).</response>
    /// <remarks>
    /// Operator-feature gate integration (issue OlyForge3D/PrintFarmer#725): this guided-swap
    /// validation endpoint is gated by <c>guidedSwapEnabled</c>. When disabled it short-circuits
    /// to <c>404 Not Found</c> with ProblemDetails extension <c>code: "featureDisabled"</c> before
    /// any read or telemetry, matching the shape defined by #725.
    /// </remarks>
    [HttpGet("{id:guid}/toolheads/{toolheadIndex:int}/swap-validation")]
    [ProducesResponseType(typeof(Farm.Infrastructure.Services.Printers.SwapValidationResultDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<Farm.Infrastructure.Services.Printers.SwapValidationResultDto>> GetToolheadSwapValidationAsync(
        Guid id,
        int toolheadIndex,
        [FromQuery] int? spoolId,
        [FromServices] Farm.Infrastructure.Services.Printers.IPrinterToolheadSwapValidator validator,
        [FromServices] Farm.Infrastructure.Services.OperatorFeatures.IOperatorFeatureGate featureGate,
        CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

        // Guided-swap gate (#725): when disabled, return the standard featureDisabled 404
        // ProblemDetails before any read/validation/telemetry.
        if (!await featureGate.IsEnabledAsync(Farm.Infrastructure.Services.OperatorFeatures.OperatorFeature.GuidedSwap, ct).ConfigureAwait(false))
        {
            return Farm.Web.Api.Infrastructure.OperatorFeatures.OperatorFeatureProblemDetails.NotFound(
                featureGate,
                Farm.Infrastructure.Services.OperatorFeatures.OperatorFeature.GuidedSwap);
        }

        if (spoolId is null || spoolId <= 0)
        {
            return BadRequest(new CommandResult(false, "spoolId query parameter is required and must be positive."));
        }

        if (toolheadIndex < 0)
        {
            return BadRequest(new CommandResult(false, "toolheadIndex must be zero or greater."));
        }

        Farm.Infrastructure.Services.Printers.SwapValidationResult validation = await validator
            .ValidateAsync(id, toolheadIndex, spoolId.Value, ct)
            .ConfigureAwait(false);

        switch (validation.Outcome)
        {
            case Farm.Infrastructure.Services.Printers.SwapValidationOutcome.PrinterNotFound:
            case Farm.Infrastructure.Services.Printers.SwapValidationOutcome.ToolheadNotFound:
                return NotFound();
            case Farm.Infrastructure.Services.Printers.SwapValidationOutcome.ToolheadOutOfRange:
                return BadRequest(new CommandResult(false, $"Toolhead index {toolheadIndex} is out of range."));
        }

        Farm.Infrastructure.Services.Printers.SwapValidationResultDto? result = validation.Result;
        if (result is null)
        {
            return NotFound();
        }

        _telemetryService.RecordPrinterOperation(
            "swap_validation",
            id.ToString(),
            result.Status == Farm.Infrastructure.Services.Printers.SwapValidationStatus.Ok);
        return result;
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
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> EnableCameraAsync(Guid id, CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.Submit, ct))
        {
            return NotFound();
        }

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
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CommandResult>> DisableCameraAsync(Guid id, CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.Submit, ct))
        {
            return NotFound();
        }

        bool ok = await _printersService.DisableCameraAsync(id, ct);
        return !ok ? NotFound() : new CommandResult(true, null);
    }

    /// <summary>
    /// Retrieves authenticated same-origin camera proxy URLs for the specified printer.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Object containing relative stream and snapshot proxy URLs.</returns>
    /// <response code="200">Returns the camera URLs.</response>
    /// <response code="404">If the printer with the specified ID was not found or camera is not available.</response>
    /// <remarks>
    /// Raw camera targets remain server-side so private network details and embedded
    /// camera credentials are never disclosed to API clients.
    /// </remarks>
    [HttpGet("{id:guid}/camera/url")]
    [ProducesResponseType(typeof(CameraUrlResult), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CameraUrlResult>> GetCameraUrlAsync(Guid id, CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

        (string? streamUrl, string? snapshotUrl) = await _printersService.GetCameraUrlsForPrinterAsync(id, ct);
        return streamUrl == null && snapshotUrl == null
            ? NotFound()
            : new CameraUrlResult(
                streamUrl == null ? null : GetCameraProxyPath(id, "stream"),
                snapshotUrl == null ? null : GetCameraProxyPath(id, "snapshot"));
    }

    /// <summary>Streams camera content without disclosing its private target URL.</summary>
    [HttpGet("{id:guid}/camera/stream")]
    [ProducesResponseType(typeof(FileStreamResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(502)]
    public Task<IActionResult> ProxyCameraStreamAsync(Guid id, CancellationToken ct) =>
        ProxyCameraAsync(id, useSnapshot: false, ct);

    /// <summary>Returns a camera snapshot without disclosing its private target URL.</summary>
    [HttpGet("{id:guid}/camera/snapshot")]
    [ProducesResponseType(typeof(FileStreamResult), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(502)]
    public Task<IActionResult> ProxyCameraSnapshotAsync(Guid id, CancellationToken ct) =>
        ProxyCameraAsync(id, useSnapshot: true, ct);

    private static PrinterCameraUrlsDto CreateSafeCameraUrls(PrinterCameraUrlsDto camera) =>
        new(
            camera.Id,
            camera.Name,
            camera.CameraStreamUrl == null ? null : GetCameraProxyPath(camera.Id, "stream"),
            camera.CameraSnapshotUrl == null ? null : GetCameraProxyPath(camera.Id, "snapshot"));

    private static string GetCameraProxyPath(Guid printerId, string kind) =>
        $"/api/printers/{printerId:D}/camera/{kind}";

    private async Task<IActionResult> ProxyCameraAsync(Guid id, bool useSnapshot, CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

        (string? streamUrl, string? snapshotUrl) = await _printersService.GetCameraUrlsForPrinterAsync(id, ct);
        string? target = useSnapshot ? snapshotUrl : streamUrl;
        if (target is null)
        {
            return NotFound();
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? targetUri) ||
            (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("Camera target for printer {PrinterId} is not an HTTP(S) URL", id);
            return CameraProxyProblem("camera_target_invalid", "The configured camera target is invalid.");
        }

        EgressCheckResult egressCheck = await _egressGuard.CheckAsync(targetUri.ToString(), ct);
        if (!egressCheck.IsAllowed)
        {
            _logger.LogWarning(
                "Camera target for printer {PrinterId} denied by egress guard: {Reason}",
                id,
                LogSanitizer.Sanitize(egressCheck.DenyReason));
            return CameraProxyProblem("camera_target_invalid", "The configured camera target is invalid.");
        }

        // Reuse the exact address the egress guard just vetted for the real connection instead
        // of letting the hostname be re-resolved independently — otherwise a DNS-rebinding
        // attacker could swap the record between the check above and the connection below.
        Uri connectUri = egressCheck.ResolvedAddress is not null
            ? EgressGuard.CreatePinnedUri(targetUri, egressCheck.ResolvedAddress)
            : targetUri;

        try
        {
            HttpClient client = _httpClientFactory.CreateClient("VettedEgress");
            using var request = new HttpRequestMessage(HttpMethod.Get, connectUri);
            if (connectUri != targetUri)
            {
                request.Headers.Host = targetUri.IsDefaultPort
                    ? targetUri.Host
                    : $"{targetUri.Host}:{targetUri.Port}";
            }

            HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Camera proxy request for printer {PrinterId} returned {StatusCode}",
                    id,
                    response.StatusCode);
                response.Dispose();
                return CameraProxyProblem("camera_upstream_failed", "The camera did not return a successful response.");
            }

            Stream content = await response.Content.ReadAsStreamAsync(ct);
            HttpContext.Response.RegisterForDispose(response);
            string contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            if (response.Content.Headers.ContentLength is long contentLength)
            {
                HttpContext.Response.ContentLength = contentLength;
            }

            return File(content, contentType);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return CameraProxyProblem("camera_upstream_timeout", "The camera request timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Camera proxy request failed for printer {PrinterId}", id);
            return CameraProxyProblem("camera_upstream_unavailable", "The camera is unavailable.");
        }
    }

    private ObjectResult CameraProxyProblem(string code, string title) =>
        Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: title,
            type: $"https://printfarmer.dev/problems/{code}",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
            });

    [HttpPost("{id:guid}/files/upload")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
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

        PrinterActuationResult begin = await BeginPhysicalControlAsync(
            id,
            "gcode_upload",
            ct);
        if (!begin.Success || begin.Lease is null)
        {
            return begin.Code == PrinterActuationResultCode.PrinterNotFound
                ? NotFound(new { error = "printer_not_found" })
                : Conflict(new
                {
                    error = "physical_control_fence_conflict",
                    detail = begin.Detail,
                });
        }

        try
        {
            await using Stream fileStream = file.OpenReadStream();
            bool success = await _printersService.UploadGcodeAsync(id, file.FileName, fileStream, ct);
            if (!success)
            {
                await _physicalActuationService!.MarkDirectUnknownAsync(
                    begin.Lease,
                    "backend_upload_outcome_unknown",
                    CancellationToken.None);
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { error = "backend_upload_outcome_unknown" });
            }

            await _physicalActuationService!.CompleteDirectAsync(
                begin.Lease,
                accepted: true,
                ct: ct);
            return Ok(new UploadGcodeResultDto("File uploaded successfully", file.FileName));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await _physicalActuationService!.MarkDirectUnknownAsync(
                begin.Lease,
                "backend_upload_exception",
                CancellationToken.None);
            _logger.LogWarning(ex, "G-code upload outcome unknown for printer {PrinterId}", id);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "backend_upload_outcome_unknown" });
        }
    }

    [HttpGet("{id:guid}/files")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(PrinterFileDto[]), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<PrinterFileDto[]>> GetFileListAsync(Guid id, CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(
                id,
                PrinterGroupAccessLevel.View,
                ct))
        {
            return NotFound();
        }

        try
        {
            PrinterFileDto[] files = await _printersService.GetFileListAsync(id, ct);
            return Ok(files);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to list printer files for {PrinterId}",
                id);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "printer_file_list_unavailable" });
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
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
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

        if (!await CanAccessPrinterAsync(
                id,
                PrinterGroupAccessLevel.View,
                ct))
        {
            return NotFound();
        }

        try
        {
            byte[]? fileContent = await _printersService.DownloadPrinterFileAsync(id, filename, ct);
            if (fileContent == null)
            {
                return NotFound(new { error = "printer_file_not_found" });
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
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to download a file from printer {PrinterId}",
                id);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "printer_file_download_unavailable" });
        }
    }

    private static readonly IReadOnlyDictionary<string, string> ThumbnailContentTypesByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
        };

    private static readonly char[] PathSegmentSeparators = ['/', '\\'];

    /// <summary>
    /// Determines whether a backend-relative file path contains a path traversal segment
    /// (<c>.</c> or <c>..</c>) or is rooted, either of which could escape the printer backend's
    /// intended files subtree (e.g. Moonraker's <c>gcodes</c> root) when the path is later
    /// combined with the backend's base URL. This check runs before the extension allowlist so a
    /// traversal-shaped filename ending in an allowed image extension (e.g.
    /// <c>../../etc/passwd.png</c>) is still rejected.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT use <see cref="System.IO.Path.IsPathRooted(string)"/>: its notion of
    /// "rooted" is host-OS-dependent (e.g. on Linux, a leading backslash, a UNC <c>\\server\share</c>
    /// prefix, or a Windows drive letter like <c>C:\</c> are not considered rooted at all), while
    /// this endpoint must reject those shapes regardless of which OS it happens to run on. The
    /// checks below are evaluated as plain string patterns so behavior is identical on every host.
    /// </remarks>
    private static bool ContainsPathTraversal(string filename)
    {
        if (filename.StartsWith('/') || filename.StartsWith('\\'))
        {
            // Leading '/' (rooted on any OS) or leading '\' (a Windows-rooted path, and the
            // common prefix of a UNC share like \\server\share\thumb.png).
            return true;
        }

        if (filename.Length >= 2 && char.IsAsciiLetter(filename[0]) && filename[1] == ':')
        {
            // A Windows drive-letter prefix (e.g. C:\thumbs\evil.png or C:/thumbs/evil.png) is
            // never a valid backend-relative path, regardless of the host OS evaluating it.
            return true;
        }

        return filename.Split(PathSegmentSeparators).Any(segment => segment is "." or "..");
    }

    /// <summary>
    /// Returns an authenticated same-origin thumbnail image for a file on a printer's storage.
    /// </summary>
    /// <param name="id">Printer ID.</param>
    /// <param name="filename">The backend-relative thumbnail path (filename query parameter).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The thumbnail image content, without exposing the printer's private base URL.</returns>
    /// <remarks>
    /// Backend clients (e.g. Moonraker) only ever surface backend-relative thumbnail paths to
    /// callers - never an absolute internal URL - specifically so that this endpoint can proxy
    /// the bytes through an authenticated, same-origin request. See issue #1650. This is
    /// deliberately NOT [AllowAnonymous]: unlike GUID-keyed thumbnail endpoints elsewhere, the
    /// filename here is not an unguessable capability token, so per-printer group access control
    /// via <see cref="CanAccessPrinterAsync"/> must still apply.
    /// </remarks>
    /// <response code="200">Returns the thumbnail image content.</response>
    /// <response code="400">The filename query parameter is missing, empty, contains a path traversal segment, or is not a recognized image type.</response>
    /// <response code="404">The printer with the specified ID was not found, or the thumbnail does not exist on the printer.</response>
    /// <response code="503">An error occurred while retrieving the thumbnail from the printer.</response>
    [HttpGet("{id:guid}/files/thumbnail")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetFileThumbnailAsync(Guid id, [FromQuery] string filename, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return BadRequest(new { error = "filename query parameter is required" });
        }

        if (ContainsPathTraversal(filename))
        {
            return BadRequest(new { error = "filename must not contain path traversal segments" });
        }

        string extension = System.IO.Path.GetExtension(filename);
        if (!ThumbnailContentTypesByExtension.TryGetValue(extension, out string? contentType))
        {
            return BadRequest(new { error = "filename must reference a recognized image type" });
        }

        if (!await CanAccessPrinterAsync(
                id,
                PrinterGroupAccessLevel.View,
                ct))
        {
            return NotFound();
        }

        try
        {
            byte[]? content = await _printersService.DownloadPrinterFileAsync(id, filename, ct);
            if (content == null)
            {
                return NotFound(new { error = "printer_file_thumbnail_not_found" });
            }

            Response.Headers.XContentTypeOptions = "nosniff";
            return File(content, contentType);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Printer not found" });
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to retrieve a file thumbnail from printer {PrinterId}",
                id);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "printer_file_thumbnail_unavailable" });
        }
    }

    // File operations with body-based parameters (handles special characters in filenames)
    [HttpPost("{id:guid}/print")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(typeof(StartPrintResultDto), 200)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 409)]
    [ProducesResponseType(typeof(CommandResult), 500)]
    public async Task<ActionResult<CommandResult>> StartPrintAsync(Guid id, [FromBody] FileOperationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request?.FileName))
        {
            return BadRequest(new CommandResult(false, "fileName is required"));
        }

        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.Submit, ct))
        {
            return NotFound();
        }

        // =====================================================================
        // Starting a file that already lives on the printer is still a START PATH and
        // must go through the shared dispatch claim (issue #900, defect 5). Without it,
        // this endpoint could start a second print on a printer that already holds a
        // dispatch lease, or on a printer that is disabled/in maintenance.
        // =====================================================================
        if (_dispatchClaimService is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new CommandResult(false, "Dispatch claim service is not available."));
        }

        string actorSubject = QueueActorIdentity.Resolve(User);

        DispatchClaimResult claim = await _dispatchClaimService.AcquireAdHocClaimAsync(
            new AdHocDispatchClaimRequest(
                id,
                actorSubject,
                "PrinterFile",
                request.FileName,
                UseDeterministicFileName: false),
            ct);

        if (!claim.Success || claim.Attempt is null)
        {
            _logger.LogWarning(
                "Printer file-start denied on printer {PrinterId}: {Code}", id, claim.ErrorCode);

            return Conflict(new CommandResult(
                false,
                $"{claim.ErrorCode}: {claim.ErrorDetail}"));
        }

        Guid attemptId = claim.Attempt.Id;

        try
        {
            string backendFileName = claim.Attempt.BackendFileName ?? request.FileName;
            if (!await _dispatchClaimService.RecordBackendCallStartedAsync(
                    attemptId,
                    ct))
            {
                return Conflict(new CommandResult(
                    false,
                    "attempt_superseded: The dispatch attempt no longer owns the printer."));
            }

            bool success = await _printersService.StartPrintFromFileAsync(id, backendFileName, ct);

            if (!success)
            {
                bool applied = await _dispatchClaimService.RecordUnknownOutcomeAsync(
                    attemptId,
                    "The legacy backend did not prove whether the start command was accepted.",
                    CancellationToken.None);
                if (!applied)
                {
                    return Conflict(new CommandResult(
                        false,
                        "attempt_superseded: The dispatch attempt no longer owns the printer."));
                }

                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new CommandResult(
                        false,
                        "The printer start outcome could not be determined; reconciliation is required."));
            }

            if (!await _dispatchClaimService.RecordBackendAcceptedAsync(
                    attemptId,
                    backendJobId: null,
                    backendFileIdentity: backendFileName,
                    ct))
            {
                return Conflict(new CommandResult(
                    false,
                    "attempt_superseded: The dispatch attempt no longer owns the printer."));
            }

            return Ok(new CommandResult(true, "Print started successfully"));
        }
        catch (Exception ex)
        {
            // Unknown outcome — keep the lease and let reconciliation decide.
            bool applied = await _dispatchClaimService.RecordUnknownOutcomeAsync(
                attemptId,
                ex.Message,
                CancellationToken.None);
            if (!applied)
            {
                return Conflict(new CommandResult(
                    false,
                    "attempt_superseded: The dispatch attempt no longer owns the printer."));
            }

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new CommandResult(false, "The printer start outcome could not be determined; reconciliation is required."));
        }
    }

    [HttpDelete("{id:guid}/files")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(typeof(CommandResult), 200)]
    [ProducesResponseType(typeof(CommandResult), 500)]
    public async Task<ActionResult<CommandResult>> DeleteFileAsync(Guid id, [FromBody] FileOperationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request?.FileName))
        {
            return BadRequest(new CommandResult(false, "fileName is required"));
        }

        if (!await CanAccessPrinterAsync(
                id,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            return NotFound(new CommandResult(false, "Printer not found."));
        }

        PrinterActuationResult begin = await BeginPhysicalControlAsync(
            id,
            "printer_file_delete",
            ct);
        if (!begin.Success || begin.Lease is null)
        {
            return MapActuationDenial(begin);
        }

        try
        {
            bool success = await _printersService.DeletePrinterFileAsync(
                id,
                request.FileName,
                ct);
            await _physicalActuationService!.CompleteDirectAsync(
                begin.Lease,
                accepted: success,
                failureCode: success ? null : "printer_file_delete_rejected",
                ct: ct);
            return success
                ? Ok(new CommandResult(true, "File deleted successfully"))
                : Conflict(new CommandResult(
                    false,
                    "The printer did not accept the file deletion."));
        }
        catch (OperationCanceledException)
        {
            await _physicalActuationService!.MarkDirectUnknownAsync(
                begin.Lease,
                "printer_file_delete_cancelled_after_send",
                CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            await _physicalActuationService!.MarkDirectUnknownAsync(
                begin.Lease,
                "printer_file_delete_outcome_unknown",
                CancellationToken.None);
            _logger.LogWarning(
                exception,
                "Printer file deletion outcome unknown for {PrinterId}",
                id);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new CommandResult(
                    false,
                    "The file deletion outcome is unknown; reconciliation is required."));
        }
    }

    /// <summary>
    /// Enforces the same PrinterGroup access rules on this printer as the mutating/physical
    /// paths and the SignalR hub, so per-printer reads (status, details, job info, camera) and
    /// file operations cannot be reached by a caller outside the printer's group.
    /// </summary>
    private async Task<bool> CanAccessPrinterAsync(
        Guid printerId,
        PrinterGroupAccessLevel accessLevel,
        CancellationToken ct)
    {
        return _queueResourceAuthorization is not null &&
            await _queueResourceAuthorization.CanAccessPrinterAsync(
                User,
                printerId,
                accessLevel,
                ct);
    }

    /// <summary>
    /// Applies the same PrinterGroup access rules as <see cref="CanAccessPrinterAsync"/> to a
    /// collection result, so restricted printers are omitted from list/summary/camera-urls
    /// responses instead of merely blocking the per-id reads (issue #1292: filtering only the
    /// per-id endpoints would still let restricted printer IDs be discovered via these lists).
    /// </summary>
    private async Task<T[]> FilterAccessiblePrintersAsync<T>(
        T[] items,
        Func<T, Guid> getId,
        CancellationToken ct)
    {
        if (_queueResourceAuthorization is null)
        {
            // Fail closed to match CanAccessPrinterAsync: an unavailable authorization
            // dependency must not silently disclose every printer in a collection response.
            return Array.Empty<T>();
        }

        if (items.Length == 0)
        {
            return items;
        }

        Guid[] ids = items.Select(getId).ToArray();
        IReadOnlySet<Guid> allowed = await _queueResourceAuthorization.FilterAccessiblePrinterIdsAsync(
            User,
            ids,
            PrinterGroupAccessLevel.View,
            ct);
        return items.Where(item => allowed.Contains(getId(item))).ToArray();
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
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

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
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(408)]
    [ProducesResponseType(502)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<HistoryListResponse>> GetHistoryAsync(Guid id, [FromQuery] int? limit = null, [FromQuery] int? start = null, [FromQuery] DateTime? since = null, [FromQuery] DateTime? before = null, [FromQuery] string? order = null, CancellationToken ct = default)
    {
        if (limit is < 1 or > MaxHistoryQueryEntries)
        {
            ModelState.AddModelError(
                nameof(limit),
                $"limit must be between 1 and {MaxHistoryQueryEntries}.");
        }

        if (start is < 0 or > MaxHistoryQueryEntries)
        {
            ModelState.AddModelError(
                nameof(start),
                $"start must be between 0 and {MaxHistoryQueryEntries}.");
        }

        if (limit.HasValue &&
            (long)(start ?? 0) + limit.Value > MaxHistoryQueryEntries)
        {
            ModelState.AddModelError(
                nameof(limit),
                $"start plus limit must not exceed {MaxHistoryQueryEntries}.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

        try
        {
            HistoryListResponse resp = await _printersService.GetHistoryListAsync(id, limit, start, since, before, order, ct);
            return Ok(resp);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex, "History requested for unsupported printer {Id}", id);
            return BadRequest("History is not available for this printer backend");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Timeout retrieving history for printer {Id}", id);
            return StatusCode(StatusCodes.Status408RequestTimeout, "Request timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error retrieving history for printer {Id}", id);
            return StatusCode(StatusCodes.Status502BadGateway, "Unable to connect to printer");
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Socket error retrieving history for printer {Id}", id);
            return StatusCode(StatusCodes.Status502BadGateway, "Unable to connect to printer");
        }
        catch (Farm.Infrastructure.Services.Printers.HistoryAuthorityException ex)
        {
            _logger.LogError(ex, "Printer {Id} could not prove the requested history range", id);
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new
                {
                    error = "history_completeness_unproven",
                    detail = ex.Message,
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get history for printer {Id}: {Message}", id, ex.Message);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Failed to retrieve printer history" });
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
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

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
            _logger.LogInformation("History job {JobId} not found for printer {Id}", LogSanitizer.Sanitize(jobId), id);
            return NotFound($"History job {jobId} not found");
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex, "History requested for unsupported printer {Id}", id);
            return BadRequest("History is not available for this printer backend");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "History requested for non-Moonraker printer {Id}", id);
            return BadRequest("History is only available for Moonraker printers");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("Network error retrieving history job {JobId} for printer {Id}: {Message}", LogSanitizer.Sanitize(jobId), id, LogSanitizer.Sanitize(ex.Message));
            return StatusCode(StatusCodes.Status502BadGateway, "Unable to connect to printer");
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Socket error retrieving history job {JobId} for printer {Id}", LogSanitizer.Sanitize(jobId), id);
            return StatusCode(StatusCodes.Status502BadGateway, "Unable to connect to printer");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Timeout retrieving history job {JobId} for printer {Id}", LogSanitizer.Sanitize(jobId), id);
            return StatusCode(StatusCodes.Status408RequestTimeout, "Request timeout");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning("Timeout retrieving history job {JobId} for printer {Id}: {Message}", LogSanitizer.Sanitize(jobId), id, LogSanitizer.Sanitize(ex.Message));
            return StatusCode(StatusCodes.Status408RequestTimeout, "Request timeout");
        }
    }

    /// <summary>
    /// Returns an authenticated same-origin thumbnail for a historical print job.
    /// </summary>
    /// <param name="id">The printer identifier.</param>
    /// <param name="jobId">The backend-specific history job identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validated image content without exposing printer credentials or its private URL.</returns>
    [HttpGet("{id:guid}/history/{jobId}/thumbnail")]
    [ProducesResponseType(typeof(FileContentResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(408)]
    [ProducesResponseType(502)]
    public async Task<IActionResult> GetHistoryThumbnailAsync(
        Guid id,
        string jobId,
        CancellationToken ct = default)
    {
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

        try
        {
            HistoryThumbnailContent thumbnail =
                await _printersService.GetHistoryThumbnailAsync(id, jobId, ct);
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(thumbnail.Content, thumbnail.ContentType);
        }
        catch (ArgumentException)
        {
            return BadRequest("Job ID is required");
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (NotSupportedException)
        {
            return NotFound();
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(
                ex,
                "Printer {PrinterId} returned invalid history thumbnail content for job {JobId}",
                id,
                LogSanitizer.Sanitize(jobId));
            return HistoryThumbnailProblem(
                "history_thumbnail_invalid",
                "The printer returned an invalid thumbnail.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return HistoryThumbnailProblem(
                "history_thumbnail_timeout",
                "The printer thumbnail request timed out.",
                StatusCodes.Status408RequestTimeout);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(
                ex,
                "History thumbnail request timed out for printer {PrinterId}, job {JobId}",
                id,
                LogSanitizer.Sanitize(jobId));
            return HistoryThumbnailProblem(
                "history_thumbnail_timeout",
                "The printer thumbnail request timed out.",
                StatusCodes.Status408RequestTimeout);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            return HistoryThumbnailProblem(
                "history_thumbnail_timeout",
                "The printer thumbnail request timed out.",
                StatusCodes.Status408RequestTimeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException)
        {
            _logger.LogWarning(
                ex,
                "History thumbnail request failed for printer {PrinterId}, job {JobId}",
                id,
                LogSanitizer.Sanitize(jobId));
            return HistoryThumbnailProblem(
                "history_thumbnail_upstream_failed",
                "The printer thumbnail is unavailable.");
        }
    }

    private ObjectResult HistoryThumbnailProblem(
        string code,
        string title,
        int statusCode = StatusCodes.Status502BadGateway) =>
        Problem(
            statusCode: statusCode,
            title: title,
            type: $"https://printfarmer.dev/problems/{code}",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
            });

    [HttpGet("{id}/history/totals")]
    [ProducesResponseType(typeof(HistoryTotals), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(408)]
    [ProducesResponseType(502)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<HistoryTotals>> GetHistoryTotalsAsync(Guid id, CancellationToken ct = default)
    {
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

        try
        {
            HistoryTotals totals = await _printersService.GetHistoryTotalsAsync(id, ct);
            return Ok(totals);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, "Request timeout");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Timeout retrieving history totals for printer {Id}", id);
            return StatusCode(StatusCodes.Status408RequestTimeout, "Request timeout");
        }
        catch (Exception ex) when (
            ex is HttpRequestException or SocketException or IOException or InvalidDataException)
        {
            _logger.LogWarning(
                ex,
                "Upstream failure retrieving history totals for printer {Id}",
                id);
            return StatusCode(
                StatusCodes.Status502BadGateway,
                "Unable to retrieve authoritative printer history totals");
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to get history totals for printer {Id}: {Message}", id, LogSanitizer.Sanitize(ex.Message));
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Failed to retrieve printer history totals" });
        }
    }

    [HttpDelete("{id}/history/{jobId}")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteHistoryJobAsync(Guid id, string jobId, CancellationToken ct = default)
    {
        if (!await CanAccessPrinterAsync(id, PrinterGroupAccessLevel.Submit, ct))
        {
            return NotFound();
        }

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
            _logger.LogError("Failed to delete history job {JobId} for printer {Id}: {Message}", LogSanitizer.Sanitize(jobId), id, LogSanitizer.Sanitize(ex.Message));
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to delete history job");
        }
    }

    [HttpGet("export")]
    [ProducesResponseType(typeof(FileContentResult), 200)]
    [ProducesResponseType(500)]
    [RequirePermission("printers", "admin")]
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
    [RequirePermission("printers", "admin")]
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
    [RequirePermission("printers", "admin")]
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
    [RequirePermission("printers", "admin")]
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

            WritePrinterEtag(printer);

            // Return printer configuration as JSON object
            var config = new
            {
                id = printer.Id,
                name = printer.Name,
                backend = printer.Backend,
                backendPort = printer.BackendPort,
                frontendPort = printer.FrontendPort,
                notes = printer.Notes,
                manufacturerId = printer.ManufacturerId,
                modelId = printer.ModelId,
                dateAcquired = printer.DateAcquired,
                inMaintenance = printer.InMaintenance,
                serverConfigured = !string.IsNullOrWhiteSpace(printer.ServerUrl),
                apiKeyConfigured = !string.IsNullOrWhiteSpace(printer.ApiKey),
                usernameConfigured = !string.IsNullOrWhiteSpace(printer.Username),
                passwordConfigured = !string.IsNullOrWhiteSpace(printer.Password),
                rowVersion = EncodeRowVersion(printer.RowVersion),
                configurationRevision = printer.ConfigurationRevision
            };

            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Config] Failed to get printer configuration for {Id}: {Message}", id, ex.Message);
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Printer configuration could not be read",
                type: "https://printfarmer.dev/problems/printer-configuration-read-failed",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "printer_configuration_read_failed",
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
    [RequirePermission("printers", "admin")]
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

            if (BindPrinterIfMatch(printer) is { } precondition)
            {
                return precondition;
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
                WritePrinterEtag(printer);
                _logger.LogInformation("[Config] Successfully updated printer configuration for {Id}", id);

                // Return updated configuration
                var updatedConfig = new
                {
                    id = printer.Id,
                    name = printer.Name,
                    backend = printer.Backend,
                    backendPort = printer.BackendPort,
                    frontendPort = printer.FrontendPort,
                    notes = printer.Notes,
                    manufacturerId = printer.ManufacturerId,
                    modelId = printer.ModelId,
                    dateAcquired = printer.DateAcquired,
                    inMaintenance = printer.InMaintenance,
                    serverConfigured = !string.IsNullOrWhiteSpace(printer.ServerUrl),
                    apiKeyConfigured = !string.IsNullOrWhiteSpace(printer.ApiKey),
                    usernameConfigured = !string.IsNullOrWhiteSpace(printer.Username),
                    passwordConfigured = !string.IsNullOrWhiteSpace(printer.Password),
                    rowVersion = EncodeRowVersion(printer.RowVersion),
                    configurationRevision = printer.ConfigurationRevision,
                    message = "Configuration updated successfully"
                };

                return Ok(updatedConfig);
            }

            return BadRequest(new { message = "Configuration must be a JSON object" });
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return PrinterRevisionConflict();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Config] Failed to update printer configuration for {Id}: {Message}", id, ex.Message);
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Printer configuration could not be updated",
                type: "https://printfarmer.dev/problems/printer-configuration-update-failed",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "printer_configuration_update_failed",
                });
        }
    }

    /// <summary>
    /// Re-probes this printer's firmware identity on demand and persists the detected facts.
    /// </summary>
    /// <remarks>
    /// This endpoint only supports Moonraker/Klipper printers — it rejects any other backend with a
    /// 409 (see <c>FirmwareDetectionFailure.BackendNotSupported</c> below) — so it is the operator-
    /// initiated way back specifically for a Moonraker/Klipper printer whose persisted firmware
    /// columns were never (re)populated by one of the passive writers: the printer's own creation
    /// DTO at onboarding (an ungated, one-time write of whatever firmware facts the caller supplied,
    /// e.g. discovery), a later discovery scan posting back a matching <c>ServerUrl</c>, or — for
    /// Moonraker/Klipper printers specifically — the live <c>GET /printers/{id}/version</c> read-through
    /// path on a cache miss (see <c>PrinterVersionCache.GetMoonrakerVersionAsync</c>). The latter two
    /// share the same <c>Discovery:FirmwareReprobeIntervalHours</c> cadence throttle (via
    /// <c>IPrintersService.IsFirmwareReprobeDue</c>) since both route through
    /// <c>RefreshDetectedFirmwareIdentityAsync</c>; onboarding is a one-time write and is not subject
    /// to that cadence at all. This endpoint itself is deliberately not throttled, since it is an
    /// explicit operator action.
    ///
    /// Note that the live <c>GET /printers/{id}/version</c> reading is still a different value from what
    /// this endpoint persists: for non-Moonraker backends (PrusaLink, OctoPrint, SDCP) it never writes
    /// these columns at all, which is why a printer can display a firmware version in the UI while
    /// calibration still reports the firmware inputs as missing. For Moonraker/Klipper printers, per
    /// the read-through path above, the version endpoint's live probe writes the same columns using
    /// known Klipper constants for family/dialect/detection-source/confidence rather than a freshly
    /// derived onboarding scan — so a Moonraker/Klipper printer can passively regain a calibratable
    /// state just by being polled, without an operator ever calling this endpoint.
    ///
    /// Detection never marks the identity verified — that stays a human confirm-only action.
    /// </remarks>
    /// <param name="id">Printer id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detected and persisted firmware identity.</returns>
    /// <response code="200">Returns the detected and persisted firmware identity.</response>
    /// <response code="404">If the printer does not exist.</response>
    /// <response code="409">If the printer's backend does not support firmware probing.</response>
    /// <response code="502">If the printer could not be reached or did not answer a known endpoint.</response>
    [HttpPost("{id:guid}/firmware/detect")]
    [ProducesResponseType(typeof(FirmwareDetectionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [RequirePermission(PrintFarmerPermissions.Calibration.Update)]
    public async Task<IActionResult> DetectPrinterFirmwareAsync(Guid id, CancellationToken ct)
    {
        FirmwareDetectionResult result = await _printersService.DetectFirmwareIdentityAsync(id, ct);

        return result.Failure switch
        {
            FirmwareDetectionFailure.PrinterNotFound =>
                NotFound(new { error = $"Printer {id} not found" }),
            FirmwareDetectionFailure.BackendNotSupported =>
                Conflict(new { error = "firmware_probe_backend_unsupported" }),
            FirmwareDetectionFailure.ServerUrlInvalid =>
                Conflict(new { error = "firmware_probe_server_url_invalid" }),
            FirmwareDetectionFailure.ProbeFailed =>
                StatusCode(StatusCodes.Status502BadGateway, new { error = "firmware_probe_failed" }),
            _ => Ok(result),
        };
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
    [RequirePermission("printers", "admin")]
    [HttpPost("discover/stream")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> StartDiscoveryStreamAsync(
        [FromBody] DiscoveryStreamRequest? request,
        CancellationToken ct)
    {
        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            return Forbid();
        }

        try
        {
            bool autoRegister = request?.AutoRegister ?? false;
            _logger.LogInformation("[DISCOVERY] Starting discovery stream via API endpoint (autoRegister={AutoRegister})", autoRegister);

            IReadOnlyList<PrinterBackend>? backends = request?.Backends?.ToList();
            DiscoveryStreamResponse result = await _discoveryProxyService.StartDiscoveryStreamAsync(
                backends: backends,
                autoRegister: autoRegister,
                ownerUserId: userId,
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
    /// Registers a redacted discovery result whose network target remains server-side.
    /// </summary>
    [RequirePermission("printers", "admin")]
    [HttpPost("discover/{sessionId}/register")]
    [ProducesResponseType(typeof(PrinterDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrinterDto>> RegisterDiscoveryResultAsync(
        [FromRoute] string sessionId,
        [FromBody] RegisterDiscoveredPrinterRequest request,
        CancellationToken ct)
    {
        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            return Forbid();
        }

        bool isOwner = _discoverySessions.IsSessionOwner(sessionId, userId);
        bool isFarmAdmin = PrintFarmerPermissions.IsFarmAdmin(User);
        if (!_discoverySessions.TryGetPrinter(
                sessionId,
                request.DiscoveryId,
                userId,
                isFarmAdmin,
                out DiscoveredPrinterDto? discovered) ||
            discovered is null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Discovery result not found",
                Type = "https://printfarmer.dev/problems/resource_not_found",
                Extensions = { ["code"] = "resource_not_found" },
            });
        }

        if (!isOwner)
        {
            _logger.LogInformation(
                "Audited farm-admin discovery result bypass by user {UserId} for session {SessionId}",
                userId,
                LogSanitizer.Sanitize(sessionId));
        }

        CreatePrinterFromDiscoveryDto dto = CreatePrinterFromDiscoveryDto.FromDiscovered(
            discovered,
            request.ManufacturerId,
            request.ModelId,
            request.NewManufacturerName,
            request.NewModelName);
        ValidationResult validationResult = await _validator.ValidateAsync(dto, ct);
        if (!validationResult.IsValid)
        {
            foreach (ValidationFailure error in validationResult.Errors)
            {
                ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
            }

            return BadRequest(ModelState);
        }

        PrinterDto created = await CreatePrinterAndImportProfilesAsync(dto, ct);
        _discoverySessions.RemovePrinter(sessionId, request.DiscoveryId);
        return CreatedAtRoute("GetPrinterById", new { id = created.Id }, created);
    }

    /// <summary>
    /// Cancel an active discovery stream.
    /// </summary>
    /// <param name="sessionId">The session ID to cancel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cancellation confirmation.</returns>
    /// <response code="200">Discovery cancelled successfully.</response>
    /// <response code="500">Failed to cancel discovery.</response>
    [RequirePermission("printers", "admin")]
    [HttpPost("discover/{sessionId}/cancel")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> CancelDiscoveryStreamAsync(
        [FromRoute] string sessionId,
        CancellationToken ct)
    {
        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            return Forbid();
        }

        bool isOwner = _discoverySessions.IsSessionOwner(sessionId, userId);
        if (!_discoverySessions.SessionExists(sessionId))
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Discovery session not found",
                Type = "https://printfarmer.dev/problems/resource_not_found",
                Extensions = { ["code"] = "resource_not_found" },
            });
        }

        if (!isOwner)
        {
            _logger.LogInformation(
                "Audited farm-admin discovery session bypass by user {UserId} for session {SessionId}",
                userId,
                LogSanitizer.Sanitize(sessionId));
        }

        try
        {
            _logger.LogInformation("[DISCOVERY] Cancelling discovery stream {SessionId}", LogSanitizer.Sanitize(sessionId));

            DiscoveryCancelResponse result = await _discoveryProxyService.CancelDiscoveryStreamAsync(sessionId, ct);

            return Ok(new { message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DISCOVERY] Failed to cancel discovery stream {SessionId}", LogSanitizer.Sanitize(sessionId));
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
    [RequirePermission("printers", "admin")]
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
    [RequirePermission("printers", "admin")]
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

    /// <summary>
    /// Maps a <see cref="FilamentUnloadResult"/> to the appropriate HTTP status code so callers
    /// still get consistent 404 semantics when the printer is missing, while success and other
    /// failure paths preserve the residual-weight payload. Uses the typed
    /// <see cref="FilamentUnloadFailureKind"/> discriminator rather than brittle message
    /// substring matching (issue #710 low-severity fix): a missing printer is 404, an invalid
    /// toolhead index is 400, and any other failure is 400.
    /// </summary>
    private ActionResult<FilamentUnloadResult> MapFilamentUnloadResult(FilamentUnloadResult result)
    {
        if (result.Success)
        {
            return Ok(result);
        }

        return result.FailureKind switch
        {
            FilamentUnloadFailureKind.PrinterNotFound => NotFound(result),
            FilamentUnloadFailureKind.InvalidToolhead => BadRequest(result),
            _ => BadRequest(result),
        };
    }
}
