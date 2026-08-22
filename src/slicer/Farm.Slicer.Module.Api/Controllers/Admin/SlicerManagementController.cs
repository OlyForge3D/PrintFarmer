using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Controllers.Admin;

/// <summary>
/// Administrative endpoints for managing registered slicer services.
/// </summary>
[ApiController]
[Route("api/admin/slicers")]
[Authorize]
[RequirePermission("slicer_engines:admin")]
public class SlicerManagementController(
    ISlicersService service,
    ILogger<SlicerManagementController> logger) : ControllerBase
{
    private readonly ISlicersService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly ILogger<SlicerManagementController> _logger = logger;

    /// <summary>
    /// Lists all registered slicer services (admin view).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<SlicerService> list = await _service.ListAsync(ct);
        return Ok(list);
    }

    /// <summary>
    /// Permanently removes a slicer service (admin action).
    /// </summary>
    /// <param name="id">The slicer service ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Uses the purge path rather than worker deregistration: worker-initiated deregistration
    /// may retain the row so a returning worker is re-identified, whereas this action means
    /// permanent removal of both the service and its paired worker record.
    /// </remarks>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeregisterAsync(Guid id, CancellationToken ct)
    {
        _logger.LogWarning("Admin deregistering slicer service {SlicerId}", id);
        bool ok = await _service.PurgeAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }
}
