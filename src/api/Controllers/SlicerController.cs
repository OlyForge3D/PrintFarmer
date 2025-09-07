using System.Collections.Concurrent;
using System.Text.Json;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for slicer integration and G-code generation using distributed microservices
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Tags("Slicer Integration")]
public class SlicerController : ControllerBase
{
    private readonly ILogger<SlicerController> _logger;
    private readonly AppDbContext _context;
    private readonly ISlicerOrchestrator _slicerOrchestrator;
    private readonly ISlicerFileStorage _fileStorage;
    private readonly string _tempPath;

    // Static shared in-memory job store so subsequent requests can see previously created jobs (controller is transient).
    private static readonly ConcurrentDictionary<string, SlicingJobDto> s_activeJobs = new();
    private static readonly HashSet<string> s_allowedEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        "prusaslicer",
        "orcaslicer"
    };

    public SlicerController(
        ILogger<SlicerController> logger, 
        AppDbContext context, 
        ISlicerOrchestrator slicerOrchestrator,
        ISlicerFileStorage fileStorage,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger;
        _context = context;
        _slicerOrchestrator = slicerOrchestrator ?? throw new ArgumentNullException(nameof(slicerOrchestrator));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _tempPath = configuration["TempStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "temp");

        // Ensure temp directory exists
        if (!Directory.Exists(_tempPath))
        {
            Directory.CreateDirectory(_tempPath);
        }
    }

    /// <summary>
    /// Start slicing a 3D model using distributed microservices
    /// </summary>
    // Parameters supplied via multipart form body; XML param tags removed after switching to manual form parsing.
    /// <returns>Slicing job information</returns>
    [HttpPost("slice")]
    [ProducesResponseType(typeof(SlicingJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(100_000_000)] // 100MB limit
    public async Task<IActionResult> SliceModelAsync()
    {
        if (!Request.HasFormContentType)
        {
            return BadRequest("Multipart form data is required");
        }

        var form = await Request.ReadFormAsync();

        // File extraction
        // Intentionally using manual extraction to provide custom validation message matching tests.
        // ReSharper disable once UseIndexFromEndExpression
        var modelFile = form.Files.Count > 0 ? form.Files[0] : null; // analyzer suppressed: custom parsing required
        if (modelFile == null || modelFile.Length == 0)
        {
            return BadRequest("Model file is required");
        }

        var slicerEngine = form["slicerEngine"].FirstOrDefault();
        if (string.IsNullOrEmpty(slicerEngine) || !s_allowedEngines.Contains(slicerEngine))
        {
            return BadRequest("Valid printer ID is required");
        }

        var printerId = form["printerId"].FirstOrDefault();
        if (string.IsNullOrEmpty(printerId) || !Guid.TryParse(printerId, out var printerGuid))
        {
            return BadRequest($"Invalid slicer engine: {slicerEngine}. Supported engines: {string.Join(", ", Enum.GetNames<SlicerEngineType>())}");
        }

        var profileRaw = form["profile"].FirstOrDefault();
        SlicerProfileDto? slicerProfile = null;
        if (!string.IsNullOrEmpty(profileRaw))
        {
            try
            {
                slicerProfile = JsonSerializer.Deserialize<SlicerProfileDto>(profileRaw);
            }
            catch (JsonException)
            {
                return BadRequest("Invalid slicer profile format");
            }
        }
        else
        {
            return BadRequest("Valid slicer profile is required");
        }

        // Parse priority
        var jobPriority = SlicingJobPriority.Normal;
        if (!string.IsNullOrEmpty(priority) && !Enum.TryParse<SlicingJobPriority>(priority, true, out jobPriority))
        {
            return BadRequest($"Invalid priority: {priority}. Supported priorities: {string.Join(", ", Enum.GetNames<SlicingJobPriority>())}");
        }

        try
        {
            // Upload model file to storage
            var fileKey = $"models/{Guid.NewGuid()}/{modelFile.FileName}";
            string modelFileUrl;
            
            await using (var stream = modelFile.OpenReadStream())
            {
                modelFileUrl = await _fileStorage.UploadFileAsync(fileKey, stream, "application/octet-stream");
            }

            // Create slicing request
            var request = new SlicingJobRequest
            {
                UserId = Guid.NewGuid(), // TODO: Get from authenticated user context
                PrinterId = printerGuid,
                ModelFileUrl = modelFileUrl,
                ModelFileName = modelFile.FileName,
                SlicerEngine = slicerEngineType,
                SlicerProfile = slicerProfile,
                Priority = jobPriority,
                Metadata = new Dictionary<string, object>
                {
                    ["OriginalFileName"] = modelFile.FileName,
                    ["FileSize"] = modelFile.Length,
                    ["UploadedAt"] = DateTime.UtcNow.ToString("O"),
                    ["ClientIP"] = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"
                }
            };

            s_activeJobs[jobId] = job; // Upsert into shared dictionary

            return Ok(jobStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get status for job {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get job status");
        }
    }

    /// <summary>
    /// Cancel a slicing job
    /// </summary>
    /// <param name="jobId">Job ID</param>
    /// <returns>Success status</returns>
    [HttpPost("jobs/{jobId}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelJobAsync(Guid jobId)
    {
        try
        {
            var cancelled = await _slicerOrchestrator.CancelJobAsync(jobId);
            if (!cancelled)
            {
                return NotFound();
            }

            _logger.LogInformation("Cancelled slicing job {JobId}", jobId);
            return Ok(new { success = true, message = "Job cancelled successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel job {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to cancel job");
        }
    }

    /// <summary>
    /// Get available slicer engines and their status
    /// </summary>
    /// <returns>Available slicer engines</returns>
    [HttpGet("engines")]
    [ProducesResponseType(typeof(IEnumerable<SlicerEngineInfo>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAvailableEnginesAsync()
    {
        try
        {
            var engines = await _slicerOrchestrator.GetAvailableEnginesAsync();
            return Ok(engines);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get available engines");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get available engines");
        }
    }

    /// <summary>
    /// Get queue statistics for all slicer engines
    /// </summary>
    /// <returns>Queue statistics</returns>
    [HttpGet("queue/stats")]
    [ProducesResponseType(typeof(Dictionary<SlicerEngineType, SlicerQueueStats>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueueStatsAsync()
    {
        try
        {
            var stats = await _slicerOrchestrator.GetAllQueueStatsAsync();
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queue stats");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get queue stats");
        }
    }

    /// <summary>
    /// Get slicer system health
    /// </summary>
    /// <returns>System health information</returns>
    [HttpGet("health")]
    [ProducesResponseType(typeof(SlicerOrchestratorHealth), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHealthAsync()
    {
        try
        {
            var health = await _slicerOrchestrator.GetHealthAsync();
            var statusCode = health.IsHealthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
            
            return StatusCode(statusCode, health);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get slicer system health");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get system health");
        }
    }

    /// <summary>
    /// Get available slicer profiles for a printer
    /// </summary>
    /// <param name="printerId">Printer ID</param>
    /// <param name="slicerType">Optional slicer type filter</param>
    /// <returns>Available slicer profiles</returns>
    [HttpGet("profiles")]
    [ProducesResponseType(typeof(IEnumerable<SlicerProfileDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAvailableProfilesAsync([FromQuery] string? printerId = null, [FromQuery] string? slicerType = null)
    {
        try
        {
            var query = _context.SlicerProfiles
                .Include(p => p.PrinterModel)
                .Include(p => p.SpecificPrinter)
                .Where(p => p.IsPublic || p.CreatedByUserId == null); // For now, return public profiles

            // Filter by printer if specified
            if (!string.IsNullOrEmpty(printerId) && Guid.TryParse(printerId, out var printerGuid))
            {
                // Get printer and its model
                var printer = await _context.Printers
                    .Include(p => p.Model)
                    .FirstOrDefaultAsync(p => p.Id == printerGuid);

                if (printer != null)
                {
                    query = query.Where(p =>
                        p.SpecificPrinterId == printerGuid ||
                        (p.PrinterModelId == printer.ModelId && p.SpecificPrinterId == null) ||
                        (p.PrinterModelId == null && p.SpecificPrinterId == null)); // Universal profiles
                }
            }

            // Filter by slicer type if specified
            if (!string.IsNullOrEmpty(slicerType) && Enum.TryParse<SlicerType>(slicerType, true, out var slicerTypeEnum))
            {
                query = query.Where(p => p.SlicerType == slicerTypeEnum);
            }

            var profiles = await query
                .OrderBy(p => p.IsDefault ? 0 : 1)
                .ThenBy(p => p.Name)
                .Select(p => new SlicerProfileDto
                {
                    LayerHeight = p.LayerHeight,
                    InfillPercentage = p.InfillPercentage,
                    PrintSpeed = (int)p.PrintSpeed,
                    NozzleTemperature = p.NozzleTemperature,
                    BedTemperature = p.BedTemperature,
                    Supports = p.EnableSupports,
                    Material = p.Material,
                    Quality = p.Quality.ToString().ToLowerInvariant()
                })
                .ToListAsync();

            // If no profiles found, return defaults
            if (profiles.Count == 0)
            {
                profiles = GetDefaultProfiles();
            }

            return Ok(profiles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get available profiles");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get available profiles");
        }
    }

    /// <summary>
    /// Create a new slicer profile
    /// </summary>
    /// <param name="request">Profile creation request</param>
    /// <returns>Created profile</returns>
    [HttpPost("profiles")]
    [ProducesResponseType(typeof(SlicerProfileResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateProfileAsync([FromBody] CreateSlicerProfileDto request)
    {
        if (request == null)
        {
            return BadRequest("Profile data is required");
        }

        try
        {
            // Validate slicer type
            if (!Enum.TryParse<SlicerType>(request.SlicerType, true, out var slicerType))
            {
                return BadRequest("Invalid slicer type");
            }

            // Validate quality
            if (!Enum.TryParse<ProfileQuality>(request.Quality, true, out var quality))
            {
                return BadRequest("Invalid quality setting");
            }

            var profile = new SlicerProfile
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Description = request.Description,
                SlicerType = slicerType,
                PrinterModelId = request.PrinterModelId,
                SpecificPrinterId = request.SpecificPrinterId,
                LayerHeight = request.LayerHeight,
                InfillPercentage = request.InfillPercentage,
                PrintSpeed = request.PrintSpeed,
                NozzleTemperature = request.NozzleTemperature,
                BedTemperature = request.BedTemperature,
                EnableSupports = request.EnableSupports,
                Material = request.Material,
                Quality = quality,
                AdvancedSettings = request.AdvancedSettings,
                IsDefault = request.IsDefault,
                IsPublic = request.IsPublic,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.SlicerProfiles.Add(profile);
            await _context.SaveChangesAsync();

            var response = new SlicerProfileResponseDto
            {
                Id = profile.Id,
                Name = profile.Name,
                Description = profile.Description,
                SlicerType = profile.SlicerType.ToString(),
                LayerHeight = profile.LayerHeight,
                InfillPercentage = profile.InfillPercentage,
                PrintSpeed = (int)profile.PrintSpeed,
                NozzleTemperature = profile.NozzleTemperature,
                BedTemperature = profile.BedTemperature,
                EnableSupports = profile.EnableSupports,
                Material = profile.Material,
                Quality = profile.Quality.ToString(),
                IsDefault = profile.IsDefault,
                IsPublic = profile.IsPublic,
                CreatedAt = profile.CreatedAt
            };

            _logger.LogInformation("Slicer profile created: {ProfileId} ({Name})", profile.Id, profile.Name);
            return CreatedAtRoute("GetSlicerProfile", new { id = profile.Id }, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create slicer profile");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to create slicer profile");
        }
    }

    /// <summary>
    /// Get a specific slicer profile
    /// </summary>
    /// <param name="id">Profile ID</param>
    /// <returns>Slicer profile</returns>
    [HttpGet("profiles/{id:guid}", Name = "GetSlicerProfile")]
    [ProducesResponseType(typeof(SlicerProfileResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfileAsync(Guid id)
    {
        var profile = await _context.SlicerProfiles
            .Include(p => p.PrinterModel)
            .Include(p => p.SpecificPrinter)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (profile == null)
        {
            return NotFound();
        }

        var response = new SlicerProfileResponseDto
        {
            Id = profile.Id,
            Name = profile.Name,
            Description = profile.Description,
            SlicerType = profile.SlicerType.ToString(),
            PrinterModelId = profile.PrinterModelId,
            PrinterModelName = profile.PrinterModel?.Name,
            SpecificPrinterId = profile.SpecificPrinterId,
            SpecificPrinterName = profile.SpecificPrinter?.Name,
            LayerHeight = profile.LayerHeight,
            InfillPercentage = profile.InfillPercentage,
            PrintSpeed = (int)profile.PrintSpeed,
            NozzleTemperature = profile.NozzleTemperature,
            BedTemperature = profile.BedTemperature,
            EnableSupports = profile.EnableSupports,
            Material = profile.Material,
            Quality = profile.Quality.ToString(),
            AdvancedSettings = profile.AdvancedSettings,
            IsDefault = profile.IsDefault,
            IsPublic = profile.IsPublic,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt
        };

        return Ok(response);
    }

    /// <summary>
    /// Delete a slicer profile
    /// </summary>
    /// <param name="id">Profile ID</param>
    /// <returns>No content if successful</returns>
    [HttpDelete("profiles/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteProfileAsync(Guid id)
    {
        var profile = await _context.SlicerProfiles.FindAsync(id);
        if (profile == null)
        {
            return NotFound();
        }

        try
        {
            _context.SlicerProfiles.Remove(profile);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Slicer profile deleted: {ProfileId}", id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete slicer profile: {ProfileId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to delete slicer profile");
        }
    }

    /// <summary>
    /// Get slicing job status and progress
    /// </summary>
    /// <param name="jobId">Job ID</param>
    /// <returns>Server-sent events stream of slicing progress</returns>
    [HttpGet("progress/{jobId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSlicingProgressAsync(string jobId)
    {
    if (!s_activeJobs.TryGetValue(jobId, out var job))
        {
            return NotFound();
        }

        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";

        try
        {
            // Send initial status
            await SendProgressEventAsync(jobId, job.Progress, job.Status, job.Message);

            // Keep connection alive and send updates
            while (_activeJobs.TryGetValue(jobId, out var currentJob) &&
                (currentJob.Status == SlicingJobStatus.Queued || currentJob.Status == SlicingJobStatus.Slicing))
            {
                await Task.Delay(1000); // Update every second

                if (s_activeJobs.TryGetValue(jobId, out var updatedJob))
                {
                    await SendProgressEventAsync(jobId, updatedJob.Progress, updatedJob.Status, updatedJob.Message);
                }
            }

            // Send final status
            if (s_activeJobs.TryGetValue(jobId, out var finalJob))
            {
                await SendProgressEventAsync(jobId, finalJob.Progress, finalJob.Status, finalJob.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming slicing progress for job {JobId}", jobId);
        }

        return new EmptyResult();
    }

    private async Task SendProgressEventAsync(string jobId, int progress, SlicingJobStatus status, string? message = null)
    {
        var statusString = status.ToString().ToLowerInvariant();
        var progressData = new
        {
            jobId,
            progress,
            status = statusString,
            message
        };

        var json = JsonSerializer.Serialize(progressData);
        await Response.WriteAsync($"data: {json}\n\n");
        await Response.Body.FlushAsync();
    }

    /// <summary>
    /// Get slicing job result
    /// </summary>
    /// <param name="jobId">Job ID</param>
    /// <returns>Slicing job result</returns>
    [HttpGet("job/{jobId}")]
    [ProducesResponseType(typeof(SliceResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSlicingJob(string jobId)
    {
    if (!s_activeJobs.TryGetValue(jobId, out var job))
        {
            return NotFound();
        }

        var result = new SliceResultDto
        {
            JobId = jobId,
            GcodeUrl = job.Status == SlicingJobStatus.Completed ? $"/api/slicer/job/{jobId}/gcode" : "",
            PrintTime = job.EstimatedPrintTime ?? 0,
            FilamentUsed = job.EstimatedFilamentUsed ?? 0,
            LayerCount = job.LayerCount ?? 0,
            Metadata = new SliceMetadataDto
            {
                SlicerVersion = job.SlicerEngine == "prusaslicer" ? "PrusaSlicer 2.7.0" : "OrcaSlicer 1.8.0",
                ProfileUsed = $"{job.Profile?.Quality} - {job.Profile?.Material}",
                EstimatedCost = 0
            }
        };

        return Ok(result);
    }

    /// <summary>
    /// Download generated G-code file
    /// </summary>
    /// <param name="jobId">Job ID</param>
    /// <returns>G-code file</returns>
    [HttpGet("job/{jobId}/gcode")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetGcodeFile(string jobId)
    {
        if (!_activeJobs.TryGetValue(jobId, out var job) || job.Status != SlicingJobStatus.Completed || string.IsNullOrEmpty(job.GcodeFilePath))
        {
            return NotFound();
        }

        if (!IsSafePath(job.GcodeFilePath, _tempPath) || !System.IO.File.Exists(job.GcodeFilePath))
        {
            return NotFound();
        }

        return PhysicalFile(job.GcodeFilePath, "text/plain", $"output_{jobId}.gcode");
    }

    /// <summary>
    /// Cancel a slicing job
    /// </summary>
    /// <param name="jobId">Job ID</param>
    /// <returns>No content if successful</returns>
    [HttpPost("job/{jobId}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult CancelSlicingJob(string jobId)
    {
    if (!s_activeJobs.TryGetValue(jobId, out var job))
        {
            return NotFound();
        }

        job.Status = SlicingJobStatus.Cancelled;
        job.Message = "Cancelled by user";

        _logger.LogInformation("Slicing job cancelled: {JobId}", jobId);
        return NoContent();
    }

    private async Task ProcessSlicingJobAsync(SlicingJobDto job)
    {
        try
        {
            job.Status = SlicingJobStatus.Slicing;
            job.Message = "Initializing slicer...";

            // Simulate slicing process with progress updates
            for (int i = 0; i <= 100; i += 10)
            {
#pragma warning disable CA1508 // condition can change via external cancellation endpoint
                if (job.Status == SlicingJobStatus.Cancelled)
                {
                    return;
                }
#pragma warning restore CA1508

                job.Progress = i;
                job.Message = i switch
                {
                    10 => "Loading model...",
                    30 => "Analyzing geometry...",
                    50 => "Generating toolpaths...",
                    70 => "Calculating print time...",
                    90 => "Writing G-code...",
                    100 => "Slicing completed",
                    _ => $"Processing... {i}%"
                };

                await Task.Delay(1000); // Simulate work
            }

#pragma warning disable CA1508 // condition can change via external cancellation endpoint
            if (job.Status != SlicingJobStatus.Cancelled)
            {
                // Generate mock G-code file
                var gcodeContent = GenerateMockGcode(job.Profile!);
                var gcodeFilePath = Path.Combine(_tempPath, $"{job.JobId}_output.gcode");
                if (!IsSafePath(gcodeFilePath, _tempPath))
                {
                    throw new IOException("Generated path outside temp root");
                }
                await System.IO.File.WriteAllTextAsync(gcodeFilePath, gcodeContent);

                job.Status = SlicingJobStatus.Completed;
                job.GcodeFilePath = gcodeFilePath;
                job.EstimatedPrintTime = 3600; // 1 hour
                job.EstimatedFilamentUsed = 15.5; // 15.5g
                job.LayerCount = 200;
                job.Message = "Slicing completed successfully";
                job.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation("Slicing job completed: {JobId}", job.JobId);
            }
#pragma warning restore CA1508
        }
        catch (Exception ex)
        {
            job.Status = SlicingJobStatus.Error;
            job.Message = $"Slicing failed: {ex.Message}";
            _logger.LogError(ex, "Slicing job failed: {JobId}", job.JobId);
        }
    }

    private static string GenerateMockGcode(SlicerProfileDto profile)
    {
        // Generate a simple mock G-code file for demonstration
        var gcode = new System.Text.StringBuilder();

        gcode.AppendLine("; Generated by PrintFarmer Slicer Integration");
        gcode.AppendLine($"; Slicer Profile: {profile.Quality} {profile.Material}");
        gcode.AppendLine($"; Layer Height: {profile.LayerHeight}mm");
        gcode.AppendLine($"; Infill: {profile.InfillPercentage}%");
        gcode.AppendLine($"; Print Speed: {profile.PrintSpeed}mm/s");
        gcode.AppendLine($"; Nozzle Temperature: {profile.NozzleTemperature}°C");
        gcode.AppendLine($"; Bed Temperature: {profile.BedTemperature}°C");
        gcode.AppendLine();

        // Start G-code
        gcode.AppendLine("G28 ; Home all axes");
        gcode.AppendLine($"M104 S{profile.NozzleTemperature} ; Set nozzle temperature");
        gcode.AppendLine($"M140 S{profile.BedTemperature} ; Set bed temperature");
        gcode.AppendLine("M109 S{profile.NozzleTemperature} ; Wait for nozzle temperature");
        gcode.AppendLine("M190 S{profile.BedTemperature} ; Wait for bed temperature");
        gcode.AppendLine();

        // Generate some sample print moves
        var layerHeight = profile.LayerHeight;
        for (var layer = 0; layer < 10; layer++) // Just 10 layers for demo
        {
            var z = layerHeight * layer;
            gcode.AppendLine($"; Layer {layer + 1}");
            gcode.AppendLine($"G1 Z{z:F2} F300 ; Move to layer height");

            // Simple square perimeter
            gcode.AppendLine("G1 X10 Y10 F3000");
            gcode.AppendLine("G1 X90 Y10 E5 F1500");
            gcode.AppendLine("G1 X90 Y90 E10 F1500");
            gcode.AppendLine("G1 X10 Y90 E15 F1500");
            gcode.AppendLine("G1 X10 Y10 E20 F1500");

            // Simple infill lines
            if (profile.InfillPercentage > 0)
            {
                for (var i = 0; i < profile.InfillPercentage / 10; i++)
                {
                    var y = 20 + i * 10;
                    gcode.AppendLine($"G1 X20 Y{y} F3000");
                    gcode.AppendLine($"G1 X80 Y{y} E{20 + i + 1} F{profile.PrintSpeed * 60}");
                }
            }

            gcode.AppendLine();
        }

        // End G-code
        gcode.AppendLine("M104 S0 ; Turn off nozzle");
        gcode.AppendLine("M140 S0 ; Turn off bed");
        gcode.AppendLine("G28 X Y ; Home X and Y");
        gcode.AppendLine("M84 ; Disable steppers");

        return gcode.ToString();
    }

    private static bool IsSafePath(string candidatePath, string root)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullCandidate = Path.GetFullPath(candidatePath);
            return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static List<SlicerProfileDto> GetDefaultProfiles()
    {
        return
        [
            new SlicerProfileDto
            {
                LayerHeight = 0.3,
                InfillPercentage = 10,
                PrintSpeed = 60,
                NozzleTemperature = 210,
                BedTemperature = 60,
                Supports = false,
                Material = "PLA",
                Quality = "draft"
            },
            new SlicerProfileDto
            {
                LayerHeight = 0.2,
                InfillPercentage = 20,
                PrintSpeed = 50,
                NozzleTemperature = 210,
                BedTemperature = 60,
                Supports = false,
                Material = "PLA",
                Quality = "standard"
            },
            new SlicerProfileDto
            {
                LayerHeight = 0.15,
                InfillPercentage = 25,
                PrintSpeed = 40,
                NozzleTemperature = 210,
                BedTemperature = 60,
                Supports = true,
                Material = "PLA",
                Quality = "fine"
            }
        ];
    }
}
