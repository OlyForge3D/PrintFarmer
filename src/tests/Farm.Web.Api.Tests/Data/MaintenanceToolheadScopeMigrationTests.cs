using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Farm.Web.Api.Tests.Data;

public sealed class MaintenanceToolheadScopeMigrationTests
{
    [Fact]
    public Task PostgreSqlDown_ConsolidatesSchedulesBeforeRestoringLegacyUniqueIndex()
        => AssertDowngradeAsync(
            new Farm.Migrations.PostgreSQL.Migrations.AddFilamentFallbackGroupsAndMaintenanceToolheadScope(),
            "\"ToolheadId\"",
            "\"CreatedAt\"",
            "\"Id\"");

    [Fact]
    public Task SqlServerDown_ConsolidatesSchedulesBeforeRestoringLegacyUniqueIndex()
        => AssertDowngradeAsync(
            new Farm.Migrations.SqlServer.Migrations.AddFilamentFallbackGroupsAndMaintenanceToolheadScope(),
            "[ToolheadId]",
            "[CreatedAt]",
            "[Id]");

    private static async Task AssertDowngradeAsync(
        Migration migration,
        string toolheadIdentifier,
        string createdAtIdentifier,
        string idIdentifier)
    {
        List<MigrationOperation> operations = [.. migration.DownOperations];
        SqlOperation consolidation = Assert.Single(operations.OfType<SqlOperation>());
        DropColumnOperation toolheadDrop = Assert.Single(
            operations.OfType<DropColumnOperation>(),
            operation => operation is { Table: "PrinterMaintenanceSchedules", Name: "ToolheadId" });
        CreateIndexOperation legacyIndex = Assert.Single(
            operations.OfType<CreateIndexOperation>(),
            operation => operation.Name == "IX_PrinterMaintenanceSchedules_MaintenancePlanId_PrinterId");

        Assert.True(operations.IndexOf(consolidation) < operations.IndexOf(toolheadDrop));
        Assert.True(operations.IndexOf(consolidation) < operations.IndexOf(legacyIndex));
        Assert.Contains("ROW_NUMBER()", consolidation.Sql, StringComparison.Ordinal);
        Assert.Contains("PARTITION BY", consolidation.Sql, StringComparison.Ordinal);
        Assert.Contains(
            $"CASE WHEN {toolheadIdentifier} IS NULL THEN 0 ELSE 1 END",
            consolidation.Sql,
            StringComparison.Ordinal);
        Assert.True(
            consolidation.Sql.IndexOf(createdAtIdentifier, StringComparison.Ordinal)
            < consolidation.Sql.LastIndexOf(idIdentifier, StringComparison.Ordinal));
        Assert.Contains("\"DuplicateRank\" > 1", NormalizeDuplicateRankIdentifier(consolidation.Sql), StringComparison.Ordinal);

        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE PrinterMaintenanceSchedules (
                Id TEXT NOT NULL PRIMARY KEY,
                MaintenancePlanId TEXT NOT NULL,
                PrinterId TEXT NOT NULL,
                ToolheadId TEXT NULL,
                CreatedAt TEXT NOT NULL
            );
            """);

        string planWithPrinterWide = Guid.NewGuid().ToString("D");
        string planWithToolsOnly = Guid.NewGuid().ToString("D");
        string printerId = Guid.NewGuid().ToString("D");
        string printerWideId = Guid.NewGuid().ToString("D");
        string earlierToolId = Guid.NewGuid().ToString("D");
        await InsertScheduleAsync(
            connection,
            Guid.NewGuid().ToString("D"),
            planWithPrinterWide,
            printerId,
            Guid.NewGuid().ToString("D"),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await InsertScheduleAsync(
            connection,
            printerWideId,
            planWithPrinterWide,
            printerId,
            toolheadId: null,
            new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        await InsertScheduleAsync(
            connection,
            Guid.NewGuid().ToString("D"),
            planWithPrinterWide,
            printerId,
            Guid.NewGuid().ToString("D"),
            new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        await InsertScheduleAsync(
            connection,
            earlierToolId,
            planWithToolsOnly,
            printerId,
            Guid.NewGuid().ToString("D"),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        await InsertScheduleAsync(
            connection,
            Guid.NewGuid().ToString("D"),
            planWithToolsOnly,
            printerId,
            Guid.NewGuid().ToString("D"),
            new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));

        await ExecuteAsync(connection, consolidation.Sql);
        await ExecuteAsync(connection, "ALTER TABLE PrinterMaintenanceSchedules DROP COLUMN ToolheadId;");
        await ExecuteAsync(
            connection,
            """
            CREATE UNIQUE INDEX IX_PrinterMaintenanceSchedules_MaintenancePlanId_PrinterId
            ON PrinterMaintenanceSchedules (MaintenancePlanId, PrinterId);
            """);

        List<string> survivors = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM PrinterMaintenanceSchedules ORDER BY MaintenancePlanId;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            survivors.Add(reader.GetString(0));
        }

        Assert.Equal(2, survivors.Count);
        Assert.Contains(printerWideId, survivors);
        Assert.Contains(earlierToolId, survivors);
    }

    private static async Task InsertScheduleAsync(
        SqliteConnection connection,
        string id,
        string planId,
        string printerId,
        string? toolheadId,
        DateTime createdAt)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO PrinterMaintenanceSchedules
                (Id, MaintenancePlanId, PrinterId, ToolheadId, CreatedAt)
            VALUES
                ($id, $planId, $printerId, $toolheadId, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$planId", planId);
        command.Parameters.AddWithValue("$printerId", printerId);
        command.Parameters.AddWithValue("$toolheadId", (object?)toolheadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string NormalizeDuplicateRankIdentifier(string sql)
        => sql.Replace("[DuplicateRank]", "\"DuplicateRank\"", StringComparison.Ordinal);
}
