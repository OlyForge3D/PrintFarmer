using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Services.DataManagement;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers.Admin;

/// <summary>
/// Admin controller for checking and applying catalog data updates from the remote repository.
/// </summary>
[ApiController]
[Route("api/admin/catalog")]
[Tags("Admin - Catalog Updates")]
public class CatalogUpdateController : ControllerBase
{
    private readonly ICatalogUpdateService _updateService;
    private readonly ILogger<CatalogUpdateController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogUpdateController"/> class.
    /// </summary>
    /// <param name="updateService">Catalog update service.</param>
    /// <param name="logger">Logger instance.</param>
    public CatalogUpdateController(
        ICatalogUpdateService updateService,
        ILogger<CatalogUpdateController> logger)
    {
        _updateService = updateService;
        _logger = logger;
    }

    /// <summary>
    /// Get the currently applied catalog version.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current catalog version info, or null if not yet tracked.</returns>
    /// <response code="200">Returns the current catalog version.</response>
    [HttpGet("version")]
    [ProducesResponseType(typeof(CatalogVersionDto), 200)]
    public async Task<ActionResult<CatalogVersionDto?>> GetCurrentVersionAsync(CancellationToken ct)
    {
        _logger.LogInformation("[CatalogUpdate] Current version requested");
        CatalogVersionDto? version = await _updateService.GetCurrentVersionAsync(ct);
        return Ok(version);
    }

    /// <summary>
    /// Check whether a catalog update is available from the remote repository.
    /// Compares local manifest SHA256 hashes against the remote manifest.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Check result with available version and list of changed files.</returns>
    /// <response code="200">Returns the update check result.</response>
    [HttpGet("updates/check")]
    [ProducesResponseType(typeof(CatalogUpdateCheckResult), 200)]
    public async Task<ActionResult<CatalogUpdateCheckResult>> CheckForUpdatesAsync(CancellationToken ct)
    {
        _logger.LogInformation("[CatalogUpdate] Update check requested");
        CatalogUpdateCheckResult result = await _updateService.CheckForUpdatesAsync(ct);
        return Ok(result);
    }

    /// <summary>
    /// Apply available catalog updates. Downloads changed YAML files from the remote repository,
    /// overwrites local copies, and re-seeds the database with updated data.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Apply result with updated categories and version info.</returns>
    /// <response code="200">Returns the apply result on success.</response>
    /// <response code="500">If there was an error applying updates.</response>
    [HttpPost("updates/apply")]
    [ProducesResponseType(typeof(CatalogUpdateApplyResult), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CatalogUpdateApplyResult>> ApplyUpdatesAsync(CancellationToken ct)
    {
        _logger.LogInformation("[CatalogUpdate] Update apply requested");
        CatalogUpdateApplyResult result = await _updateService.ApplyUpdatesAsync(ct);

        if (!result.Success && result.Error != null)
        {
            _logger.LogError("[CatalogUpdate] Update apply failed: {Error}", result.Error);
            return StatusCode(500, result);
        }

        return Ok(result);
    }
}
