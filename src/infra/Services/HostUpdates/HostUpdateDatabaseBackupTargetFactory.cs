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
    /// <paramref name="printFarmerVisibleBackupDirectory"/> and
    /// <paramref name="sqlServerVisibleBackupDirectory"/> (only meaningful when
    /// <paramref name="dbConfig"/> is SQL Server) configure the round-trip mapping check
    /// exposed via <see cref="IHostUpdateServerSideBackupTarget"/> (issue #2788); leaving either
    /// empty makes that check fail closed with explicit evidence rather than skip silently.
    /// </summary>
    public static IHostUpdateBackupTarget CreateBackupTarget(
        string name,
        Farm.Infrastructure.Data.DatabaseProviderConfiguration dbConfig,
        IHostUpdateProcessRunner processRunner,
        IHostUpdateExecutableResolver executableResolver,
        TimeSpan timeout,
        bool isExternallyOwned,
        string printFarmerVisibleBackupDirectory = "",
        string sqlServerVisibleBackupDirectory = "")
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
                () => executableResolver.Resolve("sqlite3"),
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
                () => executableResolver.Resolve("pg_dump"),
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
            var inner = new ProcessDatabaseBackupTarget(
                name,
                isExternallyOwned: false,
                processRunner,
                () => executableResolver.Resolve("sqlcmd"),
                dest =>
                [
                    "-S", builder.DataSource,
                    "-b",
                    .. connectionArgs,
                    "-Q", $"BACKUP DATABASE [{EscapeBracketedIdentifier(database)}] TO DISK = N'{EscapeQuotedLiteral(Path.Combine(dest, SqlServerFileName))}' WITH INIT",
                ],
                timeout,
                connectionEnvironment);

            // Wrapped (never bypassing the lazy resolveFileName/fail-explicit contract #2787
            // established) so availability checks can prove the visible-backup-path mapping is
            // real (#2788) without reintroducing eager resolution: the wrapper's constructor
            // captures only already-lazy delegates and configuration values, and only actually
            // resolves sqlcmd or touches the filesystem when VerifyVisibleBackupPathMappingAsync
            // is invoked.
            return new SqlServerProcessDatabaseBackupTarget(
                inner,
                processRunner,
                () => executableResolver.Resolve("sqlcmd"),
                builder.DataSource,
                connectionArgs,
                connectionEnvironment,
                printFarmerVisibleBackupDirectory,
                sqlServerVisibleBackupDirectory,
                timeout);
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
    /// the backup-target factory already provides for backup.
    /// </summary>
    public static Func<string, HostUpdateRestoreCommand> CreateRestoreCommand(
        Farm.Infrastructure.Data.DatabaseProviderConfiguration dbConfig,
        IHostUpdateExecutableResolver executableResolver)
    {
        ArgumentNullException.ThrowIfNull(dbConfig);

        if (dbConfig.IsSqlite)
        {
            string dbFilePath = new SqliteConnectionStringBuilder(dbConfig.ConnectionString).DataSource;
            return targetDirectory => new HostUpdateRestoreCommand(
                executableResolver.Resolve("sqlite3"),
                [dbFilePath, $".restore '{EscapeQuotedLiteral(Path.Combine(targetDirectory, SqliteFileName))}'"],
                null);
        }

        if (dbConfig.IsPostgres)
        {
            var builder = new NpgsqlConnectionStringBuilder(dbConfig.ConnectionString);
            string password = builder.Password ?? string.Empty;
            return targetDirectory => new HostUpdateRestoreCommand(
                executableResolver.Resolve("pg_restore"),
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
                executableResolver.Resolve("sqlcmd"),
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
    internal static string EscapeQuotedLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

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
/// Wraps a <see cref="ProcessDatabaseBackupTarget"/> configured for SQL Server with a real
/// round-trip verification of the visible-backup-path mapping (issue #2788): PrintFarmer's
/// <see cref="HostUpdateExecutionOptions.BackupRootDirectory"/>-derived directory versus the SQL
/// Server engine's own <see cref="HostUpdateExecutionOptions.SqlServerVisibleBackupDirectory"/>.
/// Delegates every normal <see cref="IHostUpdateBackupTarget"/> operation unchanged to the
/// wrapped target; only adds <see cref="IHostUpdateServerSideBackupTarget"/>. Every dependency
/// captured here is either already-lazy (<paramref name="resolveFileName"/>) or a plain
/// configuration value, so constructing this wrapper never resolves <c>sqlcmd</c> or touches the
/// filesystem -- only <see cref="VerifyVisibleBackupPathMappingAsync"/> does, and only when
/// actually invoked (preserving the lazy-resolution, no-DI-graph-crash contract #2787
/// established for <see cref="ProcessDatabaseBackupTarget"/>).
/// </summary>
internal sealed class SqlServerProcessDatabaseBackupTarget(
    IHostUpdateBackupTarget inner,
    IHostUpdateProcessRunner processRunner,
    Func<string> resolveFileName,
    string dataSource,
    IReadOnlyList<string> connectionArguments,
    IReadOnlyDictionary<string, string>? connectionEnvironment,
    string printFarmerVisibleBackupDirectory,
    string sqlServerVisibleBackupDirectory,
    TimeSpan timeout) : IHostUpdateBackupTarget, IHostUpdateServerSideBackupTarget
{
    private const string ProbeFilePrefix = "printfarmer-mapping-probe-";

    public string Name => inner.Name;

    public bool IsExternallyOwned => inner.IsExternallyOwned;

    public Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken) =>
        inner.BackupAsync(destinationDirectory, cancellationToken);

    /// <summary>
    /// Instructs the SQL Server engine itself to write a small, disposable probe file (a
    /// <c>BACKUP DATABASE [master]</c> -- the same statement shape and code path real backups
    /// use, so this proves the exact mapping production backups depend on, not a synthetic
    /// stand-in) to the server-visible directory, then confirms PrintFarmer can read that exact
    /// physical file back at its own mapped path. Never assumes success from configuration
    /// presence: an empty/relative directory, a failed <c>sqlcmd</c> invocation, or a probe file
    /// that the engine reports as written but PrintFarmer cannot see all fail closed with
    /// distinct, explicit evidence.
    /// </summary>
    public async Task<string?> VerifyVisibleBackupPathMappingAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sqlServerVisibleBackupDirectory))
        {
            return "sql_server_visible_directory_not_configured";
        }

        if (!Path.IsPathRooted(sqlServerVisibleBackupDirectory))
        {
            return "sql_server_visible_directory_not_absolute";
        }

        if (string.IsNullOrWhiteSpace(printFarmerVisibleBackupDirectory))
        {
            return "printfarmer_visible_directory_not_configured";
        }

        if (!Path.IsPathRooted(printFarmerVisibleBackupDirectory))
        {
            return "printfarmer_visible_directory_not_absolute";
        }

        string probeFileName = ProbeFilePrefix + Guid.NewGuid().ToString("N") + ".bak";
        string serverVisiblePath = Path.Combine(sqlServerVisibleBackupDirectory, probeFileName);
        string printFarmerVisiblePath = Path.Combine(printFarmerVisibleBackupDirectory, probeFileName);

        HostUpdateProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                resolveFileName(),
                [
                    "-S", dataSource,
                    "-b",
                    .. connectionArguments,
                    "-Q", $"BACKUP DATABASE [master] TO DISK = N'{HostUpdateDatabaseBackupTargetFactory.EscapeQuotedLiteral(serverVisiblePath)}' WITH INIT, COPY_ONLY",
                ],
                timeout,
                cancellationToken,
                connectionEnvironment).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"probe_backup_invocation_failed:{exception.GetType().Name}";
        }

        if (!result.Succeeded)
        {
            return "probe_backup_command_failed";
        }

        try
        {
            var probeFileInfo = new FileInfo(printFarmerVisiblePath);
            if (!probeFileInfo.Exists || probeFileInfo.Length == 0)
            {
                return "probe_file_not_visible_from_printfarmer";
            }
        }
        finally
        {
            TryDeleteProbeFile(printFarmerVisiblePath);
        }

        return null;
    }

    private static void TryDeleteProbeFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup only: a probe file that cannot be deleted (e.g. a read-only
            // mount for the app side) does not itself invalidate a mapping that was otherwise
            // successfully verified as readable.
        }
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
