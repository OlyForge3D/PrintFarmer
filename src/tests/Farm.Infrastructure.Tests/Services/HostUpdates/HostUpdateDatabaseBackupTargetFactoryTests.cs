using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
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
        runner.LastArguments.Should().Contain(a => a.Contains("BACKUP DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateRestoreCommand_Sqlite_ReturnsSqlite3RestoreScript()
    {
        var dbConfig = new DatabaseProviderConfiguration { Provider = "sqlite", ConnectionString = "Data Source=farm.db" };

        Func<string, IReadOnlyList<string>> restoreCommand = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig);
        IReadOnlyList<string> args = restoreCommand(Path.GetTempPath());

        args[0].Should().Be("-c");
        args[1].Should().Contain("sqlite3").And.Contain(".restore");
    }

    [Fact]
    public void CreateRestoreCommand_UnsupportedProvider_Throws()
    {
        var dbConfig = new DatabaseProviderConfiguration { Provider = "unknown-provider", ConnectionString = "n/a" };

        Action act = () => HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig);

        act.Should().Throw<NotSupportedException>();
    }
}
