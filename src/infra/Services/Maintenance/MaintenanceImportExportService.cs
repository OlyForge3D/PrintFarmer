using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Maintenance;
using Farm.Infrastructure.Repositories.Maintenance;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Implements JSON import/export for maintenance entities (components, tasks, plans).
/// Uses name-based matching for cross-instance portability.
/// </summary>
public class MaintenanceImportExportService(
    IMaintenanceComponentRepository componentRepository,
    IMaintenanceTaskRepository taskRepository,
    IMaintenancePlanRepository planRepository,
    ILogger<MaintenanceImportExportService> logger)
    : IMaintenanceImportExportService
{
    // ═══════════════════════════════ EXPORT ═══════════════════════════════

    public async Task<MaintenanceExportEnvelope> ExportComponentsAsync(CancellationToken ct)
    {
        List<MaintenanceComponent> components = await componentRepository.GetAllAsync(null, ct);
        return new MaintenanceExportEnvelope
        {
            ExportType = "components",
            Components = components.Select(ToComponentDto).ToList()
        };
    }

    public async Task<MaintenanceExportEnvelope> ExportTasksAsync(CancellationToken ct)
    {
        List<MaintenanceTask> tasks = await taskRepository.GetAllAsync(null, null, ct);
        return new MaintenanceExportEnvelope
        {
            ExportType = "tasks",
            Tasks = tasks.Select(ToTaskDto).ToList()
        };
    }

    public async Task<MaintenanceExportEnvelope> ExportPlansAsync(CancellationToken ct)
    {
        List<MaintenancePlan> plans = await planRepository.GetAllAsync(null, ct);
        return new MaintenanceExportEnvelope
        {
            ExportType = "plans",
            Plans = plans.Select(ToPlanDto).ToList()
        };
    }

    public async Task<MaintenanceExportEnvelope> ExportBundleAsync(CancellationToken ct)
    {
        List<MaintenanceComponent> components = await componentRepository.GetAllAsync(null, ct);
        List<MaintenanceTask> tasks = await taskRepository.GetAllAsync(null, null, ct);
        List<MaintenancePlan> plans = await planRepository.GetAllAsync(null, ct);

        return new MaintenanceExportEnvelope
        {
            ExportType = "bundle",
            Components = components.Select(ToComponentDto).ToList(),
            Tasks = tasks.Select(ToTaskDto).ToList(),
            Plans = plans.Select(ToPlanDto).ToList()
        };
    }

    // ═══════════════════════════════ IMPORT ═══════════════════════════════

    public async Task<MaintenanceImportResult> ImportComponentsAsync(
        List<ComponentExportDto> items, CancellationToken ct)
    {
        int created = 0, updated = 0;
        var errors = new List<string>();

        List<MaintenanceComponent> existing = await componentRepository.GetAllAsync(null, ct);
        var byName = existing.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < items.Count; i++)
        {
            ComponentExportDto dto = items[i];
            try
            {
                if (string.IsNullOrWhiteSpace(dto.Name))
                {
                    errors.Add($"Row {i + 1}: Name is required.");
                    continue;
                }

                if (byName.TryGetValue(dto.Name, out MaintenanceComponent? comp))
                {
                    ApplyComponentDto(comp, dto);
                    await componentRepository.UpdateAsync(comp, ct);
                    updated++;
                }
                else
                {
                    comp = new MaintenanceComponent { Id = Guid.NewGuid() };
                    ApplyComponentDto(comp, dto);
                    comp.CreatedAt = DateTime.UtcNow;
                    await componentRepository.AddAsync(comp, ct);
                    byName[comp.Name] = comp;
                    created++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Row {i + 1} ({dto.Name}): {ex.Message}");
            }
        }

        logger.LogInformation("Component import complete: {Created} created, {Updated} updated, {Errors} errors",
            created, updated, errors.Count);

        return new MaintenanceImportResult(created, updated, errors.Count, errors.ToArray(), []);
    }

    public async Task<MaintenanceImportResult> ImportTasksAsync(
        List<TaskExportDto> items, CancellationToken ct)
    {
        int created = 0, updated = 0;
        var errors = new List<string>();
        var warnings = new List<string>();

        List<MaintenanceTask> existingTasks = await taskRepository.GetAllAsync(null, null, ct);
        var tasksByName = existingTasks.ToDictionary(t => t.TaskName, StringComparer.OrdinalIgnoreCase);

        List<MaintenanceComponent> existingComponents = await componentRepository.GetAllAsync(null, ct);
        var componentsByName = existingComponents.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < items.Count; i++)
        {
            TaskExportDto dto = items[i];
            try
            {
                if (string.IsNullOrWhiteSpace(dto.TaskName))
                {
                    errors.Add($"Row {i + 1}: TaskName is required.");
                    continue;
                }

                MaintenanceTask task;
                if (tasksByName.TryGetValue(dto.TaskName, out MaintenanceTask? existingTask))
                {
                    task = existingTask;
                    ApplyTaskDto(task, dto);
                    await taskRepository.UpdateAsync(task, ct);
                    updated++;
                }
                else
                {
                    task = new MaintenanceTask { Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow };
                    ApplyTaskDto(task, dto);
                    await taskRepository.AddAsync(task, ct);
                    tasksByName[task.TaskName] = task;
                    created++;
                }

                // Reconcile task-component links
                if (dto.Components is { Count: > 0 })
                {
                    List<MaintenanceTaskComponent> existingLinks =
                        await taskRepository.GetTaskComponentsAsync(task.Id, ct);

                    foreach (TaskComponentRefDto compRef in dto.Components)
                    {
                        if (!componentsByName.TryGetValue(compRef.Name, out MaintenanceComponent? comp))
                        {
                            warnings.Add($"Task '{dto.TaskName}': component '{compRef.Name}' not found — skipped.");
                            continue;
                        }

                        MaintenanceTaskComponent? link = existingLinks
                            .FirstOrDefault(l => l.MaintenanceComponentId == comp.Id);

                        if (link is null)
                        {
                            await taskRepository.AddComponentAsync(new MaintenanceTaskComponent
                            {
                                Id = Guid.NewGuid(),
                                MaintenanceTaskId = task.Id,
                                MaintenanceComponentId = comp.Id,
                                Quantity = compRef.Quantity,
                                Notes = compRef.Notes
                            }, ct);
                        }
                        // existing link — leave as-is (don't overwrite quantity/notes for existing links)
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Row {i + 1} ({dto.TaskName}): {ex.Message}");
            }
        }

        logger.LogInformation("Task import complete: {Created} created, {Updated} updated, {Errors} errors, {Warnings} warnings",
            created, updated, errors.Count, warnings.Count);

        return new MaintenanceImportResult(created, updated, errors.Count, errors.ToArray(), warnings.ToArray());
    }

    public async Task<MaintenanceImportResult> ImportPlansAsync(
        List<PlanExportDto> items, CancellationToken ct)
    {
        int created = 0, updated = 0;
        var errors = new List<string>();
        var warnings = new List<string>();

        List<MaintenancePlan> existingPlans = await planRepository.GetAllAsync(null, ct);
        var plansByName = existingPlans.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        List<MaintenanceTask> existingTasks = await taskRepository.GetAllAsync(null, null, ct);
        var tasksByName = existingTasks.ToDictionary(t => t.TaskName, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < items.Count; i++)
        {
            PlanExportDto dto = items[i];
            try
            {
                if (string.IsNullOrWhiteSpace(dto.Name))
                {
                    errors.Add($"Row {i + 1}: Name is required.");
                    continue;
                }

                MaintenancePlan plan;
                if (plansByName.TryGetValue(dto.Name, out MaintenancePlan? existingPlan))
                {
                    plan = existingPlan;
                    plan.Description = dto.Description;
                    plan.IsActive = dto.IsActive;
                    plan.UpdatedAt = DateTime.UtcNow;
                    await planRepository.UpdateAsync(plan, ct);
                    updated++;
                }
                else
                {
                    plan = new MaintenancePlan
                    {
                        Id = Guid.NewGuid(),
                        Name = dto.Name,
                        Description = dto.Description,
                        IsActive = dto.IsActive,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    await planRepository.AddAsync(plan, ct);
                    plansByName[plan.Name] = plan;
                    created++;
                }

                // Reconcile plan-task links
                if (dto.Tasks is { Count: > 0 })
                {
                    foreach (PlanTaskRefDto taskRef in dto.Tasks)
                    {
                        if (!tasksByName.TryGetValue(taskRef.TaskName, out MaintenanceTask? task))
                        {
                            warnings.Add($"Plan '{dto.Name}': task '{taskRef.TaskName}' not found — skipped.");
                            continue;
                        }

                        bool alreadyLinked = plan.PlanTasks.Any(pt => pt.MaintenanceTaskId == task.Id);
                        if (!alreadyLinked)
                        {
                            plan.PlanTasks.Add(new PlanTask
                            {
                                Id = Guid.NewGuid(),
                                MaintenancePlanId = plan.Id,
                                MaintenanceTaskId = task.Id,
                                SortOrder = taskRef.SortOrder,
                                IntervalHoursOverride = taskRef.IntervalHoursOverride,
                                IntervalDaysOverride = taskRef.IntervalDaysOverride
                            });
                        }
                    }

                    await planRepository.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Row {i + 1} ({dto.Name}): {ex.Message}");
            }
        }

        logger.LogInformation("Plan import complete: {Created} created, {Updated} updated, {Errors} errors, {Warnings} warnings",
            created, updated, errors.Count, warnings.Count);

        return new MaintenanceImportResult(created, updated, errors.Count, errors.ToArray(), warnings.ToArray());
    }

    public async Task<MaintenanceImportResult> ImportBundleAsync(
        MaintenanceExportEnvelope envelope, CancellationToken ct)
    {
        int totalCreated = 0, totalUpdated = 0;
        var allErrors = new List<string>();
        var allWarnings = new List<string>();

        // Import order: components → tasks → plans (dependency order)
        if (envelope.Components is { Count: > 0 })
        {
            MaintenanceImportResult r = await ImportComponentsAsync(envelope.Components, ct);
            totalCreated += r.CreatedCount;
            totalUpdated += r.UpdatedCount;
            allErrors.AddRange(r.Errors);
            allWarnings.AddRange(r.Warnings);
        }

        if (envelope.Tasks is { Count: > 0 })
        {
            MaintenanceImportResult r = await ImportTasksAsync(envelope.Tasks, ct);
            totalCreated += r.CreatedCount;
            totalUpdated += r.UpdatedCount;
            allErrors.AddRange(r.Errors);
            allWarnings.AddRange(r.Warnings);
        }

        if (envelope.Plans is { Count: > 0 })
        {
            MaintenanceImportResult r = await ImportPlansAsync(envelope.Plans, ct);
            totalCreated += r.CreatedCount;
            totalUpdated += r.UpdatedCount;
            allErrors.AddRange(r.Errors);
            allWarnings.AddRange(r.Warnings);
        }

        logger.LogInformation("Bundle import complete: {Created} created, {Updated} updated, {Errors} errors, {Warnings} warnings",
            totalCreated, totalUpdated, allErrors.Count, allWarnings.Count);

        return new MaintenanceImportResult(totalCreated, totalUpdated, allErrors.Count,
            allErrors.ToArray(), allWarnings.ToArray());
    }

    // ═══════════════════════════════ MAPPING ═══════════════════════════════

    private static ComponentExportDto ToComponentDto(MaintenanceComponent c) => new()
    {
        Name = c.Name,
        Category = c.Category,
        Sku = c.Sku,
        Description = c.Description,
        UnitCost = c.UnitCost,
        Supplier = c.Supplier,
        Url = c.Url,
        InStock = c.InStock,
        MinimumStock = c.MinimumStock
    };

    private static TaskExportDto ToTaskDto(MaintenanceTask t) => new()
    {
        TaskName = t.TaskName,
        Category = t.Category,
        Description = t.Description,
        IntervalHours = t.IntervalHours,
        IntervalDays = t.IntervalDays,
        EstimatedDurationMinutes = t.EstimatedDurationMinutes,
        Priority = t.Priority,
        IsActive = t.IsActive,
        Components = t.TaskComponents.Select(tc => new TaskComponentRefDto
        {
            Name = tc.MaintenanceComponent?.Name ?? string.Empty,
            Quantity = tc.Quantity,
            Notes = tc.Notes
        }).ToList()
    };

    private static PlanExportDto ToPlanDto(MaintenancePlan p) => new()
    {
        Name = p.Name,
        Description = p.Description,
        IsActive = p.IsActive,
        Tasks = p.PlanTasks.OrderBy(pt => pt.SortOrder).Select(pt => new PlanTaskRefDto
        {
            TaskName = pt.MaintenanceTask?.TaskName ?? string.Empty,
            SortOrder = pt.SortOrder,
            IntervalHoursOverride = pt.IntervalHoursOverride,
            IntervalDaysOverride = pt.IntervalDaysOverride
        }).ToList()
    };

    private static void ApplyComponentDto(MaintenanceComponent comp, ComponentExportDto dto)
    {
        comp.Name = dto.Name;
        comp.Category = dto.Category;
        comp.Sku = dto.Sku;
        comp.Description = dto.Description;
        comp.UnitCost = dto.UnitCost;
        comp.Supplier = dto.Supplier;
        comp.Url = dto.Url;
        comp.InStock = dto.InStock;
        comp.MinimumStock = dto.MinimumStock;
        comp.UpdatedAt = DateTime.UtcNow;
    }

    private static void ApplyTaskDto(MaintenanceTask task, TaskExportDto dto)
    {
        task.TaskName = dto.TaskName;
        task.Category = dto.Category;
        task.Description = dto.Description;
        task.IntervalHours = dto.IntervalHours;
        task.IntervalDays = dto.IntervalDays;
        task.EstimatedDurationMinutes = dto.EstimatedDurationMinutes;
        task.Priority = dto.Priority;
        task.IsActive = dto.IsActive;
        task.UpdatedAt = DateTime.UtcNow;
    }
}
