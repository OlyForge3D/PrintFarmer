using System;
using System.IO;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.RateLimiting;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Authentication;
using Farm.Web.Api.DTOs;
using Farm.Web.Api.Filters;
using Farm.Web.Api.Services.Gcode;
using Farm.Web.Api.Services.OctoPrint;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// OctoPrint-compatible API endpoints for slicer integration (PrusaSlicer, OrcaSlicer, etc.)
/// Endpoints follow the standard OctoPrint API paths: /api/version, /api/files/local, /api/server
/// </summary>
[ApiController]
[Route("api")]
public class OctoPrintCompatController : ControllerBase
{
    private readonly ILogger<OctoPrintCompatController> _logger;
    private readonly OctoPrintSettings _settings;
    private readonly IGcodeFilesService _gcodeFilesService;
    private readonly IJobQueueService _jobQueueService;
    private readonly IRateLimitService _rateLimitService;

    public OctoPrintCompatController(
        ILogger<OctoPrintCompatController> logger,
        IOptions<OctoPrintSettings> settings,
        IGcodeFilesService gcodeFilesService,
        IJobQueueService jobQueueService,
        IRateLimitService rateLimitService)
    {
        _logger = logger;
        _settings = settings.Value;
        _gcodeFilesService = gcodeFilesService;
        _jobQueueService = jobQueueService;
        _rateLimitService = rateLimitService;
    }

#pragma warning disable S6932 // Controller intentionally uses raw request data for OctoPrint API compatibility
    [HttpPost("files/local")]
    // Real authentication is required here (issue #1666): either a JWT Bearer token or a
    // resolved OctoPrint API key (via OctoPrintApiKeyAuthenticationHandler), so that
    // [RequirePermission] below runs against a genuine, permission-checkable identity
    // instead of being skipped entirely by [AllowAnonymous].
    [Authorize(AuthenticationSchemes = "Bearer," + OctoPrintApiKeyDefaults.AuthenticationScheme)]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S5693", Justification = "OctoPrint compatibility uploads are explicitly capped at 50 MB.")]
    [RequestSizeLimit(52428800)] // 50 MB default; adjust based on settings
    [RequestFormLimits(MultipartBodyLengthLimit = 52_428_800)]
    public async Task<IActionResult> UploadFileAsync([FromQuery] Guid? printerId)
    {
        // Resolve and validate the real caller's identity up front, fail closed. Mirrors
        // JobQueueController.QueueJobAsync's identity-resolution pattern (see issue #1666).
        string? userIdStr;
        try
        {
            userIdStr = QueueActorIdentity.Resolve(User);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning("OctoPrint upload denied: unable to resolve user identity from claims");
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "Unable to verify group access — user identity could not be resolved." });
        }

        if (!Guid.TryParse(userIdStr, out Guid parsedUserId))
        {
            _logger.LogWarning("OctoPrint upload denied: unable to resolve user identity from claims (raw value: {UserIdStr})", userIdStr);
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "Unable to verify group access — user identity could not be resolved." });
        }

        Guid callerId = parsedUserId;

        // OctoPrint API sends 'print' and 'select' as form fields, not query params
        // We need to read form first to get these values
        bool print = false;
        bool select = false;

        if (Request.HasFormContentType)
        {
            // Check form fields for print/select (OctoPrint sends these as form fields)
            if (Request.Form.TryGetValue("print", out var printValue))
            {
                print = printValue.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            if (Request.Form.TryGetValue("select", out var selectValue))
            {
                select = selectValue.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        _logger.LogInformation(
            "OctoPrint upload request: ContentType={ContentType}, ContentLength={ContentLength}, print={Print}, select={Select}, printerId={PrinterId}",
            LogSanitizer.Sanitize(Request.ContentType), Request.ContentLength, print, select, LogSanitizer.Sanitize(printerId?.ToString()));

        // Authentication is handled by [Authorize(AuthenticationSchemes = ...)] and
        // [RequirePermission] above; callerId was already resolved and fail-closed-checked.

        // Rate limiting: key by apiKey if present otherwise by remote IP
        var apiKey = Request.Headers["X-Api-Key"].ToString();
        string rateKey = !string.IsNullOrWhiteSpace(apiKey) ? $"apikey:{apiKey}" : $"ip:{HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        RateLimitResult rateResult = await _rateLimitService.CheckOctoPrintUploadLimitAsync(rateKey, _settings.RateLimitPerMinute);
        if (!rateResult.IsAllowed)
        {
            _logger.LogWarning("Rate limit exceeded for {Key}", LogSanitizer.Sanitize(rateKey));
            return StatusCode(429, new { message = "Rate limit exceeded" });
        }

        await _rateLimitService.RecordOctoPrintUploadAttemptAsync(rateKey);

        if (!Request.HasFormContentType)
        {
            _logger.LogWarning("OctoPrint upload rejected: not multipart/form-data. ContentType={ContentType}", LogSanitizer.Sanitize(Request.ContentType));
            return BadRequest(new { message = "No file uploaded - expected multipart/form-data" });
        }

        if (Request.Form.Files.Count == 0)
        {
            _logger.LogWarning(
                "OctoPrint upload rejected: no files in form. Form keys: [{FormKeys}]",
                LogSanitizer.Sanitize(string.Join(", ", Request.Form.Keys)));
            return BadRequest(new { message = "No file uploaded - no files in form" });
        }

        IFormFile file = Request.Form.Files[0];
        string? sanitizedFileName = LogSanitizer.Sanitize(file.FileName);
        _logger.LogInformation(
            "OctoPrint upload file received: Name={FileName}, Length={Length}, ContentType={ContentType}, FormFieldName={FieldName}",
            sanitizedFileName, file.Length, LogSanitizer.Sanitize(file.ContentType), LogSanitizer.Sanitize(file.Name));

        if (file.Length == 0)
        {
            _logger.LogWarning("OctoPrint upload rejected: file is empty");
            return BadRequest(new { message = "Uploaded file is empty" });
        }

        long maxBytes = (long)_settings.MaxUploadSizeMb * 1024 * 1024;
        if (file.Length > maxBytes)
        {
            _logger.LogWarning(
                "OctoPrint upload rejected: file size {SizeMb:F1} MB exceeds limit of {MaxMb} MB",
                file.Length / (1024.0 * 1024.0),
                _settings.MaxUploadSizeMb);
            return StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                new { message = $"File size exceeds the maximum allowed size of {_settings.MaxUploadSizeMb} MB" });
        }

        // Save to IFormFile directly using existing file upload pipeline
        try
        {
            var uploadSettings = HttpContext.RequestServices.GetService(typeof(Farm.Infrastructure.Services.Interfaces.IGcodeUploadSettings)) as Farm.Infrastructure.Services.Interfaces.IGcodeUploadSettings;
            var quotaService = HttpContext.RequestServices.GetService(typeof(Farm.Infrastructure.Services.Quota.IGcodeUploadQuotaService)) as Farm.Infrastructure.Services.Quota.IGcodeUploadQuotaService;

            _logger.LogDebug(
                "OctoPrint upload: uploadSettings={HasSettings}, quotaService={HasQuota}",
                uploadSettings != null,
                quotaService != null);

            if (uploadSettings == null)
            {
                _logger.LogError("OctoPrint upload failed: IGcodeUploadSettings not registered in DI");
                return StatusCode(500, new { message = "Upload configuration missing" });
            }

            if (quotaService == null)
            {
                _logger.LogError("OctoPrint upload failed: IGcodeUploadQuotaService not registered in DI");
                return StatusCode(500, new { message = "Upload quota service missing" });
            }

            _logger.LogDebug("OctoPrint upload: calling UploadFileAsync for {FileName}", sanitizedFileName);
            GcodeFileEntryDto uploadDto = await _gcodeFilesService.UploadFileAsync(null, file, uploadSettings, quotaService, HttpContext.RequestAborted);
            _logger.LogInformation("OctoPrint upload saved: {File} name={Name}, id={Id}", sanitizedFileName, LogSanitizer.Sanitize(uploadDto.FileName), uploadDto.Id);

            if (print)
            {
                // uploadDto contains an Id string (GUID). Parse it to Guid for enqueue request.
                if (string.IsNullOrWhiteSpace(uploadDto.Id) || !Guid.TryParse(uploadDto.Id, out Guid gcodeFileGuid))
                {
                    _logger.LogError("Uploaded file missing Id, cannot enqueue print job");
                    return StatusCode(500, new { message = "Uploaded file not indexed yet" });
                }

                // Note: The printerId query param is for OctoPrint API compatibility but is typically null
                // since slicers don't know PrintFarmer's internal printer IDs.
                // Instead, we use the extracted printer model from the G-code to auto-match printers.
                // The job queue service will find the best available printer based on:
                // - RequiredPrinterModel (from G-code header, e.g., "COREONEL", "X1 Carbon")
                // - RequiredNozzleDiameter (from G-code header)
                // - RequiredMaterialType (from G-code header)
                var enqueueReq = new QueuePrintJobDto
                {
                    GcodeFileId = gcodeFileGuid,
                    AssignedPrinterId = printerId, // Usually null - auto-assign based on model match
                    Priority = PrintJobPriority.Normal,
                    RequiredNozzleDiameter = (decimal?)uploadDto.ExtractedNozzleDiameter,
                    RequiredMaterialType = uploadDto.RequiredMaterial ?? uploadDto.ExtractedMaterial,
                    RequiredPrinterModel = uploadDto.ExtractedPrinterModel // Key for printer matching!
                };

                _logger.LogInformation(
                    "OctoPrint upload+print: Enqueueing job for file={FileName}, model={Model}, nozzle={Nozzle}mm, material={Material}",
                    sanitizedFileName,
                    LogSanitizer.Sanitize(enqueueReq.RequiredPrinterModel) ?? "(any)",
                    enqueueReq.RequiredNozzleDiameter?.ToString("F2") ?? "(any)",
                    LogSanitizer.Sanitize(enqueueReq.RequiredMaterialType) ?? "(any)");

                JobQueuePrintJobDto? job = await _jobQueueService.AddJobToQueueAsync(enqueueReq, callerId, HttpContext.RequestAborted);
                if (job is null)
                {
                    _logger.LogInformation(
                        "OctoPrint upload+print: No compatible printer found for {FileName}. Model={Model}, Material={Material}",
                        sanitizedFileName,
                        LogSanitizer.Sanitize(enqueueReq.RequiredPrinterModel) ?? "(any)",
                        LogSanitizer.Sanitize(enqueueReq.RequiredMaterialType) ?? "(any)");

                    return Ok(new
                    {
                        file = uploadDto,
                        status = "UploadedOnly",
                        message = "File uploaded successfully, but no compatible printer was found for automatic queuing. " +
                                  "Please assign a printer manually from the Print Queue page."
                    });
                }

                _logger.LogInformation(
                    "OctoPrint upload+print: file={FileName}, jobId={JobId}, assignedPrinter={PrinterName} ({PrinterId})",
                    sanitizedFileName, job.Id, LogSanitizer.Sanitize(job.AssignedPrinterName), LogSanitizer.Sanitize(job.AssignedPrinterId?.ToString()));

                return Accepted(new { file = uploadDto, jobId = job.Id, status = "Queued", assignedPrinter = job.AssignedPrinterName });
            }

            return Ok(new { file = uploadDto });
        }
        catch (QueueGroupAccessDeniedException)
        {
            // gcode.PrinterGroupId ACL check (JobQueueService.AddJobToQueueAsync) denied the
            // caller submission rights to the file's printer group.
            _logger.LogWarning("OctoPrint upload+print denied: caller {UserId} lacks submit access to the file's printer group", callerId);
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "You do not have permission to submit jobs to this printer group." });
        }
        catch (UnauthorizedAccessException)
        {
            // Target-printer ACL check (JobQueueService.AddJobToQueueAsync via
            // IQueueResourceAuthorizationService.CanActorAccessPrinterAsync) denied the caller
            // Submit-level access to the specific printer resolved for this job. Mapped to 403
            // (rather than mirroring JobQueueController's 404) for consistent, unambiguous
            // fail-closed semantics on this endpoint — see issue #1666's acceptance criteria.
            _logger.LogWarning("OctoPrint upload+print denied: caller {UserId} lacks submit access to the target printer", callerId);
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "You do not have permission to submit jobs to this printer." });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "OctoPrint upload failed for file {FileName} ({Length} bytes). ExceptionType={ExceptionType}, Message={Message}",
                sanitizedFileName, file.Length, ex.GetType().Name, LogSanitizer.Sanitize(ex.Message));

            // Log inner exception if present
            if (ex.InnerException != null)
            {
                _logger.LogError(
                    "OctoPrint upload inner exception: Type={InnerType}, Message={InnerMessage}",
                    ex.InnerException.GetType().Name, LogSanitizer.Sanitize(ex.InnerException.Message));
            }

            // Include more detail in dev/debug scenarios
            return StatusCode(500, new { message = "Upload failed", error = ex.Message });
        }
    }
#pragma warning restore S6932

    /// <summary>
    /// OctoPrint API: Get version information
    /// Slicers use this to verify OctoPrint compatibility
    /// </summary>
    [HttpGet("version")]
    [AllowAnonymous] // Public so slicers can verify OctoPrint compatibility before an API key is configured.
    [OctoPrintApiKey]
    public IActionResult GetVersion()
    {
        // API key validation is handled by the action's [OctoPrintApiKey] filter.
        return Ok(new
        {
            api = "0.1",
            server = "1.9.3",
            text = "OctoPrint 1.9.3" // Must say "OctoPrint" for slicer compatibility
        });
    }

    /// <summary>
    /// OctoPrint API: Get server status
    /// </summary>
    [HttpGet("server")]
    [AllowAnonymous] // Public so slicers can inspect compatibility status before an API key is configured.
    [OctoPrintApiKey]
    public IActionResult GetServer()
    {
        // API key validation handled by [OctoPrintApiKey] filter at controller level
        return Ok(new
        {
            version = "1.9.3",
            safemode = (string?)null
        });
    }
}
