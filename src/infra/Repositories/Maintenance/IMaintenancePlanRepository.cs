using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Repository for maintenance plan CRUD operations.
/// </summary>
public interface IMaintenancePlanRepository
{
    /// <summary>
    /// Gets all plans, optionally filtered by active status.
    /// </summary>
    Task<List<MaintenancePlan>> GetAllAsync(bool? activeOnly = null, CancellationToken ct = default);

    /// <summary>
    /// Gets a plan by ID, including its tasks and their components.
    /// </summary>
    Task<MaintenancePlan?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets all plans applicable to a specific printer (by printer, model, manufacturer, or motion type).
    /// </summary>
    Task<List<MaintenancePlan>> GetPlansForPrinterAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Adds a new plan.
    /// </summary>
    Task AddAsync(MaintenancePlan plan, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing plan.
    /// </summary>
    Task UpdateAsync(MaintenancePlan plan, CancellationToken ct = default);

    /// <summary>
    /// Deletes a plan and all its tasks (cascade).
    /// </summary>
    Task DeleteAsync(MaintenancePlan plan, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to the database.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
