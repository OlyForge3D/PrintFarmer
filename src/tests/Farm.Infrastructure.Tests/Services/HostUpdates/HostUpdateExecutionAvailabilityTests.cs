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

        private readonly Dictionary<string, List<HostUpdateExecutionActivity>> byRelease = new(StringComparer.Ordinal);

        private readonly List<string> releaseOrder = [];

        public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId)
        {
            if (ThrowOnRead)
            {
                throw new InvalidDataException("journal_corrupt");
            }

            return byRelease.TryGetValue(releaseId, out List<HostUpdateExecutionActivity>? activities) ? activities : [];
        }

        public void Append(HostUpdateExecutionActivity activity)
        {
            if (!byRelease.TryGetValue(activity.ReleaseId, out List<HostUpdateExecutionActivity>? activities))
            {
                activities = [];
                byRelease[activity.ReleaseId] = activities;
                releaseOrder.Add(activity.ReleaseId);
            }

            activities.Add(activity);
        }

        public IReadOnlyList<string> ListReleaseIds() => ThrowOnRead ? throw new InvalidDataException("journal_corrupt") : releaseOrder;
    }

    private sealed class FakeMigrationTarget : IHostUpdateMigrationTarget
    {
        public string ContextName => "Fake";

        public Task<string> GetProviderNameAsync(CancellationToken cancellationToken) => Task.FromResult("Microsoft.EntityFrameworkCore.Sqlite");

        public Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken) => Task.FromResult(false);

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
        RequiredFencedWriterNames = [],
        RequiredUnavailableFacilities = [],
    };

    private sealed class FakeFenceableWriter(string name) : IFenceableWriter
    {
        public string Name { get; } = name;

        public int QuiesceCallCount { get; private set; }

        public Task QuiesceAsync(CancellationToken cancellationToken)
        {
            QuiesceCallCount++;
            return Task.CompletedTask;
        }

        public Task<bool> IsQuiescedAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeRecoveryOutcomeStore : IHostUpdateRecoveryOutcomeStore
    {
        private readonly Dictionary<string, HostUpdateRecoveryOutcomeRecord> records = new(StringComparer.Ordinal);

        public void Seed(HostUpdateRecoveryOutcomeRecord record) => records[record.ReleaseId] = record;

        public Task<HostUpdateRecoveryOutcomeRecord?> ReadAsync(string releaseId, CancellationToken cancellationToken) =>
            Task.FromResult(records.TryGetValue(releaseId, out HostUpdateRecoveryOutcomeRecord? record) ? record : null);

        public Task WriteAsync(HostUpdateRecoveryOutcomeRecord record, CancellationToken cancellationToken)
        {
            records[record.ReleaseId] = record;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CheckAsync_KnownPhysicalGapsRemain_ReportsCodeOwnedUnavailableFacilities()
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
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain("facility_unavailable:target_image_migration_runner_unavailable");
            result.Reasons.Should().Contain("facility_unavailable:queue_reconciliation_writer_fence_unavailable");
            result.Reasons.Should().Contain("facility_unavailable:sql_server_visible_backup_path_mapping_unverified");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_DefaultRequiredFencedWriters_ReportUnavailableWhenWriterCoverageMissing()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            var options = new HostUpdateExecutionOptions
            {
                RootDirectory = root,
                ComposeFiles = [composeFile],
            };
            var provider = new HostUpdateExecutionAvailabilityProvider(
                options,
                new FakeJournal(),
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain("facility_unavailable:target_image_migration_runner_unavailable");
            result.Reasons.Should().Contain(r => r.StartsWith("insufficient_fenced_writers:", StringComparison.Ordinal));
            result.Reasons.Single(r => r.StartsWith("insufficient_fenced_writers:", StringComparison.Ordinal)).Should().Contain("webhook-delivery");
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
            new HostUpdateExecutionOptions { RootDirectory = string.Empty, ComposeFiles = ["missing.yml"], RequiredFencedWriterNames = [] },
            new FakeJournal(),
            [new FakeMigrationTarget()],
            [new FakeBackupTarget()],
            [],
            new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore());

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
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore());

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
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore());

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
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore());

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
                [],
                new FakeProcessRunner(dockerAvailable: false),
                new FakeRecoveryOutcomeStore());

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
    public async Task CheckAsync_AllRequiredWritersFenced_DoesNotReportInsufficientCoverage()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            HostUpdateExecutionOptions options = ValidOptions(root, composeFile);
            options.RequiredFencedWriterNames = ["api-admission", "queue-outbox-publisher"];

            var provider = new HostUpdateExecutionAvailabilityProvider(
                options,
                new FakeJournal(),
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                [new FakeFenceableWriter("api-admission"), new FakeFenceableWriter("queue-outbox-publisher")],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain("facility_unavailable:target_image_migration_runner_unavailable");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_RequiredWriterNotFenced_ReportsInsufficientCoverageWithMissingName()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            HostUpdateExecutionOptions options = ValidOptions(root, composeFile);
            options.RequiredFencedWriterNames = ["api-admission", "queue-outbox-publisher", "history-seeding"];

            var provider = new HostUpdateExecutionAvailabilityProvider(
                options,
                new FakeJournal(),
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                [new FakeFenceableWriter("api-admission"), new FakeFenceableWriter("queue-outbox-publisher")],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain("insufficient_fenced_writers:history-seeding");
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

    [Fact]
    public async Task CheckAsync_ReleaseLeftMidFlight_ReFencesAllWritersAndReportsPendingReconciliation()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            var journal = new FakeJournal();
            journal.Append(new HostUpdateExecutionActivity("a1", "release-1", HostUpdateExecutionState.Fenced, "fence:after", DateTimeOffset.UtcNow));
            var admission = new FakeFenceableWriter("api-admission");
            var outbox = new FakeFenceableWriter("queue-outbox-publisher");

            var provider = new HostUpdateExecutionAvailabilityProvider(
                ValidOptions(root, composeFile),
                journal,
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                [admission, outbox],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain("restart_reconciliation_pending:release-1:Fenced");
            admission.QuiesceCallCount.Should().BeGreaterThanOrEqualTo(1);
            outbox.QuiesceCallCount.Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_ReleaseRecoveryRequiredWithNoRecordedOutcome_StaysFenced()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            var journal = new FakeJournal();
            journal.Append(new HostUpdateExecutionActivity("a1", "release-2", HostUpdateExecutionState.RecoveryRequired, "failure:Exception", DateTimeOffset.UtcNow));
            var admission = new FakeFenceableWriter("api-admission");

            var provider = new HostUpdateExecutionAvailabilityProvider(
                ValidOptions(root, composeFile),
                journal,
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                [admission],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain("restart_reconciliation_pending:release-2:RecoveryRequired");
            admission.QuiesceCallCount.Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_ReleaseRecoveryRequiredWithNeedsOperatorOutcome_StaysFenced()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            var journal = new FakeJournal();
            journal.Append(new HostUpdateExecutionActivity("a1", "release-3", HostUpdateExecutionState.RecoveryRequired, "failure:Exception", DateTimeOffset.UtcNow));
            var outcomeStore = new FakeRecoveryOutcomeStore();
            outcomeStore.Seed(new HostUpdateRecoveryOutcomeRecord("release-3", HostUpdateRecoveryOutcome.NeedsOperator, "rollback_incompatible", DateTimeOffset.UtcNow));
            var admission = new FakeFenceableWriter("api-admission");

            var provider = new HostUpdateExecutionAvailabilityProvider(
                ValidOptions(root, composeFile),
                journal,
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                [admission],
                new FakeProcessRunner(dockerAvailable: true),
                outcomeStore);

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain("restart_reconciliation_pending:release-3:RecoveryRequired");
            admission.QuiesceCallCount.Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_ReleaseRecoveryRequiredButDurablyRolledBack_DoesNotReportPending()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            var journal = new FakeJournal();
            journal.Append(new HostUpdateExecutionActivity("a1", "release-4", HostUpdateExecutionState.RecoveryRequired, "failure:Exception", DateTimeOffset.UtcNow));
            var outcomeStore = new FakeRecoveryOutcomeStore();
            outcomeStore.Seed(new HostUpdateRecoveryOutcomeRecord("release-4", HostUpdateRecoveryOutcome.RolledBack, "restored_prior_images", DateTimeOffset.UtcNow));

            var provider = new HostUpdateExecutionAvailabilityProvider(
                ValidOptions(root, composeFile),
                journal,
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                [],
                new FakeProcessRunner(dockerAvailable: true),
                outcomeStore);

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain("facility_unavailable:target_image_migration_runner_unavailable");
            result.Reasons.Should().NotContain(r => r.StartsWith("restart_reconciliation_pending", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_ReleaseCompletedByRealExecutor_DoesNotReFenceOrReportPending()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        string journalPath = Path.Combine(root, "state", "journal.ndjson");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            var journal = new FileHostUpdateExecutionJournal(journalPath);
            HostUpdateExecutionRequest request = ExecutionRequest();
            HostUpdateExecutionResult execution = await new HostUpdateExecutor(
                new NoopExecutionSteps(),
                journal,
                NoopHostUpdateExecutionLock.Instance).ExecuteAsync(request);
            execution.State.Should().Be(HostUpdateExecutionState.Completed);
            var admission = new FakeFenceableWriter("api-admission");

            var provider = new HostUpdateExecutionAvailabilityProvider(
                ValidOptions(root, composeFile),
                journal,
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                [admission],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain("facility_unavailable:target_image_migration_runner_unavailable");
            admission.QuiesceCallCount.Should().Be(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static HostUpdateExecutionRequest ExecutionRequest() => new(
        "release-5",
        1,
        "sha256:" + new string('a', 64),
        new string('b', 40),
        HostUpdateExecutionChannel.Stable,
        [
            new("api", "linux-amd64", "sha256:" + new string('a', 64)),
            new("frontend", "linux-amd64", "sha256:" + new string('b', 64)),
            new("slicer-host", "linux-amd64", "sha256:" + new string('c', 64)),
            new("printer-discovery", "linux-amd64", "sha256:" + new string('d', 64)),
            new("orcaslicer-worker", "linux-amd64", "sha256:" + new string('e', 64)),
            new("monolith", "linux-amd64", "sha256:" + new string('f', 64)),
        ])
    {
        RequestId = "request-5",
        TrustRoot = "root-1",
        PolicyRevision = 1,
        PolicyFingerprint = "policy-1",
        HostPlatform = "linux-amd64",
    };

    private sealed class NoopExecutionSteps : IHostUpdateExecutionSteps
    {
        public Task PreflightAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task DrainAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task FenceAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task BackupAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task MigrateAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task ApplyAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task VerifyAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DelegateAvailabilityProvider(Func<HostUpdateExecutionAvailability> compute) : IHostUpdateExecutionAvailabilityProvider
    {
        public Task<HostUpdateExecutionAvailability> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(compute());
    }
}
