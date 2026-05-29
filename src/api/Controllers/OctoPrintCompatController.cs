using System;
using System.IO;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.RateLimiting;
using Farm.Infrastructure.Settings;
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
[AllowAnonymous]
[OctoPrintApiKey] // Validates API key based on OctoPrintSettings.RequireApiKey
public class OctoPrintCompatController : ControllerBase
{
    private readonly ILogger<OctoPrintCompatController> _logger;
    private readonly IOctoPrintAuthService _authService;
    private readonly OctoPrintSettings _settings;
    private readonly IGcodeFilesService _gcodeFilesService;
    private readonly IJobQueueService _jobQueueService;
    private readonly IRateLimitService _rateLimitService;

    public OctoPrintCompatController(
        ILogger<OctoPrintCompatController> logger,
        IOctoPrintAuthService authService,
        IOptions<OctoPrintSettings> settings,
        IGcodeFilesService gcodeFilesService,
        IJobQueueService jobQueueService,
        IRateLimitService rateLimitService)
    {
        _logger = logger;
        _authService = authService;
        _settings = settings.Value;
        _gcodeFilesService = gcodeFilesService;
        _jobQueueService = jobQueueService;
        _rateLimitService = rateLimitService;
    }

#pragma warning disable S6932 // Controller intentionally uses raw request data for OctoPrint API compatibility
    [HttpPost("files/local")]
    [RequestSizeLimit(52428800)] // 50 MB default; adjust based on settings
    public async Task<IActionResult> UploadFileAsync([FromQuery] Guid? printerId)
    {
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
            Request.ContentType, Request.ContentLength, print, select, printerId);

        // API key validation handled by [OctoPrintApiKey] filter at controller level

        // Rate limiting: key by apiKey if present otherwise by remote IP
        var apiKey = Request.Headers["X-Api-Key"].ToString();
        string rateKey = !string.IsNullOrWhiteSpace(apiKey) ? $"apikey:{apiKey}" : $"ip:{HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        RateLimitResult rateResult = await _rateLimitService.CheckOctoPrintUploadLimitAsync(rateKey, _settings.RateLimitPerMinute);
        if (!rateResult.IsAllowed)
        {
            _logger.LogWarning("Rate limit exceeded for {Key}", rateKey);
            return StatusCode(429, new { message = "Rate limit exceeded" });
        }

        await _rateLimitService.RecordOctoPrintUploadAttemptAsync(rateKey);

        if (!Request.HasFormContentType)
        {
            _logger.LogWarning("OctoPrint upload rejected: not multipart/form-data. ContentType={ContentType}", Request.ContentType);
            return BadRequest(new { message = "No file uploaded - expected multipart/form-data" });
        }

        if (Request.Form.Files.Count == 0)
        {
            _logger.LogWarning(
                "OctoPrint upload rejected: no files in form. Form keys: [{FormKeys}]",
                string.Join(", ", Request.Form.Keys));
            return BadRequest(new { message = "No file uploaded - no files in form" });
        }

        IFormFile file = Request.Form.Files[0];
        _logger.LogInformation(
            "OctoPrint upload file received: Name={FileName}, Length={Length}, ContentType={ContentType}, FormFieldName={FieldName}",
            file.FileName, file.Length, file.ContentType, file.Name);

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

            _logger.LogDebug("OctoPrint upload: calling UploadFileAsync for {FileName}", file.FileName);
            GcodeFileEntryDto uploadDto = await _gcodeFilesService.UploadFileAsync(null, file, uploadSettings, quotaService, HttpContext.RequestAborted);
            _logger.LogInformation("OctoPrint upload saved: {File} name={Name}, id={Id}", file.FileName, uploadDto.FileName, uploadDto.Id);

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
                    file.FileName,
                    enqueueReq.RequiredPrinterModel ?? "(any)",
                    enqueueReq.RequiredNozzleDiameter?.ToString("F2") ?? "(any)",
                    enqueueReq.RequiredMaterialType ?? "(any)");

                JobQueuePrintJobDto? job = await _jobQueueService.AddJobToQueueAsync(enqueueReq, null, HttpContext.RequestAborted);
                if (job is null)
                {
                    _logger.LogInformation(
                        "OctoPrint upload+print: No compatible printer found for {FileName}. Model={Model}, Material={Material}",
                        file.FileName,
                        enqueueReq.RequiredPrinterModel ?? "(any)",
                        enqueueReq.RequiredMaterialType ?? "(any)");

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
                    file.FileName, job.Id, job.AssignedPrinterName, job.AssignedPrinterId);

                return Accepted(new { file = uploadDto, jobId = job.Id, status = "Queued", assignedPrinter = job.AssignedPrinterName });
            }

            return Ok(new { file = uploadDto });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "OctoPrint upload failed for file {FileName} ({Length} bytes). ExceptionType={ExceptionType}, Message={Message}",
                file.FileName, file.Length, ex.GetType().Name, ex.Message);

            // Log inner exception if present
            if (ex.InnerException != null)
            {
                _logger.LogError(
                    "OctoPrint upload inner exception: Type={InnerType}, Message={InnerMessage}",
                    ex.InnerException.GetType().Name, ex.InnerException.Message);
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
    public IActionResult GetVersion()
    {
        // API key validation handled by [OctoPrintApiKey] filter at controller level
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
