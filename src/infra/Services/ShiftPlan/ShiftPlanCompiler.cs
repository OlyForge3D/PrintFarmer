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
///         window, title, description, and priority in place. Status is left
///         alone so operator-initiated <c>InProgress</c> tasks are not
///         demoted to <c>Pending</c>.</item>
///   <item>Open task whose spec is absent from this pass → auto-complete
///         (Status=Completed, CompletedAt=now, SourceId preserved). Legacy
///         tasks (SourceKind=Unspecified) are never touched by this pass.</item>
/// </list>
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sourceFailures++;
                _logger.LogWarning(ex, "Shift-plan source {Source} failed; skipping", src.SourceName);
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

        // 3) Upserts.
        foreach (KeyValuePair<(UserTaskSourceKind, string), ShiftPlanTaskSpec> kv in specs)
        {
            ct.ThrowIfCancellationRequested();
            ShiftPlanTaskSpec spec = kv.Value;

            if (openByKey.TryGetValue(kv.Key, out UserTask? existing))
            {
                ApplySpec(existing, spec, now, isNew: false);
                await _tasks.UpdateAsync(existing, ct).ConfigureAwait(false);
                updated++;
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
                await _tasks.AddAsync(fresh, ct).ConfigureAwait(false);
                created++;
            }
        }

        // 4) Auto-complete: open compiler tasks whose spec vanished from this pass.
        foreach (KeyValuePair<(UserTaskSourceKind, string), UserTask> kv in openByKey)
        {
            if (specs.ContainsKey(kv.Key))
            {
                continue;
            }

            UserTask stale = kv.Value;
            stale.Status = UserTaskStatus.Completed;
            stale.CompletedAt = now;
            stale.UpdatedAt = now;
            await _tasks.UpdateAsync(stale, ct).ConfigureAwait(false);
            autoCompleted++;
        }

        await _tasks.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Shift-plan compile: +{Created} ~{Updated} ✓{AutoCompleted} sources_failed={Failures}",
            created, updated, autoCompleted, sourceFailures);

        return new ShiftPlanCompileResult(created, updated, autoCompleted, sourceFailures);
    }

    private static void ApplySpec(UserTask task, ShiftPlanTaskSpec spec, DateTime now, bool isNew)
    {
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
    }
}
