using Farm.Infrastructure.Data.Migrations;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Focused coverage for <see cref="HostUpdatePreflightCheck"/> (issue #2663 / Kane audit P0.5,
/// P0.6): proves a request target with no configured <c>ServiceMappings</c> entry fails closed
/// instead of silently guessing a container name at a later step, and that two in-process
/// migration targets resolving to different physical databases fail closed instead of
/// proceeding with a single-database backup that would silently miss one of them.
/// </summary>
public sealed class HostUpdatePreflightStepTests
{
    private static HostUpdateExecutionRequest Request(params string[] serviceIds) => new(
        "rel-1",
        1,
        "sha256:" + new string('a', 64),
        new string('b', 40),
        HostUpdateExecutionChannel.Stable,
        serviceIds.Select((id, i) => new HostUpdateExecutionTarget(id, "linux-amd64", "sha256:" + new string("abcdef"[i % 6], 64))).ToArray());

    private static string[] SixServiceIds => ["svc-1", "svc-2", "svc-3", "svc-4", "svc-5", "svc-6"];

    private sealed class NullInstalledHostStateStore : IInstalledHostStateStore
    {
        public Task<InstalledHostState?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<InstalledHostState?>(null);

        public Task WriteAsync(InstalledHostState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeMigrationTarget(string contextName, string provider, string? connectionStringFingerprint = null) : IHostUpdateMigrationTarget
    {
        public string ContextName { get; } = contextName;

        public Task<string> GetProviderNameAsync(CancellationToken cancellationToken) => Task.FromResult(provider);

        public Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<DatabaseMigrationResult> MigrateAsync(CancellationToken cancellationToken) => Task.FromResult(new DatabaseMigrationResult(false, []));

        public Task<string> GetConnectionStringFingerprintAsync(CancellationToken cancellationToken) =>
            Task.FromResult(connectionStringFingerprint ?? string.Empty);
    }

    private sealed class AlwaysDockerAvailableProcessRunner : IHostUpdateProcessRunner
    {
        public Task<HostUpdateProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null) =>
            Task.FromResult(new HostUpdateProcessResult(0, "24.0.0", string.Empty));
    }

    private static HostUpdatePreflightCheck CreateCheck(
        IReadOnlyList<IHostUpdateMigrationTarget> migrationTargets,
        IReadOnlySet<string>? mappedServiceIds = null) => new(
            new NullInstalledHostStateStore(),
            migrationTargets,
            new AlwaysDockerAvailableProcessRunner(),
            new BareNameResolver(),
            Path.GetTempPath(),
            0,
            new HashSet<string>(StringComparer.Ordinal) { "Npgsql.EntityFrameworkCore.PostgreSQL", "Microsoft.EntityFrameworkCore.SqlServer" },
            mappedServiceIds);

    private sealed class BareNameResolver : IHostUpdateExecutableResolver
    {
        public string Resolve(string toolName) => toolName;
    }

    [Fact]
    public async Task RunAsync_TargetServiceIdNotInServiceMappings_FailsClosedWithUnmappedServiceTarget()
    {
        HostUpdatePreflightCheck check = CreateCheck([], new HashSet<string>(StringComparer.Ordinal) { "svc-1", "svc-2", "svc-3", "svc-4", "svc-5" });

        Func<Task> act = () => check.RunAsync(Request(SixServiceIds), CancellationToken.None);

        HostUpdatePreflightFailedException exception = (await act.Should().ThrowAsync<HostUpdatePreflightFailedException>()).Which;
        exception.Code.Should().StartWith("unmapped_service_target:");
        exception.Code.Should().Contain("svc-6");
    }

    [Fact]
    public async Task RunAsync_AllTargetsMapped_DoesNotFailOnServiceMappingCheck()
    {
        HostUpdatePreflightCheck check = CreateCheck([], new HashSet<string>(SixServiceIds, StringComparer.Ordinal));

        Func<Task> act = () => check.RunAsync(Request(SixServiceIds), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_AppAndSlicerContextsShareConnectionString_DoesNotFailClosed()
    {
        HostUpdatePreflightCheck check = CreateCheck(
        [
            new FakeMigrationTarget("AppDbContext", "Npgsql.EntityFrameworkCore.PostgreSQL", "same-fingerprint"),
            new FakeMigrationTarget("SlicerDbContext", "Npgsql.EntityFrameworkCore.PostgreSQL", "same-fingerprint"),
        ]);

        Func<Task> act = () => check.RunAsync(Request(SixServiceIds), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_AppAndSlicerContextsResolveDifferentConnectionStrings_FailsClosedAsSplitDatabaseUnsupported()
    {
        HostUpdatePreflightCheck check = CreateCheck(
        [
            new FakeMigrationTarget("AppDbContext", "Npgsql.EntityFrameworkCore.PostgreSQL", "fingerprint-a"),
            new FakeMigrationTarget("SlicerDbContext", "Npgsql.EntityFrameworkCore.PostgreSQL", "fingerprint-b"),
        ]);

        Func<Task> act = () => check.RunAsync(Request(SixServiceIds), CancellationToken.None);

        HostUpdatePreflightFailedException exception = (await act.Should().ThrowAsync<HostUpdatePreflightFailedException>()).Which;
        exception.Code.Should().Be("split_database_not_supported");
    }

    [Fact]
    public async Task RunAsync_UnsupportedProvider_FailsClosed()
    {
        HostUpdatePreflightCheck check = CreateCheck([new FakeMigrationTarget("AppDbContext", "Microsoft.EntityFrameworkCore.Sqlite")]);

        Func<Task> act = () => check.RunAsync(Request(SixServiceIds), CancellationToken.None);

        HostUpdatePreflightFailedException exception = (await act.Should().ThrowAsync<HostUpdatePreflightFailedException>()).Which;
        exception.Code.Should().StartWith("unsupported_provider:");
    }
}
