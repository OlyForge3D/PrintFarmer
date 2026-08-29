using System.Collections.ObjectModel;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;

namespace Farm.Infrastructure.Authorization;

/// <summary>
/// Describes one individually selectable <see cref="ApiKeyScope"/> flag.
/// </summary>
/// <param name="Scope">The single-bit scope flag.</param>
/// <param name="Name">The canonical scope name, used as the <c>scope</c> claim value and in the
/// <c>scopeNames</c> API contract.</param>
/// <param name="Permission">
/// The single <c>permission</c> claim this scope authorizes, or <c>null</c> for the legacy
/// model/library scopes, which are gated by <see cref="DesktopScopeRequirement"/> policies and
/// never become permission claims.
/// </param>
/// <param name="Requires">
/// Other scopes that must also be selected for this scope to be usable, so a key cannot be issued
/// in a shape that is guaranteed to dead-end mid-workflow.
/// </param>
public sealed record DesktopScopeDefinition(
    ApiKeyScope Scope,
    string Name,
    string? Permission,
    IReadOnlyList<ApiKeyScope> Requires);

/// <summary>
/// The result of intersecting a key's stored scope flags with its owner's live authorization.
/// </summary>
/// <param name="Requested">Every scope flag stored on the key.</param>
/// <param name="Effective">
/// The scope flags that survive the intersection. Legacy model/library scopes always survive (they
/// carry no permission); a permission-backed scope survives only while the owner still holds its
/// mapped permission.
/// </param>
/// <param name="Dropped">The permission-backed scope flags removed because the owner lost them.</param>
public readonly record struct EffectiveDesktopScopes(
    ApiKeyScope Requested,
    ApiKeyScope Effective,
    ApiKeyScope Dropped)
{
    /// <summary>True when at least one requested scope was dropped.</summary>
    public bool WasDowngraded => Dropped != ApiKeyScope.None;
}

