using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.ShiftPlan;

/// <summary>
/// Default <see cref="IShiftPlanCompiler"/>. Runs every registered
/// <see cref="IShiftPlanTaskSource"/>, then reconciles the union of specs
/// with the current open compiler-owned tasks:
/// <list type="bullet">
///   <item>Missing task → INSERT with the spec's anchor/source fields.</item>
///   <item>Existing open task for same (SourceKind, SourceId) → UPDATE anchor,
///         window, title, description, and priority in place <em>only when a
///         field materially changed</em>. Status is left alone so
///         operator-initiated <c>InProgress</c> tasks are not demoted to
///         <c>Pending</c>.</item>
///   <item>Open task whose spec is absent from this pass AND whose
///         <see cref="UserTask.SourceKind"/> belongs to a source that
///         completed successfully → auto-complete (Status=Completed,
///         CompletedAt=now). Tasks whose source failed this pass are preserved
///         to avoid transient failures completing real tasks.</item>
///   <item>Legacy tasks (SourceKind=Unspecified) are never touched.</item>
/// </list>
/// All adds/updates are batched into a single <c>SaveChangesAsync</c> per pass.
/// </summary>
public sealed class ShiftPlanCompiler : IShiftPlanCompiler
{
    /// <summary>
    /// Fix E: serializes compile passes within a single process so a hosted-service
    /// tick cannot overlap a manually-triggered compile and double-insert the same
    /// source. Static because the compiler is registered per-scope. Cross-process
    /// races (multi-instance deploys) are additionally guarded by the unique filtered
    /// index and the <see cref="DbUpdateException"/> recovery below — a follow-up would
    /// replace this with a distributed lock.
    /// </summary>
    private static readonly SemaphoreSlim CompileGate = new(1, 1);

    /// <summary>
    /// Fix R5-E: ad hoc compiles without a cross-pass state object still need a
    /// defensive suppression query. The hosted service uses the episode-aware active-key
    /// bootstrap below; this long fallback is only for one-off callers that do not carry
    /// <see cref="ShiftPlanSuppressionState"/> between passes.
    /// </summary>
    private static readonly TimeSpan SuppressionBootstrapLookback = TimeSpan.FromDays(7);

    /// <summary>
    /// Durable terminal rows older than this are treated as prior episodes rather than
    /// current suppression. Per-source bootstrap prevents a failed source from losing
    /// recent suppression before it can successfully evaluate.
    /// </summary>
    private static readonly TimeSpan SuppressionBootstrapMaximumAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Fix R4-3: safety overlap subtracted from <c>now</c> when advancing
    /// <see cref="ShiftPlanSuppressionState.LastPassAtUtc"/> at the end of a pass. The
    /// next pass queries suppressed source-keys with <c>UpdatedAt &gt;= LastPassAtUtc</c>;
    /// without an overlap a user Skip/Dismiss whose <c>UpdatedAt</c> was stamped just
    /// before this pass's <c>now</c> but whose transaction committed just after the
    /// suppression query ran would be missed this pass AND next pass (its
    /// <c>UpdatedAt</c> is then below the advanced watermark), letting the compiler
    /// recreate the task the user just dismissed. Overlapping the watermark by 15s — the
    /// compile cadence — absorbs any commit skew up to a full cadence. Re-observed keys
    /// are idempotently absorbed by the suppression <see cref="HashSet{T}"/>, and cleared
    /// episodes are still dropped by the end-of-pass RemoveWhere, so the overlap only
    /// costs a brief, self-healing debounce on flapping conditions.
    /// </summary>
    private static readonly TimeSpan SuppressionWatermarkOverlap = TimeSpan.FromSeconds(15);

    private readonly IEnumerable<IShiftPlanTaskSource> _sources;
    private readonly IUserTaskRepository _tasks;
    private readonly ILogger<ShiftPlanCompiler> _logger;
    private readonly TimeProvider _clock;

