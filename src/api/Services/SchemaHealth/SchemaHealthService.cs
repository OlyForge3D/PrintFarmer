using Farm.Infrastructure.Repositories.SchemaHealth;

namespace Farm.Web.Api.Services.SchemaHealth;

public class SchemaHealthService(ISchemaHealthRepository repo) : ISchemaHealthService
{
    private readonly ISchemaHealthRepository _repo = repo;

    public Task<bool> IsSchemaReadyAsync(CancellationToken ct = default)
    {
        return _repo.PrintersTableExistsAsync(ct);
    }
}
