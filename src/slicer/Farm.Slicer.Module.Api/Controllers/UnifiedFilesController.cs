using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Slicer.Module.Api.Controllers;

/// <summary>
/// Provides the Files page with one globally sorted and paginated model/G-code query.
/// </summary>
[ApiController]
[Route("api/3d-models/files")]
[Tags("Files")]
[Authorize]
public sealed class UnifiedFilesController(IUnifiedFilesQueryService filesQueryService) : ControllerBase
{
    /// <summary>
    /// Returns one authoritative page from the merged 3D-model and G-code library.
    /// </summary>
    /// <param name="request">The global filter, sort, and pagination parameters.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The globally ordered page and true filtered totals.</returns>
    [HttpPost("query")]
    [Authorize(Policy = "LibrarySync")]
    [ProducesResponseType(typeof(UnifiedFilesQueryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<UnifiedFilesQueryResponse>> QueryAsync(
        [FromBody] UnifiedFilesQueryRequestDto request,
        CancellationToken ct)
    {
        UnifiedFilesQueryResponse response = await filesQueryService.QueryAsync(request, ct);
        return Ok(response);
    }
}
