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
                dest => [dbFilePath, $".backup '{EscapeQuotedLiteral(Path.Combine(dest, SqliteFileName))}'"],
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
            (IReadOnlyList<string> authArgs, IReadOnlyDictionary<string, string>? authEnvironment) = SqlServerAuthArgs(builder);
            return new ProcessDatabaseBackupTarget(
                name,
                isExternallyOwned: false,
                processRunner,
                "sqlcmd",
                dest =>
                [
                    "-S", builder.DataSource,
                    "-b",
                    .. authArgs,
                    "-Q", $"BACKUP DATABASE [{EscapeBracketedIdentifier(database)}] TO DISK = N'{EscapeQuotedLiteral(Path.Combine(dest, SqlServerFileName))}' WITH INIT",
                ],
                timeout,
                authEnvironment);
        }

        throw new NotSupportedException($"unsupported_backup_provider:{dbConfig.Provider}");
    }

    /// <summary>
    /// Builds the matching restore command for one already-verified backup target's on-disk
    /// dump, keyed by target name, for <see cref="ProcessHostUpdateRestoreExecutor"/>. Returns a
    /// structured <see cref="HostUpdateRestoreCommand"/> (file name, argument list, environment)
    /// invoked directly via <see cref="IHostUpdateProcessRunner"/> -- never through a shell -- so
    /// a connection password can only ever reach the child process via its environment (Postgres,
    /// SQL Server) or not at all (SQLite has no credential), the same guarantee
    /// <see cref="CreateBackupTarget"/> already provides for backup.
    /// </summary>
    public static Func<string, HostUpdateRestoreCommand> CreateRestoreCommand(Farm.Infrastructure.Data.DatabaseProviderConfiguration dbConfig)
    {
        ArgumentNullException.ThrowIfNull(dbConfig);

        if (dbConfig.IsSqlite)
        {
            string dbFilePath = new SqliteConnectionStringBuilder(dbConfig.ConnectionString).DataSource;
            return targetDirectory => new HostUpdateRestoreCommand(
                "sqlite3",
                [dbFilePath, $".restore '{EscapeQuotedLiteral(Path.Combine(targetDirectory, SqliteFileName))}'"],
                null);
        }

        if (dbConfig.IsPostgres)
        {
            var builder = new NpgsqlConnectionStringBuilder(dbConfig.ConnectionString);
            string password = builder.Password ?? string.Empty;
            return targetDirectory => new HostUpdateRestoreCommand(
                "pg_restore",
                [
                    "--clean",
                    "--if-exists",
                    "-h", builder.Host ?? "localhost",
                    "-p", builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-U", builder.Username ?? string.Empty,
                    "-d", builder.Database ?? string.Empty,
                    Path.Combine(targetDirectory, PostgresFileName),
                ],
                string.IsNullOrEmpty(password) ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["PGPASSWORD"] = password });
        }

        if (dbConfig.IsSqlServer)
        {
            var builder = new SqlConnectionStringBuilder(dbConfig.ConnectionString);
            string database = builder.InitialCatalog;
            (IReadOnlyList<string> authArgs, IReadOnlyDictionary<string, string>? authEnvironment) = SqlServerAuthArgs(builder);
            return targetDirectory => new HostUpdateRestoreCommand(
                "sqlcmd",
                [
                    "-S", builder.DataSource,
                    "-b",
                    .. authArgs,
                    "-Q", $"BEGIN TRY ALTER DATABASE [{EscapeBracketedIdentifier(database)}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [{EscapeBracketedIdentifier(database)}] FROM DISK = N'{EscapeQuotedLiteral(Path.Combine(targetDirectory, SqlServerFileName))}' WITH REPLACE; ALTER DATABASE [{EscapeBracketedIdentifier(database)}] SET MULTI_USER; END TRY BEGIN CATCH IF DB_ID(N'{EscapeQuotedLiteral(database)}') IS NOT NULL ALTER DATABASE [{EscapeBracketedIdentifier(database)}] SET MULTI_USER; THROW; END CATCH",
                ],
                authEnvironment);
        }

        throw new NotSupportedException($"unsupported_restore_provider:{dbConfig.Provider}");
    }

    /// <summary>
    /// <c>sqlcmd</c>'s own credential-handling convention: username may safely appear as a
    /// process argument, but the password must travel only through the <c>SQLCMDPASSWORD</c>
    /// environment variable (supported natively by <c>sqlcmd</c>) so it never appears in a
    /// process argument list -- visible via <c>ps</c>/process listings or argv-capturing
    /// audit/process logging -- for either backup or restore.
    /// </summary>
    /// <summary>
    /// Escapes a single-quoted literal for <c>sqlite3</c>'s own dot-command tokenizer (used by
    /// <c>.backup</c>/<c>.restore</c>) and for T-SQL <c>N'...'</c> string literals: both treat a
    /// doubled quote as one literal quote character, the same convention SQL itself uses. This
    /// closes a security-review finding that a backup-root path containing a single quote could
    /// otherwise terminate the literal early and inject additional dot-command/T-SQL text.
    /// </summary>
    private static string EscapeQuotedLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// Escapes a T-SQL bracketed identifier (<c>[name]</c>) by doubling any embedded <c>]</c>,
    /// the standard T-SQL identifier-escaping convention, so a database name containing <c>]</c>
    /// cannot break out of the bracket and inject additional T-SQL into the same batch.
    /// </summary>
    private static string EscapeBracketedIdentifier(string value) => value.Replace("]", "]]", StringComparison.Ordinal);

    private static (IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment) SqlServerAuthArgs(SqlConnectionStringBuilder builder)
    {
        if (builder.IntegratedSecurity)
        {
            return (["-E"], null);
        }

        string password = builder.Password ?? string.Empty;
        IReadOnlyDictionary<string, string>? environment = string.IsNullOrEmpty(password)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["SQLCMDPASSWORD"] = password };
        return (["-U", builder.UserID], environment);
    }
}

/// <summary>
/// A structured (never shell-interpolated) restore invocation: the executable, its explicit
/// argument list, and any environment variables (e.g. a database password) the child process
/// needs. Mirrors the argument shape <see cref="ProcessDatabaseBackupTarget"/> already uses for
/// backup, so a restore never has to fall back to a <c>sh -c</c> string.
/// </summary>
public sealed record HostUpdateRestoreCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment);

/// <summary>A backup target this host does not own and therefore never attempts to back up itself.</summary>
public sealed class ExternallyOwnedBackupTarget(string name) : IHostUpdateBackupTarget
{
    public string Name { get; } = name;

    public bool IsExternallyOwned => true;

    public Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"external_backup_owner_unsupported:{Name}");
}
