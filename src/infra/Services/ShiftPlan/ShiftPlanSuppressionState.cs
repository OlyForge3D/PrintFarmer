using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.ShiftPlan;

/// <summary>
/// Bounded per-key replay memory (issue #823): the highest suppression mutation version ever
/// observed for a source key, plus the injected-clock time it was last observed. It makes the
/// intentionally overlapped suppression-delta reads idempotent — an equal or older row is a
/// replay and must never re-suppress — and, unlike bootstrap-exclusion evidence, it MUST
/// survive <see cref="ShiftPlanSuppressionState.MarkBootstrapped"/> so a clean (collision-free)
/// kind cannot forget the version and treat the next overlapped replay as a fresh dismissal.
/// </summary>
/// <param name="Version">
/// Highest observed <see cref="UserTask.LastMutationSequence"/> for the key. A delta row must be
/// <em>strictly newer</em> than this to count as a genuine new dismissal; an equal/older version
/// is an overlapped-window replay and is ignored.
/// </param>
/// <param name="ObservedAtUtc">
/// Injected-clock time the version was last observed/cleared, used only for age pruning. Aligned
/// with the durable-suppression horizon so the tombstone cannot expire while the durable row it
/// guards against can still appear in an overlapped delta.
/// </param>
public readonly record struct ReplayTombstone(long Version, DateTime ObservedAtUtc);

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
/// <para>
/// Issue #823 layers two <em>separate</em> per-key concepts on top, which must not be
/// conflated (doing so reproduced the bug on the collision-free path):
/// </para>
/// <list type="bullet">
/// <item><b>Bootstrap-exclusion evidence</b> (<see cref="BootstrapExclusions"/>): a key was
/// authoritatively cleared while exact-key durable bootstrap may still run for its kind, so it
/// must be held back from that bootstrap. It is only needed while the kind is unbootstrapped, so
/// <see cref="MarkBootstrapped"/> drops it (bounding growth), and it is also age-pruned.</item>
/// <item><b>Replay-version tombstone</b> (<see cref="ReplayVersions"/>): the highest observed
/// suppression mutation version, kept so an equal/older overlapped delta row stays idempotent
/// even after the kind bootstraps. <see cref="MarkBootstrapped"/> MUST NOT drop it; it is bounded
/// only by injected-clock age pruning aligned with the durable-suppression horizon.</item>
/// </list>
/// </remarks>
public sealed class ShiftPlanSuppressionState
{
    /// <summary>
    /// Version assigned to a suppressed key that carries no explicit mutation version — i.e. a key
    /// present in <see cref="SuppressedKeys"/> without a matching <see cref="SuppressedVersions"/>
    /// entry. Production always records <see cref="UserTask.LastMutationSequence"/> alongside a
    /// suppression, so this only covers versionless/direct-seeded legacy rows. It is treated as the
    /// lowest legitimate durable version (<c>0</c>) rather than <see cref="long.MinValue"/> so that
    /// when such a key is cleared, its replay tombstone floors at <c>0</c>: an equal legacy-<c>0</c>
    /// durable/delta replay is then idempotent (<c>0 &lt;= 0</c>), while a strictly newer dismissal
    /// (version &gt;= 1) still re-suppresses. Using <see cref="long.MinValue"/> here would let a
    /// legacy-<c>0</c> replay read as newer than the tombstone and wrongly re-suppress a genuine
    /// recurrence — the exact #823 failure on the versionless-seed path.
    /// </summary>
    public const long LegacySuppressionVersion = 0L;

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
    /// Dismissal mutation version for each currently-suppressed key (the active suppression
    /// version). Present only while a key is suppressed; the durable replay memory lives in
    /// <see cref="ReplayVersions"/>, which is always kept at least as high as this.
    /// </summary>
    private Dictionary<(UserTaskSourceKind SourceKind, string SourceId), long> SuppressedVersions { get; } = [];

    /// <summary>
    /// Source kinds whose currently-active keys have been seeded from durable
    /// suppression rows since this process started.
    /// </summary>
    private HashSet<UserTaskSourceKind> BootstrappedKinds { get; } = [];

    /// <summary>
    /// Bootstrap-exclusion evidence (issue #823): keys authoritatively cleared while exact-key
    /// durable bootstrap may still run for their kind, mapped to the injected-clock time they
    /// were cleared (for age pruning). Dropped for a kind by <see cref="MarkBootstrapped"/> and
    /// otherwise age-pruned. Excluding these keys from exact-key bootstrap keeps suppression
    /// conservative without conflating unrelated keys, while never-observed and colliding keys
    /// are still recovered.
    /// </summary>
    private Dictionary<(UserTaskSourceKind SourceKind, string SourceId), DateTime> BootstrapExclusions { get; } = [];

