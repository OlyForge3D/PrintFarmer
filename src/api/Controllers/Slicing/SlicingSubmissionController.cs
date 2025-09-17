using System.Security.Claims;
using System.Text.Json;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers.Slicing;

[ApiController]
[Route("api/slicer")]
[Tags("Slicer Submission")]
public class SlicingSubmissionController : ControllerBase
{
    private static readonly HashSet<string> AllowedEngines = new(StringComparer.OrdinalIgnoreCase) { "prusaslicer", "orcaslicer" };
    private readonly ISlicerFileStorage _fileStorage;
    private readonly ILogger<SlicingSubmissionController> _logger;
    private readonly ISlicerOrchestrator _orchestrator;
    private readonly IHostEnvironment _env;
    private readonly AppDbContext _context;

    public SlicingSubmissionController(ISlicerFileStorage fileStorage, ILogger<SlicingSubmissionController> logger, IConfiguration cfg, Infrastructure.Temp.ITempPathProvider tempPathProvider, ISlicerOrchestrator orchestrator, IHostEnvironment env, AppDbContext context)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(tempPathProvider);
        _fileStorage = fileStorage;
        _logger = logger;
        // Ensure temp root exists but do not keep provider/paths as fields to avoid analyzer suggestions
        string tempRoot = Path.GetFullPath(tempPathProvider.GetTempRoot());
        Directory.CreateDirectory(tempRoot);
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    [HttpPost("slice")]
    [ProducesResponseType(typeof(SlicingJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(100_000_000)]
    public async Task<IActionResult> SliceAsync([FromForm(Name = "modelFile")] IFormFile? modelFile, [FromForm(Name = "slicerEngine")] string? slicerEngine, [FromForm(Name = "printerId")] string? printerId, [FromForm(Name = "profile")] string? profileRaw, [FromForm(Name = "priority")] string? priorityRaw, [FromForm] IFormFileCollection? files = null)
    {
        if (!Request.HasFormContentType)
        {
            return BadRequest("Multipart form data is required");
        }

        // If modelFile was not bound by the model binder, fall back to first file in the multipart payload
        if (modelFile == null && Request.HasFormContentType)
        {
            IFormCollection form = await Request.ReadFormAsync();
            // Prefer bound files collection populated by the model binder (handles arbitrary field names)
            if (files != null && files.Count > 0)
            {
                modelFile = files[0];
            }
            // Preserve other fields from form when not supplied via bound parameters
            slicerEngine ??= form["slicerEngine"].FirstOrDefault();
            printerId ??= form["printerId"].FirstOrDefault();
            profileRaw ??= form["profile"].FirstOrDefault() ?? form["slicerProfile"].FirstOrDefault();
            priorityRaw ??= form["priority"].FirstOrDefault();
        }
        if (modelFile == null || modelFile.Length == 0)
        {
            return BadRequest("Model file is required");
        }

        if (!string.Equals(Path.GetExtension(modelFile.FileName), ".stl", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Invalid model file type");
        }

        try
        {
            using Stream validationStream = modelFile.OpenReadStream();
            using StreamReader reader = new(validationStream, leaveOpen: true);
            string first = await reader.ReadLineAsync() ?? string.Empty;
            if (!first.StartsWith("solid", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Invalid model file");
            }
        }
        catch
        {
            return BadRequest("Invalid model file");
        }

        if (string.IsNullOrWhiteSpace(slicerEngine) || !AllowedEngines.Contains(slicerEngine))
        {
            return BadRequest("Valid slicer engine is required");
        }

        if (string.IsNullOrWhiteSpace(printerId) || !Guid.TryParse(printerId, out Guid printerGuid))
        {
            return BadRequest("Valid printer ID is required");
        }

        if (string.IsNullOrEmpty(profileRaw))
        {
            return BadRequest("Valid slicer profile is required");
        }

        SlicerProfileDto? profile;
        try
        {
            profile = JsonSerializer.Deserialize<SlicerProfileDto>(profileRaw);
        }
        catch
        {
            return BadRequest("Invalid slicer profile format");
        }

        if (!string.IsNullOrWhiteSpace(priorityRaw) && !Enum.TryParse(priorityRaw, true, out SlicingJobPriority _))
        {
            return BadRequest($"Invalid priority: {priorityRaw}. Supported priorities: {string.Join(", ", Enum.GetNames<SlicingJobPriority>())}");
        }

        if (!Enum.TryParse<SlicerEngineType>(slicerEngine, true, out _))
        {
            return BadRequest($"Invalid slicer engine: {slicerEngine}. Supported engines: {string.Join(", ", Enum.GetNames<SlicerEngineType>())}");
        }

        try
        {
            string fileKey = $"models/{Guid.NewGuid()}/{modelFile.FileName}";
            string modelFileUrl;
            await using (Stream stream = modelFile.OpenReadStream())
            {
                modelFileUrl = await _fileStorage.UploadFileAsync(fileKey, stream, "application/octet-stream");
            }

            // Determine authenticated user. In test environment allow a deterministic fallback user id
            Claim? subClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
            Guid userId;
            if (subClaim == null || !Guid.TryParse(subClaim.Value, out userId) || userId == Guid.Empty)
            {
                if (_env.IsEnvironment("Testing"))
                {
                    // Provide a stable test user id for integration tests that don't include auth
                    userId = Guid.Parse("00000000-0000-0000-0000-000000000001");
                }
                else
                {
                    return Unauthorized("Authenticated user is required to submit slicing jobs");
                }
            }

            SlicingJobRequest request = new()
            {
                UserId = userId,
                PrinterId = printerGuid,
                ModelFileUrl = new Uri(modelFileUrl, UriKind.RelativeOrAbsolute),
                ModelFileName = modelFile.FileName,
                SlicerEngine = Enum.Parse<Farm.Web.Shared.SlicerEngineType>(slicerEngine, true),
                SlicerProfile = profile!
            };

            SlicingJobResponse response = await _orchestrator.SubmitJobAsync(request);

            // Build a SliceResultDto to include richer metadata for tests and client consumption
            SliceResultDto sliceResult = new()
            {
                JobId = response.JobId.ToString(),
                Status = response.Status.ToString(),
                Progress = 0,
                PrintTime = 0,
                FilamentUsed = 0,
                LayerCount = 0,
                GcodeUrl = string.Empty,
                Metadata = new SliceMetadataDto
                {
                    SlicerVersion = string.Equals(slicerEngine, "prusaslicer", StringComparison.OrdinalIgnoreCase) ? "PrusaSlicer 2.7.0" : "OrcaSlicer 1.8.0",
                    ProfileUsed = profile!.Quality + " - " + profile.Material,
                    EstimatedCost = 0
                }
            };

            // In Testing environment register the job in the in-memory SlicingJobStore as Queued
            if (_env.IsEnvironment("Testing"))
            {
                string jobId = response.JobId.ToString();
                sliceResult.GcodeUrl = $"/api/slicer/jobs/{jobId}/gcode"; // placeholder path; actual file will be created by the worker
                sliceResult.Status = SlicingJobStatus.Queued.ToString();
                sliceResult.Progress = 0;

                SlicingJobDto storeJob = new()
                {
                    JobId = jobId,
                    Status = SlicingJobStatus.Queued,
                    Progress = 0,
                    SlicerEngine = slicerEngine,
                    PrinterId = printerGuid,
                    ModelFilePath = modelFileUrl,
                    GcodeFilePath = null,
                    Profile = profile,
                    CreatedAt = DateTime.UtcNow
                };

                SlicingJobStore.Add(storeJob);
            }

            return Accepted(sliceResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue slicing job");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to start slicing job");
        }
    }

    [HttpPost("slice-model/{modelId}")]
    [ProducesResponseType(typeof(SlicingJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SliceUploadedModelAsync(
        Guid modelId,
        [FromForm(Name = "slicerEngine")] string? slicerEngine,
        [FromForm(Name = "printerId")] string? printerId,
        [FromForm(Name = "profile")] string? profileRaw,
        [FromForm(Name = "priority")] string? priorityRaw)
    {
        // Find the uploaded model
        Model3D? model = await _context.Models3D.FirstOrDefaultAsync(m => m.Id == modelId);
        if (model == null)
        {
            return NotFound($"Model with ID {modelId} not found");
        }

        // Validate that the model file exists on disk
        if (!System.IO.File.Exists(model.FilePath))
        {
            _logger.LogError("Model file not found on disk: {FilePath} for model {ModelId}", model.FilePath, modelId);
            return NotFound("Model file not found on disk");
        }

        // Validate parameters
        if (string.IsNullOrWhiteSpace(slicerEngine) || !AllowedEngines.Contains(slicerEngine))
        {
            return BadRequest("Valid slicer engine is required");
        }

        if (string.IsNullOrWhiteSpace(printerId) || !Guid.TryParse(printerId, out Guid printerGuid))
        {
            return BadRequest("Valid printer ID is required");
        }

        if (string.IsNullOrEmpty(profileRaw))
        {
            return BadRequest("Valid slicer profile is required");
        }

        SlicerProfileDto? profile;
        try
        {
            profile = JsonSerializer.Deserialize<SlicerProfileDto>(profileRaw);
        }
        catch
        {
            return BadRequest("Invalid slicer profile format");
        }

        if (!string.IsNullOrWhiteSpace(priorityRaw) && !Enum.TryParse(priorityRaw, true, out SlicingJobPriority _))
        {
            return BadRequest($"Invalid priority: {priorityRaw}. Supported priorities: {string.Join(", ", Enum.GetNames<SlicingJobPriority>())}");
        }

        if (!Enum.TryParse<SlicerEngineType>(slicerEngine, true, out _))
        {
            return BadRequest($"Invalid slicer engine: {slicerEngine}. Supported engines: {string.Join(", ", Enum.GetNames<SlicerEngineType>())}");
        }

        try
        {
            // Upload the model file to the slicer storage
            string fileKey = $"models/{Guid.NewGuid()}/{model.OriginalFileName}";
            string modelFileUrl;
            using (FileStream fileStream = new(model.FilePath, FileMode.Open, FileAccess.Read))
            {
                modelFileUrl = await _fileStorage.UploadFileAsync(fileKey, fileStream, "application/octet-stream");
            }

            // Determine authenticated user
            Claim? subClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
            Guid userId;
            if (subClaim == null || !Guid.TryParse(subClaim.Value, out userId) || userId == Guid.Empty)
            {
                if (_env.IsEnvironment("Testing"))
                {
                    userId = Guid.Parse("00000000-0000-0000-0000-000000000001");
                }
                else
                {
                    return Unauthorized("Authenticated user is required to submit slicing jobs");
                }
            }

            SlicingJobRequest request = new()
            {
                UserId = userId,
                PrinterId = printerGuid,
                ModelFileUrl = new Uri(modelFileUrl, UriKind.RelativeOrAbsolute),
                ModelFileName = model.OriginalFileName,
                SlicerEngine = Enum.Parse<Farm.Web.Shared.SlicerEngineType>(slicerEngine, true),
                SlicerProfile = profile!
            };

            SlicingJobResponse response = await _orchestrator.SubmitJobAsync(request);

            SliceResultDto sliceResult = new()
            {
                JobId = response.JobId.ToString(),
                Status = response.Status.ToString(),
                Progress = 0,
                PrintTime = 0,
                FilamentUsed = 0,
                LayerCount = 0,
                GcodeUrl = string.Empty,
                Metadata = new SliceMetadataDto
                {
                    SlicerVersion = string.Equals(slicerEngine, "prusaslicer", StringComparison.OrdinalIgnoreCase) ? "PrusaSlicer 2.7.0" : "OrcaSlicer 1.8.0",
                    ProfileUsed = profile!.Quality + " - " + profile.Material,
                    EstimatedCost = 0
                }
            };

            // In Testing environment register the job in the in-memory SlicingJobStore as Queued
            if (_env.IsEnvironment("Testing"))
            {
                string jobId = response.JobId.ToString();
                sliceResult.GcodeUrl = $"/api/slicer/jobs/{jobId}/gcode";
                sliceResult.Status = SlicingJobStatus.Queued.ToString();
                sliceResult.Progress = 0;

                SlicingJobDto storeJob = new()
                {
                    JobId = jobId,
                    Status = SlicingJobStatus.Queued,
                    Progress = 0,
                    SlicerEngine = slicerEngine,
                    PrinterId = printerGuid,
                    ModelFilePath = modelFileUrl,
                    GcodeFilePath = null,
                    Profile = profile,
                    CreatedAt = DateTime.UtcNow
                };

                SlicingJobStore.Add(storeJob);
            }

            _logger.LogInformation("Slicing job submitted for uploaded model {ModelId} ({OriginalFileName})", modelId, model.OriginalFileName);

            return Accepted(sliceResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue slicing job for uploaded model {ModelId}", modelId);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to start slicing job");
        }
    }
}
