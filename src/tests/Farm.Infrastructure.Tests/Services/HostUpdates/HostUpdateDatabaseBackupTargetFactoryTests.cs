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

    [Fact]
    public async Task CreateBackupTarget_ExternallyOwned_ReturnsFailClosedTarget()
    {
        var dbConfig = new DatabaseProviderConfiguration { Provider = "postgres", ConnectionString = "Host=db;Database=x;Username=u;Password=p" };

        IHostUpdateBackupTarget target = HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
            "database", dbConfig, new RecordingProcessRunner(), TimeSpan.FromSeconds(30), isExternallyOwned: true);

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
            "database", dbConfig, runner, TimeSpan.FromSeconds(30), isExternallyOwned: false);
        await target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

        runner.LastFileName.Should().Be("sqlite3");
        target.IsExternallyOwned.Should().BeFalse();
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
            "database", dbConfig, runner, TimeSpan.FromSeconds(30), isExternallyOwned: false);
        await target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

        runner.LastFileName.Should().Be("pg_dump");
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
            "database", dbConfig, runner, TimeSpan.FromSeconds(30), isExternallyOwned: false);
        await target.BackupAsync(Path.GetTempPath(), CancellationToken.None);

        runner.LastFileName.Should().Be("sqlcmd");
        runner.LastArguments.Should().Contain("-b");
        runner.LastArguments.Should().Contain(a => a.Contains("BACKUP DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateRestoreCommand_Sqlite_ReturnsSqlite3RestoreScript()
    {
        var dbConfig = new DatabaseProviderConfiguration { Provider = "sqlite", ConnectionString = "Data Source=farm.db" };

        Func<string, HostUpdateRestoreCommand> restoreCommand = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig);
        HostUpdateRestoreCommand command = restoreCommand(Path.GetTempPath());

        command.FileName.Should().Be("sqlite3");
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

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig)(Path.GetTempPath());

        command.FileName.Should().Be("pg_restore");
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

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig)(Path.GetTempPath());

        command.FileName.Should().Be("sqlcmd");
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
            "database", dbConfig, runner, TimeSpan.FromSeconds(30), isExternallyOwned: false);
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

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig)(Path.GetTempPath());

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

        Action act = () => HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig);

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
            "database", dbConfig, runner, TimeSpan.FromSeconds(30), isExternallyOwned: false);
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

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig)(Path.GetTempPath());

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
            "database", dbConfig, runner, TimeSpan.FromSeconds(30), isExternallyOwned: false);
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

        HostUpdateRestoreCommand command = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig)(hostileTargetDirectory);

        string query = command.Arguments.Single(a => a.Contains("RESTORE DATABASE", StringComparison.Ordinal));
        query.Should().Contain("it''s-a-trap");
        query.Should().NotContain("N'" + hostileTargetDirectory);
    }
}