/// <summary>
/// The single source of truth mapping Desktop <see cref="ApiKeyScope"/> flags to the
/// <see cref="PrintFarmerPermissions"/> claim values they authorize.
/// </summary>
/// <remarks>
/// <para>
/// Both sides of the contract consume this map, so they can never drift:
/// </para>
/// <list type="bullet">
/// <item><description>
/// API-key creation (<c>UserApiKeysController</c>) validates, against the target owner's live
/// database roles and grants, that the owner actually holds every mapped permission before a
/// privileged flag can be stored - early 4xx feedback instead of a key that silently degrades at
/// exchange time.
/// </description></item>
/// <item><description>
/// Token exchange (<c>ApiKeyExchangeService</c>) re-resolves the owner's live authorization and
/// derives <b>one effective mask</b>. Both the emitted <c>scope</c> claims and the emitted
/// <c>permission</c> claims - and the scope list returned to the client - come from that same
/// mask, so a scope can never appear without its permission or vice versa. No role claim is ever
/// emitted.
/// </description></item>
/// </list>
/// <para>
/// Model/library scopes (<see cref="ApiKeyScope.ModelRead"/>, <see cref="ApiKeyScope.ModelWrite"/>,
/// <see cref="ApiKeyScope.LibrarySync"/>) deliberately have a <c>null</c> permission: they are
/// gated by <see cref="DesktopScopeRequirement"/> scope policies and must never translate into
/// permission claims. That is what keeps every pre-existing key (stored as 1, 2, 4 or the frozen
/// aggregate 7) at exactly zero calibration, slicing, and queue authority.
/// </para>
/// </remarks>
public static class DesktopScopePermissionMap
{
    /// <summary>
    /// Metadata for every individually selectable scope flag, in canonical order. The aggregate
    /// <see cref="ApiKeyScope.All"/> and <see cref="ApiKeyScope.None"/> are excluded - every entry
    /// here is a single bit, which is what makes claim expansion alias-proof.
    /// </summary>
    public static IReadOnlyList<DesktopScopeDefinition> Definitions { get; } =
        new ReadOnlyCollection<DesktopScopeDefinition>(
        [
            new(ApiKeyScope.ModelRead, nameof(ApiKeyScope.ModelRead), null, []),
            new(ApiKeyScope.ModelWrite, nameof(ApiKeyScope.ModelWrite), null, []),
            new(ApiKeyScope.LibrarySync, nameof(ApiKeyScope.LibrarySync), null, []),

            new(ApiKeyScope.CalibrationRead, nameof(ApiKeyScope.CalibrationRead),
                PrintFarmerPermissions.Calibration.Read, []),
            new(ApiKeyScope.CalibrationCreate, nameof(ApiKeyScope.CalibrationCreate),
                PrintFarmerPermissions.Calibration.Create, [ApiKeyScope.CalibrationRead]),

            // Round-3 review fix (Bishop B8, issue #2180): completing a calibration project
            // (Active -> Completed) promotes its draft profile via a slicer-module endpoint that
            // is class-gated by slicing:submit in addition to its own method-level
            // calibration:update requirement - so a key holding calibration:update alone would be
            // a validly-issuable combination (calibration:update only implies calibration:read)
            // that nonetheless dead-ends at completion. Mirrors CalibrationGenerate's rationale
            // below: declare the real dependency so GetUnsatisfiedDependencies rejects the
            // dead-end combination at key-creation time instead of failing silently mid-workflow.
            // NOTE: this only guards *new* keys - a key already stored as
            // calibration:read|calibration:update before this fix remains unable to complete a
            // project; such keys must be re-issued with slicing:submit added.
            new(ApiKeyScope.CalibrationUpdate, nameof(ApiKeyScope.CalibrationUpdate),
                PrintFarmerPermissions.Calibration.Update,
                [ApiKeyScope.CalibrationRead, ApiKeyScope.SlicingSubmit]),
            new(ApiKeyScope.CalibrationDelete, nameof(ApiKeyScope.CalibrationDelete),
                PrintFarmerPermissions.Calibration.Delete, [ApiKeyScope.CalibrationRead]),

            // Generation submits a slicing job and then polls calibration orchestration for its
            // outcome; promotion of the produced G-code is server-side. It therefore needs
            // calibration:generate + slicing:submit (and calibration:read for the workspace), but
            // NOT slicing:read-artifact - the desktop client never downloads artifact bytes.
            // SlicingReadArtifact stays independently selectable for clients that genuinely do.
            new(ApiKeyScope.CalibrationGenerate, nameof(ApiKeyScope.CalibrationGenerate),
                PrintFarmerPermissions.Calibration.Generate,
                [ApiKeyScope.CalibrationRead, ApiKeyScope.SlicingSubmit]),
            new(ApiKeyScope.CalibrationPublish, nameof(ApiKeyScope.CalibrationPublish),
                PrintFarmerPermissions.Calibration.Publish, [ApiKeyScope.CalibrationRead]),

            new(ApiKeyScope.SlicingSubmit, nameof(ApiKeyScope.SlicingSubmit),
                PrintFarmerPermissions.Slicing.Submit, []),
            new(ApiKeyScope.SlicingReadArtifact, nameof(ApiKeyScope.SlicingReadArtifact),
                PrintFarmerPermissions.Slicing.ReadArtifact, []),

            new(ApiKeyScope.QueueRead, nameof(ApiKeyScope.QueueRead),
                PrintFarmerPermissions.Queue.Read, []),
            new(ApiKeyScope.QueueWrite, nameof(ApiKeyScope.QueueWrite),
                PrintFarmerPermissions.Queue.Write, [ApiKeyScope.QueueRead]),
            new(ApiKeyScope.QueueStart, nameof(ApiKeyScope.QueueStart),
                PrintFarmerPermissions.Queue.Start, [ApiKeyScope.QueueRead]),
            new(ApiKeyScope.QueueCancel, nameof(ApiKeyScope.QueueCancel),
                PrintFarmerPermissions.Queue.Cancel, [ApiKeyScope.QueueRead]),

            // The bed-clear routes (AutoDispatchController "ready" and "pre-clear") require
            // queue:acknowledge-bed-clear AND queue:start, because acknowledging the bed is what
            // releases the next job. Without QueueStart this scope dead-ends.
            new(ApiKeyScope.QueueAcknowledgeBedClear, nameof(ApiKeyScope.QueueAcknowledgeBedClear),
                PrintFarmerPermissions.Queue.AcknowledgeBedClear,
                [ApiKeyScope.QueueRead, ApiKeyScope.QueueStart]),
        ]);

