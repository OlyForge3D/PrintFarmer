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
/// Per-kind evidence that absence may resolve tasks produced by a shift-plan source.
/// </summary>
/// <param name="SourceKind">The owned source kind covered by this evidence.</param>
/// <param name="IsAuthoritativeComplete">
/// Whether the source completely observed the kind for this evaluation.
/// </param>
/// <param name="PreservedSourceIds">
/// Specific absent source ids that remain indeterminate even when the rest of the kind is complete.
/// </param>
/// <param name="IncompleteReasons">Stable diagnostic reasons when the kind is incomplete.</param>
public sealed record ShiftPlanKindAuthority(
    UserTaskSourceKind SourceKind,
    bool IsAuthoritativeComplete,
    IReadOnlySet<string> PreservedSourceIds,
    IReadOnlyList<string> IncompleteReasons);

/// <summary>
/// Optional authoritative-absence evidence produced alongside a source result.
/// </summary>
/// <param name="Kinds">Per-kind evidence for kinds owned by the producing source.</param>
public sealed record ShiftPlanSourceAuthority(IReadOnlyList<ShiftPlanKindAuthority> Kinds);

/// <summary>
/// Complete output from one shift-plan source evaluation.
/// </summary>
/// <param name="Specs">Known positive specs observed by the source.</param>
/// <param name="OriginWatermark">Oldest proven mutation watermark captured before the required observations.</param>
public sealed record ShiftPlanSourceResult(
    IReadOnlyList<ShiftPlanTaskSpec> Specs,
    long? OriginWatermark) : IReadOnlyList<ShiftPlanTaskSpec>
{
    /// <summary>
    /// Optional negative-inference evidence. Missing evidence is positive-only and cannot
    /// authorize completion from an absent spec.
    /// </summary>
    public ShiftPlanSourceAuthority? Authority { get; init; }

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
/// - Fail closed: on error, let the exception propagate. The compiler catches it,
///   increments its failure counter, and applies no absence authority for the source.
/// - Emit stable <see cref="ShiftPlanTaskSpec.SourceId"/> values; changing the
///   id for the same condition will cause a spurious complete + recreate.
/// </remarks>
public interface IShiftPlanTaskSource
{
    /// <summary>Deterministic name for logs and diagnostics.</summary>
    string SourceName { get; }

    /// <summary>
    /// The set of <see cref="UserTaskSourceKind"/> values this source owns.
    /// The compiler uses ownership together with <see cref="ShiftPlanSourceResult.Authority"/>
    /// to restrict auto-complete to uniquely owned, authoritatively observed tasks.
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
