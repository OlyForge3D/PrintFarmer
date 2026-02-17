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
[RequirePermission("admin:slicers")]
public class SlicerManagementController(
    ISlicersService service,
    ILogger<SlicerManagementController> logger) : ControllerBase
{
    private readonly ISlicersService _service = service;
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
    /// Deregisters a slicer service (admin action).
    /// </summary>
    /// <param name="id">The slicer service ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeregisterAsync(Guid id, CancellationToken ct)
    {
        _logger.LogWarning("Admin deregistering slicer service {SlicerId}", id);
        bool ok = await _service.DeregisterAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }
}
