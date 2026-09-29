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
    /// <paramref name="backupRootDirectory"/> (only meaningful when <paramref name="dbConfig"/>
    /// is SQL Server) is the exact same directory <see cref="HostUpdateBackupCoordinator"/> uses
    /// to build every real backup's destination directory. It drives the round-trip mapping
    /// check exposed via <see cref="IHostUpdateServerSideBackupTarget"/> (issue #2788), which
    /// deliberately verifies this one directory -- not a separately configured "SQL Server side"
    /// path -- because that is the exact directory the SQL Server engine is asked to write into
    /// for a real backup; verifying any other path would not prove what production backups
    /// depend on. Leaving it empty makes the check fail closed with explicit evidence rather
    /// than skip silently.
    /// </summary>
    public static IHostUpdateBackupTarget CreateBackupTarget(
        string name,
        Farm.Infrastructure.Data.DatabaseProviderConfiguration dbConfig,
        IHostUpdateProcessRunner processRunner,
        IHostUpdateExecutableResolver executableResolver,
        TimeSpan timeout,
        bool isExternallyOwned,
        string backupRootDirectory = "")
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
                backupRootDirectory,
                timeout,
                IsSqlServerPasswordMissing(builder));
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
        IHostUpdateExecutableResolver executableResolver,
        IHostUpdatePostgresRestorePreparer? postgresRestorePreparer = null)
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
            string connectionString = dbConfig.ConnectionString;
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            string password = builder.Password ?? string.Empty;
            IHostUpdatePostgresRestorePreparer preparer = postgresRestorePreparer ?? new NpgsqlHostUpdatePostgresRestorePreparer();

            // pg_restore --clean drops only objects that are in the archive, so anything an
            // interrupted target migration created after the dump would survive the restore
            // (#3177). Clearing every user schema first makes the restore reproduce exactly the
            // pre-update snapshot; --clean --if-exists is kept as a harmless safety net.
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
                string.IsNullOrEmpty(password) ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["PGPASSWORD"] = password })
            {
                PrepareTargetAsync = cancellationToken => preparer.ClearDatabaseAsync(connectionString, cancellationToken),
            };
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
    /// Drops every user schema (including <c>public</c>) with <c>CASCADE</c>, then recreates
    /// <c>public</c> with its previous owner, ACL and comment. The schema-level metadata of
    /// <c>public</c> is preserved because pg_dump emits it only as a delta and pg_restore never
    /// recreated it before; every contained object is left for pg_restore to recreate from the
    /// archive. Runs as one atomic DO block, so a failure changes nothing. Objects outside any
    /// schema (event triggers, publications, casts, foreign servers) are out of scope.
    /// </summary>
    internal const string PostgresClearDatabaseSql = """
        DO $printfarmer_restore$
        DECLARE
            schema_name name;
            public_owner regrole;
            public_acl aclitem[];
            public_comment text;
            grant_entry record;
        BEGIN
            SELECT n.nspowner::regrole, n.nspacl, obj_description(n.oid, 'pg_namespace')
              INTO public_owner, public_acl, public_comment
              FROM pg_namespace n
             WHERE n.nspname = 'public';

            FOR schema_name IN
                SELECT n.nspname
                  FROM pg_namespace n
                 WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
                   AND n.nspname NOT LIKE 'pg\_toast%'
                   AND n.nspname NOT LIKE 'pg\_temp\_%'
                 ORDER BY n.nspname
            LOOP
                EXECUTE format('DROP SCHEMA %I CASCADE', schema_name);
            END LOOP;

            IF public_owner IS NOT NULL THEN
                CREATE SCHEMA public;
                EXECUTE format('ALTER SCHEMA public OWNER TO %s', public_owner);
                IF public_acl IS NOT NULL THEN
                    EXECUTE format('REVOKE ALL ON SCHEMA public FROM %s', public_owner);
                    FOR grant_entry IN
                        SELECT a.grantee, a.privilege_type, a.is_grantable FROM aclexplode(public_acl) a
                    LOOP
                        EXECUTE format(
                            'GRANT %s ON SCHEMA public TO %s%s',
                            grant_entry.privilege_type,
                            CASE WHEN grant_entry.grantee = 0 THEN 'PUBLIC' ELSE grant_entry.grantee::regrole::text END,
                            CASE WHEN grant_entry.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END);
                    END LOOP;
                END IF;

                IF public_comment IS NOT NULL THEN
                    EXECUTE format('COMMENT ON SCHEMA public IS %L', public_comment);
                END IF;
            END IF;
        END
        $printfarmer_restore$;
        """;

    /// <summary>
    /// Longest the pre-restore clear waits for any one lock (#3197, R3194-B03). A session that
    /// still holds a conflicting lock then fails the clear fast with PostgreSQL's
    /// <c>lock_not_available</c> (55P03) error instead of silently consuming the whole restore
    /// budget; the transaction rolls back, so the database is left untouched.
    /// </summary>
    internal static readonly TimeSpan PostgresClearLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Clears the target PostgreSQL database with <see cref="PostgresClearDatabaseSql"/> before
    /// pg_restore runs, so objects created after the dump cannot survive a rollback (#3177).
    /// Pooling is disabled so no idle session outlives the step. The command has no timeout of
    /// its own and is bounded by the restore executor's cancellation, but each lock wait is
    /// bounded by <see cref="PostgresClearLockTimeout"/>.
    /// </summary>
    internal static Task ClearPostgresDatabaseAsync(string connectionString, CancellationToken cancellationToken) =>
        ClearPostgresDatabaseAsync(connectionString, PostgresClearLockTimeout, cancellationToken);

    internal static async Task ClearPostgresDatabaseAsync(string connectionString, TimeSpan lockTimeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lockTimeout, TimeSpan.FromMilliseconds(1));
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            CommandTimeout = 0,
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string setLockTimeout = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"SET LOCAL lock_timeout = '{(long)lockTimeout.TotalMilliseconds}ms'");
        await using (var command = new NpgsqlCommand(setLockTimeout, connection, transaction))
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = new NpgsqlCommand(PostgresClearDatabaseSql, connection, transaction))
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// True when <paramref name="builder"/> uses SQL authentication but neither the connection
    /// string nor the inherited <c>SQLCMDPASSWORD</c> supplies a password. <c>sqlcmd</c> would
    /// then prompt, read end-of-file and exit 0 without running the statement (issue #3162).
    /// Other authentication modes are left to <c>sqlcmd</c>.
    /// </summary>
    internal static bool IsSqlServerPasswordMissing(SqlConnectionStringBuilder builder) =>
        !builder.IntegratedSecurity &&
        builder.Authentication is SqlAuthenticationMethod.NotSpecified or SqlAuthenticationMethod.SqlPassword &&
        string.IsNullOrEmpty(builder.Password) &&
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SQLCMDPASSWORD"));
}

