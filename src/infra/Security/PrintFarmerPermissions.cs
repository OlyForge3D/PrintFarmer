using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace Farm.Infrastructure.Security;

/// <summary>
/// Stable resource-action permission names exposed by the PrintFarmer API contract.
/// </summary>
public static class PrintFarmerPermissions
{
    public const string ClaimType = "permission";
    public const string FarmAdminRole = "farm_admin";

    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "Nested permission groups keep the public resource:action contract discoverable and typo-resistant.")]
    public static class Calibration
    {
        public const string Create = "calibration:create";
        public const string Read = "calibration:read";
        public const string Update = "calibration:update";
        public const string Delete = "calibration:delete";
        public const string Generate = "calibration:generate";
        public const string Publish = "calibration:publish";
    }

    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "Nested permission groups keep the public resource:action contract discoverable and typo-resistant.")]
    [SuppressMessage(
        "Naming",
        "CA1724:Type names should not match namespaces",
        Justification = "Queue is the canonical external resource name in the resource:action permission contract.")]
    public static class Queue
    {
        public const string Read = "queue:read";
        public const string Write = "queue:write";
        public const string Start = "queue:start";
        public const string Cancel = "queue:cancel";
        public const string AcknowledgeBedClear = "queue:acknowledge-bed-clear";
        public const string Reconcile = "queue:reconcile";
    }

    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "Nested permission groups keep the public resource:action contract discoverable and typo-resistant.")]
    public static class Slicing
    {
        public const string Submit = "slicing:submit";
        public const string ReadArtifact = "slicing:read-artifact";
        public const string Promote = "slicing:promote";
    }

    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "Nested permission groups keep the public resource:action contract discoverable and typo-resistant.")]
    public static class DispatchSettings
    {
        public const string Manage = "dispatch-settings:manage";
    }

    public static IReadOnlyList<string> CalibrationFoundation { get; } =
    [
        Calibration.Create,
        Calibration.Read,
        Calibration.Update,
        Calibration.Delete,
        Calibration.Generate,
        Calibration.Publish,
        Queue.Read,
        Queue.Write,
        Queue.Start,
        Queue.Cancel,
        Queue.AcknowledgeBedClear,
        Queue.Reconcile,
        Slicing.Submit,
        Slicing.ReadArtifact,
        Slicing.Promote,
        DispatchSettings.Manage,
    ];

    public static bool IsFarmAdmin(ClaimsPrincipal user) =>
        user.IsInRole(FarmAdminRole);

    public static bool HasPermission(ClaimsPrincipal user, string permission) =>
        IsFarmAdmin(user) || user.HasClaim(ClaimType, permission);

    public static bool TryGetUserId(ClaimsPrincipal user, out Guid userId) =>
        Guid.TryParse(
            user.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
            user.FindFirst("sub")?.Value,
            out userId);

    public static (string Resource, string Action) Split(string permission)
    {
        int separator = permission.IndexOf(':');
        if (separator <= 0 || separator == permission.Length - 1)
        {
            throw new ArgumentException(
                "Permissions must use the resource:action format.",
                nameof(permission));
        }

        return (permission[..separator], permission[(separator + 1)..]);
    }
}
