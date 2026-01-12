using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.SchemaHealth;

public interface ISchemaHealthService
{
    Task<bool> IsSchemaReadyAsync(CancellationToken ct = default);
}
