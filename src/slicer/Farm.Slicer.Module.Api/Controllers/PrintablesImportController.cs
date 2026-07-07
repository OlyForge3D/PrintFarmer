using System.Security.Claims;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Controllers;

/// <summary>
/// Endpoints for previewing Printables.com models and persisting attribution metadata.
/// File upload is handled via the standard model upload endpoint; call <c>POST /api/3d-models/printables/attribution</c>
/// afterward to attach Printables attribution fields to the uploaded record.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S6960", Justification = "Printables import endpoints share a single workflow and dependencies; splitting would add routing and state coordination without reducing risk.")]
[ApiController]
[Route("api/3d-models/printables")]
[Tags("3D Models")]
[Authorize]
public sealed class PrintablesImportController(
    IPrintablesImportService importService,
    IPrintablesOAuthService oauthService,
    ILogger<PrintablesImportController> logger) : ControllerBase
{
    private const int DefaultPageSize = 24;
    private const int MaxPageSize = 50;

    private readonly IPrintablesImportService _importService = importService;
    private readonly IPrintablesOAuthService _oauthService = oauthService;
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
    /// Imports one or more files from a Printables model into the local 3D model library.
    /// </summary>
    /// <param name="request">Import request containing the Printables URL and optional selected file IDs.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("/api/3d-models/import/printables")]
    [ProducesResponseType(typeof(IReadOnlyList<Model3DUploadResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ImportAsync([FromBody] PrintablesImportRequest? request, CancellationToken ct)
    {
        string? url = request?.Url?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest("url is required.");
        }

        try
        {
            IReadOnlyList<Model3DUploadResultDto> importedModels = await _importService.ImportAsync(url, request?.FileIds, ct);
            return Ok(importedModels);
        }
        catch (ArgumentException ex)
        {
            _logger.LogInformation("Bad Printables import request supplied for {Url}: {Message}", url, ex.Message);
            return BadRequest(ex.Message);
        }
        catch (PrintablesApiException ex)
        {
            _logger.LogWarning(ex, "Printables API error during import workflow for URL {Url}", url);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error importing Printables model for {Url}", url);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to import Printables model.");
        }
    }

    /// <summary>
    /// One-click import from browse/search cards where the model ID is already known.
    /// Imports all downloadable STL files from the selected model.
    /// </summary>
    [HttpPost("/api/3d-models/import/printables/one-click")]
    [ProducesResponseType(typeof(IReadOnlyList<Model3DUploadResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ImportOneClickAsync([FromBody] PrintablesOneClickImportRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ModelId))
        {
            return BadRequest("modelId is required.");
        }

        try
        {
            IReadOnlyList<Model3DUploadResultDto> importedModels = await _importService.ImportOneClickAsync(request, ct);
            return Ok(importedModels);
        }
        catch (ArgumentException ex)
        {
            _logger.LogInformation("Bad Printables one-click import request for model {ModelId}: {Message}", request.ModelId, ex.Message);
            return BadRequest(ex.Message);
        }
        catch (PrintablesApiException ex)
        {
            _logger.LogWarning(ex, "Printables API error during one-click import workflow for model {ModelId}", request.ModelId);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in Printables one-click import for model {ModelId}", request.ModelId);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to one-click import Printables model.");
        }
    }

    /// <summary>
    /// Starts OAuth2 linking and returns an authorization URL for Printables.
    /// </summary>
    [HttpPost("oauth/connect")]
    [ProducesResponseType(typeof(PrintablesOAuthConnectResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ConnectOAuthAsync(CancellationToken ct)
    {
        Guid userId = GetUserId();
        try
        {
            PrintablesOAuthConnectResponseDto response = await _oauthService.BuildConnectUrlAsync(userId, ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Printables OAuth connect is unavailable for user {UserId}: {Message}", userId, ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    /// <summary>
    /// Handles OAuth2 callback code exchange and stores account-link tokens server-side.
    /// </summary>
    [HttpGet("oauth/callback")]
    [ProducesResponseType(typeof(PrintablesOAuthStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> OAuthCallbackAsync([FromQuery] string code, [FromQuery] string state, CancellationToken ct)
    {
        Guid userId = GetUserId();
        try
        {
            PrintablesOAuthStatusDto status = await _oauthService.HandleCallbackAsync(userId, code, state, ct);
            return Ok(status);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Printables OAuth callback is unavailable for user {UserId}: {Message}", userId, ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (PrintablesOAuthNotLinkedException ex)
        {
            return Conflict(ex.Message);
        }
        catch (PrintablesOAuthTemporarilyUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (PrintablesApiException ex)
        {
            _logger.LogWarning(ex, "Printables OAuth callback token exchange failed for user {UserId}", userId);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    /// <summary>
    /// Gets current OAuth2 linkage status for the authenticated user.
    /// </summary>
    [HttpGet("oauth/status")]
    [ProducesResponseType(typeof(PrintablesOAuthStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> OAuthStatusAsync(CancellationToken ct)
    {
        Guid userId = GetUserId();
        PrintablesOAuthStatusDto status = await _oauthService.GetStatusAsync(userId, ct);
        return Ok(status);
    }

    /// <summary>
    /// Clears stored Printables OAuth2 linkage for the authenticated user.
    /// </summary>
    [HttpPost("oauth/disconnect")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> OAuthDisconnectAsync(CancellationToken ct)
    {
        Guid userId = GetUserId();
        try
        {
            await _oauthService.DisconnectAsync(userId, ct);
            return NoContent();
        }
        catch (PrintablesOAuthTemporarilyUnavailableException ex)
        {
            _logger.LogWarning(ex, "Printables OAuth disconnect is temporarily unavailable for user {UserId}", userId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    /// <summary>
    /// Returns the authenticated user's liked models from Printables.
    /// </summary>
    [HttpGet("liked")]
    [ProducesResponseType(typeof(PrintablesAuthenticatedCursorPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> GetLikedModelsAsync(
        [FromQuery] int limit = DefaultPageSize,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        if (!TryNormalizeLimit(limit, out int normalizedLimit, out string? limitError))
        {
            return BadRequest(limitError);
        }

        Guid userId = GetUserId();
        try
        {
            PrintablesAuthenticatedCursorPageDto page = await _oauthService.GetLikedModelsAsync(userId, normalizedLimit, cursor, ct);
            return Ok(page);
        }
        catch (PrintablesOAuthNotLinkedException ex)
        {
            return Conflict(ex.Message);
        }
        catch (PrintablesOAuthTemporarilyUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, ex.Message);
        }
    }

    /// <summary>
    /// Returns the authenticated user's Printables download history.
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(PrintablesAuthenticatedCursorPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> GetDownloadHistoryAsync(
        [FromQuery] int limit = DefaultPageSize,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        if (!TryNormalizeLimit(limit, out int normalizedLimit, out string? limitError))
        {
            return BadRequest(limitError);
        }

        Guid userId = GetUserId();
        try
        {
            PrintablesAuthenticatedCursorPageDto page = await _oauthService.GetDownloadHistoryAsync(userId, normalizedLimit, cursor, ct);
            return Ok(page);
        }
        catch (PrintablesOAuthNotLinkedException ex)
        {
            return Conflict(ex.Message);
        }
        catch (PrintablesOAuthTemporarilyUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, ex.Message);
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

    /// <summary>
    /// Resolves a Printables user and returns their public collections.
    /// </summary>
    [HttpGet("users/{username}/collections")]
    [ProducesResponseType(typeof(PrintablesCollectionsBrowseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> BrowseCollectionsAsync([FromRoute] string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return BadRequest("username is required.");
        }

        try
        {
            PrintablesCollectionsBrowseDto response = await _importService.BrowseCollectionsAsync(username, ct);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (PrintablesApiException ex)
        {
            _logger.LogWarning(ex, "Printables API error while browsing collections for {Username}", username);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    /// <summary>
    /// Returns cursor-paginated models uploaded by the specified Printables user.
    /// </summary>
    [HttpGet("users/{username}/models")]
    [ProducesResponseType(typeof(PrintablesCursorPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> BrowseUserModelsAsync(
        [FromRoute] string username,
        [FromQuery] int limit = DefaultPageSize,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        if (!TryNormalizeLimit(limit, out int normalizedLimit, out string? limitError))
        {
            return BadRequest(limitError);
        }

        try
        {
            PrintablesCursorPageDto response = await _importService.BrowseUserModelsAsync(username, normalizedLimit, cursor, ct);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (PrintablesApiException ex)
        {
            _logger.LogWarning(ex, "Printables API error while browsing user models for {Username}", username);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    /// <summary>
    /// Returns cursor-paginated models inside a Printables collection.
    /// </summary>
    [HttpGet("collections/{collectionId}/models")]
    [ProducesResponseType(typeof(PrintablesCursorPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> BrowseCollectionModelsAsync(
        [FromRoute] string collectionId,
        [FromQuery] int limit = DefaultPageSize,
        [FromQuery] string? cursor = null,
        [FromQuery] string? query = null,
        [FromQuery] string? ordering = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return BadRequest("collectionId is required.");
        }

        if (!TryNormalizeLimit(limit, out int normalizedLimit, out string? limitError))
        {
            return BadRequest(limitError);
        }

        try
        {
            PrintablesCursorPageDto response = await _importService.BrowseCollectionModelsAsync(collectionId, normalizedLimit, cursor, query, ordering, ct);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (PrintablesApiException ex)
        {
            _logger.LogWarning(ex, "Printables API error while browsing collection {CollectionId}", collectionId);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    /// <summary>
    /// Searches Printables models by keyword using backend proxying.
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(PrintablesSearchResultsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SearchModelsAsync(
        [FromQuery] string query,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = DefaultPageSize,
        [FromQuery] string? ordering = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("query is required.");
        }

        if (offset < 0)
        {
            return BadRequest("offset must be greater than or equal to zero.");
        }

        if (!TryNormalizeLimit(limit, out int normalizedLimit, out string? limitError))
        {
            return BadRequest(limitError);
        }

        try
        {
            PrintablesSearchResultsDto response = await _importService.SearchModelsAsync(query, offset, normalizedLimit, ordering, ct);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (PrintablesApiException ex)
        {
            _logger.LogWarning(ex, "Printables API error during search for query {Query}", query);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    private static bool TryNormalizeLimit(int requestedLimit, out int normalizedLimit, out string? error)
    {
        normalizedLimit = requestedLimit;
        error = null;

        if (requestedLimit <= 0)
        {
            error = "limit must be greater than zero.";
            return false;
        }

        if (requestedLimit > MaxPageSize)
        {
            error = $"limit must be less than or equal to {MaxPageSize}.";
            return false;
        }

        return true;
    }

    private Guid GetUserId()
    {
        string? raw = User?.FindFirstValue("sub")
            ?? User?.FindFirstValue(ClaimTypes.NameIdentifier);

        return string.IsNullOrEmpty(raw) || !Guid.TryParse(raw, out Guid id)
            ? throw new InvalidOperationException("User ID not found in claims.")
            : id;
    }
}