    public ShiftPlanCompiler(
        IEnumerable<IShiftPlanTaskSource> sources,
        IUserTaskRepository tasks,
        ILogger<ShiftPlanCompiler> logger,
        TimeProvider? clock = null)
    {
        _sources = sources;
        _tasks = tasks;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ShiftPlanCompileResult> CompileAsync(ShiftPlanSuppressionState? suppressionState = null, CancellationToken ct = default)
    {
        // Fix E: serialize passes within the process to prevent overlapping ticks from
        // both observing "no open task" and inserting duplicates.
        await CompileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CompileCoreAsync(suppressionState, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = CompileGate.Release();
        }
    }

    private async Task<ShiftPlanCompileResult> CompileCoreAsync(ShiftPlanSuppressionState? suppressionState, CancellationToken ct)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        int sourceFailures = 0;

        // Track which source kinds were successfully evaluated this pass.
        // Auto-complete is restricted to tasks in this set.
        HashSet<UserTaskSourceKind> successfulKinds = new();

        // 1) Collect all specs. Isolate per-source failures — the compiler
        //    must not stall if one source throws.
        Dictionary<(UserTaskSourceKind, string), ShiftPlanTaskSpec> specs = new();
        foreach (IShiftPlanTaskSource src in _sources)
        {
            ct.ThrowIfCancellationRequested();
            ShiftPlanSourceResult produced;
            try
            {
                produced = await src.ProduceAsync(ct).ConfigureAwait(false);

                // Source succeeded — mark its owned kinds as successfully evaluated.
                foreach (UserTaskSourceKind kind in src.OwnedKinds)
                {
                    successfulKinds.Add(kind);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sourceFailures++;
                _logger.LogWarning(ex, "Shift-plan source {Source} failed; skipping", src.SourceName);

                // Do NOT add its OwnedKinds to successfulKinds — auto-complete suppressed.
                continue;
            }

            foreach (ShiftPlanTaskSpec spec in produced.Specs)
            {
                if (spec.SourceKind == UserTaskSourceKind.Unspecified || string.IsNullOrWhiteSpace(spec.SourceId))
                {
                    _logger.LogDebug(
                        "Shift-plan source {Source} produced spec with no SourceKind/SourceId — skipped",
                        src.SourceName);
                    continue;
                }

                // Last-write-wins if two sources ever collide on the same key.
                specs[(spec.SourceKind, spec.SourceId)] = spec;
            }
        }

        // 2) Load all currently-open compiler tasks (SourceKind != Unspecified).
        IReadOnlyList<UserTask> openCompilerTasks = await _tasks.GetOpenCompilerTasksAsync(ct).ConfigureAwait(false);
        Dictionary<(UserTaskSourceKind, string), UserTask> openByKey = openCompilerTasks
            .Where(t => !string.IsNullOrEmpty(t.SourceId))
            .GroupBy(t => (t.SourceKind, t.SourceId!))
            .ToDictionary(g => g.Key, g => g.First());

        // Fix F / Fix R3-6: source-episode-aware suppression. A flat rolling window
        // either resurfaces a still-active source's task an hour after the user
        // dismissed it, or wrongly re-suppresses a source that cleared and then
        // genuinely re-triggered within the window. When a caller supplies a
        // <see cref="ShiftPlanSuppressionState"/> (the hosted service always does —
        // see that type's remarks) suppression is tracked precisely across passes.
        // Without one (e.g. an ad hoc/manual compile trigger with no ongoing pass
        // sequence to track), fall back to a single bootstrap query.
        HashSet<(UserTaskSourceKind, string)> suppressed;
        if (suppressionState is not null)
        {
            if (suppressionState.LastPassAtUtc is not null)
            {
                IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)> changedSinceLastPass =
                    await _tasks.GetSuppressedSourceKeysAsync(suppressionState.LastPassAtUtc.Value, ct)
                    .ConfigureAwait(false);
                foreach ((UserTaskSourceKind SourceKind, string SourceId) key in changedSinceLastPass)
                {
                    _ = suppressionState.SuppressedKeys.Add((key.SourceKind, key.SourceId));
                }
            }

            // A source that failed before its first successful pass has no active keys to
            // seed, but must remain unbootstrapped. When it recovers, this exact-key query
            // restores a recent pre-restart Skip/Dismiss before the upsert can recreate it.
            List<UserTaskSourceKind> unbootstrappedKinds = specs.Keys
                .Select(key => key.Item1)
                .Distinct()
                .Where(kind => !suppressionState.IsBootstrapped(kind))
                .ToList();
            foreach (UserTaskSourceKind sourceKind in unbootstrappedKinds)
            {
                IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)> activeKeys = specs.Keys
                    .Where(key => key.Item1 == sourceKind)
                    .Select(key => (key.Item1, key.Item2))
                    .ToList();
                IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)> bootstrapped =
                    await _tasks.GetOpenSuppressedByKeysAsync(
                        activeKeys,
                        maxAgeUtc: now - SuppressionBootstrapMaximumAge,
                        ct: ct).ConfigureAwait(false);
                foreach ((UserTaskSourceKind SourceKind, string SourceId) key in bootstrapped)
                {
                    _ = suppressionState.SuppressedKeys.Add((key.SourceKind, key.SourceId));
                }
            }

