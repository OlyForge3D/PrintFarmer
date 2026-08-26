using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request payload for deploying a maintenance plan to a printer.
/// </summary>
/// <param name="MaintenancePlanId">The plan to deploy.</param>
/// <param name="PrinterId">The printer to deploy to.</param>
/// <param name="ToolheadId">
/// Optional physical toolhead scope (issue #711, F6). When null, the schedule is printer-wide
/// (legacy behavior). When set, it must reference a physical toolhead on the target printer so
/// per-tool intervals accrue independently. MMU/AMS gates are not eligible.
/// </param>
/// <param name="Notes">Optional deployment notes.</param>
public record DeployMaintenancePlanRequest(
    [Required]
    Guid MaintenancePlanId,
    [Required]
    Guid PrinterId,
    Guid? ToolheadId = null,
    string? Notes = null);

/// <summary>
/// Request payload for updating a schedule deployment.
/// </summary>
public record UpdateScheduleDeploymentRequest(
    bool IsActive = true,
    string? Notes = null);
