using System.Data;
using System.Data.Common;
using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Infrastructure;

/// <summary>Upgrades legacy EnsureCreated databases without adopting migration history.</summary>
internal static class SqlitePrinterMonitoringSchemaUpgrade
{
    /// <summary>Converts legacy monitoring intent while protecting assigned and active jobs.</summary>
    internal static async Task ApplyAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        DbConnection connection = db.Database.GetDbConnection();
        bool closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(ct);
        }

        await using DbTransaction transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            bool hasExpectedOfflineColumn = await ColumnExistsAsync(
                connection,
                transaction,
                "ExpectedOfflineSinceUtc",
                ct);
            bool hasPrintJobsTable = await TableExistsAsync(connection, transaction, "PrintJobs", ct);
            if (hasExpectedOfflineColumn && !hasPrintJobsTable)
            {
                throw new InvalidOperationException(
                    "Cannot migrate expected-offline printer intent because the PrintJobs table is missing.");
            }

            if (hasPrintJobsTable
                && !await ColumnExistsAsync(connection, transaction, "RequiresOperatorReview", ct))
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "ALTER TABLE \"PrintJobs\" ADD COLUMN \"RequiresOperatorReview\" INTEGER NOT NULL DEFAULT 0;",
                    ct);
            }

            if (hasExpectedOfflineColumn)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    MarkActiveJobsForReviewSql,
                    ct);
                await ExecuteAsync(
                    connection,
                    transaction,
                    DisableIdleExpectedOfflinePrintersSql,
                    ct);
                await ExecuteAsync(
                    connection,
                    transaction,
                    "ALTER TABLE \"Printers\" DROP COLUMN \"ExpectedOfflineSinceUtc\";",
                    ct);
            }

            await transaction.CommitAsync(ct);
            logger.LogInformation("Applied idempotent SQLite printer service-status schema upgrade");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        CancellationToken ct)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @table LIMIT 1;";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@table";
        parameter.Value = table;
        _ = command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string column,
        CancellationToken ct)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = column switch
        {
            "ExpectedOfflineSinceUtc" =>
                "SELECT 1 FROM pragma_table_info('Printers') WHERE name = @column LIMIT 1;",
            "RequiresOperatorReview" =>
                "SELECT 1 FROM pragma_table_info('PrintJobs') WHERE name = @column LIMIT 1;",
            _ => throw new InvalidOperationException($"Unsupported SQLite printer service-status column {column}."),
        };
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@column";
        parameter.Value = column;
        _ = command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken ct)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(ct);
    }

    private const string MarkActiveJobsForReviewSql =
        "UPDATE \"PrintJobs\" SET \"RequiresOperatorReview\" = 1 "
        + "WHERE \"AssignedPrinterId\" IN "
        + "(SELECT \"Id\" FROM \"Printers\" WHERE \"ExpectedOfflineSinceUtc\" IS NOT NULL) "
        + "AND \"Status\" IN (1, 2, 3, 4);";

    private const string DisableIdleExpectedOfflinePrintersSql =
        "UPDATE \"Printers\" SET \"IsEnabled\" = 0 "
        + "WHERE \"ExpectedOfflineSinceUtc\" IS NOT NULL "
        + "AND NOT EXISTS (SELECT 1 FROM \"PrintJobs\" "
        + "WHERE \"PrintJobs\".\"AssignedPrinterId\" = \"Printers\".\"Id\" "
        + "AND \"PrintJobs\".\"Status\" IN (1, 2, 3, 4));";
}
