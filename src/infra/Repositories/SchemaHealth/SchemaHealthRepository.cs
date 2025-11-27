using System.Data.Common;
using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.SchemaHealth;

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
            DbConnection conn = _db.Database.GetDbConnection();
            await conn.OpenAsync(ct);
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Printers';";
            object? result = await cmd.ExecuteScalarAsync(ct);
            return result != null && result.ToString() == "Printers";
        }
        catch
        {
            return false;
        }
    }
}
