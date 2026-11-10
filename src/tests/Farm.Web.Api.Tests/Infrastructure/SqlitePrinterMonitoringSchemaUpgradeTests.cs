using Farm.Infrastructure.Data;
using Farm.Web.Api.Infrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure;

public sealed class SqlitePrinterMonitoringSchemaUpgradeTests
{
    [Fact]
    public async Task LegacySchema_AddsNullableIntentOnceAndPreservesExistingPrinters()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE Printers (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL);");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Printers (Id, Name) VALUES ('synthetic', 'Existing printer');");

        await SqlitePrinterMonitoringSchemaUpgrade.ApplyAsync(db, NullLogger.Instance);
        await SqlitePrinterMonitoringSchemaUpgrade.ApplyAsync(db, NullLogger.Instance);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Printers') WHERE name = 'ExpectedOfflineSinceUtc';";
        (await command.ExecuteScalarAsync()).Should().Be(1L);
        command.CommandText = "SELECT Name FROM Printers WHERE ExpectedOfflineSinceUtc IS NULL;";
        (await command.ExecuteScalarAsync()).Should().Be("Existing printer");
    }
}
