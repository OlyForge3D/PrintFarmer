using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Farm.Web.Api.Tests.Data;

public sealed class PerToolAttributionCapabilityBackfillMigrationTests
{
    [Fact]
    public Task PostgreSqlMigration_BackfillsOnlyEligibleMoonrakerPrinters_Idempotently()
        => AssertBackfillAsync(
            new Farm.Migrations.PostgreSQL.Migrations.BackfillPerToolAttributionCapability());

    [Fact]
    public Task SqlServerMigration_BackfillsOnlyEligibleMoonrakerPrinters_Idempotently()
        => AssertBackfillAsync(
            new Farm.Migrations.SqlServer.Migrations.BackfillPerToolAttributionCapability());

    private static async Task AssertBackfillAsync(Migration migration)
    {
        SqlOperation backfill = Assert.IsType<SqlOperation>(Assert.Single(migration.UpOperations));
        Assert.Contains("SupportsPerToolAttribution", backfill.Sql, StringComparison.Ordinal);
        Assert.Contains("Backend", backfill.Sql, StringComparison.Ordinal);
        Assert.Contains("Toolheads", backfill.Sql, StringComparison.Ordinal);
        Assert.Contains("Type", backfill.Sql, StringComparison.Ordinal);
        Assert.Contains(">= 2", backfill.Sql, StringComparison.Ordinal);
        Assert.Empty(migration.DownOperations);

        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE Printers (
                Id TEXT NOT NULL PRIMARY KEY,
                Backend INTEGER NOT NULL,
                SupportsPerToolAttribution INTEGER NOT NULL
            );
            CREATE TABLE Toolheads (
                Id TEXT NOT NULL PRIMARY KEY,
                PrinterId TEXT NOT NULL,
                Type INTEGER NOT NULL
            );
            """);

        string eligible = await InsertPrinterAsync(connection, backend: 1, supported: false);
        string onePhysical = await InsertPrinterAsync(connection, backend: 1, supported: false);
        string mixedToolheads = await InsertPrinterAsync(connection, backend: 1, supported: false);
        string unsupportedBackend = await InsertPrinterAsync(connection, backend: 2, supported: false);
        string existingTrue = await InsertPrinterAsync(connection, backend: 2, supported: true);

        await InsertToolheadAsync(connection, eligible, type: 0);
        await InsertToolheadAsync(connection, eligible, type: 0);
        await InsertToolheadAsync(connection, onePhysical, type: 0);
        await InsertToolheadAsync(connection, mixedToolheads, type: 0);
        await InsertToolheadAsync(connection, mixedToolheads, type: 1);
        await InsertToolheadAsync(connection, unsupportedBackend, type: 0);
        await InsertToolheadAsync(connection, unsupportedBackend, type: 0);

        await ExecuteAsync(connection, backfill.Sql);
        await ExecuteAsync(connection, backfill.Sql);

        Assert.True(await IsSupportedAsync(connection, eligible));
        Assert.False(await IsSupportedAsync(connection, onePhysical));
        Assert.False(await IsSupportedAsync(connection, mixedToolheads));
        Assert.False(await IsSupportedAsync(connection, unsupportedBackend));
        Assert.True(await IsSupportedAsync(connection, existingTrue));
    }

    private static async Task<string> InsertPrinterAsync(
        SqliteConnection connection,
        int backend,
        bool supported)
    {
        string id = Guid.NewGuid().ToString("D");
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Printers (Id, Backend, SupportsPerToolAttribution)
            VALUES ($id, $backend, $supported);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$backend", backend);
        command.Parameters.AddWithValue("$supported", supported);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task InsertToolheadAsync(
        SqliteConnection connection,
        string printerId,
        int type)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Toolheads (Id, PrinterId, Type)
            VALUES ($id, $printerId, $type);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$printerId", printerId);
        command.Parameters.AddWithValue("$type", type);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> IsSupportedAsync(
        SqliteConnection connection,
        string printerId)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SupportsPerToolAttribution
            FROM Printers
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", printerId);
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToBoolean(result);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
