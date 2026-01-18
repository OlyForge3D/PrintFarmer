using Farm.Infrastructure.Domain;
using Farm.Web.Api.Infrastructure.Authorization;
using Farm.Web.Api.Services.Slicing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Admin;

/// <summary>
/// Admin-only endpoints for managing slicer services
/// </summary>
[ApiController]
[Route("api/admin/slicers")]
[Authorize]
[RequirePermission("slicers", "admin")]
public class SlicerManagementController(ISlicersService service, ILogger<SlicerManagementController> logger) : ControllerBase
{
    private readonly ISlicersService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly ILogger<SlicerManagementController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Admin endpoint to list all slicer services with full details including API keys
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListAllAsync()
    {
        _logger.LogInformation("Admin listing all slicer services");
        IReadOnlyList<SlicerService> list = await _service.ListAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
        return Ok(list);
    }

    /// <summary>
    /// Admin endpoint to force rotate a service's API key (for security incidents)
    /// </summary>
    [HttpPost("{id}/admin-rotate-key")]
    public async Task<IActionResult> AdminRotateApiKeyAsync(Guid id)
    {
        _logger.LogWarning("Admin forcing API key rotation for slicer service {ServiceId}", id);
        CancellationToken ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        string? newApiKey = await _service.RotateApiKeyAsync(id, ct, isAdminForced: true);
        return newApiKey == null ? NotFound() : Ok(new { id, apiKey = newApiKey, message = "API key forcibly rotated by administrator" });
    }

    /// <summary>
    /// Admin endpoint to forcibly deregister a service (for maintenance/security)
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> AdminDeregisterAsync(Guid id)
    {
        _logger.LogWarning("Admin forcibly deregistering slicer service {ServiceId}", id);
        CancellationToken ct = HttpContext?.RequestAborted ?? CancellationToken.None;
        bool ok = await _service.DeregisterAsync(id, ct);
        return !ok ? NotFound() : NoContent();
    }
}
