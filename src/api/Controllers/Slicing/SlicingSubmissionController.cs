using System.Text.Json;
using Farm.Web.Api.Controllers.Slicing;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Slicing;

[ApiController]
[Route("api/slicer")]
[Tags("Slicer Submission")]
public class SlicingSubmissionController : ControllerBase
{
    private static readonly HashSet<string> AllowedEngines = new(StringComparer.OrdinalIgnoreCase){"prusaslicer","orcaslicer"};
    private readonly ISlicerFileStorage _fileStorage;
    private readonly ILogger<SlicingSubmissionController> _logger;
    private readonly string _tempPath;

    public SlicingSubmissionController(ISlicerFileStorage fileStorage, ILogger<SlicingSubmissionController> logger, IConfiguration cfg)
    {
        _fileStorage = fileStorage; _logger = logger;
        _tempPath = cfg["TempStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "temp");
        Directory.CreateDirectory(_tempPath);
    }

    [HttpPost("slice")]
    [ProducesResponseType(typeof(SlicingJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(100_000_000)]
    public async Task<IActionResult> SliceAsync()
    {
        if (!Request.HasFormContentType) return BadRequest("Multipart form data is required");
        var form = await Request.ReadFormAsync();
        var modelFile = form.Files.Count > 0 ? form.Files[0] : null;
        if (modelFile == null || modelFile.Length == 0) return BadRequest("Model file is required");
        if (Path.GetExtension(modelFile.FileName).ToLowerInvariant() != ".stl") return BadRequest("Invalid model file type");
        try
        {
            using var validationStream = modelFile.OpenReadStream();
            using var reader = new StreamReader(validationStream, leaveOpen: true);
            var first = await reader.ReadLineAsync() ?? string.Empty;
            if (!first.StartsWith("solid", StringComparison.OrdinalIgnoreCase)) return BadRequest("Invalid model file");
        } catch { return BadRequest("Invalid model file"); }

        var slicerEngine = form["slicerEngine"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(slicerEngine) || !AllowedEngines.Contains(slicerEngine)) return BadRequest("Valid slicer engine is required");
        var printerId = form["printerId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(printerId) || !Guid.TryParse(printerId, out var printerGuid)) return BadRequest("Valid printer ID is required");

        var profileRaw = form["profile"].FirstOrDefault() ?? form["slicerProfile"].FirstOrDefault();
        if (string.IsNullOrEmpty(profileRaw)) return BadRequest("Valid slicer profile is required");
        SlicerProfileDto? profile;
        try { profile = JsonSerializer.Deserialize<SlicerProfileDto>(profileRaw); }
        catch { return BadRequest("Invalid slicer profile format"); }

        var priorityRaw = form["priority"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(priorityRaw) && !Enum.TryParse(priorityRaw, true, out SlicingJobPriority _))
            return BadRequest($"Invalid priority: {priorityRaw}. Supported priorities: {string.Join(", ", Enum.GetNames<SlicingJobPriority>())}");
        if (!Enum.TryParse<SlicerEngineType>(slicerEngine, true, out _))
            return BadRequest($"Invalid slicer engine: {slicerEngine}. Supported engines: {string.Join(", ", Enum.GetNames<SlicerEngineType>())}");

        try
        {
            var fileKey = $"models/{Guid.NewGuid()}/{modelFile.FileName}";
            string modelFileUrl;
            await using (var stream = modelFile.OpenReadStream())
            {
                modelFileUrl = await _fileStorage.UploadFileAsync(fileKey, stream, "application/octet-stream");
            }

            var job = new SlicingJobDto
            {
                JobId = Guid.NewGuid().ToString(),
                Status = SlicingJobStatus.Queued,
                Progress = 0,
                SlicerEngine = slicerEngine,
                PrinterId = printerGuid,
                ModelFilePath = modelFileUrl,
                Profile = profile,
                CreatedAt = DateTime.UtcNow
            };
            SlicingJobStore.Add(job);
            _ = Task.Run(() => SimulateAsync(job));

            var version = slicerEngine.Equals("prusaslicer", StringComparison.OrdinalIgnoreCase)?"PrusaSlicer 2.7.0":"OrcaSlicer 1.8.0";
            var response = new {
                jobId = job.JobId,
                status = job.Status.ToString(),
                progress = job.Progress,
                gcodeUrl = $"/api/slicer/jobs/{job.JobId}/gcode",
                metadata = new { slicerVersion = version, profileUsed = profile != null ? $"{profile.Quality} - {profile.Material}" : string.Empty, estimatedCost = 0d },
                estimatedCompletionTime = DateTime.UtcNow.AddMinutes(5),
                queuePosition = 0,
                slicerWorkerUrl = "local-simulator"
            };
            return Accepted(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue slicing job");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to start slicing job");
        }
    }

    private static async Task SimulateAsync(SlicingJobDto job)
    {
        try
        {
            job.Status = SlicingJobStatus.Slicing; job.Message = "Initializing slicer...";
            for (var i = 0; i <= 100; i += 10)
            {
                if (job.Status == SlicingJobStatus.Cancelled) return;
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
                await Task.Delay(1000);
            }
            if (job.Status != SlicingJobStatus.Cancelled)
            {
                var gcodeContent = $"; Mock G-code for job {job.JobId}\nG28";
                var path = Path.Combine(Path.GetTempPath(), $"{job.JobId}_output.gcode");
                await System.IO.File.WriteAllTextAsync(path, gcodeContent);
                job.GcodeFilePath = path;
                job.Status = SlicingJobStatus.Completed;
                job.EstimatedPrintTime = 3600;
                job.EstimatedFilamentUsed = 15.5;
                job.LayerCount = 200;
                job.CompletedAt = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            job.Status = SlicingJobStatus.Error;
            job.Message = ex.Message;
        }
    }
}
