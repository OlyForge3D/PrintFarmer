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

    private sealed class FakeMigrationTarget(string providerName = "Microsoft.EntityFrameworkCore.Sqlite") : IHostUpdateMigrationTarget
    {
        public string ContextName => "Fake";

        public Task<string> GetProviderNameAsync(CancellationToken cancellationToken) => Task.FromResult(providerName);

        public Task<bool> HasPendingMigrationsAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<DatabaseMigrationResult> MigrateAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DatabaseMigrationResult(false, []));
    }

    private sealed class ThrowingMigrationTarget : IHostUpdateMigrationTarget
    {
        public string ContextName => "UnavailableSlicer";

        public Task<string> GetProviderNameAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("slicer_db_context_not_registered");

        public Task<bool> HasPendingMigrationsAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<DatabaseMigrationResult> MigrateAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DatabaseMigrationResult(false, []));
    }

    private sealed class FakeBackupTarget : IHostUpdateBackupTarget
    {
        public string Name => "fake";

        public bool IsExternallyOwned => false;

        public Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Stub SQL Server backup target for availability-provider-level coverage of issue #2788:
    /// proves <see cref="HostUpdateExecutionAvailabilityProvider.CheckAsync"/> itself surfaces or
    /// suppresses the facility based on <see cref="IHostUpdateServerSideBackupTarget.VerifyVisibleBackupPathMappingAsync"/>,
    /// independent of the real <c>SqlServerProcessDatabaseBackupTarget</c> round-trip mechanics
    /// covered by <c>HostUpdateDatabaseBackupTargetFactoryTests</c>.
    /// </summary>
    private sealed class StubServerSideBackupTarget(string? verificationEvidence) : IHostUpdateBackupTarget, IHostUpdateServerSideBackupTarget
    {
        public string Name => "stub-sql-server";

        public bool IsExternallyOwned => false;

        public Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> VerifyVisibleBackupPathMappingAsync(CancellationToken cancellationToken) =>
            Task.FromResult(verificationEvidence);
    }

    /// <summary>
    /// Proves <see cref="HostUpdateExecutionAvailabilityProvider.CheckAsync"/> itself -- not just
    /// <c>HostUpdateDatabaseBackupTargetFactory</c> -- fails closed when a server-side backup
    /// target's verification throws instead of returning evidence, wrapping the exception into
    /// the same <c>facility_unavailable:...:probe_exception:&lt;type&gt;</c> reason string used
    /// for a returned-evidence failure.
    /// </summary>
    private sealed class ThrowingServerSideBackupTarget : IHostUpdateBackupTarget, IHostUpdateServerSideBackupTarget
    {
        public string Name => "throwing-sql-server";

        public bool IsExternallyOwned => false;

        public Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> VerifyVisibleBackupPathMappingAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated verification failure");
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

    private sealed class TestExecutableResolver : IHostUpdateExecutableResolver
    {
        public string Resolve(string toolName) => Path.Combine(Path.GetTempPath(), toolName + ".exe");
    }

    private sealed class MissingDockerExecutableResolver : IHostUpdateExecutableResolver
    {
        public string Resolve(string toolName) => throw new InvalidOperationException($"host_update_executable_not_configured:{toolName}");
    }

    private static void ConfigureExecutablePaths(HostUpdateExecutionOptions options, string root, params string[] toolNames)
    {
        foreach (string toolName in toolNames)
        {
            options.HostExecutablePaths[toolName] = Path.Combine(root, toolName);
        }
    }

    [Fact]
    public async Task CheckAsync_UnconfiguredDockerAndUnsupportedProvider_ReportsConcreteReasons()
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
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().BeEquivalentTo(
            [
                "host_executable_not_configured:docker",
                "database_provider_tooling_unsupported:Fake:Microsoft.EntityFrameworkCore.Sqlite",
            ]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "pg_dump", "pg_restore")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "sqlcmd")]
    public async Task CheckAsync_SingleConfiguredProvider_IsAvailable(string providerName, params string[] providerTools)
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            HostUpdateExecutionOptions options = ValidOptions(root, composeFile);
            ConfigureExecutablePaths(options, root, ["docker", .. providerTools]);
            var provider = new HostUpdateExecutionAvailabilityProvider(
                options,
                new FakeJournal(),
                [new FakeMigrationTarget(providerName)],
                [new FakeBackupTarget()],
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

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
    public async Task CheckAsync_ServerSideBackupTargetVerificationFails_SurfacesFacilityWithEvidence()
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
                [new StubServerSideBackupTarget("probe_file_not_visible_from_printfarmer")],
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain(
                "facility_unavailable:sql_server_visible_backup_path_mapping_unverified:probe_file_not_visible_from_printfarmer");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_ServerSideBackupTargetVerificationThrows_SurfacesFacilityWithExceptionTypeEvidence()
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
                [new ThrowingServerSideBackupTarget()],
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);

            // Assert the complete reason string, not merely a "facility_unavailable:..." prefix:
            // a prefix match would also pass for the returned-evidence branch (covered by the
            // test above), which does not exercise CheckAsync's catch (Exception) wrapping at
            // all. Only the full string proves the exception path specifically was taken.
            result.Reasons.Should().Contain(
                "facility_unavailable:sql_server_visible_backup_path_mapping_unverified:probe_exception:InvalidOperationException");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_ServerSideBackupTargetVerificationSucceeds_DoesNotSurfaceFacility()
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
                [new StubServerSideBackupTarget(verificationEvidence: null)],
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.Reasons.Should().NotContain(r => r.StartsWith("facility_unavailable:sql_server_visible_backup_path_mapping_unverified", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_DockerResolverReportsMissingConfiguration_ReportsSingleReason()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            HostUpdateExecutionOptions options = ValidOptions(root, composeFile);
            ConfigureExecutablePaths(options, root, "sqlite3");
            var provider = new HostUpdateExecutionAvailabilityProvider(
                options,
                new FakeJournal(),
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore(),
                new MissingDockerExecutableResolver());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.Reasons.Where(reason => reason == "host_executable_not_configured:docker").Should().ContainSingle();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "pg_restore", "pg_dump")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "sqlcmd", "")]
    public async Task CheckAsync_ActiveProviderToolMissing_ReportsToolNotConfigured(
        string providerName,
        string missingTool,
        string configuredProviderTool)
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            HostUpdateExecutionOptions options = ValidOptions(root, composeFile);
            ConfigureExecutablePaths(options, root, "docker");
            if (!string.IsNullOrEmpty(configuredProviderTool))
            {
                ConfigureExecutablePaths(options, root, configuredProviderTool);
            }

            var provider = new HostUpdateExecutionAvailabilityProvider(
                options,
                new FakeJournal(),
                [new FakeMigrationTarget(providerName)],
                [new FakeBackupTarget()],
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain($"host_executable_not_configured:{missingTool}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown.EntityFrameworkCore.Provider")]
    public async Task CheckAsync_ProviderNameMissingOrUnsupported_ReportsExplicitReason(string providerName)
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            HostUpdateExecutionOptions options = ValidOptions(root, composeFile);
            ConfigureExecutablePaths(options, root, "docker");
            var provider = new HostUpdateExecutionAvailabilityProvider(
                options,
                new FakeJournal(),
                [new FakeMigrationTarget(providerName)],
                [new FakeBackupTarget()],
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain(
                string.IsNullOrEmpty(providerName)
                    ? "database_provider_not_configured:Fake"
                    : $"database_provider_tooling_unsupported:Fake:{providerName}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_ProviderInspectionThrows_ReportsUnavailableWithoutAbortingProbe()
    {
        string root = Directory.CreateTempSubdirectory("hu-avail-").FullName;
        string composeFile = Path.Combine(root, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}");
        try
        {
            HostUpdateExecutionOptions options = ValidOptions(root, composeFile);
            ConfigureExecutablePaths(options, root, "docker");
            var provider = new HostUpdateExecutionAvailabilityProvider(
                options,
                new FakeJournal(),
                [new ThrowingMigrationTarget(), new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                [],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain("database_provider_inspection_failed:UnavailableSlicer:InvalidOperationException");
            result.Reasons.Should().Contain("database_provider_tooling_unsupported:Fake:Microsoft.EntityFrameworkCore.Sqlite");
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
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
            result.Reasons.Should().Contain(r => r.StartsWith("insufficient_fenced_writers:", StringComparison.Ordinal));
            result.Reasons.Single(r => r.StartsWith("insufficient_fenced_writers:", StringComparison.Ordinal)).Should().Contain("webhook-delivery");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task CheckAsync_RootDirectoryNotConfiguredAndJournalUnavailable_ReportsUnavailableWithReasons()
    {
        var provider = new HostUpdateExecutionAvailabilityProvider(
            new HostUpdateExecutionOptions { RootDirectory = string.Empty, ComposeFiles = ["missing.yml"], RequiredFencedWriterNames = [] },
            new UnavailableHostUpdateExecutionJournal(),
            [new FakeMigrationTarget()],
            [new FakeBackupTarget()],
            [],
            new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

        HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

        result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
        result.Reasons.Should().Contain("root_directory_not_configured");
        result.Reasons.Should().Contain("host_update_execution_journal_not_available");
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
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

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
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

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
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

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
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

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
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
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
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

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
        var firstProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegateAvailabilityProvider(() =>
        {
            probeCount++;
            firstProbe.TrySetResult();
            return HostUpdateExecutionAvailability.Available(DateTimeOffset.UtcNow);
        });
        var service = new HostUpdateExecutionAvailabilityHostedService(
            provider,
            holder,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HostUpdateExecutionAvailabilityHostedService>.Instance,
            TimeSpan.FromMinutes(30));

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await firstProbe.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // StopAsync joins the hosted loop, ordering the holder assertion after the probe publishes.
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
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

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
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

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
                outcomeStore,
                new TestExecutableResolver());

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
                outcomeStore,
                new TestExecutableResolver());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
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
                NoopHostUpdateExecutionLock.Instance,
                automationPolicyRepository: new InlinePolicyRepository()).ExecuteAsync(request);
            execution.State.Should().Be(HostUpdateExecutionState.Completed);
            var admission = new FakeFenceableWriter("api-admission");

            var provider = new HostUpdateExecutionAvailabilityProvider(
                ValidOptions(root, composeFile),
                journal,
                [new FakeMigrationTarget()],
                [new FakeBackupTarget()],
                [admission],
                new FakeProcessRunner(dockerAvailable: true),
                new FakeRecoveryOutcomeStore(),
                new TestExecutableResolver());

            HostUpdateExecutionAvailability result = await provider.CheckAsync(CancellationToken.None);

            result.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
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
        PolicyFingerprint = HostStateHostUpdateSchedulerSettings.ToSchedulerSettings(new HostUpdateAutomationPolicy(Enabled: true, Revision: 1)).Fingerprint,
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

    private sealed class InlinePolicyRepository : IHostUpdateAutomationPolicyRepository
    {
        private static readonly HostUpdateAutomationPolicy Policy = new(Enabled: true, Revision: 1);

        public HostUpdatePolicyReadResult Read() => new(true, Policy, null);

        public Task<HostUpdatePolicyReadResult> ReplaceAsync(HostUpdateAutomationPolicy replacement, long expectedRevision, CancellationToken ct) =>
            Task.FromResult(new HostUpdatePolicyReadResult(true, replacement, null));
    }

    private sealed class DelegateAvailabilityProvider(Func<HostUpdateExecutionAvailability> compute) : IHostUpdateExecutionAvailabilityProvider
    {
        public Task<HostUpdateExecutionAvailability> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(compute());
    }
}
