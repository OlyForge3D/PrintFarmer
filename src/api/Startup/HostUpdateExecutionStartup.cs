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
    public static IServiceCollection AddHostUpdateExecution(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IHostUpdateAutomationPolicyRepository, UnavailableHostUpdateAutomationPolicyRepository>();

        // Shared with the host-local recovery CLI (issue #2980): options, process runner,
        // durable state, lock, admission gate, fence, apply/verify and recovery.
        services.AddHostUpdateRecoveryEngine(configuration);

        services.AddSingleton<IHostUpdateJournal>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnconfiguredHostUpdateJournal()
                : new FileHostUpdateJournal(options.StateDirectory);
        });

        // Drain: perimeter admission gate (shared engine) + observed active print/outbox work.
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

        AddBackupAndMigration(services);
        services.AddScoped<IHostUpdateSideEffectReconciler, HostUpdateSideEffectReconciler>();

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
                () => CreateMigrationEnvironment(sp.GetRequiredService<IConfiguration>()),
                options.ComposeProjectName + "-network",
                TimeSpan.FromSeconds(options.MigrationTimeoutSeconds));
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

            // BackupRootDirectory throws root_directory_not_configured when RootDirectory is
            // unset (the executor's normal default-off state). Do not let that exception
            // propagate out of DI resolution here -- it would crash any scoped resolution of
            // IHostUpdateBackupTarget (e.g. HostUpdateExecutionAvailabilityProvider.CheckAsync's
            // unconditional backupTargets.Count check) instead of the intended graceful
            // fail-closed reporting. An unset root passes an empty directory through, which the
            // #2788 mapping verification already reports as backup_root_directory_not_configured.
            string backupRootDirectory = string.IsNullOrWhiteSpace(options.RootDirectory) ? string.Empty : options.BackupRootDirectory;

            return HostUpdateDatabaseBackupTargetFactory.CreateBackupTarget(
                "database",
                dbConfig,
                sp.GetRequiredService<IHostUpdateProcessRunner>(),
                sp.GetRequiredService<IHostUpdateExecutableResolver>(),
                TimeSpan.FromSeconds(options.BackupTimeoutSeconds),
                options.DatabaseExternallyOwned,
                backupRootDirectory);
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

    private static Dictionary<string, string> CreateMigrationEnvironment(IConfiguration configuration)
    {
        (string ConfigurationKey, string EnvironmentKey)[] requiredKeys =
        [
            ("DB_PROVIDER", "DB_PROVIDER"),
            ("ConnectionStrings:Default", "ConnectionStrings__Default"),
            ("Jwt:Key", "Jwt__Key"),
            ("Jwt:Issuer", "Jwt__Issuer"),
            ("Jwt:Audience", "Jwt__Audience"),
        ];
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string configurationKey, string environmentKey) in requiredKeys)
        {
            string? value = configuration[configurationKey];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"target_image_migration_configuration_missing:{configurationKey}");
            }

            environment[environmentKey] = value;
        }

#pragma warning disable S5443 // The target image receives a private tmpfs at /tmp.
        environment["DATAPROTECTION_KEYS_PATH"] = "/tmp/dp-keys";
#pragma warning restore S5443
        return environment;
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
