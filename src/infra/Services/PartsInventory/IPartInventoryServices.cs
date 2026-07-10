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
    string? Message);

/// <summary>Adjust request as consumed by <see cref="IPartInventoryService"/>.</summary>
public record AdjustCommand(
    int Delta,
    PartAdjustmentReason Reason,
    Guid? PrintJobId,
    string? BinCode,
    string? Notes,
    string? OperationKey,
    string? UserId);

/// <summary>
/// Service that owns printed-part SKU stock arithmetic and the immutable
/// ledger. All writes happen inside a single database transaction (when the
/// provider is relational) so <see cref="PartInventory.OnHand"/> and the
/// ledger cannot diverge, even under concurrent writers.
/// </summary>
public interface IPartInventoryService
{
    Task<AdjustResult> AdjustAsync(string sku, AdjustCommand command, CancellationToken ct = default);
}

/// <summary>Service that atomically harvests a completed print job into printed-part stock.</summary>
public interface IPartHarvestService
{
    Task<HarvestResult> HarvestJobAsync(Guid jobId, HarvestJobRequest request, string? userId, CancellationToken ct = default);
}

/// <summary>
/// Seam for the F8 (#713) shift compiler that lists SKUs currently
/// below their reorder points.
/// </summary>
public interface IReorderEvaluationService
{
    Task<IReadOnlyList<ReorderCandidateResponse>> GetReorderCandidatesAsync(CancellationToken ct = default);
}
