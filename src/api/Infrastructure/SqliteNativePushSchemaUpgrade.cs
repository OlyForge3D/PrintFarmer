using System.Data;
using System.Data.Common;
using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Infrastructure;

/// <summary>
/// Applies the native-push schema delta to SQLite databases managed with EnsureCreated.
/// </summary>
internal static class SqliteNativePushSchemaUpgrade
{
    private static readonly (string Column, string Definition)[] PreferenceColumns =
    [
        ("AttentionPushCategoryPreferencesJson", "TEXT NULL"),
        ("EmailOnFilamentRunout", "INTEGER NOT NULL DEFAULT 0"),
        ("EmailOnHarvestReady", "INTEGER NOT NULL DEFAULT 0"),
        ("EmailOnMaintenanceDue", "INTEGER NOT NULL DEFAULT 0"),
        ("EmailOnPrinterFailure", "INTEGER NOT NULL DEFAULT 0"),
        ("EmailOnPrinterOffline", "INTEGER NOT NULL DEFAULT 0"),
        ("InAppOnFilamentRunout", "INTEGER NOT NULL DEFAULT 1"),
        ("InAppOnHarvestReady", "INTEGER NOT NULL DEFAULT 1"),
        ("InAppOnMaintenanceDue", "INTEGER NOT NULL DEFAULT 1"),
        ("InAppOnPrinterFailure", "INTEGER NOT NULL DEFAULT 1"),
        ("InAppOnPrinterOffline", "INTEGER NOT NULL DEFAULT 1"),
        ("PushOnFilamentRunout", "INTEGER NOT NULL DEFAULT 1"),
        ("PushOnHarvestReady", "INTEGER NOT NULL DEFAULT 1"),
        ("PushOnMaintenanceDue", "INTEGER NOT NULL DEFAULT 1"),
        ("PushOnPrinterFailure", "INTEGER NOT NULL DEFAULT 1"),
        ("PushOnPrinterOffline", "INTEGER NOT NULL DEFAULT 1"),
        ("TelegramOnFilamentRunout", "INTEGER NOT NULL DEFAULT 0"),
        ("TelegramOnHarvestReady", "INTEGER NOT NULL DEFAULT 0"),
        ("TelegramOnMaintenanceDue", "INTEGER NOT NULL DEFAULT 0"),
        ("TelegramOnPrinterFailure", "INTEGER NOT NULL DEFAULT 0"),
        ("TelegramOnPrinterOffline", "INTEGER NOT NULL DEFAULT 0"),
    ];

    /// <summary>Applies the idempotent SQLite-only upgrade in one transaction.</summary>
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
            if (!await TableExistsAsync(connection, transaction, "DeviceTokens", cancellationToken))
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    CreateDeviceTokensTableSql,
                    cancellationToken);
            }

            await EnsureColumnAsync(
                connection,
                transaction,
                "DeviceTokens",
                "RegistrationVersion",
                "INTEGER NOT NULL DEFAULT 0",
                cancellationToken);

            await ExecuteAsync(
                connection,
                transaction,
                "CREATE INDEX IF NOT EXISTS \"IX_DeviceTokens_Token\" ON \"DeviceTokens\" (\"Token\");",
                cancellationToken);
            await ExecuteAsync(
                connection,
                transaction,
                RepairActiveInstallationOwnersSql,
                cancellationToken);
            await ExecuteAsync(
                connection,
                transaction,
                ReplaceInstallationIndexesSql,
                cancellationToken);

            if (await TableExistsAsync(
                    connection,
                    transaction,
                    "NotificationPreferences",
                    cancellationToken))
            {
                foreach ((string column, string definition) in PreferenceColumns)
                {
                    await EnsureColumnAsync(
                        connection,
                        transaction,
                        "NotificationPreferences",
                        column,
                        definition,
                        cancellationToken);
                }
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

        logger.LogInformation(
            "[Startup]   ✓ Applied idempotent SQLite native-push schema upgrades");
    }

    private static async Task EnsureColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(connection, transaction, table, column, cancellationToken))
        {
            return;
        }

        string sql = (table, column) switch
        {
            ("DeviceTokens", "RegistrationVersion") =>
                "ALTER TABLE \"DeviceTokens\" ADD COLUMN \"RegistrationVersion\" INTEGER NOT NULL DEFAULT 0;",
            ("NotificationPreferences", _) =>
                $"ALTER TABLE \"NotificationPreferences\" ADD COLUMN \"{column}\" {definition};",
            _ => throw new InvalidOperationException(
                $"Unsupported SQLite native-push schema column {table}.{column}."),
        };
        await ExecuteAsync(connection, transaction, sql, cancellationToken);
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
            "DeviceTokens" =>
                "SELECT 1 FROM pragma_table_info('DeviceTokens') WHERE name = @column LIMIT 1;",
            "NotificationPreferences" =>
                "SELECT 1 FROM pragma_table_info('NotificationPreferences') WHERE name = @column LIMIT 1;",
            _ => throw new InvalidOperationException(
                $"Unsupported SQLite native-push schema table {table}."),
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

    private const string RepairActiveInstallationOwnersSql =
        """
        WITH "RankedOwners" AS (
            SELECT
                "Id",
                ROW_NUMBER() OVER (
                    PARTITION BY "InstallationId"
                    ORDER BY
                        "RegistrationVersion" DESC,
                        COALESCE("LastUsedAt", "CreatedAt") DESC,
                        "CreatedAt" DESC,
                        "Id" DESC
                ) AS "OwnerRank"
            FROM "DeviceTokens"
            WHERE "IsActive" = 1
        )
        UPDATE "DeviceTokens"
        SET "IsActive" = 0
        WHERE "Id" IN (
            SELECT "Id"
            FROM "RankedOwners"
            WHERE "OwnerRank" > 1
        );
        """;

    private const string ReplaceInstallationIndexesSql =
        """
        DROP INDEX IF EXISTS "IX_DeviceTokens_UserId_InstallationId";
        DROP INDEX IF EXISTS "IX_DeviceTokens_InstallationId";
        CREATE UNIQUE INDEX "IX_DeviceTokens_InstallationId"
            ON "DeviceTokens" ("InstallationId")
            WHERE "IsActive" = 1;
        CREATE INDEX IF NOT EXISTS "IX_DeviceTokens_UserId"
            ON "DeviceTokens" ("UserId");
        """;

    private const string CreateDeviceTokensTableSql =
        "CREATE TABLE IF NOT EXISTS \"DeviceTokens\" ("
        + "\"Id\" TEXT NOT NULL CONSTRAINT \"PK_DeviceTokens\" PRIMARY KEY, "
        + "\"UserId\" TEXT NOT NULL, "
        + "\"RegistrationVersion\" INTEGER NOT NULL DEFAULT 0, "
        + "\"InstallationId\" TEXT NOT NULL, "
        + "\"Token\" TEXT NOT NULL, "
        + "\"Platform\" TEXT NOT NULL, "
        + "\"Environment\" TEXT NOT NULL, "
        + "\"AppBundleId\" TEXT NULL, "
        + "\"CreatedAt\" TEXT NOT NULL, "
        + "\"LastUsedAt\" TEXT NULL, "
        + "\"LastFailureAt\" TEXT NULL, "
        + "\"ConsecutiveFailureCount\" INTEGER NOT NULL DEFAULT 0, "
        + "\"IsActive\" INTEGER NOT NULL DEFAULT 1, "
        + "CONSTRAINT \"FK_DeviceTokens_Users_UserId\" FOREIGN KEY (\"UserId\") "
        + "REFERENCES \"Users\" (\"Id\") ON DELETE CASCADE);";
}
