using Farm.Web.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Infrastructure.Database;

public interface IMigrationStatusProvider
{
    MigrationStatus GetStatus();
}

public sealed record MigrationStatus(string Mode, bool HasMigrations, bool AppliedAny, string? Provider);

public class MigrationStatusProvider(AppDbContext context) : IMigrationStatusProvider
{
    public MigrationStatus GetStatus()
    {
        string? provider = null;
        try
        { provider = context.Database.ProviderName; }
        catch { /* ignore */ }

        bool hasMigrations = false;
        bool appliedAny = false;
        string mode = "EnsureCreated"; // default assumption given current strategy
        try
        {
            IEnumerable<string> available = context.Database.GetMigrations();
            hasMigrations = available.Any();
            if (hasMigrations)
            {
                IEnumerable<string> applied = context.Database.GetAppliedMigrations();
                appliedAny = applied.Any();
                mode = "Migrations"; // if any migrations exist we consider migrations mode
            }
        }
        catch
        {
            // Accessing migrations can throw if no design-time services; treat as EnsureCreated.
        }
        return new MigrationStatus(mode, hasMigrations, appliedAny, provider);
    }
}
