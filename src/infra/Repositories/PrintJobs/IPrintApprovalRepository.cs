using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.PrintJobs;

/// <summary>
/// Repository for managing print approval records requiring user confirmation before printing.
/// </summary>
public interface IPrintApprovalRepository
{
    /// <summary>
    /// Adds a new print approval request.
    /// </summary>
    /// <param name="approval">The approval to add.</param>
    Task AddAsync(PrintApproval approval);

    /// <summary>
    /// Gets a print approval by its ID.
    /// </summary>
    /// <param name="id">The approval ID.</param>
    /// <returns>The approval if found; otherwise null.</returns>
    Task<PrintApproval?> GetAsync(Guid id);

    /// <summary>
    /// Removes a print approval record.
    /// </summary>
    /// <param name="approval">The approval to remove.</param>
    Task RemoveAsync(PrintApproval approval);

    /// <summary>
    /// Lists all pending print approvals awaiting user action.
    /// </summary>
    /// <returns>Collection of pending approvals.</returns>
    Task<IEnumerable<PrintApproval>> ListPendingAsync();
}
