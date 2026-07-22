using System.Globalization;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;

namespace Farm.Infrastructure.Services.ShiftPlan.Sources;

/// <summary>
/// Typed metadata for a printed-part restock task. Identity is always carried by
/// <see cref="PartInventoryId"/> rather than inferred from mutable display text.
/// </summary>
public sealed record PrintedPartRestockTaskMetadata(
    Guid PartInventoryId,
    string Sku,
    string Name,
    int OnHand,
    int ReorderPoint,
    int Deficit,
    Guid? DefaultBinId,
    string? DefaultBinCode,
    string? DefaultBinName);

/// <summary>
/// Projects authoritative printed-part reorder candidates into shift-plan tasks.
/// A pass is authoritative only when both feature gates remain enabled before and
/// after the complete reorder query has been materialized.
/// </summary>
public sealed class PrintedPartReorderShiftPlanTaskSource(
    IReorderEvaluationService reorderEvaluation,
    IOperatorFeatureGate featureGate) : IShiftPlanTaskSource
{
    private const string SourceIdPrefix = "partinventory:";

    // Mirrors UserTask.Title [MaxLength(200)] so a part name at the 200-char database limit
    // never overflows the Title column after the "Restock " prefix is prepended.
    private const int TitleMaxLength = 200;
    private const string TitlePrefix = "Restock ";

    /// <inheritdoc />
    public string SourceName => "printed-part-reorder";

    /// <inheritdoc />
    public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } =
        [UserTaskSourceKind.PrintedPartStock];

    /// <inheritdoc />
    public async Task<IReadOnlyList<ShiftPlanTaskSpec>> ProduceAsync(CancellationToken ct)
    {
        await EnsureFeaturesEnabledAsync(ct).ConfigureAwait(false);

        IReadOnlyList<ReorderCandidateResponse> candidates = await reorderEvaluation
            .GetReorderCandidatesAsync(ct)
            .ConfigureAwait(false);

        HashSet<Guid> seenInventoryIds = [];
        List<ShiftPlanTaskSpec> specs = new(candidates.Count);
        foreach (ReorderCandidateResponse candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!seenInventoryIds.Add(candidate.PartInventoryId))
            {
                throw new InvalidOperationException(
                    $"Reorder evaluation returned duplicate inventory id {candidate.PartInventoryId:D}.");
            }

            specs.Add(ToSpec(candidate));
        }

        // Re-read both persisted gates after the query. A true -> false transition
        // during evaluation is incomplete, not an authoritative empty/partial stock
        // snapshot that may resolve or refresh tasks.
        await EnsureFeaturesEnabledAsync(ct).ConfigureAwait(false);

        return specs
            .OrderBy(spec => spec.SourceId, StringComparer.Ordinal)
            .ToList();
    }

    private async Task EnsureFeaturesEnabledAsync(CancellationToken ct)
    {
        bool shiftPlanEnabled = await featureGate
            .IsEnabledStrictAsync(OperatorFeature.ShiftPlan, ct)
            .ConfigureAwait(false);
        bool inventoryEnabled = await featureGate
            .IsEnabledStrictAsync(OperatorFeature.PrintedPartsInventory, ct)
            .ConfigureAwait(false);

        if (!shiftPlanEnabled || !inventoryEnabled)
        {
            throw new InvalidOperationException(
                "Printed-part reorder evaluation is incomplete because a required operator feature is disabled.");
        }
    }

    private static ShiftPlanTaskSpec ToSpec(ReorderCandidateResponse candidate)
    {
        PrintedPartRestockTaskMetadata metadata = new(
            candidate.PartInventoryId,
            candidate.Sku,
            candidate.Name,
            candidate.OnHand,
            candidate.ReorderPoint,
            candidate.Deficit,
            candidate.DefaultBinId,
            candidate.DefaultBinCode,
            candidate.DefaultBinName);

        string description = string.Create(
            CultureInfo.InvariantCulture,
            $"{candidate.Sku}: {candidate.OnHand} on hand, reorder point {candidate.ReorderPoint}, deficit {candidate.Deficit}.");

        int maxNameLength = TitleMaxLength - TitlePrefix.Length;
        string title = candidate.Name.Length > maxNameLength
            ? $"{TitlePrefix}{candidate.Name[..maxNameLength]}"
            : $"{TitlePrefix}{candidate.Name}";

        return new ShiftPlanTaskSpec(
            TaskType: UserTaskType.PrintedPartRestock,
            SourceKind: UserTaskSourceKind.PrintedPartStock,
            SourceId: $"{SourceIdPrefix}{candidate.PartInventoryId:N}",
            Title: title,
            Description: description,
            Priority: UserTaskPriority.Normal,
            AnchorKind: UserTaskAnchorKind.AnytimeToday,
            AnchorAtUtc: null,
            WindowStartUtc: null,
            WindowEndUtc: null,
            EntityType: nameof(PartInventory),
            EntityId: candidate.PartInventoryId,
            DueAt: null,
            MetadataJson: JsonSerializer.Serialize(metadata, JsonSerializerOptions.Web));
    }
}
