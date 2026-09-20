using Farm.Infrastructure.Data;
using Farm.Infrastructure.Data.Migrations;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Slicer.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.TryAddSingleton<IHostUpdateAutomationPolicyRepository, UnavailableHostUpdateAutomationPolicyRepository>();
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

        services.AddSingleton<IHostUpdateExecutableResolver>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return new ConfiguredHostUpdateExecutableResolver(
                options.HostExecutablePaths.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        });
        services.AddSingleton<IHostUpdateProcessRunner>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            var configuredPaths = options.HostExecutablePaths.Values
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(Path.IsPathRooted)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.Ordinal);
            return new ConstrainedHostUpdateProcessRunner(new DefaultHostUpdateProcessRunner(), configuredPaths);
        });

        // Durable single-writer state, all rooted under the validated, host-controlled
        // RootDirectory (never the app DB, never a temp/cache path -- see
        // HostUpdateExecutionOptionsValidator).
        services.AddSingleton<IInstalledHostStateStore>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnconfiguredInstalledHostStateStore()
                : new FileInstalledHostStateStore(Path.Combine(options.StateDirectory, "installed-state.json"));
        });
        services.AddSingleton<IHostUpdateExecutionJournal>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnavailableHostUpdateExecutionJournal()
                : new FileHostUpdateExecutionJournal(Path.Combine(options.StateDirectory, "journal.ndjson"));
        });
        services.AddSingleton<IHostUpdateExecutionLock>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnavailableHostUpdateExecutionLock()
                : new FileHostUpdateExecutionLock(Path.Combine(options.StateDirectory, "execution.lock"));
        });
        services.AddSingleton<IHostUpdateJournal>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnconfiguredHostUpdateJournal()
                : new FileHostUpdateJournal(options.StateDirectory);
        });

        // Drain: perimeter admission gate + observed active print/outbox work.
        services.AddSingleton<IHostUpdateAdmissionGate, FileHostUpdateAdmissionGate>();
        services.AddScoped<DbActiveWorkObservationPort>();
        services.AddScoped<IActiveWorkObservationPort>(sp => sp.GetRequiredService<DbActiveWorkObservationPort>());
        services.AddScoped<IHostUpdateDrainCoordinator>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return new HostUpdateDrainCoordinator(
                sp.GetRequiredService<IHostUpdateAdmissionGate>(),
                [.. sp.GetServices<IActiveWorkObservationPort>()],
                TimeSpan.FromSeconds(options.DrainTimeoutSeconds),
                TimeSpan.FromSeconds(options.DrainPollIntervalSeconds));
        });

        // Fence: every registered writer must prove quiescence before backup. The outbox
        // publisher's fence flag is the same singleton QueueOutboxPublisherService consults
        // directly (see its optional IHostUpdateWriterActivityFlag constructor parameter).
        // PowerReadingPruneService and QueueRetentionPruneService each get their own,
        // independently-typed flag (PowerReadingPruneFenceFlag / QueueRetentionPruneFenceFlag)
        // -- IHostUpdateWriterActivityFlag's pause/acknowledge state is a single shared boolean
        // pair per instance, so distinct writers that must be independently proven quiesced
        // cannot share one registration of the bare interface (the container would hand every
        // optional-parameter consumer the same last-registered instance).
        services.AddSingleton<IHostUpdateWriterActivityFlag, InMemoryHostUpdateWriterActivityFlag>();
        services.AddSingleton<PowerReadingPruneFenceFlag>();
        services.AddSingleton<QueueRetentionPruneFenceFlag>();
        services.AddSingleton<AutoDispatchFenceFlag>();
        services.AddSingleton<WebhookDeliveryFenceFlag>();
        services.AddSingleton<IReadOnlyList<IFenceableWriter>>(sp =>
        [
            new AdmissionFenceableWriter(sp.GetRequiredService<IHostUpdateAdmissionGate>()),
            new BackgroundWriterFenceableWriter("queue-outbox-publisher", sp.GetRequiredService<IHostUpdateWriterActivityFlag>()),
            new BackgroundWriterFenceableWriter("power-reading-prune", sp.GetRequiredService<PowerReadingPruneFenceFlag>()),
            new BackgroundWriterFenceableWriter("queue-retention-prune", sp.GetRequiredService<QueueRetentionPruneFenceFlag>()),
            new BackgroundWriterFenceableWriter("auto-dispatch", sp.GetRequiredService<AutoDispatchFenceFlag>()),
            new BackgroundWriterFenceableWriter("webhook-delivery", sp.GetRequiredService<WebhookDeliveryFenceFlag>()),
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
                sp.GetRequiredService<IHostUpdateExecutableResolver>(),
                options.DiskWatchPath,
                options.MinimumFreeBytes,
                options.SupportedProviderNames.ToHashSet(StringComparer.Ordinal),
                options.ActiveServiceIds.ToHashSet(StringComparer.Ordinal));
        });
        services.AddScoped<IHostUpdateExecutionSteps, HostUpdateExecutionStepsAdapter>();
        services.AddScoped<IHostUpdateExecutor>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnavailableHostUpdateExecutor()
                : ActivatorUtilities.CreateInstance<HostUpdateExecutor>(sp);
        });

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
        services.AddScoped<HostUpdateTargetImageMigrationRunner>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return new HostUpdateTargetImageMigrationRunner(
                sp.GetRequiredService<IHostUpdateProcessRunner>(),
                sp.GetRequiredService<IHostUpdateExecutableResolver>(),
                options.ServiceMappings.ToDictionary(
                    mapping => mapping.ServiceId,
                    mapping => new HostUpdateApplyServiceMapping(mapping.ServiceId, mapping.ComposeServiceName, mapping.ImageEnvironmentVariable, mapping.ImageRepository),
                    StringComparer.Ordinal),
                TimeSpan.FromSeconds(options.BackupTimeoutSeconds));
        });
        services.AddScoped<IHostUpdateMigrationTarget>(sp => new TargetImageMigrationTarget<AppDbContext>(
            "AppDbContext",
            () => sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<HostUpdateTargetImageMigrationRunner>()));
        services.AddScoped<IHostUpdateMigrationTarget>(sp =>
        {
            SlicerDbContext? slicerDb = sp.GetService<SlicerDbContext>();
            return new TargetImageMigrationTarget<SlicerDbContext>(
                "SlicerDbContext",
                () => slicerDb ?? throw new InvalidOperationException("slicer_db_context_not_registered"),
                sp.GetRequiredService<HostUpdateTargetImageMigrationRunner>());
        });
        services.AddScoped<IReadOnlyList<IHostUpdateMigrationTarget>>(sp => [.. sp.GetServices<IHostUpdateMigrationTarget>()]);
        services.AddScoped<HostUpdateMigrationCoordinator>(sp =>
            new HostUpdateMigrationCoordinator(sp.GetRequiredService<IReadOnlyList<IHostUpdateMigrationTarget>>()));
        services.AddScoped<IHostUpdateMigrationCoordinator>(sp => sp.GetRequiredService<HostUpdateMigrationCoordinator>());
        services.AddScoped<IHostUpdateMigrationReconciler>(sp => sp.GetRequiredService<HostUpdateMigrationCoordinator>());

        services.AddScoped<IHostUpdateBackupTarget>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            DatabaseProviderConfiguration dbConfig = DatabaseProviderConfiguration.FromConfiguration(sp.GetRequiredService<IConfiguration>());
            return HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
                "database",
                dbConfig,
                sp.GetRequiredService<IHostUpdateProcessRunner>(),
                sp.GetRequiredService<IHostUpdateExecutableResolver>(),
                TimeSpan.FromSeconds(options.BackupTimeoutSeconds),
                options.DatabaseExternallyOwned);
        });

        // Bishop/Hicks review (issue #2663): do NOT register IEnumerable<IHostUpdateBackupTarget>
        // explicitly. The .NET container implements GetServices<T>() as GetRequiredService<IEnumerable<T>>();
        // an explicit registration for IEnumerable<IHostUpdateBackupTarget> whose own factory calls
        // sp.GetServices<IHostUpdateBackupTarget>() therefore resolves itself and recurses without bound
        // (a self-referential registration graph, confirmed reproducible via
        // HostUpdateBackupDiStartupTests). Build the concrete IReadOnlyList<IHostUpdateBackupTarget>
        // directly instead: GetServices<IHostUpdateBackupTarget>() safely uses the container's built-in
        // multi-registration aggregation because no IEnumerable<IHostUpdateBackupTarget> override exists.
        services.AddScoped<IReadOnlyList<IHostUpdateBackupTarget>>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            List<IHostUpdateBackupTarget> targets = [.. sp.GetServices<IHostUpdateBackupTarget>()];
            var optionalDirectoryNames = new HashSet<string>(options.OptionalOwnedDirectories, StringComparer.Ordinal);
            targets.AddRange(options.OwnedDirectories.Select(pair =>
                new DirectoryCopyBackupTarget(pair.Key, pair.Value, isRequired: !optionalDirectoryNames.Contains(pair.Key))));
            return targets;
        });
        services.AddScoped<IHostUpdateBackupCoordinator>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnconfiguredHostUpdateBackupCoordinator()
                : new HostUpdateBackupCoordinator(sp.GetRequiredService<IReadOnlyList<IHostUpdateBackupTarget>>(), options.BackupRootDirectory);
        });
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
        services.AddScoped<IHostUpdateSideEffectReconciler, HostUpdateSideEffectReconciler>();
    }

    private static HostUpdateImageApplier CreateImageApplier(IServiceProvider sp, HostUpdateExecutionOptions options)
    {
        var mappings = options.ServiceMappings.ToDictionary(
            m => m.ServiceId,
            m => new HostUpdateApplyServiceMapping(m.ServiceId, m.ComposeServiceName, m.ImageEnvironmentVariable, m.ImageRepository),
            StringComparer.Ordinal);
        return new HostUpdateImageApplier(
            sp.GetRequiredService<IHostUpdateProcessRunner>(),
            sp.GetRequiredService<IHostUpdateExecutableResolver>(),
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
            new AggregateHostUpdateHealthCheck(
                "api-comprehensive-health",
                httpClientFactory.CreateClient(HealthClientName),
                "/health",
                options.RequiredAggregateHealthResultNames.ToHashSet(StringComparer.Ordinal)),
        ];

        return new HostUpdateHealthVerifier(
            staticChecks,
            (serviceId, digest) => new DigestHostUpdateHealthCheck(
                $"digest:{serviceId}",
                processRunner,
                sp.GetRequiredService<IHostUpdateExecutableResolver>(),
                ContainerNameFor(options, serviceId),
                digest),
            TimeSpan.FromSeconds(options.VerifyTimeoutSeconds),
            TimeSpan.FromSeconds(options.VerifyPollIntervalSeconds),
            options.ActiveServiceIds.ToHashSet(StringComparer.Ordinal));
    }

    private static string ContainerNameFor(HostUpdateExecutionOptions options, string serviceId)
    {
        HostUpdateServiceMappingOptions? mapping = options.ServiceMappings.FirstOrDefault(m => string.Equals(m.ServiceId, serviceId, StringComparison.Ordinal));
        if (mapping is null)
        {
            // Preflight already fails closed when a request target has no ServiceMappings entry
            // (Kane audit P0.5); this is a second, independent guard so a caller that reaches
            // digest verification through any other path never silently guesses a container name
            // that would just fail an unrelated "docker inspect" call later.
            throw new InvalidOperationException($"service_mapping_missing:{serviceId}");
        }

        return $"{options.ComposeProjectName}-{mapping.ComposeServiceName}-1";
    }

    private static void AddRecovery(IServiceCollection services)
    {
        services.AddSingleton<IHostUpdateRecoveryCompatibilityEvaluator, DefaultHostUpdateRecoveryCompatibilityEvaluator>();
        services.AddScoped<IHostUpdateBackupManifestLocator>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnconfiguredHostUpdateBackupManifestLocator()
                : new FileHostUpdateBackupManifestLocator(options.BackupRootDirectory);
        });
        services.AddScoped<IHostUpdateRestoreExecutor>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            DatabaseProviderConfiguration dbConfig = DatabaseProviderConfiguration.FromConfiguration(sp.GetRequiredService<IConfiguration>());
            var restoreCommandsByTarget = new Dictionary<string, Func<string, HostUpdateRestoreCommand>>(StringComparer.Ordinal)
            {
                ["database"] = HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(
                    dbConfig,
                    sp.GetRequiredService<IHostUpdateExecutableResolver>()),
            };
            var directoryRestoreTargetsByName = new Dictionary<string, string>(options.OwnedDirectories, StringComparer.Ordinal);
            return new ProcessHostUpdateRestoreExecutor(
                sp.GetRequiredService<IHostUpdateProcessRunner>(),
                restoreCommandsByTarget,
                directoryRestoreTargetsByName,
                TimeSpan.FromSeconds(options.BackupTimeoutSeconds));
        });
        services.AddSingleton<IHostUpdateRecoveryOutcomeStore>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnconfiguredHostUpdateRecoveryOutcomeStore()
                : new FileHostUpdateRecoveryOutcomeStore(Path.Combine(options.StateDirectory, "recovery-outcomes"));
        });
        services.AddScoped<IHostUpdateRecoveryCoordinator>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnavailableHostUpdateRecoveryCoordinator()
                : ActivatorUtilities.CreateInstance<HostUpdateRecoveryCoordinator>(sp);
        });
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
