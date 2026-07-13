using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
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

    public async Task<ShiftPlanCompileResult> CompileAsync(CancellationToken ct = default)
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
            IReadOnlyList<ShiftPlanTaskSpec> produced;
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

            foreach (ShiftPlanTaskSpec spec in produced)
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
            stale.Status = UserTaskStatus.Completed;
            stale.CompletedAt = now;
            stale.UpdatedAt = now;
            await _tasks.TrackUpdateAsync(stale, ct).ConfigureAwait(false);
            autoCompleted++;
        }

        await _tasks.SaveChangesAsync(ct).ConfigureAwait(false);

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
            || task.WindowStartUtc != spec.WindowStartUtc
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
        task.WindowStartUtc = spec.WindowStartUtc;
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
}
