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
    /// Asynchronous, cancellation-aware equivalent of <see cref="IsEnabled(OperatorFeature)"/>.
    ///
    /// <para>
    /// Callers that run inside <see cref="System.Threading.SemaphoreSlim"/>-serialised
    /// critical sections, hold in-memory locks (e.g., the native-push dispatcher's
    /// transport-start lock, the delivery-lifecycle lock, or a request-scoped
    /// pipeline that cannot block a thread-pool worker on database I/O) MUST use this
    /// method rather than <see cref="IsEnabled(OperatorFeature)"/>. The synchronous
    /// overload blocks on the underlying async repository read via
    /// <see cref="System.Runtime.CompilerServices.TaskAwaiter.GetResult"/>, so under a
    /// lock it can pin the caller's thread on an unbounded EF/SQL round-trip and
    /// prevent cancellation from reaching later checks.
    /// </para>
    ///
    /// <para>
    /// Failure semantics differ from the synchronous overload: DB/repository errors
    /// are <b>not</b> swallowed here. Callers (typically dispatchers) MUST catch and
    /// log fail-closed at the same boundary they roll back any reservations, so
    /// operational log lines carry the specific delivery/lifecycle context. A
    /// malformed persisted JSON row still degrades to
    /// <see cref="Farm.Infrastructure.Settings.OperatorFeatureSettings"/> defaults
    /// (mirroring the sync path) — that is a data-shape failure, not an infrastructure
    /// outage, and the capability endpoint's contract still requires a stable read.
    /// </para>
    /// </summary>
    /// <param name="feature">The operator feature to resolve.</param>
    /// <param name="cancellationToken">Cancellation propagated from the caller's dispatch/send scope.</param>
    Task<bool> IsEnabledAsync(OperatorFeature feature, CancellationToken cancellationToken = default);

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
