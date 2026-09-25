using Farm.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Composition of the host-update state, lock, fence, apply/verify and recovery engine shared
/// by the API and the host-local recovery CLI (issue #2980). Keeping one registration means the
/// CLI cannot drift onto a second executor, lock, journal or recovery rule.
/// </summary>
public static class HostUpdateRecoveryEngineRegistration
{
    public const string HealthClientName = "HostUpdateHealth";

    public static IServiceCollection AddHostUpdateRecoveryEngine(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(HostUpdateExecutionOptions.SectionName);
        services.AddOptions<HostUpdateExecutionOptions>()
            .Bind(section)
            .Configure(options => ReplaceConfiguredTopologyLists(options, section))
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
                : new FileInstalledHostStateStore(Path.Join(options.StateDirectory, "installed-state.json"));
        });
        services.AddSingleton<IHostUpdateExecutionJournal>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnavailableHostUpdateExecutionJournal()
                : new FileHostUpdateExecutionJournal(Path.Join(options.StateDirectory, "journal.ndjson"));
        });
        services.AddSingleton<IHostUpdateExecutionLock>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnavailableHostUpdateExecutionLock()
                : new FileHostUpdateExecutionLock(Path.Join(options.StateDirectory, FileHostUpdateExecutionLock.FileName));
        });

        services.AddSingleton<IHostUpdateAdmissionGate, FileHostUpdateAdmissionGate>();
        AddFence(services);
        AddApplyAndVerify(services);
        AddRecovery(services);
        return services;
    }

    private static void AddFence(IServiceCollection services)
    {
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
        services.AddSingleton<BackendStartCommandConsumerFenceFlag>();
        services.AddSingleton<BackendControlCommandConsumerFenceFlag>();
        services.AddSingleton<BedClearAcknowledgementExpiryFenceFlag>();
        services.AddSingleton<AutoDispatchFenceFlag>();
        services.AddSingleton<WebhookDeliveryFenceFlag>();
        services.AddSingleton<QueueReconciliationFenceFlag>(sp =>
            new QueueReconciliationFenceFlag(sp.GetRequiredService<IHostUpdateAdmissionGate>()));
        services.AddSingleton<IReadOnlyList<IFenceableWriter>>(sp =>
        [
            new AdmissionFenceableWriter(sp.GetRequiredService<IHostUpdateAdmissionGate>()),
            new BackgroundWriterFenceableWriter("queue-outbox-publisher", sp.GetRequiredService<IHostUpdateWriterActivityFlag>()),
            new BackgroundWriterFenceableWriter("power-reading-prune", sp.GetRequiredService<PowerReadingPruneFenceFlag>()),
            new BackgroundWriterFenceableWriter("queue-retention-prune", sp.GetRequiredService<QueueRetentionPruneFenceFlag>()),
            new BackgroundWriterFenceableWriter("backend-start-command-consumer", sp.GetRequiredService<BackendStartCommandConsumerFenceFlag>()),
            new BackgroundWriterFenceableWriter("backend-control-command-consumer", sp.GetRequiredService<BackendControlCommandConsumerFenceFlag>()),
            new BackgroundWriterFenceableWriter("bed-clear-acknowledgement-expiry", sp.GetRequiredService<BedClearAcknowledgementExpiryFenceFlag>()),
            new BackgroundWriterFenceableWriter("auto-dispatch", sp.GetRequiredService<AutoDispatchFenceFlag>()),
            new BackgroundWriterFenceableWriter("webhook-delivery", sp.GetRequiredService<WebhookDeliveryFenceFlag>()),
            new BackgroundWriterFenceableWriter("queue-reconciliation", sp.GetRequiredService<QueueReconciliationFenceFlag>()),
        ]);
        services.AddSingleton<IHostUpdateFenceCoordinator>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return new HostUpdateFenceCoordinator(
                sp.GetRequiredService<IReadOnlyList<IFenceableWriter>>(),
                TimeSpan.FromSeconds(options.FenceProofTimeoutSeconds),
                TimeSpan.FromSeconds(options.FencePollIntervalSeconds),
                logger: sp.GetRequiredService<ILogger<HostUpdateFenceCoordinator>>());
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
    }

    /// <summary>
    /// The configuration binder appends bound array entries to an initialised default instead of
    /// replacing it, so a configured topology would silently keep the built-in split-topology
    /// entries too (issues #2997 and #3042). A configured <c>ComposeFiles</c> or
    /// <c>ActiveServiceIds</c> list is authoritative; the built-in default applies only when the
    /// key is absent, and an explicitly empty list fails validation. Safety allowlists/requirements (<c>SupportedProviderNames</c>,
    /// <c>RequiredAggregateHealthResultNames</c>, <c>RequiredFencedWriterNames</c>) deliberately
    /// stay additive so configuration can never drop a code-owned requirement.
    /// </summary>
    private static void ReplaceConfiguredTopologyLists(HostUpdateExecutionOptions options, IConfiguration section)
    {
        if (ConfiguredList(section, nameof(HostUpdateExecutionOptions.ComposeFiles)) is { } composeFiles)
        {
            options.ComposeFiles = composeFiles;
        }

        if (ConfiguredList(section, nameof(HostUpdateExecutionOptions.ActiveServiceIds)) is { } activeServiceIds)
        {
            options.ActiveServiceIds = activeServiceIds;
        }
    }

    /// <summary>
    /// Returns the configured list, or <see langword="null"/> when the key is absent. A key that is
    /// present but holds no entries (JSON <c>[]</c> binds as an empty-string value, as does an empty
    /// environment variable) returns an empty list so validation fails closed instead of silently
    /// restoring the built-in default.
    /// </summary>
    private static string[]? ConfiguredList(IConfiguration section, string key)
    {
        IConfigurationSection list = section.GetSection(key);
        if (list.GetChildren().Any())
        {
            return list.Get<string[]>() ?? [];
        }

        return list.Value is null ? null : [];
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
                : new FileHostUpdateRecoveryOutcomeStore(Path.Join(options.StateDirectory, "recovery-outcomes"));
        });
        services.AddScoped<IHostUpdateRecoveryCoordinator>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnavailableHostUpdateRecoveryCoordinator()
                : ActivatorUtilities.CreateInstance<HostUpdateRecoveryCoordinator>(sp);
        });
        services.AddScoped<IHostUpdateRecoveryPlanner>(sp =>
            (IHostUpdateRecoveryPlanner)sp.GetRequiredService<IHostUpdateRecoveryCoordinator>());
    }
}
