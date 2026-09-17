using Farm.Infrastructure.Data.Migrations;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Focused coverage for the host-update executor's availability contract (issue #2663): proves
/// <see cref="HostUpdateExecutionAvailabilityProvider"/> reports Available only when every
/// dependency is positively proven, and reports the exact reason otherwise, so an operator or
/// the #2666 scheduler never mistakes "unconfigured" for "ready".
/// </summary>
public class HostUpdateExecutionAvailabilityTests
{
    private sealed class FakeJournal : IHostUpdateExecutionJournal
    {
        public bool ThrowOnRead { get; set; }

        public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId)
        {
            if (ThrowOnRead)
            {
                throw new InvalidDataException("journal_corrupt");
            }

            return [];
        }

        public void Append(HostUpdateExecutionActivity activity)
        {
        }
    }

    private sealed class FakeMigrationTarget : IHostUpdateMigrationTarget
    {
        public string ContextName => "Fake";

        public Task<string> GetProviderNameAsync(CancellationToken cancellationToken) => Task.FromResult("Microsoft.EntityFrameworkCore.Sqlite");

        public Task<DatabaseMigrationResult> MigrateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DatabaseMigrationResult(false, []));
    }

    private sealed class FakeBackupTarget : IHostUpdateBackupTarget
    {
        public string Name => "fake";

        public bool IsExternallyOwned => false;

        public Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeProcessRunner(bool dockerAvailable) : IHostUpdateProcessRunner
    {
        public Task<HostUpdateProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null) =>
            Task.FromResult(dockerAvailable
                ? new HostUpdateProcessResult(0, "24.0.0", string.Empty)
                : new HostUpdateProcessResult(1, string.Empty, "not found"));
    }

    private static HostUpdateExecutionOptions ValidOptions(string root, string composeFile) => new()
    {
        RootDirectory = root,
        ComposeFiles = [composeFile],
    };

    [Fact]
    public async Task CheckAsync_EveryDependencyHealthy_ReportsAvailable()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            var provider = new HostUpdateExecutionAvailabilityProvider(
                ValidOptions(root, composeFile),
                new FakeJournal(),
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                new FakeProcessRunner(dockerAvailable: true));

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Available);
            result.Reasons.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_RootDirectoryNotConfigured_ReportsUnavailableWithReason()
    {
        var provider = new HostUpdateExecutionAvailabilityProvider(
            new HostUpdateExecutionOptions { RootDirectory = string.Empty, ComposeFiles = ["missing.yml"] },
            new FakeJournal(),
            [new FakeMigrationTarget()],
            [new FakeBackupTarget()],
            new FakeProcessRunner(dockerAvailable: true));

        HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

        result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
        result.Reasons.Should().Contain("root_directory_not_configured");
    }

    [Fact]
    public async Task CheckAsync_CorruptJournal_ReportsUnavailableWithJournalReason()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            var provider = new HostUpdateExecutionAvailabilityProvider(
                ValidOptions(root, composeFile),
                new FakeJournal { ThrowOnRead = true },
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                new FakeProcessRunner(dockerAvailable: true));

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain(r => r.StartsWith("journal_corrupt", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_NoMigrationOrBackupTargetsConfigured_ReportsBothReasons()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            var provider = new HostUpdateExecutionAvailabilityProvider(
                ValidOptions(root, composeFile),
                new FakeJournal(),
                [],
                [],
                new FakeProcessRunner(dockerAvailable: true));

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain("no_migration_targets_configured");
            result.Reasons.Should().Contain("no_backup_targets_configured");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_ComposeFileMissing_ReportsReason()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        try
        {
            var provider = new HostUpdateExecutionAvailabilityProvider(
                ValidOptions(root, Path.Combine(root, "does-not-exist.yml")),
                new FakeJournal(),
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                new FakeProcessRunner(dockerAvailable: true));

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain(r => r.StartsWith("compose_file_missing", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_DockerRuntimeUnavailable_ReportsReason()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            var provider = new HostUpdateExecutionAvailabilityProvider(
                ValidOptions(root, composeFile),
                new FakeJournal(),
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                new FakeProcessRunner(dockerAvailable: false));

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain("docker_runtime_unavailable");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Holder_DefaultsToUnavailableUntilFirstCheck()
    {
        var holder = new HostUpdateExecutionAvailabilityHolder();

        holder.Current.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
        holder.Current.Reasons.Should().Contain("not_yet_checked");
    }

    [Fact]
    public void Holder_UpdateThenCurrent_ReflectsLatestValue()
    {
        var holder = new HostUpdateExecutionAvailabilityHolder();
        HostUpdateExecutionAvailability available = HostUpdateExecutionAvailability.Available(DateTimeOffset.UtcNow);

        holder.Update(available);

        holder.Current.Should().Be(available);
    }

    [Fact]
    public async Task HostedService_RestartReconciliation_ComputesAvailabilityImmediatelyOnStartup()
    {
        var holder = new HostUpdateExecutionAvailabilityHolder();
        var probeCount = 0;
        var provider = new DelegateAvailabilityProvider(() =>
        {
            probeCount++;
            return HostUpdateExecutionAvailability.Available(DateTimeOffset.UtcNow);
        });
        var service = new HostUpdateExecutionAvailabilityHostedService(
            provider,
            holder,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HostUpdateExecutionAvailabilityHostedService>.Instance,
            TimeSpan.FromMinutes(30));

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);

        probeCount.Should().BeGreaterThanOrEqualTo(1);
        holder.Current.State.Should().Be(HostUpdateExecutionAvailabilityState.Available);
    }

    private sealed class DelegateAvailabilityProvider(Func<HostUpdateExecutionAvailability> compute) : IHostUpdateExecutionAvailabilityProvider
    {
        public Task<HostUpdateExecutionAvailability> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(compute());
    }
}
