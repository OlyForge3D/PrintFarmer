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
}
