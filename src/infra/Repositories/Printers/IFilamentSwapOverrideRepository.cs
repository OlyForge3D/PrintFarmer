using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Printers;

/// <summary>
/// Persistence for <see cref="FilamentSwapOverride"/> forensic audit records (issue #710, B6).
/// </summary>
/// <remarks>
/// Exposed through the shared <c>IUnitOfWork</c> so an override audit insert can share the
/// same DbContext / transaction as the spool binding and commit atomically.
/// </remarks>
public interface IFilamentSwapOverrideRepository
{
    /// <summary>
    /// Stages an override audit record for insertion. The row is not written until the
    /// owning unit of work's <c>SaveChangesAsync</c> runs, keeping binding + audit atomic.
    /// </summary>
    /// <param name="auditRecord">The audit record to persist.</param>
    void Add(FilamentSwapOverride auditRecord);

    /// <summary>
    /// Returns the override audit records for a printer, most recent first.
    /// </summary>
    /// <param name="printerId">Printer to query.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FilamentSwapOverride>> GetByPrinterAsync(Guid printerId, CancellationToken ct);
}
