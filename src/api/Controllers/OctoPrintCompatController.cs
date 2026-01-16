using System;
using System.IO;
using System.Threading.Tasks;
using Farm.Web.Api.Services.Gcode;
using Farm.Web.Api.Services.OctoPrint;
using Farm.Web.Api.Services.PrintJobQueue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Controllers
{
    [ApiController]
    [Route("api")]
    public class OctoPrintCompatController : ControllerBase
    {
        private readonly ILogger<OctoPrintCompatController> _logger;
        private readonly IOctoPrintAuthService _authService;
        private readonly OctoPrintSettings _settings;
        private readonly IGcodeFilesService _gcodeFilesService;
        private readonly IPrintJobQueueService _printJobQueueService;

        public OctoPrintCompatController(
            ILogger<OctoPrintCompatController> logger,
            IOctoPrintAuthService authService,
            IOptions<OctoPrintSettings> settings,
            IGcodeFilesService gcodeFilesService,
            IPrintJobQueueService printJobQueueService)
        {
            _logger = logger;
            _authService = authService;
            _settings = settings.Value;
            _gcodeFilesService = gcodeFilesService;
            _printJobQueueService = printJobQueueService;
        }

        [HttpPost("files/local")]
        [AllowAnonymous]
        [RequestSizeLimit(52428800)] // 50 MB default; adjust based on settings
        public async Task<IActionResult> UploadFileAsync([FromQuery] Guid? printerId, [FromQuery] bool print = false)
        {
            var apiKey = Request.Headers["X-Api-Key"].ToString();

            var allowed = await _authService.ValidateApiKeyAsync(string.IsNullOrWhiteSpace(apiKey) ? null : apiKey, printerId, null);
            if (!allowed)
            {
                return Unauthorized(new { message = "Invalid API key" });
            }

            // Rate limiting: key by apiKey if present otherwise by remote IP
            var rateLimiter = HttpContext.RequestServices.GetService(typeof(Farm.Web.Api.Middleware.SimpleRateLimitService)) as Farm.Web.Api.Middleware.SimpleRateLimitService;
            var octoSettings = _settings;
            string rateKey = !string.IsNullOrWhiteSpace(apiKey) ? $"apikey:{apiKey}" : $"ip:{HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
            var limitOk = rateLimiter?.TryConsume(rateKey, octoSettings.RateLimitPerMinute, TimeSpan.FromMinutes(1)) ?? true;
            if (!limitOk)
            {
                _logger.LogWarning("Rate limit exceeded for {Key}", rateKey);
                return StatusCode(429, new { message = "Rate limit exceeded" });
            }

            if (!Request.HasFormContentType || Request.Form.Files.Count == 0)
            {
                return BadRequest(new { message = "No file uploaded" });
            }

            var file = Request.Form.Files[0];

            if (file.Length == 0)
            {
                return BadRequest(new { message = "Uploaded file is empty" });
            }

            // TODO: enforce _settings.MaxUploadSizeMb

            // Save to IFormFile directly using existing file upload pipeline
            try
            {
                var uploadSettings = HttpContext.RequestServices.GetService(typeof(Farm.Web.Api.Services.IGcodeUploadSettings)) as Farm.Web.Api.Services.IGcodeUploadSettings;
                var quotaService = HttpContext.RequestServices.GetService(typeof(Farm.Web.Api.Services.IGcodeUploadQuotaService)) as Farm.Web.Api.Services.IGcodeUploadQuotaService;
                var uploadDto = await _gcodeFilesService.UploadFileAsync(null, file, uploadSettings!, quotaService!, HttpContext.RequestAborted);
                _logger.LogInformation("OctoPrint upload saved: {File} name={Name}", file.FileName, uploadDto.FileName);

                if (print)
                {
                    // uploadDto contains an Id string (GUID). Parse it to Guid for enqueue request.
                    if (string.IsNullOrWhiteSpace(uploadDto.Id) || !Guid.TryParse(uploadDto.Id, out Guid gcodeFileGuid))
                    {
                        _logger.LogError("Uploaded file missing Id, cannot enqueue print job");
                        return StatusCode(500, new { message = "Uploaded file not indexed yet" });
                    }

                    var enqueueReq = new EnqueuePrintJobRequest(gcodeFileGuid, printerId, null, uploadDto.ExtractedNozzleDiameter, uploadDto.RequiredMaterial);
                    var job = await _printJobQueueService.EnqueueAsync(enqueueReq, HttpContext.RequestAborted);
                    if (job is null)
                    {
                        return StatusCode(500, new { message = "Failed to create print job" });
                    }

                    // Create a pending approval entry so the job must be approved before scheduling
                    var approvalService = HttpContext.RequestServices.GetService(typeof(Farm.Web.Api.Services.PrintJobs.IPrintApprovalService)) as Farm.Web.Api.Services.PrintJobs.IPrintApprovalService;
                    var approvalId = await approvalService!.CreatePendingApprovalAsync(job.Id, printerId, User?.Identity?.Name);

                    return Accepted(new { file = uploadDto, jobId = job.Id, approvalId = approvalId, status = "PendingApproval" });
                }

                return Ok(new { file = uploadDto });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process OctoPrint upload");
                return StatusCode(500, new { message = "Upload failed" });
            }
        }

        /// <summary>
        /// OctoPrint API: Get version information
        /// Slicers use this to verify OctoPrint compatibility
        /// </summary>
        [HttpGet("version")]
        [AllowAnonymous]
        public IActionResult GetVersion()
        {
            return Ok(new
            {
                api = "0.1",
                server = "1.9.3", // Mimics OctoPrint 1.9.3 for slicer compatibility
                text = "PrintFarmer OctoPrint-Compatible API"
            });
        }

        /// <summary>
        /// OctoPrint API: Get server status
        /// </summary>
        [HttpGet("server")]
        [AllowAnonymous]
        public IActionResult GetServer()
        {
            return Ok(new
            {
                version = "1.9.3",
                safemode = (string?)null
            });
        }
    }
}