    private static readonly Dictionary<ApiKeyScope, DesktopScopeDefinition> DefinitionByScope =
        Definitions.ToDictionary(d => d.Scope);

    /// <summary>
    /// Every individually selectable scope flag, in <see cref="Definitions"/> order.
    /// </summary>
    public static IReadOnlyList<ApiKeyScope> AllFlags { get; } =
        new ReadOnlyCollection<ApiKeyScope>([.. Definitions.Select(d => d.Scope)]);

    /// <summary>
    /// Explicit union of every defined flag, used to reject undefined/reserved bits.
    /// </summary>
    /// <remarks>
    /// Intentionally separate from <see cref="ApiKeyScope.All"/>, which is frozen at 7 and means
    /// only the legacy model/library scopes. Validation must use this mask so that widening the
    /// enum never widens the meaning of a stored <c>7</c>.
    /// </remarks>
    public static ApiKeyScope KnownScopeMask { get; } =
        Definitions.Aggregate(ApiKeyScope.None, (mask, d) => mask | d.Scope);

    /// <summary>
    /// Union of every scope flag that translates into a <c>permission</c> claim. These are the
    /// privileged flags: they may only be stored on a Desktop-purpose key whose owner is
    /// independently authorized for the mapped permission.
    /// </summary>
    public static ApiKeyScope PermissionBackedScopes { get; } =
        Definitions
            .Where(d => d.Permission is not null)
            .Aggregate(ApiKeyScope.None, (mask, d) => mask | d.Scope);

    /// <summary>
    /// Maps each permission-backed scope flag to the single <c>permission</c> claim it authorizes.
    /// Scopes absent from this map grant no permission claim at all.
    /// </summary>
    public static IReadOnlyDictionary<ApiKeyScope, string> PermissionByScope { get; } =
        new ReadOnlyDictionary<ApiKeyScope, string>(
            Definitions
                .Where(d => d.Permission is not null)
                .ToDictionary(d => d.Scope, d => d.Permission!));

    /// <summary>
    /// True when <paramref name="scopes"/> sets any bit outside <see cref="KnownScopeMask"/>.
    /// Also catches negative values, whose sign bit is never part of the mask.
    /// </summary>
    public static bool HasUndefinedBits(ApiKeyScope scopes) =>
        (scopes & ~KnownScopeMask) != ApiKeyScope.None;

    /// <summary>
    /// Expands <paramref name="scopes"/> into the individual single-bit flags it sets, in
    /// <see cref="Definitions"/> order.
    /// </summary>
    /// <remarks>
    /// Deliberately iterates <see cref="Definitions"/> (all single-bit) rather than
    /// <c>Enum.GetValues</c>: a composite alias such as <see cref="ApiKeyScope.All"/> would
    /// otherwise satisfy <c>HasFlag</c> and leak into the claim set as a fake scope named "All".
    /// A stored <c>7</c> therefore expands into exactly ModelRead + ModelWrite + LibrarySync.
    /// </remarks>
    public static IReadOnlyList<ApiKeyScope> EnumerateFlags(ApiKeyScope scopes) =>
        [.. Definitions.Where(d => (scopes & d.Scope) == d.Scope).Select(d => d.Scope)];

    /// <summary>
    /// The canonical scope names for <paramref name="scopes"/>, suitable for <c>scope</c> claims
    /// and the <c>scopeNames</c> API contract. Never contains a composite alias.
    /// </summary>
    public static IReadOnlyList<string> GetScopeNames(ApiKeyScope scopes) =>
        [.. Definitions.Where(d => (scopes & d.Scope) == d.Scope).Select(d => d.Name)];

