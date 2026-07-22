using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.ShiftPlan;

/// <summary>
/// Immutable specification a <see cref="IShiftPlanTaskSource"/> emits for the
/// compiler to materialize into a <see cref="UserTask"/>. Sources describe
/// what should exist; the compiler owns dedupe, upsert, and auto-complete.
/// </summary>
/// <remarks>
/// The pair (<see cref="SourceKind"/>, <see cref="SourceId"/>) MUST be stable
/// across recompiles for the same underlying condition — e.g.
/// <c>runout:{printerId}:toolhead:{n}</c>. The compiler treats a spec vanishing
/// from a source's output as a signal to auto-complete the corresponding task.
/// </remarks>
public sealed record ShiftPlanTaskSpec(
    UserTaskType TaskType,
    UserTaskSourceKind SourceKind,
    string SourceId,
    string Title,
    string? Description,
    UserTaskPriority Priority,
    UserTaskAnchorKind AnchorKind,
    DateTime? AnchorAtUtc,
    DateTime? WindowStartUtc,
    DateTime? WindowEndUtc,
    string EntityType,
    Guid EntityId,
    DateTime? DueAt = null,
    string? MetadataJson = null,
    string? RelatedEntityIdsJson = null);

/// <summary>
/// Complete output from one shift-plan source evaluation.
/// </summary>
/// <param name="Specs">The authoritative specs observed by the source.</param>
/// <param name="OriginWatermark">Oldest proven mutation watermark captured before the required observations.</param>
public sealed record ShiftPlanSourceResult(
    IReadOnlyList<ShiftPlanTaskSpec> Specs,
    long? OriginWatermark) : IReadOnlyList<ShiftPlanTaskSpec>
{
    public int Count => Specs.Count;

    public ShiftPlanTaskSpec this[int index] => Specs[index];

    public IEnumerator<ShiftPlanTaskSpec> GetEnumerator() => Specs.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => GetEnumerator();
}

/// <summary>
/// Pluggable producer of shift-plan task specs. Each source is responsible
/// for one operational concern (attention, filament coverage, maintenance,
/// harvest, restock, …) and returns the full current set of specs it would
/// like materialized. The compiler diffs the union of source outputs against
/// open tasks to compute upserts and auto-completions.
/// </summary>
/// <remarks>
/// Sources SHOULD:
/// - Be safe to invoke concurrently with the rest of the system.
/// - Fail closed: on error, let the exception propagate — the compiler
///   catches it, increments its failure counter, and suppresses auto-complete
///   for this source's <see cref="OwnedKinds"/> in the current pass.
/// - Emit stable <see cref="ShiftPlanTaskSpec.SourceId"/> values; changing the
///   id for the same condition will cause a spurious complete + recreate.
/// </remarks>
public interface IShiftPlanTaskSource
{
    /// <summary>Deterministic name for logs and diagnostics.</summary>
    string SourceName { get; }

    /// <summary>
    /// The set of <see cref="UserTaskSourceKind"/> values this source owns.
    /// The compiler uses this to restrict auto-complete to tasks whose source
    /// kind belongs to a source that completed successfully in the current pass.
    /// Every source must declare at least one owned kind (excluding
    /// <see cref="UserTaskSourceKind.Unspecified"/>).
    /// </summary>
    IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; }

    /// <summary>Returns the specs this source currently wants materialized.</summary>
    Task<ShiftPlanSourceResult> ProduceAsync(CancellationToken ct);
}

/// <summary>
/// Aggregate result of one compiler pass.
/// </summary>
public sealed record ShiftPlanCompileResult(
    int Created,
    int Updated,
    int AutoCompleted,
    int SourceFailures);

/// <summary>
/// Compiles operational task specs from all registered
/// <see cref="IShiftPlanTaskSource"/> instances into <see cref="UserTask"/>
/// rows: creates missing tasks, refreshes anchor/window/description on
/// existing open compiler tasks, and auto-completes any open compiler
/// task whose source no longer emits its spec.
/// </summary>
public interface IShiftPlanCompiler
{
    /// <summary>
    /// Runs a single compile pass.
    /// </summary>
    /// <param name="suppressionState">
    /// Optional cross-pass suppression-continuity state (Fix R3-6). The hosted
    /// service always supplies its owned singleton instance; ad hoc/manual callers
    /// may omit it, in which case suppression falls back to a single bootstrap
    /// query per call. See <see cref="ShiftPlanSuppressionState"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ShiftPlanCompileResult> CompileAsync(ShiftPlanSuppressionState? suppressionState = null, CancellationToken ct = default);
}
