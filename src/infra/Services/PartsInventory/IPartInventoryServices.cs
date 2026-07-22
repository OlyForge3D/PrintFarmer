using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;

namespace Farm.Infrastructure.Services.PartsInventory;

/// <summary>
/// Outcomes returned by <see cref="IPartInventoryService.AdjustAsync"/>
/// and <see cref="IPartHarvestService.HarvestJobAsync"/>. The concrete
/// controller maps these to HTTP status codes.
/// </summary>
public enum PartInventoryOutcome
{
    Ok = 0,
    PartNotFound = 1,
    BinNotFound = 2,
    JobNotFound = 3,
    JobNotCompleted = 4,
    NoMappings = 5,
    InvalidRequest = 6,
    Conflict = 7,
    IdempotentReplay = 8,
    SkuAlreadyExists = 9,
    WrongBin = 10,
    FeatureDisabled = 11,
}

/// <summary>Result of a printed-part stock adjustment.</summary>
public record AdjustResult(
    PartInventoryOutcome Outcome,
    PartAdjustmentResponse? Adjustment,
    int NewOnHand,
    string? Message);

/// <summary>Result of a harvest attempt.</summary>
public record HarvestResult(
    PartInventoryOutcome Outcome,
    HarvestJobResponse? Response,
    string? Message,
    WrongBinResponse? WrongBin = null,
    PartMappingRequiredResponse? MappingRequired = null);

/// <summary>Result of a printed-part SKU creation attempt.</summary>
public record CreatePartResult(
    PartInventoryOutcome Outcome,
    PartInventory? Part,
    string? Message);

/// <summary>
/// Adjust request as consumed by <see cref="IPartInventoryService"/>.
/// <para>
/// Operation-key idempotency arrives on two distinct channels (issue #715, Hicks r3 blocker 2).
/// <see cref="OperationKey"/> is the <b>client-supplied</b> value and is policed: a value in the
/// reserved <c>idem:</c> namespace
/// (<see cref="Farm.Infrastructure.Services.Idempotency.IdempotencyKeyUtilities.SynthesizedOperationKeyPrefix"/>)
/// is rejected so a crafted client key can never collide with a server-synthesized key and
/// silently dedup a distinct mutation. <see cref="SynthesizedOperationKey"/> is the <b>trusted</b>
/// value the idempotency filter synthesizes when the client omitted its own key; it legitimately
/// uses the reserved prefix and is honored only when <see cref="OperationKey"/> is absent. Keeping
/// them on separate fields lets the service reserve the prefix without breaking the synthesized
/// backstop.
/// </para>
/// </summary>
public record AdjustCommand(
    int Delta,
    PartAdjustmentReason Reason,
    Guid? PrintJobId,
    string? BinCode,
    string? Notes,
    string? OperationKey,
    string? UserId,
    string? SynthesizedOperationKey = null);

/// <summary>
/// Command to atomically create a new printed-part SKU and (optionally) seed
/// an initial-stock ledger entry inside the same database transaction.
/// </summary>
public record CreatePartCommand(
    string Sku,
    string Name,
    string? Description,
    string? ModelFileRef,
    string? DefaultBinCode,
    int InitialOnHand,
    int ReorderPoint,
    string? UserId);

/// <summary>
/// Service that owns printed-part SKU stock arithmetic and the immutable
/// ledger. All writes happen inside a single database transaction (when the
/// provider is relational) so <see cref="PartInventory.OnHand"/> and the
/// ledger cannot diverge, even under concurrent writers. Duplicate operation
/// keys collide on the composite <c>(PartInventoryId, OperationKey)</c> unique
/// index and are surfaced as <see cref="PartInventoryOutcome.IdempotentReplay"/>
/// with the committed prior state, never as a stale in-memory value.
/// </summary>
public interface IPartInventoryService
{
    Task<AdjustResult> AdjustAsync(string sku, AdjustCommand command, CancellationToken ct = default);

    /// <summary>
    /// Atomically inserts a SKU row and (when <c>InitialOnHand &gt; 0</c>) an
    /// manual initial-stock ledger entry inside a single transaction, so either both
    /// rows commit or neither does. Returns the committed SKU.
    /// </summary>
    Task<CreatePartResult> CreatePartAsync(CreatePartCommand command, CancellationToken ct = default);
}

/// <summary>Service that atomically harvests a completed print job into printed-part stock.</summary>
public interface IPartHarvestService
{
    Task<HarvestResult> HarvestJobAsync(Guid jobId, HarvestJobRequest request, string? userId, CancellationToken ct = default);
}

/// <summary>
/// Seam for the F8 (#713) shift compiler that lists SKUs currently
/// at or below their reorder points.
/// </summary>
public interface IReorderEvaluationService
{
    Task<IReadOnlyList<ReorderCandidateResponse>> GetReorderCandidatesAsync(CancellationToken ct = default);
}
