using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Repository for global maintenance component (parts inventory) CRUD operations.
/// </summary>
public interface IMaintenanceComponentRepository
{
    /// <summary>
    /// Gets all components, optionally filtered by category.
    /// </summary>
    Task<List<MaintenanceComponent>> GetAllAsync(string? category = null, CancellationToken ct = default);

    /// <summary>
    /// Gets a component by ID.
    /// </summary>
    Task<MaintenanceComponent?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets all distinct component categories.
    /// </summary>
    Task<List<string>> GetCategoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets components that are below their minimum stock level.
    /// </summary>
    Task<List<MaintenanceComponent>> GetLowStockAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds a new component.
    /// </summary>
    Task AddAsync(MaintenanceComponent component, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing component.
    /// </summary>
    Task UpdateAsync(MaintenanceComponent component, CancellationToken ct = default);

    /// <summary>
    /// Deletes a component. Fails if the component is referenced by any task.
    /// </summary>
    Task DeleteAsync(MaintenanceComponent component, CancellationToken ct = default);

    /// <summary>
    /// Checks if a component is referenced by any maintenance task.
    /// </summary>
    Task<bool> IsReferencedByTasksAsync(Guid componentId, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to the database.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
