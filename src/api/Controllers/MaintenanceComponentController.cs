using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// API controller for maintenance components (global parts inventory).
/// Components represent physical parts used in maintenance tasks (bearings, belts, nozzles, etc.).
/// </summary>
[ApiController]
[Route("api/maintenance/components")]
[Authorize(Roles = "farm_admin")]
public class MaintenanceComponentController(
    ILogger<MaintenanceComponentController> logger,
    IMaintenanceComponentRepository componentRepository)
    : ControllerBase
{
    private readonly ILogger<MaintenanceComponentController> _logger = logger;
    private readonly IMaintenanceComponentRepository _componentRepository = componentRepository;

    private static MaintenanceComponentResponse ToResponse(MaintenanceComponent c) => new(
        c.Id, c.Name, c.Category, c.Sku, c.Description,
        c.UnitCost, c.Supplier, c.Url,
        c.InStock, c.MinimumStock, c.CreatedAt, c.UpdatedAt);

    /// <summary>
    /// Gets all components. Optionally filter by category.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<MaintenanceComponentResponse>>> GetAllAsync(
        [FromQuery] string? category,
        CancellationToken ct)
    {
        List<MaintenanceComponent> components = await _componentRepository.GetAllAsync(category, ct);
        return Ok(components.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Gets a component by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MaintenanceComponentResponse>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        MaintenanceComponent? component = await _componentRepository.GetByIdAsync(id, ct);
        if (component == null)
        {
            return NotFound();
        }

        return Ok(ToResponse(component));
    }

    /// <summary>
    /// Gets all distinct component categories.
    /// </summary>
    [HttpGet("categories")]
    public async Task<ActionResult<List<string>>> GetCategoriesAsync(CancellationToken ct)
    {
        List<string> categories = await _componentRepository.GetCategoriesAsync(ct);
        return Ok(categories);
    }

    /// <summary>
    /// Gets components that are below their minimum stock level.
    /// </summary>
    [HttpGet("low-stock")]
    public async Task<ActionResult<List<MaintenanceComponentResponse>>> GetLowStockAsync(CancellationToken ct)
    {
        List<MaintenanceComponent> components = await _componentRepository.GetLowStockAsync(ct);
        return Ok(components.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Creates a new component.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<MaintenanceComponentResponse>> CreateAsync(
        [FromBody] CreateMaintenanceComponentRequest request,
        CancellationToken ct)
    {
        var component = new MaintenanceComponent
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Category = request.Category,
            Sku = request.Sku,
            Description = request.Description,
            UnitCost = request.UnitCost,
            Supplier = request.Supplier,
            Url = request.Url,
            InStock = request.InStock,
            MinimumStock = request.MinimumStock,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _componentRepository.AddAsync(component, ct);
        _logger.LogInformation("Created maintenance component {ComponentId} '{ComponentName}'", component.Id, component.Name);

        return Created($"/api/maintenance/components/{component.Id}", ToResponse(component));
    }

    /// <summary>
    /// Updates an existing component.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<MaintenanceComponentResponse>> UpdateAsync(
        Guid id,
        [FromBody] UpdateMaintenanceComponentRequest request,
        CancellationToken ct)
    {
        MaintenanceComponent? component = await _componentRepository.GetByIdAsync(id, ct);
        if (component == null)
        {
            return NotFound();
        }

        component.Name = request.Name;
        component.Category = request.Category;
        component.Sku = request.Sku;
        component.Description = request.Description;
        component.UnitCost = request.UnitCost;
        component.Supplier = request.Supplier;
        component.Url = request.Url;
        component.InStock = request.InStock;
        component.MinimumStock = request.MinimumStock;
        component.UpdatedAt = DateTime.UtcNow;

        await _componentRepository.UpdateAsync(component, ct);
        _logger.LogInformation("Updated maintenance component {ComponentId} '{ComponentName}'", component.Id, component.Name);

        return Ok(ToResponse(component));
    }

    /// <summary>
    /// Deletes a component. Fails with 409 Conflict if the component is referenced by any task.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        MaintenanceComponent? component = await _componentRepository.GetByIdAsync(id, ct);
        if (component == null)
        {
            return NotFound();
        }

        bool isReferenced = await _componentRepository.IsReferencedByTasksAsync(id, ct);
        if (isReferenced)
        {
            return Conflict(new { message = "Cannot delete component because it is referenced by one or more maintenance tasks." });
        }

        await _componentRepository.DeleteAsync(component, ct);
        _logger.LogInformation("Deleted maintenance component {ComponentId} '{ComponentName}'", component.Id, component.Name);

        return NoContent();
    }
}
