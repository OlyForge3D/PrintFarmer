using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Repositories.SchemaHealth;

/// <summary>
/// Repository for checking database schema health and table existence.
/// Used during startup to verify database initialization.
/// </summary>
public interface ISchemaHealthRepository
{
    /// <summary>
    /// Returns true when the critical Printers table exists in the configured database.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> PrintersTableExistsAsync(CancellationToken ct = default);
}
