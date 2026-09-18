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
            SqlConnectionStringBuilder builder = EncryptedSqlServerBuilder(dbConfig.ConnectionString);
            string database = builder.InitialCatalog;
            (IReadOnlyList<string> connectionArgs, IReadOnlyDictionary<string, string>? connectionEnvironment) = SqlServerConnectionArgs(builder);
            return new ProcessDatabaseBackupTarget(
                name,
                isExternallyOwned: false,
                processRunner,
                "sqlcmd",
                dest =>
                [
                    "-S", builder.DataSource,
                    "-b",
                    .. connectionArgs,
                    "-Q", $"BACKUP DATABASE [{EscapeBracketedIdentifier(database)}] TO DISK = N'{EscapeQuotedLiteral(Path.Combine(dest, SqlServerFileName))}' WITH INIT",
                ],
                timeout,
                connectionEnvironment);
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
            SqlConnectionStringBuilder builder = EncryptedSqlServerBuilder(dbConfig.ConnectionString);
            string database = builder.InitialCatalog;
            (IReadOnlyList<string> connectionArgs, IReadOnlyDictionary<string, string>? connectionEnvironment) = SqlServerConnectionArgs(builder);
            return targetDirectory => new HostUpdateRestoreCommand(
                "sqlcmd",
                [
                    "-S", builder.DataSource,
                    "-b",
                    .. connectionArgs,
                    "-Q", $"BEGIN TRY ALTER DATABASE [{EscapeBracketedIdentifier(database)}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [{EscapeBracketedIdentifier(database)}] FROM DISK = N'{EscapeQuotedLiteral(Path.Combine(targetDirectory, SqlServerFileName))}' WITH REPLACE; ALTER DATABASE [{EscapeBracketedIdentifier(database)}] SET MULTI_USER; END TRY BEGIN CATCH IF DB_ID(N'{EscapeQuotedLiteral(database)}') IS NOT NULL ALTER DATABASE [{EscapeBracketedIdentifier(database)}] SET MULTI_USER; THROW; END CATCH",
                ],
                connectionEnvironment);
        }

        throw new NotSupportedException($"unsupported_restore_provider:{dbConfig.Provider}");
    }

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

    /// <summary>
    /// Parses a SQL Server connection string and unconditionally hardens its transport security:
    /// host-update backup and restore stream an entire database over that connection, so this
    /// factory *requires* encryption rather than honouring whatever the ambient application
    /// connection string happened to configure. A configured <c>Encrypt=false</c> is deliberately
    /// overridden to <see cref="SqlConnectionEncryptOption.Mandatory"/>; only an explicitly
    /// stronger setting (<c>Strict</c>) is preserved. The returned builder is therefore the single
    /// place where the "host-update SQL Server traffic is always encrypted" invariant is
    /// established, for both the parsed/effective connection string and the derived
    /// <c>sqlcmd</c> switches.
    /// </summary>
    private static SqlConnectionStringBuilder EncryptedSqlServerBuilder(string connectionString)
    {
        // Keep the security invariant visible to both the parser and static analysis. The
        // explicit property assignment below remains authoritative when the application string
        // contains Encrypt=False.
        var builder = new SqlConnectionStringBuilder($"Encrypt=True;{connectionString}");

        if (builder.Encrypt != SqlConnectionEncryptOption.Strict)
        {
            builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
        }

        return builder;
    }

    /// <summary>
    /// Projects one already-hardened <see cref="EncryptedSqlServerBuilder"/> connection onto the
    /// equivalent <c>sqlcmd</c> switches, so backup and restore share a single argument grammar.
    /// <para>
    /// <c>sqlcmd</c> applies its own build-specific encryption default rather than inheriting the
    /// application's connection string, so <c>-N</c> is always emitted: without it a backup or
    /// restore could traverse the network unencrypted even when every ordinary application
    /// connection to the same server requires encryption.
    /// </para>
    /// <para>
    /// <c>-C</c> (trust the server certificate without validating it) is emitted *only* when the
    /// deployment explicitly configured <c>TrustServerCertificate=true</c>. That is an explicit
    /// deployment trust policy -- appropriate for a self-signed certificate on a host-local or
    /// private-network SQL Server -- and never disables encryption: the channel is still
    /// encrypted under <c>-N</c>, only the certificate chain check is waived. It is never
    /// inferred, so the default posture is encrypt *and* validate.
    /// </para>
    /// <para>
    /// Credentials follow <c>sqlcmd</c>'s own convention: the username may safely appear as a
    /// process argument, but the password travels only through the <c>SQLCMDPASSWORD</c>
    /// environment variable so it never appears in a process argument list -- visible via
    /// <c>ps</c>/process listings or argv-capturing audit logging -- for either backup or restore.
    /// </para>
    /// </summary>
    private static (IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment) SqlServerConnectionArgs(SqlConnectionStringBuilder builder)
    {
        var arguments = new List<string> { "-N" };

        if (builder.TrustServerCertificate)
        {
            arguments.Add("-C");
        }

        if (builder.IntegratedSecurity)
        {
            arguments.Add("-E");
            return (arguments, null);
        }

        string password = builder.Password ?? string.Empty;
        IReadOnlyDictionary<string, string>? environment = string.IsNullOrEmpty(password)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["SQLCMDPASSWORD"] = password };
        arguments.Add("-U");
        arguments.Add(builder.UserID);
        return (arguments, environment);
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
