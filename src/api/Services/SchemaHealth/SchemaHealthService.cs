using Farm.Web.Api.Repositories.SchemaHealth;

namespace Farm.Web.Api.Services.SchemaHealth;

public class SchemaHealthService : ISchemaHealthService
{
    private readonly ISchemaHealthRepository _repo;

    public SchemaHealthService(ISchemaHealthRepository repo)
    {
        _repo = repo;
    }

    public Task<bool> IsSchemaReadyAsync(CancellationToken ct = default)
    {
        return _repo.PrintersTableExistsAsync(ct);
    }
}
