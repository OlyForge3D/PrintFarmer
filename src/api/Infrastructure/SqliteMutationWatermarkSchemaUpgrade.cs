using System.Data;
using System.Data.Common;
using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Infrastructure;

/// <summary>
/// Applies mutation-watermark schema changes to SQLite databases managed by EnsureCreated.
/// </summary>
internal static class SqliteMutationWatermarkSchemaUpgrade
{
    /// <summary>
    /// Applies the additive, idempotent SQLite schema upgrade in one transaction.
    /// </summary>
    internal static async Task ApplyAsync(
        AppDbContext db,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        if (!string.Equals(
                db.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.Sqlite",
                StringComparison.Ordinal))
        {
            return;
        }

        DbConnection connection = db.Database.GetDbConnection();
        bool closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(
                connection,
                transaction,
                CreateMutationCountersTableSql,
                cancellationToken);
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT OR IGNORE INTO \"MutationCounters\" (\"Id\", \"Value\") VALUES (1, 0);",
                cancellationToken);

            if (await TableExistsAsync(connection, transaction, "UserTasks", cancellationToken)
                && !await ColumnExistsAsync(
                    connection,
                    transaction,
                    "UserTasks",
                    "LastMutationSequence",
                    cancellationToken))
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "ALTER TABLE \"UserTasks\" ADD COLUMN \"LastMutationSequence\" INTEGER NOT NULL DEFAULT 0;",
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
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

        logger.LogInformation("[Startup]   ✓ Applied idempotent SQLite mutation-watermark schema upgrades");
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @table LIMIT 1;";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@table";
        parameter.Value = table;
        _ = command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = table switch
        {
            "UserTasks" =>
                "SELECT 1 FROM pragma_table_info('UserTasks') WHERE name = @column LIMIT 1;",
            _ => throw new InvalidOperationException(
                $"Unsupported SQLite mutation-watermark schema table {table}."),
        };
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@column";
        parameter.Value = column;
        _ = command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string CreateMutationCountersTableSql =
        "CREATE TABLE IF NOT EXISTS \"MutationCounters\" ("
        + "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_MutationCounters\" PRIMARY KEY, "
        + "\"Value\" INTEGER NOT NULL);";
}
