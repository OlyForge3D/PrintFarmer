using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request payload for creating a maintenance task in the global catalog.
/// </summary>
public record CreateMaintenanceTaskRequest(
    [Required, MinLength(1), MaxLength(200)]
    string TaskName,
    [Required, MinLength(1), MaxLength(100)]
    string Category,
    [MaxLength(1000)]
    string? Description = null,
    [Range(0.1, double.MaxValue)]
    double? IntervalHours = null,
    [Range(1, int.MaxValue)]
    int? IntervalDays = null,
    [Range(1, int.MaxValue)]
    int? EstimatedDurationMinutes = null,
    [Range(1, 4)]
    int Priority = 2,
    bool IsActive = true,
    bool IsDefault = false,
    bool? RequiresEnclosure = null,
    bool? RequiresCarbonFilter = null,
    bool? RequiresHepaFilter = null,
    bool? RequiresBowdenTube = null,
    bool? RequiresPtfeLiner = null,
    bool? RequiresLinearRails = null,
    bool? RequiresLeadScrews = null,
    bool? RequiresToolchanger = null,
    bool? RequiresFilamentCutter = null,
    bool? RequiresHeatedChamber = null,
    bool? RequiresHeatedBed = null,
    bool? RequiresMultiMaterial = null);

/// <summary>
/// Request payload for updating a maintenance task.
/// </summary>
public record UpdateMaintenanceTaskRequest(
    [Required, MinLength(1), MaxLength(200)]
    string TaskName,
    [Required, MinLength(1), MaxLength(100)]
    string Category,
    [MaxLength(1000)]
    string? Description = null,
    [Range(0.1, double.MaxValue)]
    double? IntervalHours = null,
    [Range(1, int.MaxValue)]
    int? IntervalDays = null,
    [Range(1, int.MaxValue)]
    int? EstimatedDurationMinutes = null,
    [Range(1, 4)]
    int Priority = 2,
    bool IsActive = true,
    bool IsDefault = false,
    bool? RequiresEnclosure = null,
    bool? RequiresCarbonFilter = null,
    bool? RequiresHepaFilter = null,
    bool? RequiresBowdenTube = null,
    bool? RequiresPtfeLiner = null,
    bool? RequiresLinearRails = null,
    bool? RequiresLeadScrews = null,
    bool? RequiresToolchanger = null,
    bool? RequiresFilamentCutter = null,
    bool? RequiresHeatedChamber = null,
    bool? RequiresHeatedBed = null,
    bool? RequiresMultiMaterial = null);

/// <summary>
/// Request payload for adding a component to a task.
/// </summary>
public record AddTaskComponentRequest(
    [Required]
    Guid ComponentId,
    [Range(1, int.MaxValue)]
    int Quantity = 1,
    string? Notes = null);
