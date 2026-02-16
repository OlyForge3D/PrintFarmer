using System.Security.Claims;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Controllers.Slicing;

/// <summary>
/// Endpoints for submitting slicing jobs via the new submission service.
/// </summary>
[ApiController]
[Route("api/slicing/submit")]
public class SlicingSubmissionController(
    ISlicingSubmissionService submissionService,
    IConfiguration cfg,
    ITempPathProvider tempPathProvider,
    IHostEnvironment env) : ControllerBase
{
    private readonly ISlicingSubmissionService _submissionService = submissionService;
    private readonly IConfiguration _cfg = cfg;
    private readonly ITempPathProvider _tempPathProvider = tempPathProvider;
    private readonly IHostEnvironment _env = env;

    /// <summary>
    /// Submits a new file for slicing.
    /// </summary>
    /// <param name="file">The model file to slice.</param>
    /// <param name="slicerEngine">The slicer engine to use.</param>
    /// <param name="printerId">Target printer ID.</param>
    /// <param name="profileJson">Slicer profile JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost]
    public async Task<IActionResult> SubmitFileAsync(
        IFormFile file,
        [FromForm] string? slicerEngine,
        [FromForm] Guid? printerId,
        [FromForm] string? profileJson,
        CancellationToken ct)
    {
        if (file.Length == 0)
        {
            return BadRequest(new { error = "File is empty." });
        }

        Guid userId = GetUserId();
        SlicerProfileDto profile = DeserializeProfile(profileJson);

        SlicingSubmissionResult result = await _submissionService.SubmitSlicingJobAsync(
            file,
            slicerEngine ?? SlicerEngineType.OrcaSlicer.ToString(),
            printerId ?? Guid.Empty,
            profile,
            userId,
            ct);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Result);
    }

    /// <summary>
    /// Submits an existing model for slicing.
    /// </summary>
    /// <param name="modelId">The existing model ID.</param>
    /// <param name="slicerEngine">The slicer engine to use.</param>
    /// <param name="printerId">Target printer ID.</param>
    /// <param name="profileJson">Slicer profile JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("model/{modelId}")]
    public async Task<IActionResult> SubmitModelAsync(
        Guid modelId,
        [FromForm] string? slicerEngine,
        [FromForm] Guid? printerId,
        [FromForm] string? profileJson,
        CancellationToken ct)
    {
        Guid userId = GetUserId();
        SlicerProfileDto profile = DeserializeProfile(profileJson);

        SlicingSubmissionResult result = await _submissionService.SubmitSlicingJobFromModelAsync(
            modelId,
            slicerEngine ?? SlicerEngineType.OrcaSlicer.ToString(),
            printerId ?? Guid.Empty,
            profile,
            userId,
            ct);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Result);
    }

    private Guid GetUserId()
    {
        string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdStr, out Guid userId) ? userId : Guid.Empty;
    }

    private static SlicerProfileDto DeserializeProfile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SlicerProfileDto();
        }

        return System.Text.Json.JsonSerializer.Deserialize<SlicerProfileDto>(json) ?? new SlicerProfileDto();
    }
}
