using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Controllers;

/// <summary>
/// Endpoints for previewing Printables.com models and persisting attribution metadata.
/// File upload is handled via the standard model upload endpoint; call <c>POST /import/attribution</c>
/// afterward to attach Printables attribution fields to the uploaded record.
/// </summary>
[ApiController]
[Route("api/3d-models/printables")]
[Tags("3D Models")]
[Authorize]
public sealed class PrintablesImportController(
    IPrintablesImportService importService,
    ILogger<PrintablesImportController> logger) : ControllerBase
{
    private readonly IPrintablesImportService _importService = importService;
    private readonly ILogger<PrintablesImportController> _logger = logger;

    /// <summary>
    /// Fetches metadata for a public Printables model without storing anything.
    /// Use this to display a preview before the user commits to an import.
    /// </summary>
    /// <param name="url">
    /// Printables model URL — accepts both
    /// <c>https://www.printables.com/model/{id}-{slug}</c> and
    /// <c>https://www.printables.com/model/{id}</c> forms.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Model metadata including title, author, license, thumbnail, and file list.</returns>
    [HttpGet("preview")]
    [ProducesResponseType(typeof(PrintablesPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> PreviewAsync([FromQuery] string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest("url query parameter is required.");
        }

        try
        {
            PrintablesPreviewDto preview = await _importService.PreviewAsync(url, ct);
            return Ok(preview);
        }
        catch (ArgumentException ex)
        {
            _logger.LogInformation("Bad Printables URL supplied: {Url} — {Message}", url, ex.Message);
            return BadRequest(ex.Message);
        }
        catch (PrintablesApiException ex)
        {
            _logger.LogWarning(ex, "Printables API error for URL {Url}", url);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching Printables preview for {Url}", url);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to fetch Printables preview.");
        }
    }

    /// <summary>
    /// Attaches Printables attribution metadata to an already-uploaded model record.
    /// Call this after uploading the file via <c>POST /api/3d-models/upload</c>.
    /// </summary>
    /// <param name="request">Model ID and source Printables URL.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("attribution")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> PersistAttributionAsync([FromBody] PersistAttributionRequestDto request, CancellationToken ct)
    {
        if (request is null || request.ModelId == Guid.Empty || string.IsNullOrWhiteSpace(request.PrintablesUrl))
        {
            return BadRequest("modelId and printablesUrl are required.");
        }

        try
        {
            await _importService.PersistAttributionAsync(request.ModelId, request.PrintablesUrl, ct);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            _logger.LogInformation("Bad attribution request for model {ModelId}: {Message}", request.ModelId, ex.Message);
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Model {ModelId} not found for attribution update: {Message}", request.ModelId, ex.Message);
            return NotFound(ex.Message);
        }
        catch (PrintablesApiException ex)
        {
            _logger.LogWarning(ex, "Printables API error resolving attribution for {Url}", request.PrintablesUrl);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error persisting attribution for model {ModelId}", request.ModelId);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to persist attribution.");
        }
    }
}
