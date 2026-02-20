using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.SchemaHealth;

/// <summary>
/// Service for checking database schema health and readiness.
/// </summary>
public interface ISchemaHealthService
{
    /// <summary>Checks if the database schema is ready and properly initialized.</summary>
    Task<bool> IsSchemaReadyAsync(CancellationToken ct = default);
}
