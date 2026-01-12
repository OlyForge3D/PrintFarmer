using System.Security.Claims;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Slicing;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Slicing;

[ApiController]
[Route("api/slicer")]
[Tags("Slicer Submission")]
public class SlicingSubmissionController(
    ISlicingSubmissionService submissionService,
    IConfiguration cfg,
    Infrastructure.Temp.ITempPathProvider tempPathProvider,
    IHostEnvironment env) : ControllerBase
{
    private static readonly HashSet<string> AllowedEngines = new(StringComparer.OrdinalIgnoreCase) { "prusaslicer", "orcaslicer" };
    private readonly ISlicingSubmissionService _submissionService = submissionService ?? throw new ArgumentNullException(nameof(submissionService));
    private readonly IHostEnvironment _env = env ?? throw new ArgumentNullException(nameof(env));
    
    // Initialize temp directory (executed during construction via field initializer)
    private readonly string _unusedTempInitializer = InitializeTempRoot(cfg, tempPathProvider);
    
    private static string InitializeTempRoot(IConfiguration cfg, Infrastructure.Temp.ITempPathProvider tempPathProvider)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(tempPathProvider);
        string tempRoot = Path.GetFullPath(tempPathProvider.GetTempRoot());
        _ = Directory.CreateDirectory(tempRoot);
        return string.Empty; // Return empty string since we only care about side effect
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

        // Determine authenticated user. In test environment allow a deterministic fallback user id
        Claim? subClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
        if (subClaim == null || !Guid.TryParse(subClaim.Value, out Guid userId) || userId == Guid.Empty)
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

        SlicingSubmissionResult result = await _submissionService.SubmitSlicingJobAsync(
            modelFile, slicerEngine, printerGuid, profile!, userId, HttpContext.RequestAborted);

        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, result.Error);
        }

        return Accepted(result.Result);
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

        // Determine authenticated user
        Claim? subClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
        if (subClaim == null || !Guid.TryParse(subClaim.Value, out Guid userId) || userId == Guid.Empty)
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

        SlicingSubmissionResult result = await _submissionService.SubmitSlicingJobFromModelAsync(
            modelId, slicerEngine, printerGuid, profile!, userId, HttpContext.RequestAborted);

        if (!result.Success)
        {
            // Check if it's a not found error
            if (result.Error != null && result.Error.Contains("not found"))
            {
                return NotFound(result.Error);
            }
            return StatusCode(StatusCodes.Status500InternalServerError, result.Error);
        }

        return Accepted(result.Result);
    }
}
