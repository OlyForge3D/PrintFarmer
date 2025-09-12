using System.Security.Claims;
using System.Text.Json;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Slicing;

[ApiController]
[Route("api/slicer")]
[Tags("Slicer Submission")]
public class SlicingSubmissionController : ControllerBase
{
    private static readonly HashSet<string> AllowedEngines = new(StringComparer.OrdinalIgnoreCase) { "prusaslicer", "orcaslicer" };
    private readonly ISlicerFileStorage _fileStorage;
    private readonly ILogger<SlicingSubmissionController> _logger;
    private readonly Infrastructure.Temp.ITempPathProvider _tempPathProvider;
    private readonly string _tempRoot;
    private readonly ISlicerOrchestrator _orchestrator;

    public SlicingSubmissionController(ISlicerFileStorage fileStorage, ILogger<SlicingSubmissionController> logger, IConfiguration cfg, Infrastructure.Temp.ITempPathProvider tempPathProvider, ISlicerOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _fileStorage = fileStorage;
        _logger = logger;
        _tempPathProvider = tempPathProvider;
        _tempRoot = Path.GetFullPath(_tempPathProvider.GetTempRoot());
        Directory.CreateDirectory(_tempRoot);
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    }

    [HttpPost("slice")]
    [Authorize]
    [ProducesResponseType(typeof(SlicingJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(100_000_000)]
    public async Task<IActionResult> SliceAsync()
    {
        if (!Request.HasFormContentType)
        {
            return BadRequest("Multipart form data is required");
        }

        var form = await Request.ReadFormAsync();
        var modelFile = form.Files.Count > 0 ? form.Files[0] : null;
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
            using var validationStream = modelFile.OpenReadStream();
            using var reader = new StreamReader(validationStream, leaveOpen: true);
            var first = await reader.ReadLineAsync() ?? string.Empty;
            if (!first.StartsWith("solid", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Invalid model file");
            }
        }
        catch { return BadRequest("Invalid model file"); }

        var slicerEngine = form["slicerEngine"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(slicerEngine) || !AllowedEngines.Contains(slicerEngine))
        {
            return BadRequest("Valid slicer engine is required");
        }

        var printerId = form["printerId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(printerId) || !Guid.TryParse(printerId, out var printerGuid))
        {
            return BadRequest("Valid printer ID is required");
        }

        var profileRaw = form["profile"].FirstOrDefault() ?? form["slicerProfile"].FirstOrDefault();
        if (string.IsNullOrEmpty(profileRaw))
        {
            return BadRequest("Valid slicer profile is required");
        }

        SlicerProfileDto? profile;
        try
        { profile = JsonSerializer.Deserialize<SlicerProfileDto>(profileRaw); }
        catch { return BadRequest("Invalid slicer profile format"); }

        var priorityRaw = form["priority"].FirstOrDefault();
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
            var fileKey = $"models/{Guid.NewGuid()}/{modelFile.FileName}";
            string modelFileUrl;
            await using (var stream = modelFile.OpenReadStream())
            {
                modelFileUrl = await _fileStorage.UploadFileAsync(fileKey, stream, "application/octet-stream");
            }

            // Determine authenticated user
            var subClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
            if (subClaim == null || !Guid.TryParse(subClaim.Value, out var userId) || userId == Guid.Empty)
            {
                return Unauthorized("Authenticated user is required to submit slicing jobs");
            }

            var request = new Farm.Web.Shared.SlicingJobRequest
            {
                UserId = userId,
                PrinterId = printerGuid,
                ModelFileUrl = new Uri(modelFileUrl, UriKind.RelativeOrAbsolute),
                ModelFileName = modelFile.FileName,
                SlicerEngine = Enum.Parse<Farm.Web.Shared.SlicerEngineType>(slicerEngine, true),
                SlicerProfile = profile!
            };

            var response = await _orchestrator.SubmitJobAsync(request);

            var accepted = new
            {
                jobId = response.JobId,
                status = response.Status.ToString(),
                estimatedCompletionTime = response.EstimatedCompletionTime,
                queuePosition = response.QueuePosition,
                slicerWorkerUrl = response.SlicerWorkerUrl
            };

            return Accepted(accepted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue slicing job");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to start slicing job");
        }
    }
}
