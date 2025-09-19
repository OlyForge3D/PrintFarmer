using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Web.Api.Controllers.Responses;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
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
[Route("api/[controller]")]
[Tags("Printers")]
public class PrintersController(AppDbContext db, IMoonrakerClient moon, IPrusaLinkClient prusa, ISdcpClient sdcp, IOctoPrintClient octoprint, INetworkDiscoveryService networkDiscovery, ILogger<PrintersController> logger, IValidator<CreatePrinterDto> validator, ICircuitBreakerService circuitBreaker, IPrinterCapabilityDiscoveryService capabilityDiscovery, IDefaultCatalogService defaultCatalog) : ControllerBase
{
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
    private static string NormalizeServerUrl(string url, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }
        string trimmed = url.Trim();
        // Ensure scheme
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }
        try
        {
            UriBuilder ub = new(trimmed);
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
        List<Printer> items = await db.Printers.AsNoTracking().Include(p => p.Manufacturer).Include(p => p.Model).ToListAsync(ct);

        // Use aggressive timeouts and circuit breaker patterns for bulk status loading
        using CancellationTokenSource fastTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fastTimeoutCts.CancelAfter(TimeSpan.FromSeconds(2)); // Aggressive 2-second timeout for bulk operations

        PrinterDto[] dtos = await Task.WhenAll(items.Select(async p =>
        {
            try
            {
                if (p.Backend == 1) // PrusaLink
                {
                    CircuitBreaker breaker = circuitBreaker.GetCircuitBreaker($"prusalink-{p.Id}");
                    PrusaCompositeStatus status = await breaker.ExecuteAsync(async ct =>
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
                    CircuitBreaker breaker = circuitBreaker.GetCircuitBreaker($"sdcp-{p.Id}");
                    PrinterCompositeStatus status = await breaker.ExecuteAsync(async ct =>
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
                else if (p.Backend == 3) // OctoPrint
                {
                    CircuitBreaker breaker = circuitBreaker.GetCircuitBreaker($"octoprint-{p.Id}");
                    // Fetch both printer and job status
                    string printerJson = await breaker.ExecuteAsync(async ct =>
                        await octoprint.GetPrinterStateAsync(p.ServerUrl, p.ApiKey ?? string.Empty), fastTimeoutCts.Token);
                    string jobJson = await breaker.ExecuteAsync(async ct =>
                        await octoprint.GetJobStatusAsync(p.ServerUrl, p.ApiKey ?? string.Empty), fastTimeoutCts.Token);
                    // Plugin detection: query /api/plugins
                    string pluginsJson = string.Empty;
                    bool hasPositionPlugin = false;
                    bool hasSpoolManager = false;
                    bool hasSpoolmanPlugin = false;
                    try
                    {
                        var pluginsRequest = new HttpRequestMessage(HttpMethod.Get, $"{p.ServerUrl.TrimEnd('/')}/api/plugins");
                        pluginsRequest.Headers.Add("X-Api-Key", p.ApiKey ?? string.Empty);
                        var pluginsResponse = await ((OctoPrintClient)octoprint).HttpClient.SendAsync(pluginsRequest, fastTimeoutCts.Token);
                        pluginsJson = await pluginsResponse.Content.ReadAsStringAsync();
                    }
                    catch { }

                    if (!string.IsNullOrWhiteSpace(pluginsJson))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(pluginsJson);
                            var root = doc.RootElement;
                            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("plugins", out var pluginsProp))
                            {
                                foreach (var plugin in pluginsProp.EnumerateArray())
                                {
                                    if (plugin.TryGetProperty("key", out var keyProp))
                                    {
                                        var key = keyProp.GetString()?.ToLowerInvariant();
                                        if (key == "display_current_position" || key == "positioninfo")
                                        {
                                            hasPositionPlugin = true;
                                        }
                                        if (key == "spoolmanager")
                                        {
                                            hasSpoolManager = true;
                                        }
                                        if (key == "spoolman")
                                        {
                                            hasSpoolmanPlugin = true;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    // Parse printer state
                    bool isOnline = false;
                    string? state = null;
                    double? hotendTemp = null;
                    double? bedTemp = null;
                    double? hotendTarget = null;
                    double? bedTarget = null;
                    double? x = null, y = null, z = null;
                    PrinterSpoolInfoDto? spoolInfo = null;
                    if (!string.IsNullOrWhiteSpace(printerJson))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(printerJson);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("state", out var stateProp))
                            {
                                state = stateProp.GetString();
                                isOnline = state != null && state != "Offline";
                            }
                            if (root.TryGetProperty("temperature", out var tempProp))
                            {
                                if (tempProp.TryGetProperty("tool0", out var tool0))
                                {
                                    if (tool0.TryGetProperty("actual", out var actual))
                                    {
                                        hotendTemp = actual.GetDouble();
                                    }
                                    if (tool0.TryGetProperty("target", out var target))
                                    {
                                        hotendTarget = target.GetDouble();
                                    }
                                }
                                if (tempProp.TryGetProperty("bed", out var bed))
                                {
                                    if (bed.TryGetProperty("actual", out var actual))
                                    {
                                        bedTemp = actual.GetDouble();
                                    }
                                    if (bed.TryGetProperty("target", out var target))
                                    {
                                        bedTarget = target.GetDouble();
                                    }
                                }
                            }

                            // X/Y/Z position plugin support
                            if (hasPositionPlugin && root.TryGetProperty("position", out var posProp))
                            {
                                if (posProp.TryGetProperty("x", out var xProp))
                                {
                                    x = xProp.GetDouble();
                                }
                                if (posProp.TryGetProperty("y", out var yProp))
                                {
                                    y = yProp.GetDouble();
                                }
                                if (posProp.TryGetProperty("z", out var zProp))
                                {
                                    z = zProp.GetDouble();
                                }
                            }

                            // SpoolManager plugin support
                            if (hasSpoolManager && root.TryGetProperty("spoolmanager", out var spoolProp))
                            {
                                // Map OctoPrint SpoolManager plugin fields to PrinterSpoolInfoDto
                                spoolInfo = new PrinterSpoolInfoDto(
                                    HasActiveSpool: true,
                                    ActiveSpoolId: spoolProp.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : null,
                                    SpoolName: spoolProp.TryGetProperty("display_name", out var nameProp) ? nameProp.GetString() : null,
                                    Material: spoolProp.TryGetProperty("material", out var matProp) ? matProp.GetString() : null,
                                    ColorHex: spoolProp.TryGetProperty("color", out var colorProp) ? colorProp.GetString() : null,
                                    FilamentName: spoolProp.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null,
                                    Vendor: spoolProp.TryGetProperty("vendor", out var vendorProp) ? vendorProp.GetString() : null,
                                    RemainingWeightG: spoolProp.TryGetProperty("remaining_weight", out var remProp) ? remProp.GetDouble() : null,
                                    SpoolInUse: spoolProp.TryGetProperty("in_use", out var inUseProp) ? inUseProp.GetBoolean() : null
                                );
                            }

                            // Spoolman plugin support (OctoPrint-Spoolman bridge)
                            if (hasSpoolmanPlugin)
                            {
                                try
                                {
                                    var spoolmanRequest = new HttpRequestMessage(HttpMethod.Get, $"{p.ServerUrl.TrimEnd('/')}/plugin/spoolman/api/v1/printer");
                                    spoolmanRequest.Headers.Add("X-Api-Key", p.ApiKey ?? string.Empty);
                                    var spoolmanResponse = await ((OctoPrintClient)octoprint).HttpClient.SendAsync(spoolmanRequest, fastTimeoutCts.Token);
                                    if (spoolmanResponse.IsSuccessStatusCode)
                                    {
                                        var spoolmanJson = await spoolmanResponse.Content.ReadAsStringAsync();
                                        using var spoolmanDoc = JsonDocument.Parse(spoolmanJson);
                                        var spoolmanRoot = spoolmanDoc.RootElement;
                                        // Map Spoolman fields to PrinterSpoolInfoDto (example fields, adjust as needed)
                                        spoolInfo = new PrinterSpoolInfoDto(
                                            HasActiveSpool: spoolmanRoot.TryGetProperty("has_active_spool", out var hasSpool) && hasSpool.GetBoolean(),
                                            ActiveSpoolId: spoolmanRoot.TryGetProperty("active_spool_id", out var spoolId) ? spoolId.GetInt32() : null,
                                            SpoolName: spoolmanRoot.TryGetProperty("spool_name", out var spoolName) ? spoolName.GetString() : null,
                                            Material: spoolmanRoot.TryGetProperty("material", out var mat) ? mat.GetString() : null,
                                            ColorHex: spoolmanRoot.TryGetProperty("color", out var color) ? color.GetString() : null,
                                            FilamentName: spoolmanRoot.TryGetProperty("filament_name", out var filName) ? filName.GetString() : null,
                                            Vendor: spoolmanRoot.TryGetProperty("vendor", out var vendor) ? vendor.GetString() : null,
                                            RemainingWeightG: spoolmanRoot.TryGetProperty("remaining_weight_g", out var remG) ? remG.GetDouble() : null,
                                            SpoolInUse: spoolmanRoot.TryGetProperty("spool_in_use", out var inUse) ? inUse.GetBoolean() : null
                                        );
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }

                    // Parse job info
                    double? progress = null;
                    string? jobName = null;
                    if (!string.IsNullOrWhiteSpace(jobJson))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(jobJson);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("progress", out var progressProp))
                            {
                                if (progressProp.TryGetProperty("completion", out var completion))
                                {
                                    progress = completion.GetDouble();
                                }
                            }
                            if (root.TryGetProperty("job", out var jobProp))
                            {
                                if (jobProp.TryGetProperty("file", out var fileProp))
                                {
                                    if (fileProp.TryGetProperty("name", out var nameProp))
                                    {
                                        jobName = nameProp.GetString();
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    return new PrinterDto(
                        Id: p.Id,
                        Name: p.Name,
                        ServerUrl: p.ServerUrl,
                        Notes: p.Notes,
                        IsOnline: isOnline,
                        State: state,
                        ManufacturerName: p.Manufacturer?.Name,
                        ModelName: p.Model?.Name,
                        Progress: progress,
                        JobName: jobName,
                        ThumbnailUrl: null,
                        CameraStreamUrl: await octoprint.GetCameraStreamUrlAsync(p.ServerUrl, p.ApiKey ?? string.Empty),
                        CameraSnapshotUrl: null,
                        HotendTemp: hotendTemp,
                        BedTemp: bedTemp,
                        HotendTarget: hotendTarget,
                        BedTarget: bedTarget,
                        X: x, // Will be populated if plugin is installed
                        Y: y,
                        Z: z,
                        SpoolInfo: spoolInfo, // Will be populated if plugin is installed
                        Backend: Farm.Web.Shared.PrinterBackend.OctoPrint,
                        ApiKey: p.ApiKey,
                        OriginalServerUrl: p.OriginalServerUrl,
                        IpAddress: p.IpAddress
                    );
                }
                else // Moonraker
                {
                    CircuitBreaker breaker = circuitBreaker.GetCircuitBreaker($"moonraker-{p.Id}");
                    PrinterCompositeStatus status = await breaker.ExecuteAsync(async ct =>
                        await moon.GetCompositeStatusAsync(p.ServerUrl, ct), fastTimeoutCts.Token);
                    PrinterSpoolInfoDto? spoolInfo = await GetSpoolInfoAsync(p.ServerUrl, fastTimeoutCts.Token);
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
        List<Printer> items = await db.Printers.AsNoTracking().Include(p => p.Manufacturer).Include(p => p.Model).ToListAsync(ct);
        List<PrinterBasicDto> dtos = items.Select(p => new PrinterBasicDto(
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
            List<Printer> items = await db.Printers.AsNoTracking().Include(p => p.Manufacturer).Include(p => p.Model).ToListAsync(ct);

            // Return all printers as offline initially - let the client load statuses progressively
            List<PrinterFastDto> dtos = items.Select(p => new PrinterFastDto(
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
        catch (Exception ex) when (FastEndpointDefensive && IsTransientStartupDbException(ex))
        {
            // During early startup the DB might not yet be fully initialised (e.g. migrations running).
            // Instead of surfacing a 500 to the UI, return an empty list so the UI can retry shortly.
            logger.LogDebug(ex, "Printers fast endpoint accessed before startup completed; returning empty list.");
            return Ok(Array.Empty<PrinterFastDto>());
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
            List<Printer> items = await db.Printers.AsNoTracking().ToListAsync(ct);

            PrinterCameraUrlsDto[] dtos = await Task.WhenAll(items.Select(async p =>
            {
                string? streamUrl = null;
                string? snapshotUrl = null;

                // Only return camera URLs if we can verify the camera endpoints are actually available
                if (await IsCameraAvailableAsync(p.ServerUrl, p.Backend, ct))
                {
                    streamUrl = GenerateStaticCameraStreamUrl(p.ServerUrl, p.Backend);
                    snapshotUrl = GenerateStaticCameraSnapshotUrl(p.ServerUrl, p.Backend);
                }

                return new PrinterCameraUrlsDto(
                    Id: p.Id,
                    Name: p.Name,
                    CameraStreamUrl: streamUrl,
                    CameraSnapshotUrl: snapshotUrl
                );
            }));

            return Ok(dtos.ToList());
        }
        catch (Exception ex) when (FastEndpointDefensive && IsTransientStartupDbException(ex))
        {
            logger.LogDebug(ex, "Printers camera-urls endpoint accessed before startup completed; returning empty list.");
            return Ok(Array.Empty<PrinterCameraUrlsDto>());
        }
    }

    /// <summary>
    /// Checks if a camera is actually available for the given printer by testing the camera endpoint.
    /// </summary>
    /// <param name="serverUrl">The printer server URL</param>
    /// <param name="backend">The printer backend type</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if camera is available, false otherwise</returns>
    private async Task<bool> IsCameraAvailableAsync(string serverUrl, int backend, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return false;
        }

        try
        {
            // Test the snapshot URL with a short timeout
            string? snapshotUrl = GenerateStaticCameraSnapshotUrl(serverUrl, backend);
            if (string.IsNullOrWhiteSpace(snapshotUrl))
            {
                return false;
            }

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(2); // Short timeout to avoid blocking

            using var request = new HttpRequestMessage(HttpMethod.Head, snapshotUrl);
            using var response = await httpClient.SendAsync(request, ct);

            // Camera is available if we get a successful response (2xx) or even a 4xx
            // (404 might mean camera exists but no current image, 401/403 means auth required but camera exists)
            // 5xx errors typically mean the camera service is not running/configured
            return response.StatusCode < System.Net.HttpStatusCode.InternalServerError;
        }
        catch (Exception ex)
        {
            // Log the exception for debugging but don't expose it
            logger.LogDebug(ex, "Camera availability check failed for printer {ServerUrl} (backend {Backend})", serverUrl, backend);
            return false;
        }
    }

    /// <summary>
    /// Generates static camera stream URL based on printer configuration without external API calls.
    /// </summary>
    private static string? GenerateStaticCameraStreamUrl(string serverUrl, int backend)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return null;
        }

        try
        {
            Uri baseUri = new(serverUrl);
            return backend switch
            {
                0 => new Uri(baseUri, "/webcam/?action=stream").ToString(), // Moonraker
                1 => new Uri(baseUri, "/webcam/?action=stream").ToString(), // PrusaLink (often same pattern)
                2 => new Uri(baseUri, "/camera/stream").ToString(),         // SDCP
                _ => new Uri(baseUri, "/webcam/?action=stream").ToString()  // Default to Moonraker pattern
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Generates static camera snapshot URL based on printer configuration without external API calls.
    /// </summary>
    private static string? GenerateStaticCameraSnapshotUrl(string serverUrl, int backend)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return null;
        }

        try
        {
            Uri baseUri = new(serverUrl);
            return backend switch
            {
                0 => new Uri(baseUri, "/webcam/?action=snapshot").ToString(), // Moonraker
                1 => new Uri(baseUri, "/webcam/?action=snapshot").ToString(), // PrusaLink (often same pattern)
                2 => new Uri(baseUri, "/camera/snapshot").ToString(),         // SDCP
                _ => new Uri(baseUri, "/webcam/?action=snapshot").ToString()  // Default to Moonraker pattern
            };
        }
        catch
        {
            return null;
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
        Printer? p = await db.Printers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        {
            return NotFound();
        }

        // Use moderate timeout for individual status checks (balance between responsiveness and accuracy)
        using CancellationTokenSource statusCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        statusCts.CancelAfter(TimeSpan.FromSeconds(3)); // 3-second timeout for individual status

        try
        {
            if (p.Backend == 1) // PrusaLink
            {
                CircuitBreaker breaker = circuitBreaker.GetCircuitBreaker($"prusalink-{p.Id}");
                PrusaCompositeStatus status = await breaker.ExecuteAsync(async ct =>
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
                CircuitBreaker breaker = circuitBreaker.GetCircuitBreaker($"sdcp-{p.Id}");
                PrinterCompositeStatus status = await breaker.ExecuteAsync(async ct =>
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
                CircuitBreaker breaker = circuitBreaker.GetCircuitBreaker($"moonraker-{p.Id}");
                PrinterCompositeStatus status = await breaker.ExecuteAsync(async ct =>
                    await moon.GetCompositeStatusAsync(p.ServerUrl, ct), statusCts.Token);
                PrinterSpoolInfoDto? spoolInfo = await GetSpoolInfoAsync(p.ServerUrl, statusCts.Token);
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
        Printer? p = await db.Printers.Include(x => x.Manufacturer).Include(x => x.Model).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        {
            return NotFound();
        }
        if (p.Backend == 1) // PrusaLink
        {
            PrusaCompositeStatus status = await prusa.GetCompositeStatusAsync(p.ServerUrl, p.ApiKey, ct);
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
            PrinterCompositeStatus status = await sdcp.GetCompositeStatusAsync(p.ServerUrl, ct);
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
            PrinterCompositeStatus status = await moon.GetCompositeStatusAsync(p.ServerUrl, ct);
            PrinterSpoolInfoDto? spoolInfo = await GetSpoolInfoAsync(p.ServerUrl, ct);
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
        Printer? p = await db.Printers.AsNoTracking().Include(x => x.Manufacturer).Include(x => x.Model).FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null
            ? NotFound()
            : new PrinterDetailsDto(
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
        ValidationResult validationResult = await validator.ValidateAsync(dto, ct);
        if (!validationResult.IsValid)
        {
            logger.LogWarning("Printer creation validation failed: {Errors}",
                string.Join("; ", validationResult.Errors.Select(e => e.ErrorMessage)));

            foreach (ValidationFailure? error in validationResult.Errors)
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
            string name = dto.NewManufacturerName!.Trim();
            Manufacturer? existing = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name == name, ct);
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
            string mname = dto.NewModelName!.Trim();
            PrinterModel? existingModel = await db.Models.FirstOrDefaultAsync(m => m.ManufacturerId == manufacturerId && m.Name == mname, ct);
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
                IPAddress? firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
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
        db.Printers.Add(p);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Successfully created printer: {Name} with ID {Id}", p.Name, p.Id);

        // Auto-discover capabilities for the newly created printer
        try
        {
            logger.LogInformation("Starting capability discovery for newly created printer: {Name} ({Id})", p.Name, p.Id);

            // Reload the printer with includes for proper discovery
            Printer? printerForDiscovery = await db.Printers
                .Include(pr => pr.Manufacturer)
                .Include(pr => pr.Model)
                .FirstOrDefaultAsync(pr => pr.Id == p.Id, ct);

            if (printerForDiscovery != null)
            {
                PrinterCapabilities? discoveredCapabilities = await capabilityDiscovery.DiscoverCapabilitiesAsync(printerForDiscovery, ct);
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
            Manufacturer? manufacturer = await db.Manufacturers.FirstOrDefaultAsync(m => m.Id == manufacturerId, ct);
            manufacturerName = manufacturer?.Name;
        }

        if (modelId != Guid.Empty)
        {
            PrinterModel? model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct);
            modelName = model?.Name;
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
        Printer? p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        {
            return NotFound();
        }
        // resolve or create manufacturer/model
        Guid manufacturerId = dto.ManufacturerId ?? p.ManufacturerId;
        if (dto.ManufacturerId is null && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            string name = dto.NewManufacturerName!.Trim();
            Manufacturer? existing = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name == name, ct);
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
            string mname = dto.NewModelName!.Trim();
            PrinterModel? existingModel = await db.Models.FirstOrDefaultAsync(m => m.ManufacturerId == manufacturerId && m.Name == mname, ct);
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
                IPAddress? firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
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

        await db.SaveChangesAsync(ct);

        // Build updated manufacturer/model names
        string? manufacturerName = null;
        string? modelName = null;
        if (p.ManufacturerId != Guid.Empty)
        {
            Manufacturer? man = await db.Manufacturers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == p.ManufacturerId, ct);
            manufacturerName = man?.Name;
        }
        if (p.ModelId != Guid.Empty)
        {
            PrinterModel? mod = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == p.ModelId, ct);
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
                    IPAddress? firstIp = Array.Find(addrs, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
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
        Printer? p = await db.Printers.FindAsync([id], ct);
        if (p is null)
        {
            return NotFound();
        }
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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
        Printer? p = await db.Printers.FindAsync([id], ct);
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

        Printer? p = await db.Printers.FirstOrDefaultAsync(x => x.Id == id, ct);
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
                : StatusCode(500, "Failed to upload file to printer");
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
        Printer? p = await db.Printers.FirstOrDefaultAsync(x => x.Id == id, ct);
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
            return StatusCode(500, $"Failed to get file list: {ex.Message}");
        }
    }

    [HttpPost("{id:guid}/files/{fileName}/print")]
    [ProducesResponseType(typeof(Farm.Web.Shared.StartPrintResultDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Farm.Web.Shared.StartPrintResultDto>> StartPrintFromFileAsync(Guid id, string fileName, CancellationToken ct)
    {
        Printer? p = await db.Printers.FirstOrDefaultAsync(x => x.Id == id, ct);
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
                : StatusCode(500, "Failed to start print");
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
            int? activeSpoolId = await moon.GetSpoolmanActiveSpoolAsync(serverUrl, ct);
            if (activeSpoolId == null)
            {
                return new PrinterSpoolInfoDto(HasActiveSpool: false);
            }

            // Get spool details from Spoolman via Moonraker
            string? spoolDetailsJson = await moon.GetSpoolmanSpoolByIdAsync(serverUrl, activeSpoolId.Value, ct);
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
                using JsonDocument doc = System.Text.Json.JsonDocument.Parse(spoolDetailsJson);
                JsonElement root = doc.RootElement;

                string? spoolName = root.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
                string? material = root.TryGetProperty("material", out JsonElement matEl) ? matEl.GetString() : null;
                string? colorHex = root.TryGetProperty("color_hex", out JsonElement colorEl) ? colorEl.GetString() : null;
                double? remainingWeight = root.TryGetProperty("remaining_weight", out JsonElement weightEl) && weightEl.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? weightEl.GetDouble() : (double?)null;

                // Check if filament information is nested
                string? filamentName = null;
                string? vendor = null;
                if (root.TryGetProperty("filament", out JsonElement filamentEl) && filamentEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    filamentName = filamentEl.TryGetProperty("name", out JsonElement fnameEl) ? fnameEl.GetString() : null;
                    if (filamentEl.TryGetProperty("vendor", out JsonElement vendorEl) && vendorEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        vendor = vendorEl.TryGetProperty("name", out JsonElement vNameEl) ? vNameEl.GetString() : null;
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
        Printer? printer = await db.Printers.FindAsync(new object?[] { id }, cancellationToken: ct);
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

        Printer? printer = await db.Printers.FindAsync(new object?[] { id, ct }, cancellationToken: ct);
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
            Services.HistoryJob? moonrakerJob = await moon.GetHistoryJobAsync(printer.ServerUrl, jobId, ct);
            if (moonrakerJob == null)
            {
                logger.LogInformation("History job {JobId} not found for printer {PrinterId}", jobId, id);
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
        Printer? printer = await db.Printers.FindAsync(new object?[] { id }, cancellationToken: ct);
        if (printer == null)
        {
            return NotFound();
        }

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
            Services.HistoryTotals? moonrakerTotals = await moon.GetHistoryTotalsAsync(printer.ServerUrl, ct);
            if (moonrakerTotals == null)
            {
                logger.LogWarning("Moonraker API returned null totals");
                return new Farm.Web.Shared.HistoryTotals { JobTotals = new Farm.Web.Shared.JobTotals() };
            }

            logger.LogDebug("Moonraker totals received - Jobs: {Jobs}, PrintTime: {PrintTime}, FilamentUsed: {Filament}", moonrakerTotals.JobTotals.TotalJobs, moonrakerTotals.JobTotals.TotalPrintTime, moonrakerTotals.JobTotals.TotalFilamentUsed);

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
        Printer? printer = await db.Printers.FindAsync(new object?[] { id }, cancellationToken: ct);
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

        StringBuilder csv = new();
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

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
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

        List<object> results = new();
        List<string> errors = new();

        try
        {
            using StreamReader reader = new(file.OpenReadStream());
            string csvContent = await reader.ReadToEndAsync(ct);
            string[] lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length < 2)
            {
                return BadRequest("CSV file must contain at least a header row and one data row");
            }

            string[] header = lines[0].Split(',');
            string[] expectedHeaders = new[] { "Name", "ServerUrl", "OriginalServerUrl", "Notes", "ManufacturerName", "ModelName", "Backend", "ApiKey", "DateAcquired" };

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
                        string[] values = ParseCsvLine(lines[i]);
                        if (values.Length >= 9)
                        {
                            CreatePrinterDto createDto = new()
                            {
                                Name = values[0]?.Trim() ?? "",
                                ServerUrl = values[1]?.Trim() ?? "",
                                OriginalServerUrl = string.IsNullOrWhiteSpace(values[2]) ? null : values[2].Trim(),
                                Notes = string.IsNullOrWhiteSpace(values[3]) ? null : values[3].Trim(),
                                NewManufacturerName = string.IsNullOrWhiteSpace(values[4]) ? null : values[4].Trim(),
                                NewModelName = string.IsNullOrWhiteSpace(values[5]) ? null : values[5].Trim(),
                                Backend = Enum.TryParse<PrinterBackend>(values[6]?.Trim(), true, out PrinterBackend backend) ? backend : PrinterBackend.Moonraker,
                                ApiKey = string.IsNullOrWhiteSpace(values[7]) ? null : values[7].Trim(),
                                DateAcquired = DateTime.TryParse(values[8]?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date) ? date : null
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
                            Printer? existingPrinter = await db.Printers
                                .FirstOrDefaultAsync(p => p.Name == createDto.Name, ct);

                            if (existingPrinter != null)
                            {
                                results.Add(new { row = i + 1, name = createDto.Name, status = "Skipped", reason = "Printer already exists" });
                                continue;
                            }

                            // Create the printer using existing logic
                            PrinterDto result = await CreatePrinterFromDtoAsync(createDto, ct);
                            results.Add(new { row = i + 1, name = createDto.Name, status = "Imported", id = result.Id });
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
        List<string> result = new();
        StringBuilder current = new();
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
            string name = dto.NewManufacturerName!.Trim();
            Manufacturer? existing = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name == name, ct);
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
            string mname = dto.NewModelName!.Trim();
            PrinterModel? existingModel = await db.Models.FirstOrDefaultAsync(m => m.ManufacturerId == manufacturerId && m.Name == mname, ct);
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
                IPAddress? firstIp = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
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
        db.Printers.Add(p);
        await db.SaveChangesAsync(ct);

        // Auto-discover capabilities for the newly created printer (import scenario)
        try
        {
            // Reload the printer with includes for proper discovery
            Printer? printerForDiscovery = await db.Printers
                .Include(pr => pr.Manufacturer)
                .Include(pr => pr.Model)
                .FirstOrDefaultAsync(pr => pr.Id == p.Id, ct);

            if (printerForDiscovery != null)
            {
                PrinterCapabilities? discoveredCapabilities = await capabilityDiscovery.DiscoverCapabilitiesAsync(printerForDiscovery, ct);
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
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(15)); // 15 minute total timeout for full network scan

            List<DiscoveredPrinterDto> discovered = await networkDiscovery.DiscoverPrintersAsync(timeoutCts.Token);

            // Get existing printer ServerUrls to filter out duplicates
            List<string> existingUrls = await db.Printers
                .AsNoTracking()
                .Select(p => p.ServerUrl)
                .ToListAsync(ct);

            // Normalize both existing and discovered URLs for proper comparison
            HashSet<string> normalizedExistingUrls = existingUrls
                .Select(url => NormalizeServerUrl(url, 80))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Filter out printers that already exist in the database
            List<DiscoveredPrinterDto> newPrinters = discovered
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
            string sessionId = Guid.NewGuid().ToString();

            logger.LogInformation("Starting streaming network printer discovery with session ID: {SessionId}", sessionId);

            // Start the discovery process in the background
            // The progress and results will be sent via SignalR
            _ = Task.Run(async () =>
            {
                try
                {
                    using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