/// <summary>
/// Wraps a <see cref="ProcessDatabaseBackupTarget"/> configured for SQL Server with a real
/// round-trip verification of the visible-backup-path mapping (issue #2788): asks the SQL
/// Server engine to write a probe file into the exact same
/// <see cref="HostUpdateExecutionOptions.BackupRootDirectory"/> real backups use, then confirms
/// PrintFarmer can read that exact file back from that same directory. Delegates every normal
/// <see cref="IHostUpdateBackupTarget"/> operation unchanged to the wrapped target; only adds
/// <see cref="IHostUpdateServerSideBackupTarget"/>. Every dependency captured here is either
/// already-lazy (<paramref name="resolveFileName"/>) or a plain configuration value, so
/// constructing this wrapper never resolves <c>sqlcmd</c> or touches the filesystem -- only
/// <see cref="VerifyVisibleBackupPathMappingAsync"/> does, and only when actually invoked
/// (preserving the lazy-resolution, no-DI-graph-crash contract #2787 established for
/// <see cref="ProcessDatabaseBackupTarget"/>).
/// </summary>
internal sealed class SqlServerProcessDatabaseBackupTarget(
    IHostUpdateBackupTarget inner,
    IHostUpdateProcessRunner processRunner,
    Func<string> resolveFileName,
    string dataSource,
    IReadOnlyList<string> connectionArguments,
    IReadOnlyDictionary<string, string>? connectionEnvironment,
    string backupRootDirectory,
    TimeSpan timeout,
    bool sqlPasswordMissing = false) : IHostUpdateBackupTarget, IHostUpdateServerSideBackupTarget
{
    /// <summary>Evidence returned by the mapping check when SQL authentication has no password (#3162).</summary>
    internal const string SqlPasswordMissingEvidence = "sql_password_missing";

    // Fixed (not per-invocation-unique) name: BACKUP DATABASE ... WITH INIT overwrites an
    // existing file at this path, so every ~5-minute availability re-check reuses and
    // overwrites the same single probe file instead of accumulating a new one on the SQL
    // Server volume each time the mapping is broken and PrintFarmer cannot see (and so cannot
    // clean up) the file it just asked the engine to write.
    internal const string ProbeFileName = "printfarmer-mapping-probe.bak";

    // The probe backs up only [master] with COPY_ONLY -- a tiny, fixed-size operation -- so it
    // should never need anywhere near the full configured BackupTimeoutSeconds (which may be
    // tens of minutes, sized for a real production-database backup). Capping it keeps a
    // hung/unreachable SQL Server from blocking every ~5-minute availability check for that long.
    private static readonly TimeSpan MaxProbeTimeout = TimeSpan.FromSeconds(30);

    public string Name => inner.Name;

    public bool IsExternallyOwned => inner.IsExternallyOwned;

    public Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken) =>
        sqlPasswordMissing
            ? throw new HostUpdateBackupCredentialsMissingException(Name)
            : inner.BackupAsync(destinationDirectory, cancellationToken);

    /// <summary>
    /// Instructs the SQL Server engine itself to write a small, disposable probe file (a
    /// <c>BACKUP DATABASE [master]</c> -- the same statement shape and code path real backups
    /// use) into the configured <c>backupRootDirectory</c>'s value -- the same
    /// <see cref="HostUpdateExecutionOptions.BackupRootDirectory"/> every real backup destination
    /// is created under (see <see cref="HostUpdateBackupCoordinator"/>) -- then confirms
    /// PrintFarmer can read that exact physical file back
    /// from that same directory. Deliberately does not accept a separately configured
    /// "SQL Server side" directory: production backups never translate the destination path, so
    /// verifying anything other than the literal directory real backups use would not prove the
    /// mapping those backups actually depend on. Never assumes success from configuration
    /// presence: an empty/relative directory, a failed <c>sqlcmd</c> invocation, or a probe file
    /// that the engine reports as written but PrintFarmer cannot see all fail closed with
    /// distinct, explicit evidence.
    /// </summary>
    public async Task<string?> VerifyVisibleBackupPathMappingAsync(CancellationToken cancellationToken)
    {
        if (sqlPasswordMissing)
        {
            return SqlPasswordMissingEvidence;
        }

        if (string.IsNullOrWhiteSpace(backupRootDirectory))
        {
            return "backup_root_directory_not_configured";
        }

        if (!Path.IsPathRooted(backupRootDirectory))
        {
            return "backup_root_directory_not_absolute";
        }

        string probePath = Path.Combine(backupRootDirectory, ProbeFileName);
        TimeSpan probeTimeout = timeout < MaxProbeTimeout ? timeout : MaxProbeTimeout;

        // Ensure the directory exists from PrintFarmer's own side before asking the engine to
        // write into it. On a freshly configured host, RootDirectory may exist while its
        // "backups" subdirectory has not yet been created (real backups create it lazily too),
        // which would otherwise make BACKUP DATABASE fail with an OS-level "path not found"
        // error indistinguishable from a genuinely broken mapping.
        try
        {
            Directory.CreateDirectory(backupRootDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"probe_directory_creation_failed:{exception.GetType().Name}";
        }

        // Remove any pre-existing probe file from PrintFarmer's own view before asking the
        // engine to write a fresh one. Without this, a stale file left behind by an earlier
        // successful verification (e.g. because the best-effort cleanup below failed) could
        // still be sitting at probePath if the mapping is later broken -- and the read-back
        // check further down would then observe that stale file and report the mapping
        // verified even though the engine's write never reached PrintFarmer's filesystem at
        // all. Failing closed here (rather than proceeding with an indeterminate starting
        // state) is required so a positive verification always reflects this specific
        // invocation's round trip, not a leftover from a previous one.
        try
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"probe_stale_file_removal_failed:{exception.GetType().Name}";
        }

        HostUpdateProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                resolveFileName(),
                [
                    "-S", dataSource,
                    "-b",
                    .. connectionArguments,
                    "-Q", $"BACKUP DATABASE [master] TO DISK = N'{HostUpdateDatabaseBackupTargetFactory.EscapeQuotedLiteral(probePath)}' WITH INIT, COPY_ONLY",
                ],
                probeTimeout,
                cancellationToken,
                connectionEnvironment).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"probe_backup_invocation_failed:{exception.GetType().Name}";
        }

        if (!result.Succeeded)
        {
            return $"probe_backup_command_failed:{result.ExitCode}";
        }

        try
        {
            // Actually open and read a byte from the file rather than only checking FileInfo
            // metadata: proving PrintFarmer can read real bytes back is what the mapping needs
            // to guarantee (verification/restore later reads this same directory), not merely
            // that a directory-listing sees an entry. A real BACKUP DATABASE [master] file can
            // be several MB, so this deliberately reads a single byte via a stream instead of
            // File.ReadAllBytesAsync -- proving readability does not require buffering the
            // whole backup into memory on every periodic availability check.
            int bytesRead;
            try
            {
                byte[] probeBuffer = new byte[1];
                await using FileStream probeStream = new(probePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                bytesRead = await probeStream.ReadAsync(probeBuffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return "probe_file_not_visible_from_printfarmer";
            }

            if (bytesRead == 0)
            {
                return "probe_file_not_visible_from_printfarmer";
            }
        }
        finally
        {
            TryDeleteProbeFile(probePath);
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
            // Best-effort cleanup only: the round trip this invocation just performed (a fresh
            // sqlcmd write, verified readable above) already proved the mapping, so a failure to
            // delete the probe file here does not invalidate *that* result. It is not silently
            // tolerated indefinitely, though: the next check's pre-invocation removal (above)
            // will hit this same file and, if it still cannot be deleted, fail closed with
            // probe_stale_file_removal_failed -- so a persistently undeletable probe still ends
            // up reporting Unavailable rather than masking the problem forever.
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
    IReadOnlyDictionary<string, string>? Environment)
{
    /// <summary>
    /// Optional in-process step that must succeed before the restore process runs, e.g. clearing
    /// objects the restore tool would not drop itself. It runs only after the backup's checksums
    /// have been verified. Like <see cref="Arguments"/>, it takes part in the generated record
    /// equality by reference, so two commands built by separate factory calls never compare
    /// equal; nothing relies on value equality of this record (R3194-B07).
    /// </summary>
    public Func<CancellationToken, Task>? PrepareTargetAsync { get; init; }
}

/// <summary>
/// Clears a PostgreSQL database before <c>pg_restore</c> runs (#3177). A host boundary like
/// <see cref="IHostUpdateProcessRunner"/>: it is the only restore step that opens a database
/// connection in-process, so it is injectable and replaceable in tests that must never touch
/// a real database.
/// </summary>
public interface IHostUpdatePostgresRestorePreparer
{
    Task ClearDatabaseAsync(string connectionString, CancellationToken cancellationToken);
}

/// <summary>Clears the database over a non-pooled Npgsql connection.</summary>
public sealed class NpgsqlHostUpdatePostgresRestorePreparer : IHostUpdatePostgresRestorePreparer
{
    public Task ClearDatabaseAsync(string connectionString, CancellationToken cancellationToken) =>
        HostUpdateDatabaseBackupTargetFactory.ClearPostgresDatabaseAsync(connectionString, cancellationToken);
}

/// <summary>A backup target this host does not own and therefore never attempts to back up itself.</summary>
public sealed class ExternallyOwnedBackupTarget(string name) : IHostUpdateBackupTarget
{
    public string Name { get; } = name;

    public bool IsExternallyOwned => true;

    public Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"external_backup_owner_unsupported:{Name}");
}
