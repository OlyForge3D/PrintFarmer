using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Locations;
using Farm.Infrastructure.Services.Startup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing printer locations with hierarchy support.
/// Locations are organized in a tree structure for the PrintFarmer dashboard.
/// </summary>
[ApiController]
[Route("api/locations")]
[Tags("Locations")]
[Authorize]
public class LocationsController(
    ILocationService locationService,
    IStartupStatus startupStatus,
    ILogger<LocationsController> logger) : ControllerBase
{
    private readonly ILocationService _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));
    private readonly IStartupStatus _startupStatus = startupStatus ?? throw new ArgumentNullException(nameof(startupStatus));
    private readonly ILogger<LocationsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Gets all printer locations (flat list).
    /// </summary>
    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<LocationDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IEnumerable<LocationDto>>> GetLocationsAsync(CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            LocationDto[] locations = await _locationService.GetAllLocationDtosAsync(ct);
            return Ok(locations);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LocationsController] Exception in GetLocationsAsync: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Gets the full location tree as nested JSON.
    /// </summary>
    /// <param name="rootId">Optional root ID to get a subtree.</param>
    /// <param name="ct">Cancellation token.</param>
    [AllowAnonymous]
    [HttpGet("tree")]
    [ProducesResponseType(typeof(List<LocationTreeDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<List<LocationTreeDto>>> GetLocationTreeAsync([FromQuery] Guid? rootId, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            List<LocationTreeDto> tree = await _locationService.GetTreeAsync(rootId, ct);
            return Ok(tree);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LocationsController] Exception in GetLocationTreeAsync: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Gets a specific location by ID.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(LocationDetailsDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<LocationDetailsDto>> GetLocationAsync(Guid id, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            LocationDetailsDto? location = await _locationService.GetLocationDetailsAsync(id, ct);
            return location is null ? NotFound(new { message = "Location not found" }) : Ok(location);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LocationsController] Exception in GetLocationAsync for ID {LocationId}: {Message}", id.ToString(), ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Gets the ancestor chain for a location (for breadcrumbs).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{id}/ancestors")]
    [ProducesResponseType(typeof(List<LocationBreadcrumbDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<List<LocationBreadcrumbDto>>> GetAncestorsAsync(Guid id, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            List<LocationBreadcrumbDto> ancestors = await _locationService.GetAncestorsAsync(id, ct);
            return Ok(ancestors);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LocationsController] Exception in GetAncestorsAsync for ID {LocationId}: {Message}", id.ToString(), ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Gets all descendants of a location (flat list).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{id}/descendants")]
    [ProducesResponseType(typeof(List<LocationDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<List<LocationDto>>> GetDescendantsAsync(Guid id, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            List<LocationDto> descendants = await _locationService.GetDescendantsAsync(id, ct);
            return Ok(descendants);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LocationsController] Exception in GetDescendantsAsync for ID {LocationId}: {Message}", id.ToString(), ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Creates a new printer location.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPost]
    [ProducesResponseType(typeof(LocationDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<LocationDto>> CreateLocationAsync(CreateLocationDto request, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (request is null)
            {
                return BadRequest(new { message = "Request body cannot be null" });
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { message = "Location name is required" });
            }

            LocationDto location = await _locationService.CreateLocationAsync(request, ct);
            return StatusCode(201, location);
        }
        catch (KeyNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists") || ex.Message.Contains("maximum depth"))
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LocationsController] Exception in CreateLocationAsync: {Message}", ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Updates an existing printer location.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(LocationDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<LocationDto>> UpdateLocationAsync(Guid id, UpdateLocationDto request, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (request is null)
            {
                return BadRequest(new { message = "Request body cannot be null" });
            }

            LocationDto? location = await _locationService.UpdateLocationAsync(id, request, ct);
            return location is null ? NotFound(new { message = "Location not found" }) : Ok(location);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LocationsController] Exception in UpdateLocationAsync for ID {LocationId}: {Message}", id.ToString(), ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Moves a location to a new parent.
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("{id}/move")]
    [ProducesResponseType(typeof(LocationDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<LocationDto>> MoveLocationAsync(Guid id, MoveLocationDto request, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            if (request is null)
            {
                return BadRequest(new { message = "Request body cannot be null" });
            }

            LocationDto? location = await _locationService.MoveAsync(id, request.NewParentId, ct);
            return location is null ? NotFound(new { message = "Location not found" }) : Ok(location);
        }
        catch (KeyNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("cannot be") || ex.Message.Contains("already exists") || ex.Message.Contains("maximum depth"))
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LocationsController] Exception in MoveLocationAsync for ID {LocationId}: {Message}", id.ToString(), ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }

    /// <summary>
    /// Deletes a printer location (soft delete).
    /// </summary>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> DeleteLocationAsync(Guid id, CancellationToken ct)
    {
        try
        {
            if (!_startupStatus.IsReady)
            {
                return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
            }

            bool success = await _locationService.DeleteLocationAsync(id, ct);
            return !success ? NotFound(new { message = "Location not found" }) : NoContent();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("child locations"))
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException)
        {
            return StatusCode(503, new { message = "System is still initializing. Please wait a moment and try again." });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LocationsController] Exception in DeleteLocationAsync for ID {LocationId}: {Message}", id.ToString(), ex.Message);
            return StatusCode(500, new { error = ex.Message, detail = ex.ToString() });
        }
    }
}
