using System.Data;
using System.Data.Common;
using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Infrastructure;

/// <summary>Upgrades legacy EnsureCreated databases without adopting migration history.</summary>
internal static class SqlitePrinterMonitoringSchemaUpgrade
{
    /// <summary>Adds nullable monitoring intent; existing printers remain expected online.</summary>
    internal static async Task ApplyAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        DbConnection connection = db.Database.GetDbConnection();
        bool closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using DbTransaction transaction = await connection.BeginTransactionAsync(ct);
            await using DbCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM pragma_table_info('Printers') WHERE name = 'ExpectedOfflineSinceUtc' LIMIT 1;";
            if (await command.ExecuteScalarAsync(ct) is null)
            {
                command.CommandText = "ALTER TABLE \"Printers\" ADD COLUMN \"ExpectedOfflineSinceUtc\" TEXT NULL;";
                _ = await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            logger.LogInformation("Applied idempotent SQLite printer monitoring schema upgrade");
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }
}
