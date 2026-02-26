using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Repository for maintenance task CRUD operations (global task catalog).
/// </summary>
public interface IMaintenanceTaskRepository
{
    /// <summary>
    /// Gets all tasks in the global catalog, optionally filtered.
    /// </summary>
    Task<List<MaintenanceTask>> GetAllAsync(string? category = null, bool? activeOnly = null, CancellationToken ct = default);

    /// <summary>
    /// Gets a task by ID, including its component associations.
    /// </summary>
    Task<MaintenanceTask?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Adds a new task to a plan.
    /// </summary>
    Task AddAsync(MaintenanceTask task, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing task.
    /// </summary>
    Task UpdateAsync(MaintenanceTask task, CancellationToken ct = default);

    /// <summary>
    /// Deletes a task (cascades to component associations).
    /// </summary>
    Task DeleteAsync(MaintenanceTask task, CancellationToken ct = default);

    /// <summary>
    /// Adds a component association to a task.
    /// </summary>
    Task AddComponentAsync(MaintenanceTaskComponent taskComponent, CancellationToken ct = default);

    /// <summary>
    /// Removes a component association from a task.
    /// </summary>
    Task RemoveComponentAsync(MaintenanceTaskComponent taskComponent, CancellationToken ct = default);

    /// <summary>
    /// Gets all component associations for a task.
    /// </summary>
    Task<List<MaintenanceTaskComponent>> GetTaskComponentsAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Finds a specific task-component association.
    /// </summary>
    Task<MaintenanceTaskComponent?> FindTaskComponentAsync(Guid taskId, Guid componentId, CancellationToken ct = default);

    /// <summary>
    /// Gets all distinct task categories, ordered alphabetically.
    /// </summary>
    Task<List<string>> GetCategoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists changes to the database.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