    /// <summary>
    /// The distinct permission claim values that <paramref name="scopes"/> maps to, in
    /// <see cref="Definitions"/> order. Empty for legacy model/library-only keys.
    /// </summary>
    public static IReadOnlyList<string> GetPermissions(ApiKeyScope scopes) =>
        [.. Definitions
            .Where(d => d.Permission is not null && (scopes & d.Scope) == d.Scope)
            .Select(d => d.Permission!)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Parses a canonical scope name (case-insensitive) into its flag. Composite aliases such as
    /// <c>"All"</c> are deliberately <b>not</b> accepted.
    /// </summary>
    public static bool TryParseScopeName(string? name, out ApiKeyScope scope)
    {
        scope = ApiKeyScope.None;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        DesktopScopeDefinition? match = Definitions
            .FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        scope = match.Scope;
        return true;
    }

    /// <summary>
    /// Reports scopes in <paramref name="scopes"/> whose declared prerequisites are not also
    /// selected, so the caller can be told exactly what to add.
    /// </summary>
    /// <returns>
    /// One entry per unsatisfied dependency: the selected scope and the prerequisite it is missing.
    /// Empty when the combination is coherent.
    /// </returns>
    public static IReadOnlyList<(string Scope, string MissingPrerequisite)> GetUnsatisfiedDependencies(
        ApiKeyScope scopes) =>
        [.. Definitions
            .Where(d => (scopes & d.Scope) == d.Scope)
            .SelectMany(d => d.Requires
                .Where(required => (scopes & required) != required)
                .Select(required => (d.Name, DefinitionByScope[required].Name)))];

    /// <summary>
    /// Intersects the key's stored flags with the owner's live authorization, producing the single
    /// effective mask that both scope claims and permission claims are derived from.
    /// </summary>
    /// <param name="storedScopes">The flags explicitly stored on the API key.</param>
    /// <param name="isOwnerFarmAdmin">
    /// Whether the owner currently holds <see cref="PrintFarmerPermissions.FarmAdminRole"/>. An
    /// admin authorizes any explicitly selected mapped permission; the role itself is still never
    /// copied into the issued token.
    /// </param>
    /// <param name="ownerPermissions">The owner's live granted permission values.</param>
    /// <param name="ownerDeniedPermissions">
    /// The owner's explicit denies. Required because a same-resource <c>{resource}:admin</c> grant
    /// would otherwise resurrect an action the operator explicitly denied.
    /// </param>
    /// <remarks>
    /// <para>
    /// Unaffected model/library scopes are always retained, so revoking a calibration role never
    /// breaks a desktop client's model sync - the key only loses the authority that was revoked.
    /// </para>
    /// <para>
    /// Authority is evaluated with <see cref="PrintFarmerPermissions.SetGrantsPermission"/>, so a
    /// same-resource <c>{resource}:admin</c> grant satisfies the mapped permission exactly as it
    /// does at the enforcement points, and an explicit deny suppresses it exactly as it does there.
    /// Without that, an owner holding <c>calibration:admin</c> would lose calibration scopes here
    /// even though PrintFarmer authorizes those actions for them. The implication never crosses
    /// resources, so <c>calibration:admin</c> grants no queue or slicing scope.
    /// </para>
    /// </remarks>
    public static EffectiveDesktopScopes ResolveEffectiveScopes(
        ApiKeyScope storedScopes,
        bool isOwnerFarmAdmin,
        IReadOnlySet<string> ownerPermissions,
        IReadOnlySet<string> ownerDeniedPermissions)
    {
        ArgumentNullException.ThrowIfNull(ownerPermissions);
        ArgumentNullException.ThrowIfNull(ownerDeniedPermissions);

        ApiKeyScope effective = ApiKeyScope.None;
        ApiKeyScope dropped = ApiKeyScope.None;

        foreach (DesktopScopeDefinition definition in Definitions)
        {
            if ((storedScopes & definition.Scope) != definition.Scope)
            {
                continue;
            }

            bool authorized = definition.Permission is null ||
                isOwnerFarmAdmin ||
                PrintFarmerPermissions.SetGrantsPermission(
                    ownerPermissions, ownerDeniedPermissions, definition.Permission);

            if (authorized)
            {
                effective |= definition.Scope;
            }
            else
            {
                dropped |= definition.Scope;
            }
        }

        return new EffectiveDesktopScopes(storedScopes, effective, dropped);
    }
}
