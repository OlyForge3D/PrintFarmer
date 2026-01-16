using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    [Route("api/octoprint")]
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
                    // uploadDto contains a GcodeFileId string (GUID). Parse it to Guid for enqueue request.
                    if (string.IsNullOrWhiteSpace(uploadDto.GcodeFileId) || !Guid.TryParse(uploadDto.GcodeFileId, out Guid gcodeFileGuid))
                    {
                        _logger.LogError("Uploaded file missing GcodeFileId, cannot enqueue print job");
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

        /// <summary>
        /// OctoPrint API: List files
        /// Returns all uploaded G-code files in OctoPrint-compatible format
        /// </summary>
        [HttpGet("files")]
        [AllowAnonymous]
        public async Task<IActionResult> ListFilesAsync([FromQuery] bool recursive = false)
        {
            var apiKey = Request.Headers["X-Api-Key"].ToString();

            // Optional authentication - if API key provided, validate it
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var allowed = await _authService.ValidateApiKeyAsync(apiKey, null, null);
                if (!allowed)
                {
                    return Unauthorized(new { message = "Invalid API key" });
                }
            }

            try
            {
                // Query all files from library
                var gcodeFiles = await _gcodeFilesService.QueryLibraryAsync(
                    search: null,
                    material: null,
                    nozzleDiameter: null,
                    printerModelId: null,
                    ct: HttpContext.RequestAborted
                );

                var files = gcodeFiles.Select(f => new
                {
                    name = f.FileName,
                    display = f.FileName,
                    path = $"local/{f.FileName}",
                    type = "machinecode",
                    typePath = new[] { "machinecode", "gcode" },
                    origin = "local",
                    refs = new
                    {
                        resource = $"{Request.Scheme}://{Request.Host}/api/octoprint/files/local/{f.FileName}",
                        download = $"{Request.Scheme}://{Request.Host}/api/gcode-files/{f.Id}/download"
                    },
                    gcodeAnalysis = new
                    {
                        estimatedPrintTime = f.EstimatedPrintTimeMinutes.HasValue ? f.EstimatedPrintTimeMinutes.Value * 60 : (double?)null,
                        filament = new
                        {
                            tool0 = new
                            {
                                length = f.EstimatedFilamentLengthMm,
                                volume = f.EstimatedFilamentLengthMm.HasValue ? f.EstimatedFilamentLengthMm.Value * 2.405 : (double?)null // rough approximation
                            }
                        }
                    },
                    date = ((DateTimeOffset)f.UploadedAt).ToUnixTimeSeconds(),
                    size = f.FileSize
                }).ToList();

                return Ok(new
                {
                    files = files,
                    free = 1000000000L, // 1GB free space (placeholder)
                    total = 10000000000L // 10GB total (placeholder)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list files for OctoPrint API");
                return StatusCode(500, new { message = "Failed to list files" });
            }
        }

        /// <summary>
        /// OctoPrint API: Delete a file
        /// </summary>
        [HttpDelete("files/local/{filename}")]
        [AllowAnonymous]
        public async Task<IActionResult> DeleteFileAsync(string filename)
        {
            var apiKey = Request.Headers["X-Api-Key"].ToString();

            var allowed = await _authService.ValidateApiKeyAsync(string.IsNullOrWhiteSpace(apiKey) ? null : apiKey, null, null);
            if (!allowed)
            {
                return Unauthorized(new { message = "Invalid API key" });
            }

            try
            {
                // Query all files and find the matching one by filename
                var gcodeFiles = await _gcodeFilesService.QueryLibraryAsync(
                    search: null,
                    material: null,
                    nozzleDiameter: null,
                    printerModelId: null,
                    ct: HttpContext.RequestAborted
                );

                var file = gcodeFiles.FirstOrDefault(f => f.FileName == filename);

                if (file == null)
                {
                    return NotFound(new { message = $"File '{filename}' not found" });
                }

                var deleted = await _gcodeFilesService.DeleteFileAsync(file.Id, HttpContext.RequestAborted);

                if (!deleted)
                {
                    _logger.LogWarning("Failed to delete file {FileName} (ID: {FileId})", filename, file.Id);
                    return StatusCode(500, new { message = "Failed to delete file" });
                }

                _logger.LogInformation("OctoPrint API: Deleted file {FileName} (ID: {FileId})", filename, file.Id);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete file {FileName}", filename);
                return StatusCode(500, new { message = "Failed to delete file" });
            }
        }
    }
}
