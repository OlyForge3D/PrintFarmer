using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request payload for creating a maintenance plan.
/// </summary>
public record CreateMaintenancePlanRequest(
    [Required, MinLength(1), MaxLength(200)]
    string Name,
    [MaxLength(1000)]
    string? Description = null,
    Guid? PrinterId = null,
    Guid? PrinterModelId = null,
    Guid? ManufacturerId = null,
    int? MotionType = null,
    bool IsActive = true,
    bool IsDefault = false);

/// <summary>
/// Request payload for updating a maintenance plan.
/// </summary>
public record UpdateMaintenancePlanRequest(
    [Required, MinLength(1), MaxLength(200)]
    string Name,
    [MaxLength(1000)]
    string? Description = null,
    Guid? PrinterId = null,
    Guid? PrinterModelId = null,
    Guid? ManufacturerId = null,
    int? MotionType = null,
    bool IsActive = true,
    bool IsDefault = false);
