using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Builds a real, provider-native <see cref="IHostUpdateBackupTarget"/>/restore-command pair for
/// one <see cref="Farm.Infrastructure.Data.DatabaseProviderConfiguration"/>, invoking the host's
/// existing <c>sqlite3</c>/<c>pg_dump</c>/<c>sqlcmd</c> tooling (never a duplicate ad-hoc dump
/// implementation) via explicit process argument lists. Connection secrets (passwords) are
/// passed only through the child process's environment or its own standard argument form; they
/// are never written to <see cref="HostUpdateProcessResult"/>'s captured streams by this
/// factory, and are never logged.
/// </summary>
public static class HostUpdateDatabaseBackupTargetFactory
{
    private const string PostgresFileName = "database.dump";
    private const string SqlServerFileName = "database.bak";
    private const string SqliteFileName = "database.sqlite3";

    /// <summary>
    /// Creates the backup target for <paramref name="dbConfig"/>. Returns an
    /// externally-owned target (which fails the backup step closed, per
    /// <see cref="HostUpdateBackupCoordinator"/>) when <paramref name="isExternallyOwned"/> is
    /// true -- e.g. a customer-managed external PostgreSQL/SQL Server instance this host does
    /// not control.
    /// </summary>
    public static IHostUpdateBackupTarget CreateBackupTarget(
        string name,
        Farm.Infrastructure.Data.DatabaseProviderConfiguration dbConfig,
        IHostUpdateProcessRunner processRunner,
        TimeSpan timeout,
        bool isExternallyOwned)
    {
        ArgumentNullException.ThrowIfNull(dbConfig);

        if (isExternallyOwned)
        {
            return new ExternallyOwnedBackupTarget(name);
        }

        if (dbConfig.IsSqlite)
        {
            string dbFilePath = new SqliteConnectionStringBuilder(dbConfig.ConnectionString).DataSource;
            return new ProcessDatabaseBackupTarget(
                name,
                isExternallyOwned: false,
                processRunner,
                "sqlite3",
                dest => [dbFilePath, $".backup '{Path.Combine(dest, SqliteFileName)}'"],
                timeout);
        }

        if (dbConfig.IsPostgres)
        {
            var builder = new NpgsqlConnectionStringBuilder(dbConfig.ConnectionString);
            string password = builder.Password ?? string.Empty;
            return new ProcessDatabaseBackupTarget(
                name,
                isExternallyOwned: false,
                processRunner,
                "pg_dump",
                dest =>
                [
                    "-h", builder.Host ?? "localhost",
                    "-p", builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-U", builder.Username ?? string.Empty,
                    "-Fc",
                    "-f", Path.Combine(dest, PostgresFileName),
                    builder.Database ?? string.Empty,
                ],
                timeout,
                string.IsNullOrEmpty(password) ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["PGPASSWORD"] = password });
        }

        if (dbConfig.IsSqlServer)
        {
            var builder = new SqlConnectionStringBuilder(dbConfig.ConnectionString);
            string database = builder.InitialCatalog;
            List<string> authArgs = builder.IntegratedSecurity
                ? ["-E"]
                : ["-U", builder.UserID, "-P", builder.Password];
            return new ProcessDatabaseBackupTarget(
                name,
                isExternallyOwned: false,
                processRunner,
                "sqlcmd",
                dest =>
                [
                    "-S", builder.DataSource,
                    .. authArgs,
                    "-Q", $"BACKUP DATABASE [{database}] TO DISK = N'{Path.Combine(dest, SqlServerFileName)}' WITH INIT",
                ],
                timeout);
        }

        throw new NotSupportedException($"unsupported_backup_provider:{dbConfig.Provider}");
    }

    /// <summary>
    /// Builds the matching restore command for one already-verified backup target's on-disk
    /// dump, keyed by target name, for <see cref="ProcessHostUpdateRestoreExecutor"/>.
    /// </summary>
    public static Func<string, IReadOnlyList<string>> CreateRestoreCommand(Farm.Infrastructure.Data.DatabaseProviderConfiguration dbConfig)
    {
        ArgumentNullException.ThrowIfNull(dbConfig);

        if (dbConfig.IsSqlite)
        {
            string dbFilePath = new SqliteConnectionStringBuilder(dbConfig.ConnectionString).DataSource;
            return targetDirectory =>
            [
                "-c",
                $"sqlite3 '{dbFilePath}' \".restore '{Path.Combine(targetDirectory, SqliteFileName)}'\"",
            ];
        }

        if (dbConfig.IsPostgres)
        {
            var builder = new NpgsqlConnectionStringBuilder(dbConfig.ConnectionString);
            string password = builder.Password ?? string.Empty;
            string passwordPrefix = string.IsNullOrEmpty(password) ? string.Empty : $"PGPASSWORD='{password}' ";
            return targetDirectory =>
            [
                "-c",
                $"{passwordPrefix}pg_restore --clean --if-exists -h {builder.Host} -p {builder.Port} -U {builder.Username} -d {builder.Database} '{Path.Combine(targetDirectory, PostgresFileName)}'",
            ];
        }

        if (dbConfig.IsSqlServer)
        {
            var builder = new SqlConnectionStringBuilder(dbConfig.ConnectionString);
            string authArgs = builder.IntegratedSecurity ? "-E" : $"-U {builder.UserID} -P {builder.Password}";
            return targetDirectory =>
            [
                "-c",
                $"sqlcmd -S {builder.DataSource} {authArgs} -Q \"RESTORE DATABASE [{builder.InitialCatalog}] FROM DISK = N'{Path.Combine(targetDirectory, SqlServerFileName)}' WITH REPLACE\"",
            ];
        }

        throw new NotSupportedException($"unsupported_restore_provider:{dbConfig.Provider}");
    }
}

/// <summary>A backup target this host does not own and therefore never attempts to back up itself.</summary>
public sealed class ExternallyOwnedBackupTarget(string name) : IHostUpdateBackupTarget
{
    public string Name { get; } = name;

    public bool IsExternallyOwned => true;

    public Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"external_backup_owner_unsupported:{Name}");
}
