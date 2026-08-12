using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace Farm.Infrastructure.Security;

/// <summary>
/// Stable resource-action permission names exposed by the PrintFarmer API contract.
/// </summary>
public static class PrintFarmerPermissions
{
    public const string ClaimType = "permission";
    public const string DenyClaimType = "permission-deny";
    public const string FarmAdminRole = "farm_admin";

    /// <summary>
    /// The action name that, when granted on a resource, implies every other action on
    /// that same resource (e.g. "calibration:admin" implies "calibration:read"). The
    /// implication never crosses resources and does not extend to any other action.
    /// </summary>
    public const string AdminAction = "admin";

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

    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "Nested permission groups keep the public resource:action contract discoverable and typo-resistant.")]
    public static class Integrations
    {
        /// <summary>
        /// Managing the Obico ML failure-detection integration (create/update/delete server
        /// records, run connectivity probes). This is an administrative surface: farm_admin
        /// holds it implicitly, and it is not granted to farm_user by default.
        /// </summary>
        public const string ManageObico = "obico:manage";
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

    public static bool HasPermission(ClaimsPrincipal user, string permission)
    {
        if (IsFarmAdmin(user) || user.HasClaim(ClaimType, permission))
        {
            return true;
        }

        (string resource, string action) = Split(permission);
        return ImpliesViaResourceAdmin(user, resource, action);
    }

    /// <summary>
    /// Returns true when the principal holds "{resource}:admin" and the requested
    /// <paramref name="action"/> is not itself "admin". A resource-level admin grant
    /// implies every finer-grained action on that same resource (e.g. "calibration:admin"
    /// implies "calibration:read"); the implication never crosses resources and does not
    /// extend beyond the "admin" action. This is the single source of truth for the
    /// implication so every enforcement point (the authorization handler, SignalR hubs,
    /// capability services) stays coherent.
    ///
    /// An explicit deny claim (<see cref="DenyClaimType"/>) for "{resource}:{action}"
    /// always suppresses the implication: per docs/ROLE_PERMISSION_PRECEDENCE.md, an
    /// explicit deny on a specific action must win even when the same user also holds a
    /// resource-level admin grant, otherwise the deny would be silently unenforceable.
    /// </summary>
    public static bool ImpliesViaResourceAdmin(ClaimsPrincipal user, string resource, string action) =>
        ImpliesViaResourceAdminCore(
            resource,
            action,
            permission => user.HasClaim(ClaimType, permission),
            permission => user.HasClaim(DenyClaimType, permission));

    /// <summary>
    /// Permission-set overload of <see cref="ImpliesViaResourceAdmin(ClaimsPrincipal, string, string)"/>,
    /// for callers that resolve a user's authority directly from the database rather than from a
    /// <see cref="ClaimsPrincipal"/> — notably the Desktop API-key exchange, which must decide
    /// authority for a key's owner who is not the current request principal.
    /// </summary>
    /// <remarks>
    /// Shares <see cref="ImpliesViaResourceAdminCore"/> with the principal overload so the rule —
    /// including the explicit-deny suppression — cannot drift between the enforcement path and the
    /// provisioning path. Synthesizing a <see cref="ClaimsPrincipal"/> just to reuse the other
    /// overload would be worse: it would invent a principal that never authenticated, and any
    /// future role-sensitive change to the implication would then silently apply to a fabricated
    /// identity.
    /// </remarks>
    public static bool ImpliesViaResourceAdmin(
        IReadOnlySet<string> grantedPermissions,
        IReadOnlySet<string> deniedPermissions,
        string resource,
        string action)
    {
        ArgumentNullException.ThrowIfNull(grantedPermissions);
        ArgumentNullException.ThrowIfNull(deniedPermissions);
        return ImpliesViaResourceAdminCore(
            resource,
            action,
            grantedPermissions.Contains,
            deniedPermissions.Contains);
    }

    /// <summary>
    /// The implication rule itself, expressed once. The overloads above differ only in how they
    /// answer "does this subject hold that permission" and "is this action explicitly denied".
    /// </summary>
    private static bool ImpliesViaResourceAdminCore(
        string resource,
        string action,
        Func<string, bool> holdsPermission,
        Func<string, bool> holdsDeny) =>
        !string.Equals(action, AdminAction, StringComparison.Ordinal)
        && holdsPermission($"{resource}:{AdminAction}")
        && !holdsDeny($"{resource}:{action}");

    /// <summary>
    /// Whether a resolved set of granted permissions satisfies <paramref name="permission"/>,
    /// by exact match or by same-resource admin implication.
    /// </summary>
    /// <param name="grantedPermissions">
    /// The subject's effective grants. Callers obtain these from
    /// <c>IUsersRepository.GetGrantedPermissionsAsync</c>, which already subtracts denied pairs,
    /// so an exact match here is inherently deny-safe.
    /// </param>
    /// <param name="deniedPermissions">
    /// The subject's explicit denies, from <c>IUsersRepository.GetDeniedPermissionsAsync</c>.
    /// Required because the admin implication would otherwise resurrect a denied action from a
    /// <c>{resource}:admin</c> grant - the exact gap #1472 closed on the claims-based path.
    /// </param>
    /// <param name="permission">The <c>resource:action</c> permission being tested.</param>
    /// <remarks>
    /// This is the set-based counterpart of <see cref="HasPermission"/> minus the
    /// <see cref="FarmAdminRole"/> bypass, which callers handle separately because a role is not
    /// part of a permission set.
    /// </remarks>
    public static bool SetGrantsPermission(
        IReadOnlySet<string> grantedPermissions,
        IReadOnlySet<string> deniedPermissions,
        string permission)
    {
        ArgumentNullException.ThrowIfNull(grantedPermissions);
        ArgumentNullException.ThrowIfNull(deniedPermissions);

        if (deniedPermissions.Contains(permission))
        {
            return false;
        }

        if (grantedPermissions.Contains(permission))
        {
            return true;
        }

        (string resource, string action) = Split(permission);
        return ImpliesViaResourceAdmin(grantedPermissions, deniedPermissions, resource, action);
    }

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
