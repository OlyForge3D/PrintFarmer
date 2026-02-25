using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request payload for creating a maintenance task within a plan.
/// </summary>
public record CreateMaintenanceTaskRequest(
    [Required, MinLength(1), MaxLength(200)]
    string TaskName,
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
    int SortOrder = 0);

/// <summary>
/// Request payload for updating a maintenance task.
/// </summary>
public record UpdateMaintenanceTaskRequest(
    [Required, MinLength(1), MaxLength(200)]
    string TaskName,
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
    int SortOrder = 0);

/// <summary>
/// Request payload for adding a component to a task.
/// </summary>
public record AddTaskComponentRequest(
    [Required]
    Guid ComponentId,
    [Range(1, int.MaxValue)]
    int Quantity = 1,
    string? Notes = null);
