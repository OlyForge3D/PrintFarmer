using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Focused coverage for <see cref="HostUpdateDatabaseBackupTargetFactory"/> (issue #2663): proves
/// the factory dispatches to the correct provider-native tool per <see cref="DatabaseProviderConfiguration"/>,
/// that an externally-owned database always fails closed, and that PostgreSQL passwords never
/// leak into the process argument list (only via the environment dictionary).
/// </summary>
public class HostUpdateDatabaseBackupTargetFactoryTests
{
    /// <summary>
    /// A deliberately non-secret placeholder used only to prove the credential never reaches the
    /// argument list; it authenticates nothing.
    /// </summary>
    private const string FixtureSqlPassword = "fixture-not-a-real-credential";

    private static readonly ConfiguredHostUpdateExecutableResolver TestExecutableResolver = new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sqlite3"] = Path.Combine(Path.GetTempPath(), "sqlite3.exe"),
        ["pg_dump"] = Path.Combine(Path.GetTempPath(), "pg_dump.exe"),
        ["pg_restore"] = Path.Combine(Path.GetTempPath(), "pg_restore.exe"),
        ["sqlcmd"] = Path.Combine(Path.GetTempPath(), "sqlcmd.exe"),
    });

    private sealed class RecordingProcessRunner : IHostUpdateProcessRunner
    {
        public string? LastFileName { get; private set; }

        public IReadOnlyList<string>? LastArguments { get; private set; }

        public IReadOnlyDictionary<string, string>? LastEnvironment { get; private set; }

        public Task<HostUpdateProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            LastFileName = fileName;
            LastArguments = arguments;
            LastEnvironment = environment;
            return Task.FromResult(new HostUpdateProcessResult(0, string.Empty, string.Empty));
        }
    }

    /// <summary>
    /// Simulates the process runner itself throwing (e.g. the OS refusing to start the
    /// executable) rather than sqlcmd running and returning a nonzero exit code -- a distinct
    /// failure mode from <see cref="ServerSideWritingProcessRunner"/>'s <c>succeeds: false</c>.
    /// </summary>
    private sealed class ThrowingProcessRunner : IHostUpdateProcessRunner
    {
        public Task<HostUpdateProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null) =>
            throw new InvalidOperationException("simulated process start failure");
    }

    [Fact]
    public async Task CreateBackupTarget_ExternallyOwned_ReturnsFailClosedTarget()
    {
        var dbConfig = new DatabaseProviderConfiguration { Provider = "postgres", ConnectionString = "Host=db;Database=x;Username=u;Password=p" };

        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database", dbConfig, new RecordingProcessRunner(), TestExecutableResolver, TimeSpan.FromSeconds(30), isExternallyOwned: true);

        target.IsExternallyOwned.Should().BeTrue();
        Func<Task> act = () => target.BackupAsync(Path.GetTempPath(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateBackupTarget_Sqlite_InvokesSqlite3()
    {
        var dbConfig = new DatabaseProviderConfiguration { Provider = "sqlite", ConnectionString = "Data Source=farm.db" };
        var runner = new RecordingProcessRunner();

        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database", dbConfig, runner, TestExecutableResolver, TimeSpan.FromSeconds(30), isExternallyOwned: false);
        await target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

        runner.LastFileName.Should().Be(TestExecutableResolver.Resolve("sqlite3"));
        target.IsExternallyOwned.Should().BeFalse();
    }

    [Fact]
    public async Task CreateBackupTarget_MissingExecutableMapping_FailsWhenBackupRuns()
    {
        var dbConfig = new DatabaseProviderConfiguration { Provider = "sqlite", ConnectionString = "Data Source=farm.db" };
        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database",
            dbConfig,
            new RecordingProcessRunner(),
            new ConfiguredHostUpdateExecutableResolver(new Dictionary<string, string>(StringComparer.Ordinal)),
            TimeSpan.FromSeconds(30),
            isExternallyOwned: false);

        Func<Task> act = () => target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("host_update_executable_not_configured:sqlite3");
    }

    [Fact]
    public async Task CreateBackupTarget_Postgres_InvokesPgDumpWithPasswordOnlyInEnvironment()
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "postgres",
            ConnectionString = "Host=dbhost;Port=5433;Database=printfarmer;Username=pf;Password=super-secret",
        };
        var runner = new RecordingProcessRunner();

        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database", dbConfig, runner, TestExecutableResolver, TimeSpan.FromSeconds(30), isExternallyOwned: false);
        await target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

        runner.LastFileName.Should().Be(TestExecutableResolver.Resolve("pg_dump"));
        runner.LastArguments.Should().NotContain("super-secret");
        runner.LastEnvironment.Should().ContainKey("PGPASSWORD").WhoseValue.Should().Be("super-secret");
    }

    [Fact]
    public async Task CreateBackupTarget_SqlServer_InvokesSqlcmdWithBackupDatabase()
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "sqlserver",
            ConnectionString = "Server=sqlhost;Database=printfarmer;User Id=sa;Password=super-secret;TrustServerCertificate=True",
        };
        var runner = new RecordingProcessRunner();

        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database", dbConfig, runner, TestExecutableResolver, TimeSpan.FromSeconds(30), isExternallyOwned: false);
        await target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

        runner.LastFileName.Should().Be(TestExecutableResolver.Resolve("sqlcmd"));
        runner.LastArguments.Should().Contain("-b");
        runner.LastArguments.Should().Contain(a => a.Contains("BACKUP DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateRestoreCommand_Sqlite_ReturnsSqlite3RestoreScript()
    {
        var dbConfig = new DatabaseProviderConfiguration { Provider = "sqlite", ConnectionString = "Data Source=farm.db" };

        Func<string, HostUpdateRestoreCommand> restoreCommand = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig, TestExecutableResolver);
        HostUpdateRestoreCommand command = restoreCommand(Path.GetTempPath());

        command.FileName.Should().Be(TestExecutableResolver.Resolve("sqlite3"));
        command.Arguments.Should().Contain(a => a.Contains(".restore", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateRestoreCommand_Postgres_PasswordOnlyInEnvironmentNeverInArguments()
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "postgres",
            ConnectionString = "Host=dbhost;Port=5433;Database=printfarmer;Username=pf;Password=test-only-pw-1",
        };

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig, TestExecutableResolver)(Path.GetTempPath());

        command.FileName.Should().Be(TestExecutableResolver.Resolve("pg_restore"));
        command.Arguments.Should().NotContain(a => a.Contains("test-only-pw-1", StringComparison.Ordinal));
        command.Environment.Should().ContainKey("PGPASSWORD").WhoseValue.Should().Be("test-only-pw-1");
    }

    [Fact]
    public void CreateRestoreCommand_SqlServer_PasswordOnlyInEnvironmentNeverInArguments()
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "sqlserver",
            ConnectionString = "Server=sqlhost;Database=printfarmer;User Id=sa;Password=test-only-pw-2;TrustServerCertificate=True",
        };

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig, TestExecutableResolver)(Path.GetTempPath());

        command.FileName.Should().Be(TestExecutableResolver.Resolve("sqlcmd"));
        command.Arguments.Should().Contain("-b");
        command.Arguments.Should().Contain(a => a.Contains("RESTORE DATABASE", StringComparison.Ordinal));
        command.Arguments.Should().NotContain(a => a.Contains("test-only-pw-2", StringComparison.Ordinal));
        command.Environment.Should().ContainKey("SQLCMDPASSWORD").WhoseValue.Should().Be("test-only-pw-2");
    }

    [Fact]
    public async Task CreateBackupTarget_SqlServer_PasswordOnlyInEnvironmentNeverInArguments()
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "sqlserver",
            ConnectionString = "Server=sqlhost;Database=printfarmer;User Id=sa;Password=test-only-pw-3;TrustServerCertificate=True",
        };
        var runner = new RecordingProcessRunner();

        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database", dbConfig, runner, TestExecutableResolver, TimeSpan.FromSeconds(30), isExternallyOwned: false);
        await target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

        runner.LastArguments.Should().NotContain(a => a.Contains("test-only-pw-3", StringComparison.Ordinal));
        runner.LastEnvironment.Should().ContainKey("SQLCMDPASSWORD").WhoseValue.Should().Be("test-only-pw-3");
    }


    [Fact]
    public void CreateRestoreCommand_SqlServer_UsesExclusiveRestoreBatchWithFailureSafety()
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "sqlserver",
            ConnectionString = "Server=sqlhost;Database=printfarmer;User Id=sa;Password=test-only-pw-6;TrustServerCertificate=True",
        };

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig, TestExecutableResolver)(Path.GetTempPath());

        string query = command.Arguments.Single(a => a.Contains("RESTORE DATABASE", StringComparison.Ordinal));
        query.Should().Contain("SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        query.Should().Contain("WITH REPLACE");
        query.Should().Contain("SET MULTI_USER");
        query.Should().Contain("BEGIN CATCH");
    }
    [Fact]
    public void CreateRestoreCommand_UnsupportedProvider_Throws()
    {
        var dbConfig = new DatabaseProviderConfiguration { Provider = "unknown-provider", ConnectionString = "n/a" };

        Action act = () => HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig, TestExecutableResolver);

        act.Should().Throw<NotSupportedException>();
    }

    // Security-review finding (Kane audit follow-up): a database name or backup-root path
    // containing a single quote or closing bracket must never be able to terminate a T-SQL
    // literal/identifier or a sqlite3 dot-command literal early and inject additional
    // dot-command/T-SQL text into the same batch, even though these values never reach an OS
    // shell. The tests below use hostile-but-realistic values (a bracket in a database name, a
    // single quote in a backup path) and assert the emitted command always contains the escaped
    // (doubled) form, never the raw unescaped value.
    [Fact]
    public async Task CreateBackupTarget_SqlServer_DatabaseNameWithBracket_EscapesIdentifier()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = "sqlhost",
            InitialCatalog = "printfarmer]; DROP TABLE Users; --",
            UserID = "sa",
            Password = "test-only-pw-4",
        };
        var dbConfig = new DatabaseProviderConfiguration { Provider = "sqlserver", ConnectionString = builder.ConnectionString };
        var runner = new RecordingProcessRunner();

        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database", dbConfig, runner, TestExecutableResolver, TimeSpan.FromSeconds(30), isExternallyOwned: false);
        await target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

        string query = runner.LastArguments!.Single(a => a.Contains("BACKUP DATABASE", StringComparison.Ordinal));
        query.Should().Contain("[printfarmer]]; DROP TABLE Users; --]");
    }

    [Fact]
    public void CreateRestoreCommand_SqlServer_DatabaseNameWithBracket_EscapesIdentifier()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = "sqlhost",
            InitialCatalog = "printfarmer]; DROP TABLE Users; --",
            UserID = "sa",
            Password = "test-only-pw-5",
        };
        var dbConfig = new DatabaseProviderConfiguration { Provider = "sqlserver", ConnectionString = builder.ConnectionString };

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig, TestExecutableResolver)(Path.GetTempPath());

        string query = command.Arguments.Single(a => a.Contains("RESTORE DATABASE", StringComparison.Ordinal));
        query.Should().Contain("[printfarmer]]; DROP TABLE Users; --]");
    }

    [Fact]
    public async Task CreateBackupTarget_Sqlite_DestinationPathWithSingleQuote_EscapesLiteral()
    {
        var dbConfig = new DatabaseProviderConfiguration { Provider = "sqlite", ConnectionString = "Data Source=farm.db" };
        var runner = new RecordingProcessRunner();
        string hostileDestination = Path.Combine(Path.GetTempPath(), "it's-a-trap");

        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database", dbConfig, runner, TestExecutableResolver, TimeSpan.FromSeconds(30), isExternallyOwned: false);
        await target.BackupAsync(hostileDestination, CancellationToken.None);

        string backupCommand = runner.LastArguments!.Single(a => a.Contains(".backup", StringComparison.Ordinal));
        backupCommand.Should().Contain("it''s-a-trap");
        backupCommand.Should().NotContain("it's-a-trap");
    }

    [Fact]
    public void CreateRestoreCommand_SqlServer_PathWithSingleQuote_EscapesLiteral()
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "sqlserver",
            ConnectionString = "Server=sqlhost;Database=printfarmer;User Id=sa;Password=test-only-pw-6;TrustServerCertificate=True",
        };
        string hostileTargetDirectory = Path.Combine(Path.GetTempPath(), "it's-a-trap");

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig, TestExecutableResolver)(hostileTargetDirectory);

        string query = command.Arguments.Single(a => a.Contains("RESTORE DATABASE", StringComparison.Ordinal));
        query.Should().Contain("it''s-a-trap");
        query.Should().NotContain("N'" + hostileTargetDirectory);
    }

    /// <summary>
    /// A backup or restore streams the whole database over the sqlcmd connection, and sqlcmd
    /// applies its own build-specific encryption default instead of reading the application's
    /// connection string. Host-update backup/restore therefore *requires* encryption rather than
    /// honouring the ambient configuration: even an explicit <c>Encrypt=False</c> must still be
    /// forced on, so that channel can never carry a full database dump in cleartext.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(";Encrypt=False")]
    [InlineData(";Encrypt=True")]
    [InlineData(";Encrypt=Strict")]
    public async Task CreateBackupTarget_SqlServer_AlwaysRequiresEncryptedTransport(string encryptSetting)
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "sqlserver",
            ConnectionString = $"Server=sqlhost;Database=printfarmer;User Id=sa;Password={FixtureSqlPassword}{encryptSetting}",
        };
        var runner = new RecordingProcessRunner();

        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database", dbConfig, runner, TestExecutableResolver, TimeSpan.FromSeconds(30), isExternallyOwned: false);
        await target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

        runner.LastArguments.Should().Contain("-N");
    }

    [Theory]
    [InlineData("")]
    [InlineData(";Encrypt=False")]
    [InlineData(";Encrypt=True")]
    [InlineData(";Encrypt=Strict")]
    public void CreateRestoreCommand_SqlServer_AlwaysRequiresEncryptedTransport(string encryptSetting)
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "sqlserver",
            ConnectionString = $"Server=sqlhost;Database=printfarmer;User Id=sa;Password={FixtureSqlPassword}{encryptSetting}",
        };

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig, TestExecutableResolver)(Path.GetTempPath());

        command.Arguments.Should().Contain("-N");
    }

    /// <summary>
    /// <c>-C</c> waives only the certificate-chain check, never encryption itself, so it is an
    /// explicit deployment trust policy (a self-signed certificate on a host-local or
    /// private-network SQL Server) rather than a downgrade to unencrypted transport. It is
    /// emitted only when the deployment configured <c>TrustServerCertificate=True</c>, so the
    /// default posture stays encrypt *and* validate.
    /// </summary>
    [Theory]
    [InlineData("", false)]
    [InlineData(";TrustServerCertificate=False", false)]
    [InlineData(";TrustServerCertificate=True", true)]
    public async Task CreateBackupTarget_SqlServer_TrustsServerCertificateOnlyWhenExplicitlyConfigured(
        string trustSetting,
        bool expectTrustCertificate)
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "sqlserver",
            ConnectionString = $"Server=sqlhost;Database=printfarmer;User Id=sa;Password={FixtureSqlPassword}{trustSetting}",
        };
        var runner = new RecordingProcessRunner();

        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database", dbConfig, runner, TestExecutableResolver, TimeSpan.FromSeconds(30), isExternallyOwned: false);
        await target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

        runner.LastArguments!.Contains("-C").Should().Be(expectTrustCertificate);
        runner.LastArguments.Should().Contain("-N");
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(";TrustServerCertificate=False", false)]
    [InlineData(";TrustServerCertificate=True", true)]
    public void CreateRestoreCommand_SqlServer_TrustsServerCertificateOnlyWhenExplicitlyConfigured(
        string trustSetting,
        bool expectTrustCertificate)
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "sqlserver",
            ConnectionString = $"Server=sqlhost;Database=printfarmer;User Id=sa;Password={FixtureSqlPassword}{trustSetting}",
        };

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig, TestExecutableResolver)(Path.GetTempPath());

        command.Arguments.Contains("-C").Should().Be(expectTrustCertificate);
        command.Arguments.Should().Contain("-N");
    }

    /// <summary>
    /// Hardening transport must not regress sqlcmd's credential convention: the password travels
    /// only through <c>SQLCMDPASSWORD</c> and never appears in the argument list, and <c>-b</c>
    /// (fail the process on a T-SQL error) is still passed so a failed backup is never mistaken
    /// for a successful one.
    /// </summary>
    [Fact]
    public async Task CreateBackupTarget_SqlServer_KeepsCredentialsOutOfArgumentsAndFailsOnError()
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "sqlserver",
            ConnectionString = $"Server=sqlhost;Database=printfarmer;User Id=sa;Password={FixtureSqlPassword};Encrypt=False",
        };
        var runner = new RecordingProcessRunner();

        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database", dbConfig, runner, TestExecutableResolver, TimeSpan.FromSeconds(30), isExternallyOwned: false);
        await target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

        runner.LastArguments.Should().Contain("-b");
        runner.LastArguments.Should().NotContain(FixtureSqlPassword);
        runner.LastEnvironment.Should().ContainKey("SQLCMDPASSWORD").WhoseValue.Should().Be(FixtureSqlPassword);
    }

    [Fact]
    public void CreateRestoreCommand_SqlServer_KeepsCredentialsOutOfArguments()
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "sqlserver",
            ConnectionString = $"Server=sqlhost;Database=printfarmer;User Id=sa;Password={FixtureSqlPassword};Encrypt=False",
        };

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig, TestExecutableResolver)(Path.GetTempPath());

        command.Arguments.Should().Contain("-b");
        command.Arguments.Should().NotContain(FixtureSqlPassword);
        command.Environment.Should().ContainKey("SQLCMDPASSWORD").WhoseValue.Should().Be(FixtureSqlPassword);
    }

    /// <summary>
    /// Integrated security keeps the same transport invariant: the credential form changes, but
    /// the channel carrying the dump is the same one, so encryption is still required.
    /// </summary>
    [Fact]
    public void CreateRestoreCommand_SqlServerIntegratedSecurity_StillRequiresEncryptedTransport()
    {
        var dbConfig = new DatabaseProviderConfiguration
        {
            Provider = "sqlserver",
            ConnectionString = "Server=sqlhost;Database=printfarmer;Integrated Security=True;Encrypt=False;TrustServerCertificate=True",
        };

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig, TestExecutableResolver)(Path.GetTempPath());

        command.Arguments.Should().Contain("-E");
        command.Arguments.Should().Contain("-N");
        command.Arguments.Should().Contain("-C");
        command.Arguments.Should().NotContain("-U");
        command.Environment.Should().BeNull();
    }

    /// <summary>
    /// A process runner standing in for the SQL Server engine: when it sees a
    /// <c>BACKUP DATABASE ... TO DISK = N'...'</c> statement it writes a file, simulating the
    /// engine's own filesystem write, so tests exercise a genuine round trip rather than
    /// asserting on configuration shape alone. When <paramref name="physicalWriteRedirectDirectory"/>
    /// is set, the file is written there instead of at the literal path extracted from the SQL
    /// text -- simulating a broken bind mount: the SQL Server container's local disk happens to
    /// have something at that path label, but it is not actually the shared volume PrintFarmer
    /// reads from. This is the realistic "same configured path, different physical storage"
    /// failure mode a single shared <c>BackupRootDirectory</c> can still exhibit.
    /// </summary>
    private sealed class ServerSideWritingProcessRunner(bool succeeds, string? physicalWriteRedirectDirectory = null) : IHostUpdateProcessRunner
    {
        public IReadOnlyList<string>? LastArguments { get; private set; }

        public int CallCount { get; private set; }

        public Task<HostUpdateProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            CallCount++;
            LastArguments = arguments;
            if (succeeds)
            {
                string? query = arguments.FirstOrDefault(a => a.Contains("BACKUP DATABASE", StringComparison.Ordinal));
                if (query is not null)
                {
                    int start = query.IndexOf("N'", StringComparison.Ordinal) + 2;
                    int end = query.IndexOf('\'', start);
                    string configuredPath = query[start..end];
                    string physicalPath = physicalWriteRedirectDirectory is null
                        ? configuredPath
                        : Path.Combine(physicalWriteRedirectDirectory, Path.GetFileName(configuredPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
                    File.WriteAllText(physicalPath, "probe");
                }
            }

            return Task.FromResult(succeeds
                ? new HostUpdateProcessResult(0, string.Empty, string.Empty)
                : new HostUpdateProcessResult(1, string.Empty, "backup failed"));
        }
    }

    private static DatabaseProviderConfiguration SqlServerConfig() => new()
    {
        Provider = "sqlserver",
        ConnectionString = $"Server=sqlhost;Database=printfarmer;User Id=sa;Password={FixtureSqlPassword};TrustServerCertificate=True",
    };

    [Fact]
    public async Task VerifyVisibleBackupPathMappingAsync_SameDirectoryRealBackupsUse_RealRoundTripSucceeds()
    {
        string backupRootDirectory = Directory.CreateTempSubdirectory("hu-mapping-shared-").FullName;
        try
        {
            var runner = new ServerSideWritingProcessRunner(succeeds: true);
            IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
                "database", SqlServerConfig(), runner, TestExecutableResolver,
                TimeSpan.FromSeconds(30), isExternallyOwned: false,
                backupRootDirectory: backupRootDirectory);

            string? evidence = await ((IHostUpdateServerSideBackupTarget)target).VerifyVisibleBackupPathMappingAsync(CancellationToken.None);

            evidence.Should().BeNull();
            Directory.GetFiles(backupRootDirectory).Should().BeEmpty("the probe file must be cleaned up after a successful verification");

            // A stubbed-success runner that never actually writes anything would also leave the
            // directory empty and return null evidence -- so the two assertions above alone do not
            // prove a real write happened. Assert the engine was actually invoked exactly once
            // with a genuine BACKUP DATABASE [master] command targeting the probe path under the
            // configured backup root, using WITH INIT, COPY_ONLY (the same real-backup command
            // shape production code issues), so this test cannot pass vacuously.
            runner.CallCount.Should().Be(1, "the probe must invoke sqlcmd exactly once per verification, not zero times or repeatedly");
            string probeQuery = runner.LastArguments!.Single(a => a.Contains("BACKUP DATABASE", StringComparison.Ordinal));
            probeQuery.Should().Contain("BACKUP DATABASE [master] TO DISK");
            probeQuery.Should().Contain(backupRootDirectory);
            probeQuery.Should().Contain("WITH INIT, COPY_ONLY");
        }
        finally
        {
            Directory.Delete(backupRootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyVisibleBackupPathMappingAsync_ConfiguredPathIsNotActuallyASharedMount_FailsClosedWithDistinctEvidence()
    {
        // Same literal directory string is configured on both "sides" (there is only one
        // configuration value now), but the fake engine's physical write is redirected
        // elsewhere -- reproducing a broken bind mount where the SQL Server container's local
        // disk at that path label is not really the volume PrintFarmer reads from.
        string backupRootDirectory = Directory.CreateTempSubdirectory("hu-mapping-client-").FullName;
        string serverPhysicalStorage = Directory.CreateTempSubdirectory("hu-mapping-server-").FullName;
        try
        {
            IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
                "database", SqlServerConfig(), new ServerSideWritingProcessRunner(succeeds: true, serverPhysicalStorage), TestExecutableResolver,
                TimeSpan.FromSeconds(30), isExternallyOwned: false,
                backupRootDirectory: backupRootDirectory);

            string? evidence = await ((IHostUpdateServerSideBackupTarget)target).VerifyVisibleBackupPathMappingAsync(CancellationToken.None);

            evidence.Should().Be("probe_file_not_visible_from_printfarmer");
            Directory.GetFiles(serverPhysicalStorage).Should().ContainSingle("the engine really did write the probe file -- only PrintFarmer's own read (at the shared path) failed");
        }
        finally
        {
            Directory.Delete(backupRootDirectory, recursive: true);
            Directory.Delete(serverPhysicalStorage, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyVisibleBackupPathMappingAsync_SqlcmdReportsFailure_FailsClosedWithoutAssumingSuccess()
    {
        string backupRootDirectory = Directory.CreateTempSubdirectory("hu-mapping-fail-").FullName;
        try
        {
            IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
                "database", SqlServerConfig(), new ServerSideWritingProcessRunner(succeeds: false), TestExecutableResolver,
                TimeSpan.FromSeconds(30), isExternallyOwned: false,
                backupRootDirectory: backupRootDirectory);

            string? evidence = await ((IHostUpdateServerSideBackupTarget)target).VerifyVisibleBackupPathMappingAsync(CancellationToken.None);

            evidence.Should().Be("probe_backup_command_failed:1", "the evidence must retain sqlcmd's exit code, not just a generic failure marker");
        }
        finally
        {
            Directory.Delete(backupRootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyVisibleBackupPathMappingAsync_ProcessRunnerThrows_FailsClosedWithExceptionTypeEvidence()
    {
        string backupRootDirectory = Directory.CreateTempSubdirectory("hu-mapping-throw-").FullName;
        try
        {
            IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
                "database", SqlServerConfig(), new ThrowingProcessRunner(), TestExecutableResolver,
                TimeSpan.FromSeconds(30), isExternallyOwned: false,
                backupRootDirectory: backupRootDirectory);

            string? evidence = await ((IHostUpdateServerSideBackupTarget)target).VerifyVisibleBackupPathMappingAsync(CancellationToken.None);

            evidence.Should().Be(
                "probe_backup_invocation_failed:InvalidOperationException",
                "a thrown exception from starting the process is a distinct failure mode from sqlcmd running and reporting a nonzero exit code");
        }
        finally
        {
            Directory.Delete(backupRootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyVisibleBackupPathMappingAsync_BackupRootParentIsAFile_FailsClosedWithDirectoryCreationEvidence()
    {
        // Directory.CreateDirectory(backupRootDirectory) throws when a path segment above it
        // already exists as a file rather than a directory -- reproducing a misconfigured
        // RootDirectory that collides with an existing file. This must fail closed with the
        // dedicated probe_directory_creation_failed evidence (distinguishable from every other
        // evidence string), not silently proceed or report a different failure mode.
        string parentDirectory = Directory.CreateTempSubdirectory("hu-mapping-dircreate-").FullName;
        string fileBlockingCreation = Path.Combine(parentDirectory, "not-a-directory");
        await File.WriteAllTextAsync(fileBlockingCreation, "this is a file, not a directory");
        string backupRootDirectory = Path.Combine(fileBlockingCreation, "backups");
        try
        {
            IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
                "database", SqlServerConfig(), new ServerSideWritingProcessRunner(succeeds: true), TestExecutableResolver,
                TimeSpan.FromSeconds(30), isExternallyOwned: false,
                backupRootDirectory: backupRootDirectory);

            string? evidence = await ((IHostUpdateServerSideBackupTarget)target).VerifyVisibleBackupPathMappingAsync(CancellationToken.None);

            evidence.Should().Be(
                "probe_directory_creation_failed:IOException",
                "a backup root whose parent path is occupied by a file must fail closed with directory-creation evidence, not a different code path's evidence");
        }
        finally
        {
            Directory.Delete(parentDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyVisibleBackupPathMappingAsync_RepeatedChecksAgainstBrokenMapping_ReuseSingleProbeFileWithoutAccumulating()
    {
        // Reproduces the availability hosted service re-checking a persistently broken mapping
        // every ~5 minutes: the fixed probe filename must be overwritten in place on the SQL
        // Server volume, not accumulate a new file per check.
        string backupRootDirectory = Directory.CreateTempSubdirectory("hu-mapping-repeat-client-").FullName;
        string serverPhysicalStorage = Directory.CreateTempSubdirectory("hu-mapping-repeat-server-").FullName;
        try
        {
            IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
                "database", SqlServerConfig(), new ServerSideWritingProcessRunner(succeeds: true, serverPhysicalStorage), TestExecutableResolver,
                TimeSpan.FromSeconds(30), isExternallyOwned: false,
                backupRootDirectory: backupRootDirectory);
            var serverSideTarget = (IHostUpdateServerSideBackupTarget)target;

            await serverSideTarget.VerifyVisibleBackupPathMappingAsync(CancellationToken.None);
            await serverSideTarget.VerifyVisibleBackupPathMappingAsync(CancellationToken.None);
            await serverSideTarget.VerifyVisibleBackupPathMappingAsync(CancellationToken.None);

            Directory.GetFiles(serverPhysicalStorage).Should().ContainSingle(
                "a fixed, reused probe filename must overwrite in place on every re-check, never accumulate a new file per invocation");
        }
        finally
        {
            Directory.Delete(backupRootDirectory, recursive: true);
            Directory.Delete(serverPhysicalStorage, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyVisibleBackupPathMappingAsync_StalePrintFarmerVisibleProbeFile_DoesNotFalselyVerifyBrokenMapping()
    {
        // Simulates a stale probe file left behind at the PrintFarmer-visible path (e.g. from an
        // earlier successful verification whose best-effort cleanup failed). If the mapping is
        // then broken, the engine's write is redirected elsewhere -- but without removing the
        // stale file first, the read-back would observe it and incorrectly report success.
        string backupRootDirectory = Directory.CreateTempSubdirectory("hu-mapping-stale-client-").FullName;
        string serverPhysicalStorage = Directory.CreateTempSubdirectory("hu-mapping-stale-server-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(backupRootDirectory, SqlServerProcessDatabaseBackupTarget.ProbeFileName),
                "stale probe from an earlier check");

            IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
                "database", SqlServerConfig(), new ServerSideWritingProcessRunner(succeeds: true, serverPhysicalStorage), TestExecutableResolver,
                TimeSpan.FromSeconds(30), isExternallyOwned: false,
                backupRootDirectory: backupRootDirectory);

            string? evidence = await ((IHostUpdateServerSideBackupTarget)target).VerifyVisibleBackupPathMappingAsync(CancellationToken.None);

            evidence.Should().Be(
                "probe_file_not_visible_from_printfarmer",
                "a stale leftover file must never be mistaken for this invocation's own round trip");
        }
        finally
        {
            Directory.Delete(backupRootDirectory, recursive: true);
            Directory.Delete(serverPhysicalStorage, recursive: true);
        }
    }

    [Theory]
    [InlineData("", "backup_root_directory_not_configured")]
    [InlineData("relative/backups", "backup_root_directory_not_absolute")]
    public async Task VerifyVisibleBackupPathMappingAsync_UnconfiguredOrRelativeDirectory_FailsClosedWithoutInvokingSqlcmd(
        string backupRootDirectory, string expectedEvidence)
    {
        var runner = new RecordingProcessRunner();
        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database", SqlServerConfig(), runner, TestExecutableResolver,
            TimeSpan.FromSeconds(30), isExternallyOwned: false,
            backupRootDirectory: backupRootDirectory);

        string? evidence = await ((IHostUpdateServerSideBackupTarget)target).VerifyVisibleBackupPathMappingAsync(CancellationToken.None);

        evidence.Should().Be(expectedEvidence);
        runner.LastFileName.Should().BeNull("an unresolved/misconfigured mapping must never invoke sqlcmd");
    }

    [Fact]
    public async Task CreateBackupTarget_SqlServer_StillDelegatesNormalBackupAsyncToInnerTargetWhenWrapped()
    {
        string backupRootDirectory = Directory.CreateTempSubdirectory("hu-mapping-delegate-").FullName;
        try
        {
            var runner = new RecordingProcessRunner();
            IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
                "database", SqlServerConfig(), runner, TestExecutableResolver, TimeSpan.FromSeconds(30), isExternallyOwned: false,
                backupRootDirectory: backupRootDirectory);

            await target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

            runner.LastFileName.Should().Be(TestExecutableResolver.Resolve("sqlcmd"));
            runner.LastArguments.Should().Contain(a => a.Contains("BACKUP DATABASE", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(backupRootDirectory, recursive: true);
        }
    }
}

