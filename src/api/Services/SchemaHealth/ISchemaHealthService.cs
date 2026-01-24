using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.SchemaHealth;

/// <summary>
/// Service for checking database schema health and readiness.
/// </summary>
public interface ISchemaHealthService
{
    /// <summary>Checks if the database schema is ready and properly initialized.</summary>
    Task<bool> IsSchemaReadyAsync(CancellationToken ct = default);
}