            suppressed = suppressionState.SuppressedKeys;
        }
        else
        {
            IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>? suppressedRaw =
                await _tasks.GetSuppressedSourceKeysAsync(now - SuppressionBootstrapLookback, ct).ConfigureAwait(false);
            suppressed = suppressedRaw is null
                ? new()
                : [.. suppressedRaw.Select(k => (k.SourceKind, k.SourceId))];
        }

        int created = 0, updated = 0, autoCompleted = 0;

        // 3) Upserts — batch all changes, save once at the end.
        foreach (KeyValuePair<(UserTaskSourceKind, string), ShiftPlanTaskSpec> kv in specs)
        {
            ct.ThrowIfCancellationRequested();
            ShiftPlanTaskSpec spec = kv.Value;

            if (openByKey.TryGetValue(kv.Key, out UserTask? existing))
            {
                // Fix 3: only write if a material field changed.
                if (ApplySpec(existing, spec, now, isNew: false))
                {
                    await _tasks.TrackUpdateAsync(existing, ct).ConfigureAwait(false);
                    updated++;
                }
            }
            else
            {
                // Fix F: honor a recent user skip/dismiss instead of re-creating.
                if (suppressed.Contains(kv.Key))
                {
                    _logger.LogDebug(
                        "Suppressing re-creation of {Kind}/{SourceId}: user recently skipped/dismissed it",
                        kv.Key.Item1, kv.Key.Item2);
                    continue;
                }

                UserTask fresh = new()
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = now,
                    Status = UserTaskStatus.Pending,
                };
                ApplySpec(fresh, spec, now, isNew: true);
                await _tasks.TrackAddAsync(fresh, ct).ConfigureAwait(false);
                created++;
            }
        }

        // 4) Auto-complete: open compiler tasks whose spec vanished, restricted to
        //    source kinds that completed successfully this pass (Fix 4).
        foreach (KeyValuePair<(UserTaskSourceKind, string), UserTask> kv in openByKey)
        {
            if (specs.ContainsKey(kv.Key))
            {
                continue;
            }

            // Fix 4: if the source that owns this kind failed, preserve the task.
            if (!successfulKinds.Contains(kv.Key.Item1))
            {
                _logger.LogDebug(
                    "Preserving task {TaskId} (source kind {Kind}) because its source failed this pass",
                    kv.Value.Id, kv.Key.Item1);
                continue;
            }

            UserTask stale = kv.Value;

            // Fix R3-5: complete only if the row is still Pending/InProgress at the
            // moment of the write. An unconditional overwrite here would clobber a
            // terminal state (Skipped/Dismissed) a user set concurrently — the DB-level
            // conditional update lets the user's action win the race instead of the
            // compiler blindly stomping it on the next batched SaveChanges.
            bool completedInDb = await _tasks.TryAutoCompleteAsync(stale.Id, now, ct).ConfigureAwait(false);
            if (!completedInDb)
            {
                _logger.LogDebug(
                    "Skipping auto-complete for task {TaskId}: status changed concurrently (a user action won the race)",
                    stale.Id);
                continue;
            }

            stale.Status = UserTaskStatus.Completed;
            stale.CompletedAt = now;
            stale.UpdatedAt = now;

            // The row was already written directly above — detach the tracked entity
            // so the batched SaveChangesAsync below does not redundantly (and
            // unconditionally) re-write the same task, reopening the exact race this
            // fix closes.
            await _tasks.DetachTrackedAsync([stale], ct).ConfigureAwait(false);
            autoCompleted++;
        }

        // Fix R3-6: drop suppression for any key this pass's sources stopped
        // producing (for a source that itself succeeded this pass) — the user's
        // dismissal was honored for that condition's episode; if it recurs later it
        // is a new occurrence, not a resurrection suppressed by a stale rolling window.
        if (suppressionState is not null)
        {
            _ = suppressionState.SuppressedKeys.RemoveWhere(
                key => !specs.ContainsKey(key) && successfulKinds.Contains(key.SourceKind));

            // A successful source with no current keys confirms any old episode cleared,
            // while a successful source with active keys was seeded above. Failed sources
            // deliberately remain unbootstrapped for their eventual recovery pass.
            foreach (UserTaskSourceKind successfulKind in successfulKinds)
            {
                suppressionState.MarkBootstrapped(successfulKind);
            }

            // Fix R4-3: advance the watermark to now MINUS a safety overlap rather than
            // exactly now, so a user Skip/Dismiss committed just after this pass's
            // suppression query (but stamped just before now) is still caught next pass.
            suppressionState.LastPassAtUtc = now - SuppressionWatermarkOverlap;
        }

        // Fix E / Fix R3-2: a concurrent tick on another instance may have inserted the
        // same (SourceKind, SourceId) first, tripping the unique filtered index.
        // Recover gracefully — the next idempotent pass reconciles — instead of
        // crashing the hosted-service tick. Any OTHER DbUpdateException (foreign-key
        // violation, connection reset, etc.) is a genuine failure and must propagate;
        // swallowing it unconditionally (as before) would silently lose data.
        try
        {
            await _tasks.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (EfUserTaskRepository.IsUniqueConstraintViolation(ex))
        {
            _logger.LogWarning(
                ex,
                "Shift-plan compile save hit a unique-index conflict (likely a racing tick); detaching affected tasks and reconciling next pass");

            IEnumerable<UserTask> affected = ex.Entries
                .Select(entry => entry.Entity)
                .OfType<UserTask>();
            await _tasks.DetachTrackedAsync(affected, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Shift-plan compile: +{Created} ~{Updated} ✓{AutoCompleted} sources_failed={Failures}",
            created, updated, autoCompleted, sourceFailures);

        return new ShiftPlanCompileResult(created, updated, autoCompleted, sourceFailures);
    }

    /// <returns><c>true</c> if any material field was mutated; <c>false</c> if the task is unchanged.</returns>
    private static bool ApplySpec(UserTask task, ShiftPlanTaskSpec spec, DateTime now, bool isNew)
    {
        // Fix 3: compare before writing so we do not bump UpdatedAt on unchanged tasks.
        bool changed = isNew
            || task.TaskType != spec.TaskType
            || task.SourceKind != spec.SourceKind
            || task.SourceId != spec.SourceId
            || task.EntityType != spec.EntityType
            || task.EntityId != spec.EntityId
            || task.Title != spec.Title
            || task.Description != spec.Description
            || task.Priority != spec.Priority
            || task.AnchorKind != spec.AnchorKind
            || task.AnchorAtUtc != spec.AnchorAtUtc
            || WindowStartMateriallyChanged(task, spec)
            || task.WindowEndUtc != spec.WindowEndUtc
            || task.DueAt != spec.DueAt
            || (spec.MetadataJson is not null && task.MetadataJson != spec.MetadataJson)
            || (spec.RelatedEntityIdsJson is not null && task.RelatedEntityIdsJson != spec.RelatedEntityIdsJson);

        if (!changed)
        {
            return false;
        }

        task.TaskType = spec.TaskType;
        task.SourceKind = spec.SourceKind;
        task.SourceId = spec.SourceId;
        task.EntityType = spec.EntityType;
        task.EntityId = spec.EntityId;
        task.Title = spec.Title;
        task.Description = spec.Description;
        task.Priority = spec.Priority;
        task.AnchorKind = spec.AnchorKind;
        task.AnchorAtUtc = spec.AnchorAtUtc;

        // Fix R3-7: only rewrite the window start on a genuine episode boundary
        // change, so a continuously-idle printer's start is preserved indefinitely
        // regardless of accumulated wall-clock drift (see WindowStartMateriallyChanged).
        if (isNew || WindowStartMateriallyChanged(task, spec))
        {
            task.WindowStartUtc = spec.WindowStartUtc;
        }

        task.WindowEndUtc = spec.WindowEndUtc;
        task.DueAt = spec.DueAt;

        if (spec.MetadataJson is not null)
        {
            task.MetadataJson = spec.MetadataJson;
        }

        if (spec.RelatedEntityIdsJson is not null)
        {
            task.RelatedEntityIdsJson = spec.RelatedEntityIdsJson;
        }

        task.UpdatedAt = now;

        if (isNew)
        {
            task.CreatedAt = now;
        }

        return true;
    }

    /// <summary>
    /// Fix R3-7: reports a genuine idle-window episode boundary change rather than
    /// wall-clock drift. <see cref="IdleWindowService"/> always anchors an incoming
    /// window's start to a fresh <see cref="DateTime.UtcNow"/>, so a naive drift-
    /// tolerance comparison (the original Fix G) caused the persisted start to be
    /// rewritten every few minutes for a continuously-idle printer, resetting the
    /// displayed episode indefinitely. Instead, the stored start is preserved unless
    /// the window's end boundary itself materially changed (e.g. an open-ended
    /// window became bounded, or vice versa — a real transition, not drift) or the
    /// incoming start is earlier than the stored one (defensive; should not occur
    /// since sources anchor to an advancing clock, but an earlier start can only mean
    /// a genuinely new episode).
    /// </summary>
    private static bool WindowStartMateriallyChanged(UserTask task, ShiftPlanTaskSpec spec)
    {
        DateTime? existing = task.WindowStartUtc;
        DateTime? incoming = spec.WindowStartUtc;

        if (existing is null && incoming is null)
        {
            return false;
        }

        if (existing is null || incoming is null)
        {
            return true;
        }

        if (task.WindowEndUtc != spec.WindowEndUtc)
        {
            return true;
        }

        return incoming.Value < existing.Value;
    }
}
