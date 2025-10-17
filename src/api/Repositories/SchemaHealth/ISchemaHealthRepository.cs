using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Repositories.SchemaHealth;

public interface ISchemaHealthRepository
{
    /// <summary>
    /// Returns true when the critical Printers table exists in the configured database.
    /// </summary>
    Task<bool> PrintersTableExistsAsync(CancellationToken ct = default);
}
