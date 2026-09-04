using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Queue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Reports the coarse shape of the farm (account/location/printer counts only) to any
/// authenticated caller. Deliberately separate from <see cref="SystemCapabilitiesController"/>,
/// which stays anonymous and must not carry these figures. See issue #2411.
/// </summary>
[ApiController]
[Authorize]
[Route("api/system")]
public sealed class FarmShapeController(
    AppDbContext dbContext,
    IQueueResourceAuthorizationService queueResourceAuthorizationService) : ControllerBase
{
    private readonly AppDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly IQueueResourceAuthorizationService _queueResourceAuthorizationService =
        queueResourceAuthorizationService ??
        throw new ArgumentNullException(nameof(queueResourceAuthorizationService));

    /// <summary>
    /// Returns bare account, location, and printer counts for the caller. Account count is
    /// intentionally not admin-gated: it is a plain integer with no identities, emails, or
    /// roles attached. Printer count reflects the same enabled/PrinterGroup ACL scoping the
    /// caller's own <c>GET /api/printers</c> view applies; location count is unscoped, matching
    /// <c>GET /api/locations</c>.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The farm's shape as bare counts.</returns>
    [HttpGet("farm-shape")]
    [ProducesResponseType(typeof(FarmShapeDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<FarmShapeDto>> GetFarmShapeAsync(CancellationToken ct)
    {
        Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue
        {
            NoStore = true,
        };

        int accountCount = await _dbContext.Users.CountAsync(ct);
        int locationCount = await _dbContext.Locations.CountAsync(ct);
        int printerCount = await _queueResourceAuthorizationService.CountAccessiblePrintersAsync(
            User,
            PrinterGroupAccessLevel.View,
            ct);

        return Ok(new FarmShapeDto
        {
            AccountCount = accountCount,
            LocationCount = locationCount,
            PrinterCount = printerCount,
        });
    }
}
