using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Farm.Web.Shared;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for slicer integration and G-code generation
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SlicerController : ControllerBase
{
    private readonly ILogger<SlicerController> _logger;
    private readonly string _tempPath;
    private readonly Dictionary<string, SlicingJobDto> _activeJobs = new();
    
    public SlicerController(ILogger<SlicerController> logger, IConfiguration configuration)
    {
        _logger = logger;
        _tempPath = configuration["TempStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "temp");
        
        // Ensure temp directory exists
        if (!Directory.Exists(_tempPath))
        {
            Directory.CreateDirectory(_tempPath);
        }
    }

    /// <summary>
    /// Start slicing a 3D model
    /// </summary>
    /// <param name="modelFile">3D model file</param>
    /// <param name="slicerEngine">Slicer engine to use</param>
    /// <param name="printerId">Target printer ID</param>
    /// <param name="profile">Slicer profile settings</param>
    /// <returns>Slicing job information</returns>
    [HttpPost("slice")]
    [ProducesResponseType(typeof(SliceResultDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(100_000_000)] // 100MB limit
    public async Task<IActionResult> SliceModelAsync(
        IFormFile modelFile,
        [FromForm] string slicerEngine,
        [FromForm] string printerId,
        [FromForm] string profile)
    {
        if (modelFile == null || modelFile.Length == 0)
        {
            return BadRequest("Model file is required");
        }

        if (string.IsNullOrEmpty(slicerEngine) || !new[] { "prusaslicer", "orcaslicer" }.Contains(slicerEngine.ToLower()))
        {
            return BadRequest("Valid slicer engine is required (prusaslicer or orcaslicer)");
        }

        if (string.IsNullOrEmpty(printerId) || !Guid.TryParse(printerId, out var printerGuid))
        {
            return BadRequest("Valid printer ID is required");
        }

        SlicerProfileDto? slicerProfile;
        try
        {
            slicerProfile = JsonSerializer.Deserialize<SlicerProfileDto>(profile);
            if (slicerProfile == null)
            {
                return BadRequest("Valid slicer profile is required");
            }
        }
        catch (JsonException)
        {
            return BadRequest("Invalid slicer profile format");
        }

        var jobId = Guid.NewGuid().ToString();
        
        try
        {
            // Save model file temporarily
            var tempModelPath = Path.Combine(_tempPath, $"{jobId}_model{Path.GetExtension(modelFile.FileName)}");
            using (var stream = new FileStream(tempModelPath, FileMode.Create))
            {
                await modelFile.CopyToAsync(stream);
            }

            // Create slicing job
            var job = new SlicingJobDto
            {
                JobId = jobId,
                Status = "queued",
                Progress = 0,
                SlicerEngine = slicerEngine,
                PrinterId = printerGuid,
                ModelFilePath = tempModelPath,
                Profile = slicerProfile,
                CreatedAt = DateTime.UtcNow
            };

            _activeJobs[jobId] = job;

            // Start slicing process (in background)
            _ = Task.Run(() => ProcessSlicingJobAsync(job));

            var result = new SliceResultDto
            {
                JobId = jobId,
                GcodeUrl = $"/api/slicer/job/{jobId}/gcode",
                PrintTime = 0, // Will be updated when slicing completes
                FilamentUsed = 0,
                LayerCount = 0,
                Metadata = new SliceMetadataDto
                {
                    SlicerVersion = slicerEngine == "prusaslicer" ? "PrusaSlicer 2.7.0" : "OrcaSlicer 1.8.0",
                    ProfileUsed = $"{slicerProfile.Quality} - {slicerProfile.Material}",
                    EstimatedCost = 0
                }
            };

            _logger.LogInformation("Slicing job started: {JobId} using {SlicerEngine}", jobId, slicerEngine);
            return Accepted(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start slicing job");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to start slicing job");
        }
    }

    /// <summary>
    /// Get available slicer profiles for a printer
    /// </summary>
    /// <param name="printerId">Printer ID</param>
    /// <returns>Available slicer profiles</returns>
    [HttpGet("profiles")]
    [ProducesResponseType(typeof(IEnumerable<SlicerProfileDto>), StatusCodes.Status200OK)]
    public IActionResult GetAvailableProfiles([FromQuery] string printerId)
    {
        // For now, return some default profiles
        // In a real implementation, this would be based on printer capabilities
        var profiles = new[]
        {
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
        };

        return Ok(profiles);
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
        if (!_activeJobs.TryGetValue(jobId, out var job))
        {
            return NotFound();
        }

        Response.Headers.Add("Content-Type", "text/event-stream");
        Response.Headers.Add("Cache-Control", "no-cache");
        Response.Headers.Add("Connection", "keep-alive");

        try
        {
            // Send initial status
            await SendProgressEventAsync(jobId, job.Progress, job.Status, job.Message);

            // Keep connection alive and send updates
            while (_activeJobs.TryGetValue(jobId, out var currentJob) && 
                   currentJob.Status is "queued" or "slicing")
            {
                await Task.Delay(1000); // Update every second
                
                if (_activeJobs.TryGetValue(jobId, out var updatedJob))
                {
                    await SendProgressEventAsync(jobId, updatedJob.Progress, updatedJob.Status, updatedJob.Message);
                }
            }

            // Send final status
            if (_activeJobs.TryGetValue(jobId, out var finalJob))
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

    private async Task SendProgressEventAsync(string jobId, int progress, string status, string? message = null)
    {
        var progressData = new
        {
            jobId,
            progress,
            status,
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
        if (!_activeJobs.TryGetValue(jobId, out var job))
        {
            return NotFound();
        }

        var result = new SliceResultDto
        {
            JobId = jobId,
            GcodeUrl = job.Status == "completed" ? $"/api/slicer/job/{jobId}/gcode" : "",
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
        if (!_activeJobs.TryGetValue(jobId, out var job) || job.Status != "completed" || string.IsNullOrEmpty(job.GcodeFilePath))
        {
            return NotFound();
        }

        if (!System.IO.File.Exists(job.GcodeFilePath))
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
        if (!_activeJobs.TryGetValue(jobId, out var job))
        {
            return NotFound();
        }

        job.Status = "cancelled";
        job.Message = "Cancelled by user";
        
        _logger.LogInformation("Slicing job cancelled: {JobId}", jobId);
        return NoContent();
    }

    private async Task ProcessSlicingJobAsync(SlicingJobDto job)
    {
        try
        {
            job.Status = "slicing";
            job.Message = "Initializing slicer...";

            // Simulate slicing process with progress updates
            for (int i = 0; i <= 100; i += 10)
            {
                if (job.Status == "cancelled")
                    return;

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

            if (job.Status != "cancelled")
            {
                // Generate mock G-code file
                var gcodeContent = GenerateMockGcode(job.Profile!);
                var gcodeFilePath = Path.Combine(_tempPath, $"{job.JobId}_output.gcode");
                await System.IO.File.WriteAllTextAsync(gcodeFilePath, gcodeContent);

                job.Status = "completed";
                job.GcodeFilePath = gcodeFilePath;
                job.EstimatedPrintTime = 3600; // 1 hour
                job.EstimatedFilamentUsed = 15.5; // 15.5g
                job.LayerCount = 200;
                job.Message = "Slicing completed successfully";
                job.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation("Slicing job completed: {JobId}", job.JobId);
            }
        }
        catch (Exception ex)
        {
            job.Status = "error";
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
}