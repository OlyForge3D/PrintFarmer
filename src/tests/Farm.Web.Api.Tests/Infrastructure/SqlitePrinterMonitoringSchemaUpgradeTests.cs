using Farm.Infrastructure.Data;
using Farm.Web.Api.Infrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Infrastructure;

public sealed class SqlitePrinterMonitoringSchemaUpgradeTests
{
    [Fact]
    public async Task LegacySchema_ConvertsIntentWithoutDisablingPrintersWithAssignedOrActiveJobs()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Printers (Id TEXT NOT NULL PRIMARY KEY, Revision INTEGER NOT NULL DEFAULT 1, Name TEXT NOT NULL, IsEnabled INTEGER NOT NULL, ExpectedOfflineSinceUtc TEXT NULL);");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE PrintJobs (Id TEXT NOT NULL PRIMARY KEY, Revision INTEGER NOT NULL DEFAULT 1, AssignedPrinterId TEXT NULL, Status INTEGER NOT NULL);");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Printers (Id, Name, IsEnabled, ExpectedOfflineSinceUtc) VALUES "
            + "('idle', 'Idle', 1, '2026-11-10T00:00:00Z'), "
            + "('assigned', 'Assigned', 1, '2026-11-10T00:00:00Z'), "
            + "('printing', 'Printing', 1, '2026-11-10T00:00:00Z'), "
            + "('completed', 'Completed', 1, '2026-11-10T00:00:00Z'), "
            + "('online', 'Online', 1, NULL);");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO PrintJobs (Id, AssignedPrinterId, Status) VALUES "
            + "('assigned-job', 'assigned', 1), "
            + "('printing-job', 'printing', 3), "
            + "('completed-job', 'completed', 5);");

        await SqlitePrinterMonitoringSchemaUpgrade.ApplyAsync(db, NullLogger.Instance);
        await SqlitePrinterMonitoringSchemaUpgrade.ApplyAsync(db, NullLogger.Instance);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT \"IsEnabled\" FROM \"Printers\" WHERE \"Id\" = 'idle';";
        (await command.ExecuteScalarAsync()).Should().Be(0L);
        command.CommandText = "SELECT \"IsEnabled\" FROM \"Printers\" WHERE \"Id\" = 'completed';";
        (await command.ExecuteScalarAsync()).Should().Be(0L);
        command.CommandText = "SELECT \"IsEnabled\" FROM \"Printers\" WHERE \"Id\" = 'assigned';";
        (await command.ExecuteScalarAsync()).Should().Be(1L);
        command.CommandText = "SELECT \"Revision\" FROM \"Printers\" WHERE \"Id\" = 'idle';";
        (await command.ExecuteScalarAsync()).Should().Be(2L);
        command.CommandText = "SELECT \"Revision\" FROM \"Printers\" WHERE \"Id\" = 'online';";
        (await command.ExecuteScalarAsync()).Should().Be(1L);
        command.CommandText = "SELECT \"Revision\" FROM \"PrintJobs\" WHERE \"Id\" = 'printing-job';";
        (await command.ExecuteScalarAsync()).Should().Be(2L);
        command.CommandText = "SELECT \"Revision\" FROM \"PrintJobs\" WHERE \"Id\" = 'completed-job';";
        (await command.ExecuteScalarAsync()).Should().Be(1L);
        command.CommandText = "SELECT \"IsEnabled\" FROM \"Printers\" WHERE \"Id\" = 'printing';";
        (await command.ExecuteScalarAsync()).Should().Be(1L);
        command.CommandText =
            "SELECT COUNT(*) FROM \"PrintJobs\" WHERE \"RequiresOperatorReview\" = 1;";
        (await command.ExecuteScalarAsync()).Should().Be(2L);
        command.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('Printers') WHERE name = 'ExpectedOfflineSinceUtc';";
        (await command.ExecuteScalarAsync()).Should().Be(0L);
        command.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('PrintJobs') WHERE name = 'RequiresOperatorReview';";
        (await command.ExecuteScalarAsync()).Should().Be(1L);
        command.CommandText = "SELECT \"IsEnabled\" FROM \"Printers\" WHERE \"Id\" = 'online';";
        (await command.ExecuteScalarAsync()).Should().Be(1L);
    }
}
