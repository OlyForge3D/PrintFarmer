using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request payload for deploying a maintenance plan to a printer.
/// </summary>
public record DeployMaintenancePlanRequest(
    [Required]
    Guid MaintenancePlanId,
    [Required]
    Guid PrinterId,
    string? Notes = null);

/// <summary>
/// Request payload for updating a schedule deployment.
/// </summary>
public record UpdateScheduleDeploymentRequest(
    bool IsActive = true,
    string? Notes = null);
