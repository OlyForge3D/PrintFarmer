using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Locations;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for managing printer locations.
/// Locations are used to organize and categorize printers in the PrintFarmer dashboard.
/// </summary>
[ApiController]
[Route("api/locations")]
[Tags("Locations")]
public class LocationsController : ControllerBase
{
    private readonly ILocationService _locationService;
    private readonly IStartupStatus _startupStatus;
    private readonly IUnifiedLoggingService _logger;

    public LocationsController(ILocationService locationService, IStartupStatus startupStatus, IUnifiedLoggingService logger)
    {
        _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));
        _startupStatus = startupStatus ?? throw new ArgumentNullException(nameof(startupStatus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets all printer locations.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>List of all locations</returns>
    /// <response code="200">Returns the list of locations</response>
    /// <response code="503">If the system is still initializing</response>
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

            var locations = await _locationService.GetAllLocationDtosAsync(ct);
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
    /// Gets a specific location by ID.
    /// </summary>
    /// <param name="id">The location ID</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The location details</returns>
    /// <response code="200">Returns the location</response>
    /// <response code="404">If the location is not found</response>
    /// <response code="503">If the system is still initializing</response>
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

            var location = await _locationService.GetLocationDetailsAsync(id, ct);
            if (location == null)
            {
                return NotFound(new { message = "Location not found" });
            }

            return Ok(location);
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
    /// Creates a new printer location.
    /// </summary>
    /// <param name="request">The location creation request</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The created location</returns>
    /// <response code="201">Returns the created location</response>
    /// <response code="400">If the request is invalid</response>
    /// <response code="503">If the system is still initializing</response>
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

            if (request == null)
            {
                return BadRequest(new { message = "Request body cannot be null" });
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { message = "Location name is required" });
            }

            var location = await _locationService.CreateLocationAsync(request, ct);
            return StatusCode(201, location);
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
    /// <param name="id">The location ID</param>
    /// <param name="request">The location update request</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>The updated location</returns>
    /// <response code="200">Returns the updated location</response>
    /// <response code="404">If the location is not found</response>
    /// <response code="503">If the system is still initializing</response>
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

            if (request == null)
            {
                return BadRequest(new { message = "Request body cannot be null" });
            }

            var location = await _locationService.UpdateLocationAsync(id, request, ct);
            if (location == null)
            {
                return NotFound(new { message = "Location not found" });
            }

            return Ok(location);
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
    /// Deletes a printer location (soft delete).
    /// </summary>
    /// <param name="id">The location ID</param>
    /// <param name="ct">Cancellation token for the operation</param>
    /// <returns>No content</returns>
    /// <response code="204">Successfully deleted the location</response>
    /// <response code="404">If the location is not found</response>
    /// <response code="503">If the system is still initializing</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
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

            var success = await _locationService.DeleteLocationAsync(id, ct);
            if (!success)
            {
                return NotFound(new { message = "Location not found" });
            }

            return NoContent();
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
