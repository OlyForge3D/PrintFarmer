using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.ShiftPlan;

/// <summary>
/// Cross-pass suppression continuity for <see cref="ShiftPlanCompiler"/> (issue #713,
/// Fix R3-6). <see cref="ShiftPlanCompilerHostedService"/> owns a single instance for
/// the process lifetime and passes it to every <see cref="IShiftPlanCompiler.CompileAsync"/>
/// call. A compiler instance itself is scoped per-tick (a fresh <c>AppDbContext</c> each
/// time, via <c>IServiceScopeFactory.CreateAsyncScope()</c>), so it cannot hold state
/// across passes on its own — this state must live on the singleton hosted service.
/// </summary>
/// <remarks>
/// Without this, suppression was a flat rolling time window: a Skipped/Dismissed task
/// became eligible for re-materialization exactly one hour after the user's action,
/// regardless of whether the underlying source condition was still active. That meant
/// (a) a still-active source resurfaced the same annoyance an hour later, and (b) a
/// source that cleared and re-triggered within the hour stayed wrongly suppressed.
/// Tracking which keys were suppressed on the previous pass — and dropping a key from
/// suppression the moment its source stops producing it for a full successful pass —
/// makes suppression track the actual condition episode instead of a fixed timer.
/// </remarks>
public sealed class ShiftPlanSuppressionState
{
    /// <summary>
    /// UTC start-of-pass timestamp from the previous compile, or <c>null</c> before
    /// the first pass. Used to bootstrap newly-suppressed keys precisely from
    /// "since the last pass" instead of a fixed rolling window.
    /// </summary>
    public DateTime? LastPassAtUtc { get; set; }

    /// <summary>
    /// Source keys currently suppressed — the user Skipped/Dismissed the
    /// corresponding task and the source is still producing the same key.
    /// Mutated in place by the compiler each pass.
    /// </summary>
    public HashSet<(UserTaskSourceKind SourceKind, string SourceId)> SuppressedKeys { get; } = [];

    /// <summary>
    /// Source kinds whose currently-active keys have been seeded from durable
    /// suppression rows since this process started.
    /// </summary>
    private HashSet<UserTaskSourceKind> BootstrappedKinds { get; } = [];

    /// <summary>
    /// Per-key evidence (issue #823) that a specific source key was authoritatively
    /// cleared during this process lifetime — the source proved the key's condition is
    /// gone after the user had Skipped/Dismissed it, so its episode is over.
    /// </summary>
    /// <remarks>
    /// A persistent spec collision on one key of a source kind keeps the entire kind
    /// unbootstrapped, which leaves the per-pass exact-key durable bootstrap permanently
    /// active for every key of that kind. Without this evidence, a genuinely new episode
    /// of an already-cleared key would re-import that key's stale durable Skip/Dismiss row
    /// and be wrongly suppressed. Excluding authoritatively-cleared keys from exact-key
    /// bootstrap keeps suppression conservative without conflating unrelated keys, while
    /// never-observed and colliding keys are still recovered. This is intentionally
    /// in-memory only: a restart resets it, and the durable exact-key bootstrap then
    /// re-establishes fail-closed suppression.
    /// </remarks>
    private HashSet<(UserTaskSourceKind SourceKind, string SourceId)> ClearedKeys { get; } = [];

    /// <summary>
    /// Returns whether durable suppression has been seeded for the source kind.
    /// </summary>
    public bool IsBootstrapped(UserTaskSourceKind sourceKind) => BootstrappedKinds.Contains(sourceKind);

    /// <summary>
    /// Marks a source kind as durably seeded after it successfully evaluates.
    /// </summary>
    public void MarkBootstrapped(UserTaskSourceKind sourceKind)
    {
        _ = BootstrappedKinds.Add(sourceKind);
    }

    /// <summary>
    /// Returns whether the exact source key was authoritatively cleared this process
    /// lifetime and should therefore be excluded from exact-key durable bootstrap.
    /// </summary>
    public bool IsCleared((UserTaskSourceKind SourceKind, string SourceId) key) => ClearedKeys.Contains(key);

    /// <summary>
    /// Records that the exact source key was authoritatively cleared after a prior
    /// Skip/Dismiss. Its stale durable terminal row must not re-suppress a later episode.
    /// </summary>
    public void MarkCleared((UserTaskSourceKind SourceKind, string SourceId) key)
    {
        _ = ClearedKeys.Add(key);
    }

    /// <summary>
    /// Evicts cleared evidence for the exact source key — invoked on a fresh user
    /// dismissal so the new dismissal is honored instead of being treated as a prior,
    /// already-cleared episode.
    /// </summary>
    public void EvictCleared((UserTaskSourceKind SourceKind, string SourceId) key)
    {
        _ = ClearedKeys.Remove(key);
    }
}
