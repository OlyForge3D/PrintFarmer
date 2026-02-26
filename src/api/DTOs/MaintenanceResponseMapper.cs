using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.DTOs;

/// <summary>
/// Shared mapping methods for maintenance domain entities to response DTOs.
/// Eliminates duplicate mapping logic across MaintenanceTasksController and MaintenancePlanController.
/// </summary>
public static class MaintenanceResponseMapper
{
    public static MaintenanceTaskResponse ToTaskResponse(MaintenanceTask task) => new(
        task.Id,
        task.TaskName,
        task.Description,
        task.Category,
        task.IntervalHours,
        task.IntervalDays,
        task.EstimatedDurationMinutes,
        task.Priority,
        task.IsActive,
        task.IsDefault,
        task.RequiresEnclosure,
        task.RequiresCarbonFilter,
        task.RequiresHepaFilter,
        task.RequiresBowdenTube,
        task.RequiresPtfeLiner,
        task.RequiresLinearRails,
        task.RequiresLeadScrews,
        task.RequiresToolchanger,
        task.RequiresFilamentCutter,
        task.RequiresHeatedChamber,
        task.RequiresHeatedBed,
        task.RequiresMultiMaterial,
        task.CreatedAt,
        task.UpdatedAt,
        task.TaskComponents.Select(ToTaskComponentResponse).ToList());

    public static MaintenanceTaskComponentResponse ToTaskComponentResponse(MaintenanceTaskComponent tc) => new(
        tc.Id,
        tc.MaintenanceTaskId,
        tc.MaintenanceComponentId,
        tc.MaintenanceComponent?.Name,
        tc.Quantity,
        tc.Notes);

    public static MaintenancePlanResponse ToPlanResponse(MaintenancePlan plan) => new(
        plan.Id,
        plan.Name,
        plan.Description,
        plan.PrinterId,
        plan.Printer?.Name,
        plan.PrinterModelId,
        plan.PrinterModel?.Name,
        plan.ManufacturerId,
        plan.Manufacturer?.Name,
        plan.MotionType,
        plan.IsActive,
        plan.IsDefault,
        plan.CreatedAt,
        plan.UpdatedAt,
        plan.PlanTasks.Select(ToPlanTaskResponse).ToList());

    public static PlanTaskResponse ToPlanTaskResponse(PlanTask pt) => new(
        pt.Id,
        pt.MaintenancePlanId,
        pt.MaintenanceTaskId,
        pt.SortOrder,
        pt.IntervalHoursOverride,
        pt.IntervalDaysOverride,
        ToTaskResponse(pt.MaintenanceTask));
}
