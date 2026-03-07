using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.PrinterGroups;

/// <summary>
/// Repository interface for PrinterGroup persistence.
/// </summary>
public interface IPrinterGroupRepository
{
    /// <summary>
    /// Gets all printer groups.
    /// </summary>
    Task<IReadOnlyList<PrinterGroup>> ListAllAsync(CancellationToken ct);

    /// <summary>
    /// Gets a printer group by its unique identifier, including its printers.
    /// </summary>
    Task<PrinterGroup?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Gets a printer group by its name (case-insensitive).
    /// </summary>
    Task<PrinterGroup?> GetByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Adds a new printer group.
    /// </summary>
    Task AddAsync(PrinterGroup group, CancellationToken ct);

    /// <summary>
    /// Removes a printer group (printers get PrinterGroupId = null via SetNull cascade).
    /// </summary>
    void Remove(PrinterGroup group);

    /// <summary>
    /// Persists pending changes to the database.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct);
}