    /// <summary>
    /// Replay-version tombstones (issue #823): the highest observed suppression mutation version
    /// per key, retained so the 15-second suppression-delta overlap is idempotent even after the
    /// key's kind bootstraps. Never dropped by <see cref="MarkBootstrapped"/>; bounded only by
    /// injected-clock age pruning aligned with the durable-suppression horizon. In-memory only —
    /// a restart resets it and durable exact-key bootstrap re-establishes fail-closed suppression.
    /// </summary>
    private Dictionary<(UserTaskSourceKind SourceKind, string SourceId), ReplayTombstone> ReplayVersions { get; } = [];

    /// <summary>
    /// Returns whether durable suppression has been seeded for the source kind.
    /// </summary>
    public bool IsBootstrapped(UserTaskSourceKind sourceKind) => BootstrappedKinds.Contains(sourceKind);

    /// <summary>
    /// Marks a source kind as durably seeded after it successfully evaluates. Only the
    /// bootstrap-exclusion evidence for that kind is dropped: once the kind bootstraps, exact-key
    /// durable bootstrap no longer runs for it, so exclusion evidence is no longer needed (bounds
    /// growth). The replay-version tombstones are deliberately retained so a later overlapped
    /// delta replay of an already-observed row stays idempotent on the collision-free path.
    /// </summary>
    public void MarkBootstrapped(UserTaskSourceKind sourceKind)
    {
        _ = BootstrappedKinds.Add(sourceKind);
        List<(UserTaskSourceKind SourceKind, string SourceId)> kindExclusions = BootstrapExclusions.Keys
            .Where(key => key.SourceKind == sourceKind)
            .ToList();
        foreach ((UserTaskSourceKind SourceKind, string SourceId) key in kindExclusions)
        {
            _ = BootstrapExclusions.Remove(key);
        }
    }

    /// <summary>
    /// Returns whether the exact source key is currently held back from exact-key durable
    /// bootstrap because it was authoritatively cleared while its kind is unbootstrapped.
    /// </summary>
    public bool IsExcludedFromBootstrap((UserTaskSourceKind SourceKind, string SourceId) key)
        => BootstrapExclusions.ContainsKey(key);

    /// <summary>
    /// Returns the injected-clock time a key's bootstrap-exclusion evidence was recorded, if any.
    /// Exposed for pruning assertions.
    /// </summary>
    public bool TryGetBootstrapExclusion(
        (UserTaskSourceKind SourceKind, string SourceId) key, out DateTime clearedAtUtc)
        => BootstrapExclusions.TryGetValue(key, out clearedAtUtc);

    /// <summary>Number of live bootstrap-exclusion entries. Exposed for pruning assertions.</summary>
    public int BootstrapExclusionCount => BootstrapExclusions.Count;

    /// <summary>
    /// Returns the replay-version tombstone for a key, if any. Exposed for assertions.
    /// </summary>
    public bool TryGetReplayTombstone(
        (UserTaskSourceKind SourceKind, string SourceId) key, out ReplayTombstone tombstone)
        => ReplayVersions.TryGetValue(key, out tombstone);

    /// <summary>Number of live replay-version tombstones. Exposed for pruning assertions.</summary>
    public int ReplayTombstoneCount => ReplayVersions.Count;

    /// <summary>
    /// Highest suppression mutation version ever observed for the key across the active
    /// suppression version and the replay tombstone, or <see cref="long.MinValue"/> if the key
    /// has never been observed.
    /// </summary>
    private long HighestObservedVersion((UserTaskSourceKind SourceKind, string SourceId) key)
    {
        long highest = long.MinValue;
        if (ReplayVersions.TryGetValue(key, out ReplayTombstone tombstone))
        {
            highest = tombstone.Version;
        }

        if (SuppressedVersions.TryGetValue(key, out long suppressed) && suppressed > highest)
        {
            highest = suppressed;
        }

        return highest;
    }

    /// <summary>
    /// Records that a version was observed for a key at <paramref name="observedAtUtc"/>, keeping
    /// the tombstone at the highest version and never lowering it (so no stale lower bootstrap or
    /// delta version can overwrite a higher one). The observation time only ever advances.
    /// </summary>
    private void RememberVersion(
        (UserTaskSourceKind SourceKind, string SourceId) key, long version, DateTime observedAtUtc)
    {
        if (ReplayVersions.TryGetValue(key, out ReplayTombstone existing))
        {
            long highestVersion = Math.Max(existing.Version, version);
            DateTime latestObserved = observedAtUtc > existing.ObservedAtUtc ? observedAtUtc : existing.ObservedAtUtc;
            ReplayVersions[key] = new ReplayTombstone(highestVersion, latestObserved);
        }
        else
        {
            ReplayVersions[key] = new ReplayTombstone(version, observedAtUtc);
        }
    }

