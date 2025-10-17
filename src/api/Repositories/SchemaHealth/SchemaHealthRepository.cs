using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Repositories.SchemaHealth;

public class SchemaHealthRepository : ISchemaHealthRepository
{
    private readonly AppDbContext _db;

    public SchemaHealthRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> PrintersTableExistsAsync(CancellationToken ct = default)
    {
        try
        {
            var conn = _db.Database.GetDbConnection();
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Printers';";
            var result = await cmd.ExecuteScalarAsync(ct);
            return result != null && result.ToString() == "Printers";
        }
        catch
        {
            return false;
        }
    }
}
