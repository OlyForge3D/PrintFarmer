using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateMigrationStepTests
{
    [Theory]
    [InlineData("AppDbContext", "Npgsql.EntityFrameworkCore.PostgreSQL", "api", "Farm.Web.Api.dll")]
    [InlineData("AppDbContext", "Microsoft.EntityFrameworkCore.SqlServer", "api", "Farm.Web.Api.dll")]
    [InlineData("SlicerDbContext", "Npgsql.EntityFrameworkCore.PostgreSQL", "slicer-host", "Farm.Slicer.Host.dll")]
    [InlineData("SlicerDbContext", "Microsoft.EntityFrameworkCore.SqlServer", "slicer-host", "Farm.Slicer.Host.dll")]
    public async Task HasPendingMigrationsAsync_SupportedPair_UsesDigestPinnedTargetImage(
        string contextName,
        string providerName,
        string serviceId,
        string assembly)
    {
        var processRunner = new RecordingProcessRunner(new HostUpdateProcessResult(0, $"HOST_UPDATE_MIGRATION_PENDING:{contextName}:1", string.Empty));
        var runner = CreateRunner(processRunner);

        bool pending = await runner.HasPendingMigrationsAsync(CreateRequest(), contextName, _ => Task.FromResult(providerName), CancellationToken.None);

        pending.Should().BeTrue();
        processRunner.Calls.Should().HaveCount(2);
        processRunner.Calls[0].Arguments.Should().Equal(
            "image", "pull", "--platform", "linux/amd64",
            $"ghcr.io/olyforge3d/printfarmer-{serviceId}@sha256:{new string('a', 64)}");
        processRunner.Calls[1].Arguments.Should().ContainInOrder(
            "run", "--rm", "--pull", "never", "--platform", "linux/amd64",
            "--network", "printfarmer-network",
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "--user", "appuser", "--read-only",
            "--entrypoint", "dotnet",
            "--env", "ConnectionStrings__Default", "--env", "DB_PROVIDER",
            $"ghcr.io/olyforge3d/printfarmer-{serviceId}@sha256:{new string('a', 64)}",
            assembly, "--host-update-migration", contextName, "probe", providerName);
        processRunner.Calls[1].Environment.Should().ContainKey("ConnectionStrings__Default");
    }

    [Fact]
    public async Task HasPendingMigrationsAsync_UnsupportedProvider_FailsBeforeDockerInvocation()
    {
        var processRunner = new RecordingProcessRunner(new HostUpdateProcessResult(0, string.Empty, string.Empty));
        var runner = CreateRunner(processRunner);

        Func<Task> act = () => runner.HasPendingMigrationsAsync(
            CreateRequest(),
            "AppDbContext",
            _ => Task.FromResult("Microsoft.EntityFrameworkCore.Sqlite"),
            CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateTargetImageMigrationException>()
            .WithMessage("target_image_migration_provider_unsupported:AppDbContext:Microsoft.EntityFrameworkCore.Sqlite");
        processRunner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ProbeFailure_PreventsAnyMigrationMutation()
    {
        var first = new ProbeTarget("AppDbContext", null);
        var second = new ProbeTarget("SlicerDbContext", new InvalidOperationException("target unavailable"));
        var coordinator = new HostUpdateMigrationCoordinator([first, second]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(CreateRequest(), CancellationToken.None));

        first.MigrateCalls.Should().Be(0);
        second.MigrateCalls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_NoPendingMigrations_StillValidatesEveryTargetInTargetImage()
    {
        var first = new ProbeTarget("AppDbContext", null, pending: false);
        var second = new ProbeTarget("SlicerDbContext", null, pending: false);
        var coordinator = new HostUpdateMigrationCoordinator([first, second]);

        await coordinator.RunAsync(CreateRequest(), CancellationToken.None);

        first.MigrateCalls.Should().Be(1);
        second.MigrateCalls.Should().Be(1);
    }

    [Fact]
    public async Task MigrateAsync_TargetImageStageFailure_FailsClosed()
    {
        var processRunner = new RecordingProcessRunner(new HostUpdateProcessResult(17, string.Empty, "migration failed"));
        var runner = CreateRunner(processRunner);

        Func<Task> act = () => runner.MigrateAsync(
            CreateRequest(),
            "AppDbContext",
            _ => Task.FromResult("Npgsql.EntityFrameworkCore.PostgreSQL"),
            CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateTargetImageMigrationException>()
            .WithMessage("target_image_migration_stage_failed:AppDbContext:exit=17");
    }

    [Fact]
    public async Task MigrateAsync_TargetImageReportsAppliedMigrationIds()
    {
        var processRunner = new RecordingProcessRunner(new HostUpdateProcessResult(
            0,
            "HOST_UPDATE_MIGRATION_APPLIED:AppDbContext:20260101010101_One,20260101010102_Two",
            string.Empty));
        var runner = CreateRunner(processRunner);

        var result = await runner.MigrateAsync(
            CreateRequest(),
            "AppDbContext",
            _ => Task.FromResult("Npgsql.EntityFrameworkCore.PostgreSQL"),
            CancellationToken.None);

        result.AppliedMigrations.Should().Equal("20260101010101_One", "20260101010102_Two");
        processRunner.Calls.Should().HaveCount(2);
        processRunner.Calls[1].Arguments.Should().ContainInOrder(
            "--host-update-migration", "AppDbContext", "apply", "Npgsql.EntityFrameworkCore.PostgreSQL");
    }

    [Fact]
    public async Task MigrateAsync_TargetImageExecutionFailure_ReportsTargetDiagnostic()
    {
        var processRunner = new RecordingProcessRunner(
            new HostUpdateProcessResult(0, string.Empty, string.Empty),
            new HostUpdateProcessResult(17, string.Empty, "HOST_UPDATE_MIGRATION_ERROR:AppDbContext:schema_validation_failed"));
        var runner = CreateRunner(processRunner);

        Func<Task> act = () => runner.MigrateAsync(
            CreateRequest(),
            "AppDbContext",
            _ => Task.FromResult("Npgsql.EntityFrameworkCore.PostgreSQL"),
            CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateTargetImageMigrationException>()
            .WithMessage("target_image_migration_execution_failed:AppDbContext:exit=17:HOST_UPDATE_MIGRATION_ERROR:AppDbContext:schema_validation_failed");
    }

    [Fact]
    public async Task HasPendingMigrationsAsync_DuplicateTargetMarkers_FailsClosed()
    {
        var processRunner = new RecordingProcessRunner(new HostUpdateProcessResult(
            0,
            "HOST_UPDATE_MIGRATION_PENDING:AppDbContext:1\nHOST_UPDATE_MIGRATION_PENDING:AppDbContext:1",
            string.Empty));
        var runner = CreateRunner(processRunner);

        Func<Task> act = () => runner.HasPendingMigrationsAsync(
            CreateRequest(),
            "AppDbContext",
            _ => Task.FromResult("Npgsql.EntityFrameworkCore.PostgreSQL"),
            CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateTargetImageMigrationException>()
            .WithMessage("target_image_migration_probe_invalid:AppDbContext");
    }

    private static HostUpdateTargetImageMigrationRunner CreateRunner(RecordingProcessRunner processRunner) =>
        new(
            processRunner,
            new DockerResolver(),
            new Dictionary<string, HostUpdateApplyServiceMapping>(StringComparer.Ordinal)
            {
                ["api"] = new("api", "api", "PRINTFARMER_API_IMAGE", "ghcr.io/olyforge3d/printfarmer-api"),
                ["slicer-host"] = new("slicer-host", "slicer-host", "PRINTFARMER_SLICER_HOST_IMAGE", "ghcr.io/olyforge3d/printfarmer-slicer-host"),
            },
            () => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DB_PROVIDER"] = "Postgres",
                ["ConnectionStrings__Default"] = "Host=postgres",
                ["Jwt__Key"] = "test-secret",
            },
            "printfarmer-network",
            TimeSpan.FromSeconds(30));

    private static HostUpdateExecutionRequest CreateRequest() =>
        new(
            "release-1",
            1,
            "sha256:" + new string('b', 64),
            new string('c', 40),
            HostUpdateExecutionChannel.Stable,
            [
                new("api", "linux-amd64", "sha256:" + new string('a', 64)),
                new("slicer-host", "linux-amd64", "sha256:" + new string('a', 64)),
            ]);

    private sealed class DockerResolver : IHostUpdateExecutableResolver
    {
        public string Resolve(string toolName) => Path.Combine(Path.GetTempPath(), "docker");
    }

    private sealed record ProcessCall(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);

    private sealed class RecordingProcessRunner(params HostUpdateProcessResult[] results) : IHostUpdateProcessRunner
    {
        public List<ProcessCall> Calls { get; } = [];

        public Task<HostUpdateProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(new ProcessCall(arguments, environment ?? new Dictionary<string, string>()));
            return Task.FromResult(results[Math.Min(Calls.Count - 1, results.Length - 1)]);
        }
    }

    private sealed class ProbeTarget(string contextName, Exception? probeException, bool pending = true) : IHostUpdateMigrationTarget
    {
        public int MigrateCalls { get; private set; }

        public string ContextName { get; } = contextName;

        public Task<string> GetProviderNameAsync(CancellationToken cancellationToken) => Task.FromResult("Npgsql.EntityFrameworkCore.PostgreSQL");

        public Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken)
        {
            if (probeException is not null)
            {
                throw probeException;
            }

            return Task.FromResult(pending);
        }

        public Task<Farm.Infrastructure.Data.Migrations.DatabaseMigrationResult> MigrateAsync(CancellationToken cancellationToken)
        {
            MigrateCalls++;
            return Task.FromResult(new Farm.Infrastructure.Data.Migrations.DatabaseMigrationResult(false, []));
        }
    }
}
