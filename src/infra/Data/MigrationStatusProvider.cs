using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Data;

public class MigrationStatusProvider(AppDbContext context) : IMigrationStatusProvider
{
    public MigrationStatus GetStatus()
    {
        string? provider = context.Database.ProviderName;

        bool hasMigrations = false;
        bool appliedAny = false;
        string mode;
        try
        {
            IEnumerable<string> available = context.Database.GetMigrations();
            hasMigrations = available.Any();
            if (hasMigrations)
            {
                IEnumerable<string> applied = context.Database.GetAppliedMigrations();
                appliedAny = applied.Any();
                mode = "Migrations";
            }
            else
            {
                mode = "MigrationAssemblyMissing";
            }
        }
        catch (InvalidOperationException)
        {
            mode = "Unavailable";
        }
        catch (DbException)
        {
            mode = "Unavailable";
        }

        return new MigrationStatus(mode, hasMigrations, appliedAny, provider);
    }
}
