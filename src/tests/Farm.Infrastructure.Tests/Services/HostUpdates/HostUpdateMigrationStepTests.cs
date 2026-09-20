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
        processRunner.Arguments.Should().ContainInOrder(
            "run", "--rm", "--network", "host", "--entrypoint", "dotnet",
            $"ghcr.io/olyforge3d/printfarmer-{serviceId}@sha256:{new string('a', 64)}",
            assembly, "--host-update-migration", contextName, "probe");
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
        processRunner.Arguments.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ProbeFailure_PreventsAnyMigrationMutation()
    {
        var first = new ProbeTarget("AppDbContext", new InvalidOperationException("target unavailable"));
        var second = new ProbeTarget("SlicerDbContext", null);
        var coordinator = new HostUpdateMigrationCoordinator([first, second]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(CreateRequest(), CancellationToken.None));

        first.MigrateCalls.Should().Be(0);
        second.MigrateCalls.Should().Be(0);
    }

    [Fact]
    public async Task MigrateAsync_TargetImageReturnsFailure_FailsClosed()
    {
        var processRunner = new RecordingProcessRunner(new HostUpdateProcessResult(17, string.Empty, "migration failed"));
        var runner = CreateRunner(processRunner);

        Func<Task> act = () => runner.MigrateAsync(
            CreateRequest(),
            "AppDbContext",
            _ => Task.FromResult("Npgsql.EntityFrameworkCore.PostgreSQL"),
            CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateTargetImageMigrationException>()
            .WithMessage("target_image_migration_execution_failed:AppDbContext:exit=17");
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

    private sealed class RecordingProcessRunner(HostUpdateProcessResult result) : IHostUpdateProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<HostUpdateProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
        {
            Arguments = arguments;
            return Task.FromResult(result);
        }
    }

    private sealed class ProbeTarget(string contextName, Exception? probeException) : IHostUpdateMigrationTarget
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

            return Task.FromResult(true);
        }

        public Task<Farm.Infrastructure.Data.Migrations.DatabaseMigrationResult> MigrateAsync(CancellationToken cancellationToken)
        {
            MigrateCalls++;
            return Task.FromResult(new Farm.Infrastructure.Data.Migrations.DatabaseMigrationResult(false, []));
        }
    }
}