    /// <summary>
    /// Applies a delta-observed Skip/Dismiss row (<paramref name="version"/> =
    /// <see cref="UserTask.LastMutationSequence"/>) observed at <paramref name="observedAtUtc"/>.
    /// Returns <c>true</c> when this is a genuinely newer dismissal than anything already observed
    /// for the key — in which case the key is (re-)suppressed, any bootstrap-exclusion evidence is
    /// evicted, and the replay tombstone advances. Returns <c>false</c> for an overlapped-window
    /// replay (version not strictly newer), leaving all state untouched so the delta overlap is
    /// idempotent.
    /// </summary>
    public bool ObserveDismissal(
        (UserTaskSourceKind SourceKind, string SourceId) key, long version, DateTime observedAtUtc)
    {
        if (version <= HighestObservedVersion(key))
        {
            return false;
        }

        _ = BootstrapExclusions.Remove(key);
        _ = SuppressedKeys.Add(key);
        SuppressedVersions[key] = version;
        RememberVersion(key, version, observedAtUtc);
        return true;
    }

    /// <summary>
    /// Recovers durable suppression for a key seeded by the exact-key bootstrap
    /// (<paramref name="version"/> = the durable row's <see cref="UserTask.LastMutationSequence"/>),
    /// observed at <paramref name="observedAtUtc"/>. Records the version in the replay tombstone
    /// (never lowering a higher one) so a later overlapped delta of the same row is treated as a
    /// replay, and so the correct clear point is captured if the key is subsequently cleared.
    /// </summary>
    public void RecoverSuppression(
        (UserTaskSourceKind SourceKind, string SourceId) key, long version, DateTime observedAtUtc)
    {
        _ = SuppressedKeys.Add(key);
        long existing = SuppressedVersions.TryGetValue(key, out long current) ? current : long.MinValue;
        SuppressedVersions[key] = Math.Max(existing, version);
        RememberVersion(key, version, observedAtUtc);
    }

    /// <summary>
    /// Records that a currently-suppressed key was authoritatively cleared at
    /// <paramref name="clearedAtUtc"/>. The key leaves active suppression, gains bootstrap-exclusion
    /// evidence (held back from exact-key bootstrap while its kind is unbootstrapped), and its
    /// replay tombstone is retained/advanced so a stale durable-row replay cannot re-suppress it
    /// while a genuinely newer dismissal still can.
    /// </summary>
    public void MarkCleared(
        (UserTaskSourceKind SourceKind, string SourceId) key, DateTime clearedAtUtc)
    {
        long version = SuppressedVersions.TryGetValue(key, out long suppressedVersion)
            ? suppressedVersion
            : LegacySuppressionVersion;
        MarkCleared(key, version, clearedAtUtc);
    }

    /// <summary>
    /// Records cleared state with an explicit dismissal <paramref name="version"/> and
    /// <paramref name="clearedAtUtc"/>. The key leaves active suppression, gains bootstrap-exclusion
    /// evidence, and its replay tombstone is retained/advanced.
    /// </summary>
    public void MarkCleared(
        (UserTaskSourceKind SourceKind, string SourceId) key, long version, DateTime clearedAtUtc)
    {
        _ = SuppressedKeys.Remove(key);
        _ = SuppressedVersions.Remove(key);
        BootstrapExclusions[key] = clearedAtUtc;
        RememberVersion(key, version, clearedAtUtc);
    }

    /// <summary>
    /// Prunes bootstrap-exclusion evidence recorded strictly before <paramref name="olderThanUtc"/>.
    /// Bounds growth for a persistently-unbootstrapped (e.g. colliding) kind. Independent of replay
    /// tombstone pruning.
    /// </summary>
    public void PruneBootstrapExclusions(DateTime olderThanUtc)
    {
        List<(UserTaskSourceKind SourceKind, string SourceId)> stale = BootstrapExclusions
            .Where(entry => entry.Value < olderThanUtc)
            .Select(entry => entry.Key)
            .ToList();
        foreach ((UserTaskSourceKind SourceKind, string SourceId) key in stale)
        {
            _ = BootstrapExclusions.Remove(key);
        }
    }

    /// <summary>
    /// Prunes replay-version tombstones last observed strictly before <paramref name="olderThanUtc"/>.
    /// Aligned with the durable-suppression horizon so a tombstone never expires while the durable
    /// row it guards against can still appear in an overlapped delta. Independent of bootstrap-
    /// exclusion pruning, so a bootstrapped kind's replay memory still ages out on its own schedule.
    /// </summary>
    public void PruneReplayTombstones(DateTime olderThanUtc)
    {
        List<(UserTaskSourceKind SourceKind, string SourceId)> stale = ReplayVersions
            .Where(entry => entry.Value.ObservedAtUtc < olderThanUtc)
            .Select(entry => entry.Key)
            .ToList();
        foreach ((UserTaskSourceKind SourceKind, string SourceId) key in stale)
        {
            _ = ReplayVersions.Remove(key);
        }
    }
}
