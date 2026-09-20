using System.Collections.Generic;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Web.Api.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Bishop/Hicks review (issue #2663): <c>HostUpdateExecutionStartup.AddHostUpdateExecution</c>
/// previously registered <c>IEnumerable&lt;IHostUpdateBackupTarget&gt;</c> explicitly, whose own
/// factory called <c>sp.GetServices&lt;IHostUpdateBackupTarget&gt;()</c> -- which the .NET
/// container implements as <c>GetRequiredService&lt;IEnumerable&lt;IHostUpdateBackupTarget&gt;&gt;()</c>,
/// so the explicit registration resolved itself and recursed without bound (an unrecoverable
/// <see cref="System.StackOverflowException"/> the first time anything actually asked for the
/// backup target list -- not merely a slow/incorrect result). This test proves the real DI graph
/// this startup method wires resolves <see cref="IReadOnlyList{T}"/> of
/// <see cref="IHostUpdateBackupTarget"/> (and its dependents) without recursing.
/// </summary>
public sealed class HostUpdateExecutionStartupDiGraphTests
{
    private static IConfiguration BuildConfiguration(string rootDirectory, bool includeExecutablePaths = true)
    {
        var values = new Dictionary<string, string?>
        {
            ["HostUpdateExecution:RootDirectory"] = rootDirectory,
        };
        if (includeExecutablePaths)
        {
            values["HostUpdateExecution:HostExecutablePaths:docker"] = Path.Combine(AppContext.BaseDirectory, "docker.exe");
            values["HostUpdateExecution:HostExecutablePaths:sqlite3"] = Path.Combine(AppContext.BaseDirectory, "sqlite3.exe");
            values["HostUpdateExecution:HostExecutablePaths:pg_dump"] = Path.Combine(AppContext.BaseDirectory, "pg_dump.exe");
            values["HostUpdateExecution:HostExecutablePaths:pg_restore"] = Path.Combine(AppContext.BaseDirectory, "pg_restore.exe");
            values["HostUpdateExecution:HostExecutablePaths:sqlcmd"] = Path.Combine(AppContext.BaseDirectory, "sqlcmd.exe");
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // Must satisfy HostUpdateExecutionOptionsValidator (absolute; not under the OS temp
    // directory; not the current/working directory or a subdirectory of it) because resolving
    // IOptions<HostUpdateExecutionOptions>.Value always runs IValidateOptions, independent of
    // ValidateOnStart's eager host-startup hook. Local application data is user-writable on
    // both Windows and Linux without requiring filesystem-root permissions.
    private static string CreateValidRoot()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("A user-local application data directory is required for this test.");
        }

        return Path.Combine(localData, "pf-hostupdate-di-graph-" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void AddHostUpdateExecution_ResolvingBackupTargetList_DoesNotRecurseAndReturnsRealTargets()
    {
        string root = CreateValidRoot();
        try
        {
            ServiceCollection services = new();
            services.AddLogging();
            IConfiguration configuration = BuildConfiguration(root);
            services.AddSingleton(configuration);
            services.AddHostUpdateExecution(configuration);

            using ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope scope = provider.CreateScope();

            IReadOnlyList<IHostUpdateBackupTarget> targets = scope.ServiceProvider.GetRequiredService<IReadOnlyList<IHostUpdateBackupTarget>>();

            // At minimum, every directory in HostUpdateExecutionOptions.OwnedDirectories plus the
            // database target itself must be present -- proving the factory actually built a
            // real, non-empty list rather than merely avoiding an exception.
            Assert.True(targets.Count >= 2);
            Assert.Contains(targets, t => t.Name == "database");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void AddHostUpdateExecution_ResolvingBackupCoordinator_DoesNotRecurse()
    {
        string root = CreateValidRoot();
        try
        {
            ServiceCollection services = new();
            services.AddLogging();
            IConfiguration configuration = BuildConfiguration(root);
            services.AddSingleton(configuration);
            services.AddHostUpdateExecution(configuration);

            using ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope scope = provider.CreateScope();

            IHostUpdateBackupCoordinator coordinator = scope.ServiceProvider.GetRequiredService<IHostUpdateBackupCoordinator>();

            Assert.NotNull(coordinator);
        }

        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void AddHostUpdateExecution_ResolvingBackupTargetsWithoutExecutableMappings_DoesNotThrow()
    {
        string root = CreateValidRoot();
        try
        {
            ServiceCollection services = new();
            services.AddLogging();
            IConfiguration configuration = BuildConfiguration(root, includeExecutablePaths: false);
            services.AddSingleton(configuration);
            services.AddHostUpdateExecution(configuration);

            using ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope scope = provider.CreateScope();

            IReadOnlyList<IHostUpdateBackupTarget> targets =
                scope.ServiceProvider.GetRequiredService<IReadOnlyList<IHostUpdateBackupTarget>>();

            Assert.Contains(targets, target => target.Name == "database");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Bishop review (issue #2788): the <c>IHostUpdateBackupTarget</c> factory delegate was
    /// briefly (and accidentally, mid-redesign) evaluating <c>options.BackupRootDirectory</c>
    /// unconditionally for every database provider before passing it to
    /// <see cref="HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget"/>.
    /// <c>BackupRootDirectory</c> throws <c>root_directory_not_configured</c> when
    /// <c>RootDirectory</c> is unset, so that regression would crash resolution of
    /// <see cref="IReadOnlyList{T}"/> of <see cref="IHostUpdateBackupTarget"/> -- and therefore
    /// <c>HostUpdateExecutionAvailabilityProvider.CheckAsync</c>'s unconditional
    /// <c>backupTargets.Count</c> check -- for a SQL Server deployment in the executor's normal
    /// default-off state (root unconfigured), instead of resolving cleanly and reporting
    /// <c>backup_root_directory_not_configured</c> only when the mapping is actually verified.
    /// This proves the DI graph resolves without throwing in exactly that state.
    /// </summary>
    [Fact]
    public void AddHostUpdateExecution_ResolvingSqlServerBackupTargetsWithUnconfiguredRoot_DoesNotThrow()
    {
        ServiceCollection services = new();
        services.AddLogging();
        var values = new Dictionary<string, string?>
        {
            ["HostUpdateExecution:RootDirectory"] = string.Empty,
            ["DB_PROVIDER"] = "sqlserver",
            ["ConnectionStrings:Default"] = "Server=sqlhost;Database=printfarmer;User Id=sa;Password=fixture-not-a-real-credential;TrustServerCertificate=True",
            ["HostUpdateExecution:HostExecutablePaths:sqlcmd"] = Path.Combine(AppContext.BaseDirectory, "sqlcmd.exe"),
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        services.AddSingleton(configuration);
        services.AddHostUpdateExecution(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        IReadOnlyList<IHostUpdateBackupTarget> targets = scope.ServiceProvider.GetRequiredService<IReadOnlyList<IHostUpdateBackupTarget>>();

        Assert.Contains(targets, t => t.Name == "database");
    }

    [Fact]
    public void AddHostUpdateExecution_ResolvesOnlyConstrainedProcessRunner()
    {
        string root = CreateValidRoot();
        try
        {
            ServiceCollection services = new();
            services.AddLogging();
            IConfiguration configuration = BuildConfiguration(root);
            services.AddSingleton(configuration);
            services.AddHostUpdateExecution(configuration);

            using ServiceProvider provider = services.BuildServiceProvider();

            Assert.IsType<ConstrainedHostUpdateProcessRunner>(
                provider.GetRequiredService<IHostUpdateProcessRunner>());
            Assert.Throws<InvalidOperationException>(() =>
                provider.GetRequiredService<DefaultHostUpdateProcessRunner>());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddHostUpdateExecution_ResolvesExecutorAndRecoveryCoordinatorForProvisioningState(bool provisioned)
    {
        string root = provisioned ? CreateValidRoot() : string.Empty;
        try
        {
            ServiceCollection services = new();
            services.AddLogging();
            IConfiguration configuration = BuildConfiguration(root);
            services.AddSingleton(configuration);
            services.AddHostUpdateExecution(configuration);
            services.AddScoped<IHostUpdateExecutionSteps, NoopHostUpdateExecutionSteps>();

            using ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope scope = provider.CreateScope();

            IHostUpdateExecutor executor = scope.ServiceProvider.GetRequiredService<IHostUpdateExecutor>();
            IHostUpdateRecoveryCoordinator recovery = scope.ServiceProvider.GetRequiredService<IHostUpdateRecoveryCoordinator>();

            if (provisioned)
            {
                Assert.IsType<HostUpdateExecutor>(executor);
                Assert.IsType<HostUpdateRecoveryCoordinator>(recovery);
            }
            else
            {
                Assert.IsType<UnavailableHostUpdateExecutor>(executor);
                Assert.IsType<UnavailableHostUpdateRecoveryCoordinator>(recovery);
            }
        }
        finally
        {
            if (provisioned && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AddHostUpdateExecution_JournalAndRecoveryOutcomeUseExecutionRoot()
    {
        string root = CreateValidRoot();
        try
        {
            ServiceCollection services = new();
            services.AddLogging();
            IConfiguration configuration = BuildConfiguration(root);
            services.AddSingleton(configuration);
            services.AddHostUpdateExecution(configuration);

            using ServiceProvider provider = services.BuildServiceProvider();

            FileHostUpdateExecutionJournal journal = Assert.IsType<FileHostUpdateExecutionJournal>(provider.GetRequiredService<IHostUpdateExecutionJournal>());
            FileHostUpdateRecoveryOutcomeStore outcomeStore = Assert.IsType<FileHostUpdateRecoveryOutcomeStore>(provider.GetRequiredService<IHostUpdateRecoveryOutcomeStore>());
            HostUpdateExecutionOptions options = provider.GetRequiredService<HostUpdateExecutionOptions>();
            Assert.Equal(Path.Combine(root, "state"), options.StateDirectory);

            journal.Append(new HostUpdateExecutionActivity("activity-1", "release-1", HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow));
            await outcomeStore.WriteAsync(
                new HostUpdateRecoveryOutcomeRecord("release-1", HostUpdateRecoveryOutcome.NeedsOperator, "test", DateTimeOffset.UtcNow),
                CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(options.StateDirectory, "journal.ndjson")));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(options.StateDirectory, "recovery-outcomes")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class NoopHostUpdateExecutionSteps : IHostUpdateExecutionSteps
    {
        public Task PreflightAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task DrainAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task FenceAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task BackupAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task MigrateAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task ApplyAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task VerifyAsync(HostUpdateExecutionRequest request, CancellationToken ct) => Task.CompletedTask;
    }
}
