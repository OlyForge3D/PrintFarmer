using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.OperatorFeatures;

/// <summary>
/// Resolves operator feature flags from persisted settings, with a hard-disable environment
/// override for emergency rollback. See <c>docs/OPERATOR_FEATURE_GATES.md</c> and issue #725.
///
/// Resolution order per feature:
/// <list type="number">
///   <item>If the ASP.NET configuration key <c>OperatorFeatures:&lt;flagName&gt;</c> resolves to
///     an explicit <c>false</c>, the feature is disabled regardless of the database value.</item>
///   <item>Otherwise, the persisted row from the AppSettings table under key
///     <c>OperatorFeatures</c> is deserialized as
///     <see cref="Farm.Infrastructure.Settings.OperatorFeatureSettings"/>. If no row exists
///     yet, the property defaults on that class apply.</item>
/// </list>
///
/// Absent or explicitly <c>true</c> environment values do NOT force-enable a feature; the
/// <c>OperatorFeatures</c> configuration section is intentionally never bound as the base
/// value. When persisted-settings acquisition fails (DB down, malformed row), the gate
/// logs and falls back to the documented defaults so the capability endpoint keeps working.
///
/// Implementations are registered <b>scoped</b>. Singleton consumers (e.g. hosted services)
/// must inject <c>IServiceScopeFactory</c> and resolve the gate inside a per-tick scope
/// rather than caching a gate instance.
/// </summary>
public interface IOperatorFeatureGate
{
    /// <summary>Returns the effective flags snapshot after applying environment overrides.</summary>
    OperatorFeatureFlagsDto GetEffectiveFlags();

    /// <summary>Returns whether a specific operator feature is enabled for the current request.</summary>
    bool IsEnabled(OperatorFeature feature);

    /// <summary>
    /// Asynchronous, cancellation-aware equivalent of <see cref="IsEnabled(OperatorFeature)"/>
    /// for <b>general</b> callers (controllers, filters, hosted services, and domain
    /// services that merely gate a capability).
    ///
    /// <para>
    /// Callers that run inside <see cref="System.Threading.SemaphoreSlim"/>-serialised
    /// critical sections or a request-scoped pipeline that cannot block a thread-pool
    /// worker on database I/O should prefer this method over
    /// <see cref="IsEnabled(OperatorFeature)"/>. The synchronous overload blocks on the
    /// underlying async repository read via
    /// <see cref="System.Runtime.CompilerServices.TaskAwaiter.GetResult"/>, so under a
    /// lock it can pin the caller's thread on an unbounded EF/SQL round-trip and
    /// prevent cancellation from reaching later checks.
    /// </para>
    ///
    /// <para>
    /// <b>Failure semantics mirror the synchronous overload:</b> a repository/DB error
    /// (DB down, provider startup race, missing table) is logged and degraded to the
    /// documented configured/default result, so a general migrated caller keeps the
    /// pre-migration behaviour and never turns a transient infrastructure outage into
    /// an HTTP 500. A malformed persisted JSON row likewise degrades to
    /// <see cref="Farm.Infrastructure.Settings.OperatorFeatureSettings"/> defaults. The
    /// explicit-<c>false</c> environment hard-disable override still applies in the
    /// degraded path so on-call rollback works even when the DB itself is the incident.
    /// </para>
    ///
    /// <para>
    /// Caller-requested cancellation is <b>never</b> swallowed: when
    /// <paramref name="cancellationToken"/> is cancelled the resulting
    /// <see cref="System.OperationCanceledException"/> propagates instead of falling
    /// back, so a shutdown/abort is observed as control flow rather than a spurious
    /// "feature enabled/disabled" answer.
    /// </para>
    ///
    /// <para>
    /// Security/correctness boundaries that must NOT silently authorize on a DB failure
    /// (for example the native-push transport reservation/authorization path) MUST use
    /// <see cref="IsEnabledStrictAsync(OperatorFeature, CancellationToken)"/> instead.
    /// </para>
    /// </summary>
    /// <param name="feature">The operator feature to resolve.</param>
    /// <param name="cancellationToken">Cancellation propagated from the caller's request/operation scope.</param>
    Task<bool> IsEnabledAsync(OperatorFeature feature, CancellationToken cancellationToken = default);

    /// <summary>
    /// Strict, fail-closed, cancellation-aware feature resolution for security/correctness
    /// boundaries — currently the native-push transport reservation/authorization path.
    ///
    /// <para>
    /// Unlike <see cref="IsEnabledAsync(OperatorFeature, CancellationToken)"/>, a
    /// repository/DB failure is <b>not</b> swallowed: it propagates so the caller can
    /// fail closed and roll back any reservation at the exact boundary that logs the
    /// delivery/lifecycle context (the gate itself does not know which envelope caused
    /// the read). This prevents an outage from silently authorizing a send when a
    /// feature's default happens to be enabled, and keeps the dispatcher's existing
    /// catch/rollback semantics explicit.
    /// </para>
    ///
    /// <para>
    /// A malformed persisted JSON row still degrades to
    /// <see cref="Farm.Infrastructure.Settings.OperatorFeatureSettings"/> defaults
    /// (mirroring the general and sync paths): that is a data-shape issue, not an
    /// infrastructure outage, and the caller can still enforce fail-closed policy
    /// downstream. The explicit-<c>false</c> environment hard-disable override applies
    /// as usual. Caller-requested cancellation propagates as an
    /// <see cref="System.OperationCanceledException"/>.
    /// </para>
    /// </summary>
    /// <param name="feature">The operator feature to resolve.</param>
    /// <param name="cancellationToken">Cancellation propagated from the caller's dispatch/send scope.</param>
    Task<bool> IsEnabledStrictAsync(OperatorFeature feature, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether the given feature is hard-disabled by the environment (an explicit
    /// <c>OperatorFeatures__&lt;flagName&gt;=false</c>). Used by admin/UI surfaces to indicate
    /// that changing the database value alone will not re-enable the feature.
    /// </summary>
    bool IsHardDisabledByEnvironment(OperatorFeature feature);

    /// <summary>
    /// Returns the canonical camelCase flag name (as used on the wire) for a feature.
    /// </summary>
    string GetFlagName(OperatorFeature feature);

    /// <summary>Enumerates all known features with their canonical camelCase flag names.</summary>
    IReadOnlyList<(OperatorFeature Feature, string FlagName)> AllFeatures { get; }
}
