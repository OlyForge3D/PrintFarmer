using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.ShiftPlan;

/// <summary>
/// Per-key evidence (issue #823) that a specific source key was authoritatively cleared,
/// capturing the dismissal mutation version that was cleared and the pass clock time it was
/// cleared at (for deterministic age pruning).
/// </summary>
/// <param name="Version">
/// The <see cref="UserTask.LastMutationSequence"/> of the Skip/Dismiss that was cleared. A
/// later delta row must be <em>strictly newer</em> than this to count as a genuine new
/// dismissal; an equal version is an overlapped-window replay and is ignored.
/// </param>
/// <param name="ClearedAtUtc">Pass clock time the key was cleared, used for pruning.</param>
public readonly record struct ClearedEvidence(long Version, DateTime ClearedAtUtc);

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
    /// Dismissal mutation version for each currently-suppressed key. Disjoint from
    /// <see cref="ClearedKeys"/>: a key is either suppressed (here) or cleared (there).
    /// Used to make overlapped suppression-delta reads idempotent.
    /// </summary>
    private Dictionary<(UserTaskSourceKind SourceKind, string SourceId), long> SuppressedVersions { get; } = [];

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
    /// never-observed and colliding keys are still recovered.
    /// <para>
    /// The stored <see cref="ClearedEvidence.Version"/> makes the 15-second suppression-delta
    /// overlap idempotent: a replay of the same durable row (equal version) leaves the cleared
    /// evidence intact, while a strictly-newer Skip/Dismiss evicts it and re-suppresses. The
    /// evidence is intentionally in-memory only: a restart resets it and the durable exact-key
    /// bootstrap re-establishes fail-closed suppression. Growth is bounded — evidence for a
    /// kind is dropped once it bootstraps, and stale evidence is pruned against the same
    /// durable-suppression horizon.
    /// </para>
    /// </remarks>
    private Dictionary<(UserTaskSourceKind SourceKind, string SourceId), ClearedEvidence> ClearedKeys { get; } = [];

    /// <summary>
    /// Returns whether durable suppression has been seeded for the source kind.
    /// </summary>
    public bool IsBootstrapped(UserTaskSourceKind sourceKind) => BootstrappedKinds.Contains(sourceKind);

    /// <summary>
    /// Marks a source kind as durably seeded after it successfully evaluates. Cleared
    /// evidence for that kind is dropped: once the kind bootstraps, exact-key durable
    /// bootstrap no longer runs for it, so the evidence is no longer needed (bounds growth).
    /// </summary>
    public void MarkBootstrapped(UserTaskSourceKind sourceKind)
    {
        _ = BootstrappedKinds.Add(sourceKind);
        List<(UserTaskSourceKind SourceKind, string SourceId)> kindEvidence = ClearedKeys.Keys
            .Where(key => key.SourceKind == sourceKind)
            .ToList();
        foreach ((UserTaskSourceKind SourceKind, string SourceId) key in kindEvidence)
        {
            _ = ClearedKeys.Remove(key);
        }
    }

    /// <summary>
    /// Returns whether the exact source key was authoritatively cleared this process
    /// lifetime and should therefore be excluded from exact-key durable bootstrap.
    /// </summary>
    public bool IsCleared((UserTaskSourceKind SourceKind, string SourceId) key) => ClearedKeys.ContainsKey(key);

    /// <summary>
    /// Returns the cleared-episode evidence for a key, if any. Exposed for assertions.
    /// </summary>
    public bool TryGetClearedEvidence(
        (UserTaskSourceKind SourceKind, string SourceId) key, out ClearedEvidence evidence)
        => ClearedKeys.TryGetValue(key, out evidence);

    /// <summary>
    /// Number of keys with live cleared evidence. Exposed for pruning assertions.
    /// </summary>
    public int ClearedEvidenceCount => ClearedKeys.Count;

    /// <summary>
    /// Applies a delta-observed Skip/Dismiss row (<paramref name="version"/> =
    /// <see cref="UserTask.LastMutationSequence"/>). Returns <c>true</c> when this is a
    /// genuinely newer dismissal than anything already processed or cleared for the key — in
    /// which case the key is (re-)suppressed and any cleared evidence is evicted. Returns
    /// <c>false</c> for an overlapped-window replay (version not strictly newer), leaving
    /// suppression and cleared evidence untouched so the 15-second delta overlap is idempotent.
    /// </summary>
    public bool ObserveDismissal((UserTaskSourceKind SourceKind, string SourceId) key, long version)
    {
        long observed = long.MinValue;
        if (SuppressedVersions.TryGetValue(key, out long suppressedVersion))
        {
            observed = suppressedVersion;
        }
        else if (ClearedKeys.TryGetValue(key, out ClearedEvidence evidence))
        {
            observed = evidence.Version;
        }

        if (version <= observed)
        {
            return false;
        }

        _ = ClearedKeys.Remove(key);
        _ = SuppressedKeys.Add(key);
        SuppressedVersions[key] = version;
        return true;
    }

    /// <summary>
    /// Recovers durable suppression for a key seeded by the exact-key bootstrap
    /// (<paramref name="version"/> = the durable row's <see cref="UserTask.LastMutationSequence"/>).
    /// Records the version so a later overlapped delta of the same row is treated as a replay,
    /// and so the correct clear point is captured if the key is subsequently cleared.
    /// </summary>
    public void RecoverSuppression((UserTaskSourceKind SourceKind, string SourceId) key, long version)
    {
        _ = SuppressedKeys.Add(key);
        long existing = SuppressedVersions.TryGetValue(key, out long current) ? current : long.MinValue;
        SuppressedVersions[key] = Math.Max(existing, version);
    }

    /// <summary>
    /// Records that a currently-suppressed key was authoritatively cleared at
    /// <paramref name="clearedAtUtc"/>, capturing its dismissal version so a stale durable row
    /// replay cannot re-suppress the key while a genuinely newer dismissal still can.
    /// </summary>
    public void MarkCleared(
        (UserTaskSourceKind SourceKind, string SourceId) key, DateTime clearedAtUtc)
    {
        long version = SuppressedVersions.TryGetValue(key, out long suppressedVersion)
            ? suppressedVersion
            : long.MinValue;
        MarkCleared(key, version, clearedAtUtc);
    }

    /// <summary>
    /// Records cleared evidence with an explicit dismissal <paramref name="version"/> and
    /// <paramref name="clearedAtUtc"/>. The key leaves the suppressed set.
    /// </summary>
    public void MarkCleared(
        (UserTaskSourceKind SourceKind, string SourceId) key, long version, DateTime clearedAtUtc)
    {
        _ = SuppressedKeys.Remove(key);
        _ = SuppressedVersions.Remove(key);
        ClearedKeys[key] = new ClearedEvidence(version, clearedAtUtc);
    }

    /// <summary>
    /// Prunes cleared evidence last cleared strictly before <paramref name="olderThanUtc"/>.
    /// Aligned with the durable-suppression bootstrap horizon so evidence never outlives the
    /// terminal rows it guards against, bounding growth under persistent collisions.
    /// </summary>
    public void PruneClearedEvidence(DateTime olderThanUtc)
    {
        List<(UserTaskSourceKind SourceKind, string SourceId)> stale = ClearedKeys
            .Where(entry => entry.Value.ClearedAtUtc < olderThanUtc)
            .Select(entry => entry.Key)
            .ToList();
        foreach ((UserTaskSourceKind SourceKind, string SourceId) key in stale)
        {
            _ = ClearedKeys.Remove(key);
        }
    }
}
