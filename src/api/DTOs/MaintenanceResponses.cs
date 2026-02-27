namespace Farm.Web.Api.DTOs;

/// <summary>
/// Response DTO for a maintenance plan. Excludes navigation entities that carry sensitive data.
/// </summary>
public record MaintenancePlanResponse(
    Guid Id,
    string Name,
    string? Description,
    Guid? PrinterId,
    string? PrinterName,
    Guid? PrinterModelId,
    string? PrinterModelName,
    Guid? ManufacturerId,
    string? ManufacturerName,
    int? MotionType,
    bool IsActive,
    bool IsDefault,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<PlanTaskResponse> PlanTasks);

/// <summary>
/// Response DTO for a task within a plan (join entity with sort order and interval overrides).
/// </summary>
public record PlanTaskResponse(
    Guid Id,
    Guid MaintenancePlanId,
    Guid MaintenanceTaskId,
    int SortOrder,
    double? IntervalHoursOverride,
    int? IntervalDaysOverride,
    MaintenanceTaskResponse Task);

/// <summary>
/// Response DTO for a maintenance task (global catalog item).
/// </summary>
public record MaintenanceTaskResponse(
    Guid Id,
    string TaskName,
    string? Description,
    string Category,
    double? IntervalHours,
    int? IntervalDays,
    int? EstimatedDurationMinutes,
    int Priority,
    bool IsActive,
    bool IsDefault,
    bool? RequiresEnclosure,
    bool? RequiresCarbonFilter,
    bool? RequiresHepaFilter,
    bool? RequiresBowdenTube,
    bool? RequiresPtfeLiner,
    bool? RequiresLinearRails,
    bool? RequiresLeadScrews,
    bool? RequiresToolchanger,
    bool? RequiresFilamentCutter,
    bool? RequiresHeatedChamber,
    bool? RequiresHeatedBed,
    bool? RequiresMultiMaterial,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<MaintenanceTaskComponentResponse> TaskComponents);

/// <summary>
/// Response DTO for a task-component association.
/// </summary>
public record MaintenanceTaskComponentResponse(
    Guid Id,
    Guid MaintenanceTaskId,
    Guid MaintenanceComponentId,
    string? ComponentName,
    int Quantity,
    string? Notes);

/// <summary>
/// Response DTO for a maintenance component (parts inventory item).
/// </summary>
public record MaintenanceComponentResponse(
    Guid Id,
    string Name,
    string Category,
    string? Sku,
    string? Description,
    decimal? UnitCost,
    string? Supplier,
    string? Url,
    int InStock,
    int MinimumStock,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Response DTO for a maintenance plan deployed to a specific printer.
/// </summary>
public record PrinterMaintenanceScheduleResponse(
    Guid Id,
    Guid MaintenancePlanId,
    string PlanName,
    Guid PrinterId,
    string? PrinterName,
    bool IsActive,
    DateTime DeployedAt,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt);
