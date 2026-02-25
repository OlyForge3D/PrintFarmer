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
    double? IntervalHours = null,
    int? IntervalDays = null,
    int? EstimatedDurationMinutes = null,
    int Priority = 0,
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
    double? IntervalHours = null,
    int? IntervalDays = null,
    int? EstimatedDurationMinutes = null,
    int Priority = 0,
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
