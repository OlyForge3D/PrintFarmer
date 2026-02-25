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
    List<MaintenanceTaskResponse> Tasks);

/// <summary>
/// Response DTO for a maintenance task within a plan.
/// </summary>
public record MaintenanceTaskResponse(
    Guid Id,
    Guid MaintenancePlanId,
    string TaskName,
    string? Description,
    double? IntervalHours,
    int? IntervalDays,
    int? EstimatedDurationMinutes,
    int Priority,
    bool IsActive,
    int SortOrder,
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
