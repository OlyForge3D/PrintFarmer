using Farm.Infrastructure.Data;
using Farm.Infrastructure.Data.Migrations;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Slicer.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Production DI wiring for the concrete host-update executor step adapters (issue #2663).
/// Composes every real preflight/drain/fence/backup/migration/apply/verify/recovery adapter
/// behind <see cref="IHostUpdateExecutionSteps"/>/<see cref="IHostUpdateExecutor"/>, using the
/// existing <see cref="ProviderAwareMigrationRunner"/> and the existing pinned-image compose
/// templates rather than duplicating either. Only wires the manual admin API's dependencies --
/// it never grants #2666's scheduler any standing automatic-execution permission.
/// </summary>
public static class HostUpdateExecutionStartup
{
    private const string HealthClientName = "HostUpdateHealth";

    public static IServiceCollection AddHostUpdateExecution(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HostUpdateExecutionOptions>()
            .Bind(configuration.GetSection(HostUpdateExecutionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HostUpdateExecutionOptions>, HostUpdateExecutionOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<HostUpdateExecutionOptions>>().Value);

        services.AddHttpClient(HealthClientName, (sp, client) =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            client.BaseAddress = new Uri(options.HealthCheckBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.ProcessDefaultTimeoutSeconds);
        });

        services.AddSingleton<IHostUpdateProcessRunner, DefaultHostUpdateProcessRunner>();

        // Durable single-writer state, all rooted under the validated, host-controlled
        // RootDirectory (never the app DB, never a temp/cache path -- see
        // HostUpdateExecutionOptionsValidator).
        services.AddSingleton<IInstalledHostStateStore>(sp =>
            new FileInstalledHostStateStore(Path.Combine(sp.GetRequiredService<HostUpdateExecutionOptions>().StateDirectory, "installed-state.json")));
        services.AddSingleton<IHostUpdateExecutionJournal>(sp =>
            new FileHostUpdateExecutionJournal(Path.Combine(sp.GetRequiredService<HostUpdateExecutionOptions>().StateDirectory, "journal.ndjson")));
        services.AddSingleton<IHostUpdateExecutionLock>(sp =>
            new FileHostUpdateExecutionLock(Path.Combine(sp.GetRequiredService<HostUpdateExecutionOptions>().StateDirectory, "execution.lock")));

        // Drain: perimeter admission gate + observed active print/outbox work.
        services.AddSingleton<IHostUpdateAdmissionGate, InMemoryHostUpdateAdmissionGate>();
        services.AddScoped<IActiveWorkObservationPort, DbActiveWorkObservationPort>();
        services.AddScoped<IHostUpdateDrainCoordinator>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return new HostUpdateDrainCoordinator(
                sp.GetRequiredService<IHostUpdateAdmissionGate>(),
                sp.GetRequiredService<IActiveWorkObservationPort>(),
                TimeSpan.FromSeconds(options.DrainTimeoutSeconds),
                TimeSpan.FromSeconds(options.DrainPollIntervalSeconds));
        });

        // Fence: every registered writer must prove quiescence before backup. The outbox
        // publisher's fence flag is the same singleton QueueOutboxPublisherService consults
        // directly (see its optional IHostUpdateWriterActivityFlag constructor parameter).
        services.AddSingleton<IHostUpdateWriterActivityFlag, InMemoryHostUpdateWriterActivityFlag>();
        services.AddSingleton<IReadOnlyList<IFenceableWriter>>(sp =>
        [
            new AdmissionFenceableWriter(sp.GetRequiredService<IHostUpdateAdmissionGate>()),
            new BackgroundWriterFenceableWriter("queue-outbox-publisher", sp.GetRequiredService<IHostUpdateWriterActivityFlag>()),
        ]);
        services.AddSingleton<IHostUpdateFenceCoordinator>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return new HostUpdateFenceCoordinator(
                sp.GetRequiredService<IReadOnlyList<IFenceableWriter>>(),
                TimeSpan.FromSeconds(options.FenceProofTimeoutSeconds),
                TimeSpan.FromSeconds(options.FencePollIntervalSeconds));
        });

        AddBackupAndMigration(services);
        AddApplyAndVerify(services);
        AddRecovery(services);

        // Composition root: the concrete IHostUpdateExecutionSteps consumed by the pre-existing,
        // unmodified HostUpdateExecutor state machine.
        services.AddScoped<IHostUpdatePreflightCheck>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return new HostUpdatePreflightCheck(
                sp.GetRequiredService<IInstalledHostStateStore>(),
                sp.GetRequiredService<IReadOnlyList<IHostUpdateMigrationTarget>>(),
                sp.GetRequiredService<IHostUpdateProcessRunner>(),
                options.DiskWatchPath,
                options.MinimumFreeBytes,
                options.SupportedProviderNames.ToHashSet(StringComparer.Ordinal));
        });
        services.AddScoped<IHostUpdateExecutionSteps, HostUpdateExecutionStepsAdapter>();
        services.AddScoped<IHostUpdateExecutor, HostUpdateExecutor>();

        // Availability: proves (not assumes) the executor is actually usable -- root writable,
        // journal intact, adapters configured, compose files present, docker runtime reachable --
        // and republishes the result immediately at startup (restart reconciliation) and on a
        // periodic recheck, so both the admin API and the #2666 scheduler can poll status
        // without ever attempting an execution against a misconfigured host.
        services.AddScoped<IHostUpdateExecutionAvailabilityProvider, HostUpdateExecutionAvailabilityProvider>();
        services.AddSingleton<HostUpdateExecutionAvailabilityHolder>();
        services.AddHostedService(sp => new HostUpdateExecutionAvailabilityHostedService(
            CreateAvailabilityProviderFactory(sp),
            sp.GetRequiredService<HostUpdateExecutionAvailabilityHolder>(),
            sp.GetRequiredService<ILogger<HostUpdateExecutionAvailabilityHostedService>>(),
            TimeSpan.FromMinutes(5)));

        return services;
    }

    /// <summary>
    /// <see cref="IHostUpdateExecutionAvailabilityProvider"/> is registered Scoped (it resolves
    /// scoped migration/backup targets), but the periodic recheck runs from a singleton
    /// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>. This factory opens a fresh
    /// scope per check so scoped dependencies (e.g. DbContext-backed migration targets) are
    /// never held longer than one probe.
    /// </summary>
    private static ScopedHostUpdateExecutionAvailabilityProvider CreateAvailabilityProviderFactory(IServiceProvider rootProvider) =>
        new(rootProvider);

    private static void AddBackupAndMigration(IServiceCollection services)
    {
        services.AddScoped<IHostUpdateMigrationTarget>(sp => new DbContextMigrationTarget<AppDbContext>(
            "AppDbContext",
            DatabaseMigrationTarget.Core,
            () => sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<ILogger<AppDbContext>>()));
        services.AddScoped<IHostUpdateMigrationTarget>(sp =>
        {
            SlicerDbContext? slicerDb = sp.GetService<SlicerDbContext>();
            return new DbContextMigrationTarget<SlicerDbContext>(
                "SlicerDbContext",
                DatabaseMigrationTarget.Slicer,
                () => slicerDb ?? throw new InvalidOperationException("slicer_db_context_not_registered"),
                sp.GetRequiredService<ILogger<SlicerDbContext>>());
        });
        services.AddScoped<IReadOnlyList<IHostUpdateMigrationTarget>>(sp => [.. sp.GetServices<IHostUpdateMigrationTarget>()]);
        services.AddScoped<IHostUpdateMigrationCoordinator>(sp =>
            new HostUpdateMigrationCoordinator(sp.GetRequiredService<IReadOnlyList<IHostUpdateMigrationTarget>>()));

        services.AddScoped<IHostUpdateBackupTarget>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            DatabaseProviderConfiguration dbConfig = DatabaseProviderConfiguration.FromConfiguration(sp.GetRequiredService<IConfiguration>());
            return HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
                "database",
                dbConfig,
                sp.GetRequiredService<IHostUpdateProcessRunner>(),
                TimeSpan.FromSeconds(options.BackupTimeoutSeconds),
                options.DatabaseExternallyOwned);
        });
        services.AddScoped<IEnumerable<IHostUpdateBackupTarget>>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            List<IHostUpdateBackupTarget> targets = [.. sp.GetServices<IHostUpdateBackupTarget>()];
            targets.AddRange(options.OwnedDirectories.Select(pair => new DirectoryCopyBackupTarget(pair.Key, pair.Value)));
            return targets;
        });
        services.AddScoped<IReadOnlyList<IHostUpdateBackupTarget>>(sp => [.. sp.GetRequiredService<IEnumerable<IHostUpdateBackupTarget>>()]);
        services.AddScoped<IHostUpdateBackupCoordinator>(sp => new HostUpdateBackupCoordinator(
            sp.GetRequiredService<IReadOnlyList<IHostUpdateBackupTarget>>(),
            sp.GetRequiredService<HostUpdateExecutionOptions>().BackupRootDirectory));
    }

    private static void AddApplyAndVerify(IServiceCollection services)
    {
        services.AddSingleton<IHostUpdateApplyCoordinator>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return CreateImageApplier(sp, options);
        });
        services.AddSingleton<IHostUpdateDigestApplier>(sp => (IHostUpdateDigestApplier)sp.GetRequiredService<IHostUpdateApplyCoordinator>());

        services.AddScoped<IHostUpdateHealthVerifier>(sp => CreateHealthVerifier(sp));
        services.AddScoped<IHostUpdateDigestVerifier>(sp => (IHostUpdateDigestVerifier)sp.GetRequiredService<IHostUpdateHealthVerifier>());
    }

    private static HostUpdateImageApplier CreateImageApplier(IServiceProvider sp, HostUpdateExecutionOptions options)
    {
        var mappings = options.ServiceMappings.ToDictionary(
            m => m.ServiceId,
            m => new HostUpdateApplyServiceMapping(m.ServiceId, m.ComposeServiceName, m.ImageEnvironmentVariable, m.ImageRepository),
            StringComparer.Ordinal);
        return new HostUpdateImageApplier(
            sp.GetRequiredService<IHostUpdateProcessRunner>(),
            options.ComposeFiles,
            options.ComposeProjectName,
            mappings,
            TimeSpan.FromSeconds(options.ApplyTimeoutSeconds));
    }

    private static HostUpdateHealthVerifier CreateHealthVerifier(IServiceProvider sp)
    {
        HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
        IHttpClientFactory httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
        IHostUpdateProcessRunner processRunner = sp.GetRequiredService<IHostUpdateProcessRunner>();

        List<IHostUpdateHealthCheck> staticChecks =
        [
            new HttpHostUpdateHealthCheck("api", httpClientFactory.CreateClient(HealthClientName), "/healthz"),
        ];

        return new HostUpdateHealthVerifier(
            staticChecks,
            (serviceId, digest) => new DigestHostUpdateHealthCheck(
                $"digest:{serviceId}",
                processRunner,
                ContainerNameFor(options, serviceId),
                digest),
            TimeSpan.FromSeconds(options.VerifyTimeoutSeconds),
            TimeSpan.FromSeconds(options.VerifyPollIntervalSeconds));
    }

    private static string ContainerNameFor(HostUpdateExecutionOptions options, string serviceId)
    {
        HostUpdateServiceMappingOptions? mapping = options.ServiceMappings.FirstOrDefault(m => string.Equals(m.ServiceId, serviceId, StringComparison.Ordinal));
        string composeServiceName = mapping?.ComposeServiceName ?? serviceId;
        return $"{options.ComposeProjectName}-{composeServiceName}-1";
    }

    private static void AddRecovery(IServiceCollection services)
    {
        services.AddSingleton<IHostUpdateRecoveryCompatibilityEvaluator, DefaultHostUpdateRecoveryCompatibilityEvaluator>();
        services.AddScoped<IHostUpdateBackupManifestLocator>(sp =>
            new FileHostUpdateBackupManifestLocator(sp.GetRequiredService<HostUpdateExecutionOptions>().BackupRootDirectory));
        services.AddScoped<IHostUpdateRestoreExecutor>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            DatabaseProviderConfiguration dbConfig = DatabaseProviderConfiguration.FromConfiguration(sp.GetRequiredService<IConfiguration>());
            var restoreCommandsByTarget = new Dictionary<string, Func<string, IReadOnlyList<string>>>(StringComparer.Ordinal)
            {
                ["database"] = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(dbConfig),
            };
            var directoryRestoreTargetsByName = new Dictionary<string, string>(options.OwnedDirectories, StringComparer.Ordinal);
            return new ProcessHostUpdateRestoreExecutor(
                sp.GetRequiredService<IHostUpdateProcessRunner>(),
                restoreCommandsByTarget,
                directoryRestoreTargetsByName,
                TimeSpan.FromSeconds(options.BackupTimeoutSeconds));
        });
        services.AddScoped<IHostUpdateRecoveryCoordinator, HostUpdateRecoveryCoordinator>();
    }
}

/// <summary>
/// Opens a fresh DI scope per availability probe so the periodic singleton background service
/// never holds a scoped <see cref="Microsoft.EntityFrameworkCore.DbContext"/>-backed dependency
/// (e.g. a migration target) beyond that single check.
/// </summary>
internal sealed class ScopedHostUpdateExecutionAvailabilityProvider(IServiceProvider rootProvider) : IHostUpdateExecutionAvailabilityProvider
{
    public async Task<HostUpdateExecutionAvailability> CheckAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = rootProvider.CreateScope();
        IHostUpdateExecutionAvailabilityProvider provider = scope.ServiceProvider.GetRequiredService<IHostUpdateExecutionAvailabilityProvider>();
        return await provider.CheckAsync(cancellationToken).ConfigureAwait(false);
    }
}
