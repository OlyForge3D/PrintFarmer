using System.Text.Json;
using Farm.Web.Shared;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
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
    /// <param name="modelFile">3D model file</param>
    /// <param name="slicerEngine">Slicer engine to use</param>
    /// <param name="printerId">Target printer ID</param>
    /// <param name="profile">Slicer profile settings</param>
    /// <param name="priority">Job priority (optional)</param>
    /// <returns>Slicing job information</returns>
    [HttpPost("slice")]
    [ProducesResponseType(typeof(SlicingJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(100_000_000)] // 100MB limit
    public async Task<IActionResult> SliceModelAsync(
        IFormFile modelFile,
        [FromForm] string slicerEngine,
        [FromForm] string printerId,
        [FromForm] string profile,
        [FromForm] string? priority = null)
    {
        if (modelFile == null || modelFile.Length == 0)
        {
            return BadRequest("Model file is required");
        }

        if (string.IsNullOrEmpty(printerId) || !Guid.TryParse(printerId, out var printerGuid))
        {
            return BadRequest("Valid printer ID is required");
        }

        // Parse slicer engine
        if (!Enum.TryParse<SlicerEngineType>(slicerEngine, true, out var slicerEngineType))
        {
            return BadRequest($"Invalid slicer engine: {slicerEngine}. Supported engines: {string.Join(", ", Enum.GetNames<SlicerEngineType>())}");
        }

        // Parse slicer profile
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

            // Submit job to orchestrator
            var jobResponse = await _slicerOrchestrator.SubmitJobAsync(request);

            _logger.LogInformation("Submitted slicing job {JobId} for file {FileName} using {SlicerEngine}", 
                jobResponse.JobId, modelFile.FileName, slicerEngineType);

            return Accepted(jobResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit slicing job for file {FileName}", modelFile.FileName);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to submit slicing job");
        }
    }

    /// <summary>
    /// Get slicing job status
    /// </summary>
    /// <param name="jobId">Job ID</param>
    /// <returns>Job status information</returns>
    [HttpGet("jobs/{jobId}")]
    [ProducesResponseType(typeof(SlicingJobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobStatusAsync(Guid jobId)
    {
        try
        {
            var jobStatus = await _slicerOrchestrator.GetJobStatusAsync(jobId);
            if (jobStatus == null)
            {
                return NotFound($"Job {jobId} not found");
            }

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
                return NotFound($"Job {jobId} not found or cannot be cancelled");
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
            return CreatedAtAction(nameof(GetProfileAsync), new { id = profile.Id }, response);
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
    [HttpGet("profiles/{id:guid}")]
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
