using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Data.Migrations;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.HostUpdate.Cli;

/// <summary>
/// Host-local, API-independent status and recovery entry point (issue #2980, first slice).
/// Reuses the shared engine registration, the journal-binding resolver, the execution lock and
/// the recovery coordinator; it adds no second executor and accepts no replacement release
/// material, arbitrary paths, shell text, or credentials on the command line.
/// </summary>
public static partial class HostUpdateCli
{
    public const string Usage = """
        Usage:
          printfarmer-host-update status [--release <releaseId>] [--json]
          printfarmer-host-update recover --release <releaseId> [--request-id <requestId>] --preview [--json]
          printfarmer-host-update recover --release <releaseId> [--request-id <requestId>] --confirm <releaseId> [--reapprove-drift <token>] [--printers-reconciled <token>] [--json]
          printfarmer-host-update offline-admit --staging <absolute-verified-staging-dir> --channel <stable|insider> --trusted-root <absolute-trusted_root.json> [--cosign <absolute-path>] [--json]
          printfarmer-host-update offline-activate --staging <absolute-verified-staging-dir> --channel <stable|insider> --trusted-root <absolute-trusted_root.json> [--cosign <absolute-path>] [--json]

        Configuration comes from --config <absolute-json-path> and environment variables
        (HostUpdateExecution__*, HostUpdates__HostState__*, DB_PROVIDER, ConnectionStrings__Default).
        Credentials are never accepted as arguments. This tool is not rollout authorization.

        When the host has drifted from the recorded authorization (platform, standing policy,
        prior installed state), --confirm is refused until it is reapproved with the exact
        --reapprove-drift token printed by --preview.

        After a rollback, admission stays fenced until every printer has been physically checked
        and that is recorded with the exact --printers-reconciled token printed by --preview.
        Recovery never replays, cancels or issues a printer command.

        offline-admit re-verifies the staged signed manifest offline against --trusted-root and
        records it in the durable replay store (issue #3064). It refuses a replayed, downgraded or cross-channel
        release; it installs nothing and is not rollout authorization.

        offline-activate re-verifies the same staged signed manifest, requires matching imported
        replay evidence, verifies every target image locally, and then asks the existing host-update
        executor to run in preloaded-image mode with no registry fallback or local build.

        Exit codes: 0 ok, 2 usage, 3 configuration/namespace unproven, 4 state unreadable,
        5 no history, 6 refused, 7 lock held, 10 needs operator, 11 fence release pending,
        12 drift not reapproved, 13 physical printer reconciliation not recorded.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Task<int> RunAsync(
        IReadOnlyList<string> args,
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return RunAsync(args, () => configuration, output, error, cancellationToken);
    }

    /// <summary>
    /// Parses the command first, then loads configuration, so a malformed or unreadable
    /// configuration source still honours the exit-code and <c>--json</c> contract (exit 3).
    /// </summary>
    public static Task<int> RunAsync(
        IReadOnlyList<string> args,
        Func<IConfiguration> configurationFactory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        RunAsync(args, configurationFactory, output, error, configureServices: null, cancellationToken);

    /// <summary>
    /// Test seam: <paramref name="configureServices"/> runs after the shared engine registration so
    /// host process and network boundaries can be replaced with fakes. Production never passes it.
    /// </summary>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        Func<IConfiguration> configurationFactory,
        TextWriter output,
        TextWriter error,
        Action<IServiceCollection>? configureServices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configurationFactory);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!HostUpdateCliArguments.TryParse(args, out HostUpdateCliArguments? parsed, out string? usageError))
        {
            await error.WriteLineAsync(usageError).ConfigureAwait(false);
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return HostUpdateCliExitCodes.Usage;
        }

        if (parsed!.Command == HostUpdateCliCommand.Help)
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return HostUpdateCliExitCodes.Success;
        }

        IConfiguration configuration;
        try
        {
            configuration = configurationFactory();
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or IOException
            or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            // The message may contain host paths; only the exception type is reported.
            return await EmitAsync(output, parsed.Json, HostUpdateCliExitCodes.ConfigurationUnproven, new CliFailure("configuration_unreadable", [exception.GetType().Name])).ConfigureAwait(false);
        }

        ServiceProvider provider;
        HostUpdateExecutionOptions options;
        try
        {
            provider = BuildServices(configuration, error, configureServices);
            options = provider.GetRequiredService<HostUpdateExecutionOptions>();
        }
        catch (OptionsValidationException exception)
        {
            return await EmitAsync(output, parsed.Json, HostUpdateCliExitCodes.ConfigurationUnproven, new CliFailure("configuration_invalid", exception.Failures.ToArray())).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            // Binder conversion failures (e.g. a non-boolean Enabled value).
            return await EmitAsync(output, parsed.Json, HostUpdateCliExitCodes.ConfigurationUnproven, new CliFailure("configuration_invalid", [exception.GetType().Name])).ConfigureAwait(false);
        }

        await using (provider.ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(options.RootDirectory) || !Directory.Exists(options.RootDirectory) || !Directory.Exists(options.StateDirectory))
            {
                return await EmitAsync(output, parsed.Json, HostUpdateCliExitCodes.ConfigurationUnproven, new CliFailure("root_directory_not_visible", [])).ConfigureAwait(false);
            }

            return parsed.Command switch
            {
                HostUpdateCliCommand.Status => await StatusAsync(provider, parsed, output, cancellationToken).ConfigureAwait(false),
                HostUpdateCliCommand.OfflineAdmit => await HostUpdateOfflineAdmission.RunAsync(provider, configuration, parsed, output, cancellationToken).ConfigureAwait(false),
                HostUpdateCliCommand.OfflineActivate => await HostUpdateOfflineActivation.RunAsync(provider, configuration, parsed, output, cancellationToken).ConfigureAwait(false),
                _ => await RecoverAsync(provider, configuration, options, parsed, output, cancellationToken).ConfigureAwait(false),
            };
        }
    }

    internal static ServiceProvider BuildServices(IConfiguration configuration, TextWriter error, Action<IServiceCollection>? configureServices = null)
    {
        IServiceCollection services = ConfigureServices(new ServiceCollection(), configuration, error);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>
    /// The CLI's complete service graph. It deliberately registers no printer backend client,
    /// plugin, or dispatcher: recovery can read the command inventory but can never send a
    /// printer command, so nothing is replayed and no real command is used as a smoke test.
    /// </summary>
    internal static IServiceCollection ConfigureServices(IServiceCollection services, IConfiguration configuration, TextWriter error)
    {
        services.AddSingleton(configuration);
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddProvider(new HostUpdateCliLoggerProvider(error)));
        services.AddHostUpdateRecoveryEngine(configuration);
        services.AddOptions<HostStateOptions>()
            .Bind(configuration.GetSection(HostStateOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HostStateOptions>, HostStateOptionsValidator>();

        // Decorate the installed-state store so a confirm is bound to the state it evaluated.
        ServiceDescriptor installedStore = services.Last(d => d.ServiceType == typeof(IInstalledHostStateStore));
        services.Remove(installedStore);
        services.AddSingleton(sp => new ApprovalBoundInstalledHostStateStore(
            (IInstalledHostStateStore)installedStore.ImplementationFactory!(sp)));
        services.AddSingleton<IInstalledHostStateStore>(sp => sp.GetRequiredService<ApprovalBoundInstalledHostStateStore>());
        services.AddSingleton<IHostUpdatePrinterCommandInventoryReader>(sp =>
            new HostUpdateCliPrinterCommandInventoryReader(DatabaseProviderConfiguration.FromConfiguration(sp.GetRequiredService<IConfiguration>())));
        services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe, DockerComposeApiAbsenceProbe>();
        AddOfflineActivationExecution(services, configuration);
        return services;
    }

    private static void AddOfflineActivationExecution(IServiceCollection services, IConfiguration configuration)
    {
        DatabaseProviderConfiguration dbConfig = DatabaseProviderConfiguration.FromConfiguration(configuration);
        services.AddDbContext<AppDbContext>(options => ConfigureMainDbProvider(options, dbConfig));
        services.AddDbContext<SlicerDbContext>(options => ConfigureSlicerDbProvider(options, dbConfig));
        services.AddScoped<DbActiveWorkObservationPort>();
        services.AddScoped<IActiveWorkObservationPort>(sp => sp.GetRequiredService<DbActiveWorkObservationPort>());
        services.AddScoped<IActiveWorkObservationPort, SlicerActiveWorkObservationPort>();
        services.AddSingleton<FileHostUpdateAutomationPolicyRepository>(sp =>
        {
            HostStateOptions hostState = sp.GetRequiredService<IOptions<HostStateOptions>>().Value;
            return new FileHostUpdateAutomationPolicyRepository(HostStatePath.OpenReadOnly(hostState));
        });
        services.AddSingleton<IHostUpdateAutomationPolicyRepository>(sp => sp.GetRequiredService<FileHostUpdateAutomationPolicyRepository>());
        services.AddScoped<IHostUpdateExecutionStartGuard, HostUpdateOfflineActivationStartGuard>();
        services.AddScoped<IHostUpdateExecutionCompletionHook, HostUpdateOfflineActivationCompletionHook>();
        services.AddScoped<IHostUpdateJournal>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnconfiguredHostUpdateJournal()
                : new FileHostUpdateJournal(options.StateDirectory);
        });
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
        services.AddScoped<IHostUpdateSideEffectReconciler, HostUpdateSideEffectReconciler>();
        services.AddScoped<IHostUpdateAuthorizationBaselineProvider>(sp => new HostUpdateAuthorizationBaselineProvider(
            sp.GetRequiredService<IInstalledHostStateStore>(),
            sp.GetRequiredService<HostUpdateExecutionOptions>(),
            DatabaseProviderConfiguration.FromConfiguration(sp.GetRequiredService<IConfiguration>()),
            sp.GetRequiredService<IHostUpdateManifestBindingReader>()));
        services.AddScoped<IHostUpdateExecutor>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            return string.IsNullOrWhiteSpace(options.RootDirectory)
                ? new UnavailableHostUpdateExecutor()
                : ActivatorUtilities.CreateInstance<HostUpdateExecutor>(sp);
        });
    }

    private static void ConfigureMainDbProvider(DbContextOptionsBuilder options, DatabaseProviderConfiguration dbConfig)
    {
        if (dbConfig.IsSqlServer)
        {
            _ = options.UseSqlServer(dbConfig.ConnectionString, x => x.MigrationsAssembly("Farm.Migrations.SqlServer"));
        }
        else if (dbConfig.IsPostgres)
        {
            _ = options.UseNpgsql(dbConfig.ConnectionString, x => x.MigrationsAssembly("Farm.Migrations.PostgreSQL"));
        }
        else
        {
            _ = options.UseSqlite(dbConfig.ConnectionString, x => x.MigrationsAssembly("Farm.Migrations.Sqlite"));
        }
    }

    private static void ConfigureSlicerDbProvider(DbContextOptionsBuilder options, DatabaseProviderConfiguration dbConfig)
    {
        if (dbConfig.IsSqlServer)
        {
            _ = options.UseSqlServer(dbConfig.ConnectionString, x => x.MigrationsAssembly("Farm.Slicer.Migrations.SqlServer"));
        }
        else if (dbConfig.IsPostgres)
        {
            _ = options.UseNpgsql(dbConfig.ConnectionString, x => x.MigrationsAssembly("Farm.Slicer.Migrations.PostgreSQL"));
        }
        else
        {
            _ = options.UseSqlite(dbConfig.ConnectionString, x => x.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"));
        }
    }

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
        services.AddScoped<IHostUpdateMigrationTarget>(sp => new TargetImageMigrationTarget<SlicerDbContext>(
            "SlicerDbContext",
            () => sp.GetRequiredService<SlicerDbContext>(),
            sp.GetRequiredService<HostUpdateTargetImageMigrationRunner>()));
        services.AddScoped<IReadOnlyList<IHostUpdateMigrationTarget>>(sp => [.. sp.GetServices<IHostUpdateMigrationTarget>()]);
        services.AddScoped<HostUpdateMigrationCoordinator>(sp =>
            new HostUpdateMigrationCoordinator(sp.GetRequiredService<IReadOnlyList<IHostUpdateMigrationTarget>>()));
        services.AddScoped<IHostUpdateMigrationCoordinator>(sp => sp.GetRequiredService<HostUpdateMigrationCoordinator>());
        services.AddScoped<IHostUpdateMigrationReconciler>(sp => sp.GetRequiredService<HostUpdateMigrationCoordinator>());

        services.AddScoped<IHostUpdateBackupTarget>(sp =>
        {
            HostUpdateExecutionOptions options = sp.GetRequiredService<HostUpdateExecutionOptions>();
            DatabaseProviderConfiguration dbConfig = DatabaseProviderConfiguration.FromConfiguration(sp.GetRequiredService<IConfiguration>());
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

    private static async Task<int> StatusAsync(IServiceProvider provider, HostUpdateCliArguments args, TextWriter output, CancellationToken cancellationToken)
    {
        IHostUpdateAdmissionGate gate = provider.GetRequiredService<IHostUpdateAdmissionGate>();
        bool admissionClosed = await gate.IsClosedAsync(cancellationToken).ConfigureAwait(false);

        // Journal reads run only while holding the execution lock so a status probe can never
        // race a live executor's staged rewrite. A held lock is reported, never waited on.
        IHostUpdateExecutionLease? lease = TryAcquireLock(provider);
        if (lease is null)
        {
            return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.LockHeld, new StatusReport(true, admissionClosed, null, null)).ConfigureAwait(false);
        }

        using (lease)
        {
            IHostUpdateExecutionJournal journal = provider.GetRequiredService<IHostUpdateExecutionJournal>();
            try
            {
                if (args.ReleaseId is null)
                {
                    ReleaseSummary[] releases = [.. journal.ListReleaseIds().Select(id =>
                    {
                        IReadOnlyList<HostUpdateExecutionActivity> activities = journal.Read(id);
                        return new ReleaseSummary(id, activities[^1].State, activities[^1].Phase, activities[^1].RecordedAt);
                    })];
                    return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.Success, new StatusReport(false, admissionClosed, releases, null)).ConfigureAwait(false);
                }

                IReadOnlyList<HostUpdateExecutionActivity> history = journal.Read(args.ReleaseId);
                if (history.Count == 0)
                {
                    return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.NoHistory, new CliFailure(HostUpdateRecoveryRequestResolver.NoHistory, [])).ConfigureAwait(false);
                }

                HostUpdateRecoveryOutcomeRecord? outcome = await provider.GetRequiredService<IHostUpdateRecoveryOutcomeStore>()
                    .ReadAsync(args.ReleaseId, cancellationToken).ConfigureAwait(false);
                HostUpdateExecutionActivity last = history[^1];
                var detail = new ReleaseDetail(
                    args.ReleaseId,
                    last.State,
                    last.Phase,
                    last.RecordedAt,
                    last.RequestBinding?.RequestId,
                    FindUncertainPhases(history),
                    outcome is null ? null : new OutcomeSummary(outcome.Outcome, outcome.Detail, outcome.RecordedAt),
                    [.. history.Select(a => new ActivitySummary(a.State, a.Phase, a.RecordedAt))]);
                return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.Success, new StatusReport(false, admissionClosed, null, detail)).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsStateFailure(exception))
            {
                return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.StateUnreadable, new CliFailure(StateFailureCode(exception), [])).ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> RecoverAsync(
        IServiceProvider provider,
        IConfiguration configuration,
        HostUpdateExecutionOptions options,
        HostUpdateCliArguments args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> proofFailures = HostUpdateNamespaceProof.Check(
            options,
            DatabaseProviderConfiguration.FromConfiguration(configuration),
            provider.GetRequiredService<IHostUpdateExecutableResolver>());
        if (args.Confirm && proofFailures.Count > 0)
        {
            return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.ConfigurationUnproven, new CliFailure("namespace_unproven", [.. proofFailures])).ConfigureAwait(false);
        }

        IReadOnlyList<HostUpdateExecutionActivity> activities;
        InstalledHostState? installed;
        HostUpdateRecoveryOutcomeRecord? existingOutcome;
        IHostUpdateExecutionLease? lease = TryAcquireLock(provider);
        if (lease is null)
        {
            return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.LockHeld, new CliFailure("host_update_lock_held", [])).ConfigureAwait(false);
        }

        using (lease)
        {
            try
            {
                activities = provider.GetRequiredService<IHostUpdateExecutionJournal>().Read(args.ReleaseId!);
                installed = await provider.GetRequiredService<IInstalledHostStateStore>().ReadAsync(cancellationToken).ConfigureAwait(false);
                existingOutcome = await provider.GetRequiredService<IHostUpdateRecoveryOutcomeStore>()
                    .ReadAsync(args.ReleaseId!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsStateFailure(exception))
            {
                return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.StateUnreadable, new CliFailure(StateFailureCode(exception), [])).ConfigureAwait(false);
            }
        }

        HostUpdateRecoveryRequestResolution resolution = HostUpdateRecoveryRequestResolver.Resolve(activities, args.RequestId);
        if (!resolution.Succeeded)
        {
            int code = resolution.ErrorCode == HostUpdateRecoveryRequestResolver.NoHistory ? HostUpdateCliExitCodes.NoHistory : HostUpdateCliExitCodes.Refused;
            return await EmitAsync(output, args.Json, code, new CliFailure(resolution.ErrorCode!, [])).ConfigureAwait(false);
        }

        HostUpdateExecutionRequest request = resolution.Request!;
        DatabaseProviderConfiguration database = DatabaseProviderConfiguration.FromConfiguration(configuration);
        string configurationFingerprint = HostUpdateRecoveryDrift.ConfigurationFingerprint(options, database);
        string? currentPlatform = HostUpdateRecoveryDrift.CurrentPlatform();

        // A terminal RolledBack outcome makes confirm a durable no-op, so there is nothing to reapprove;
        // the manifest binding is still observed and reported so an unreadable one is never hidden.
        bool rolledBack = existingOutcome is { Outcome: HostUpdateRecoveryOutcome.RolledBack };
        string observedManifestBinding = await ReadManifestBindingAsync(provider, request.ReleaseId, cancellationToken).ConfigureAwait(false);
        HostUpdateDriftReport drift = rolledBack
            ? HostUpdateRecoveryDrift.DetectAfterRollback(request, activities, installed, configurationFingerprint, observedManifestBinding)
            : HostUpdateRecoveryDrift.Detect(
                request,
                activities,
                installed,
                existingOutcome,
                HostUpdateRecoveryDrift.ReadPolicy(configuration),
                currentPlatform,
                configurationFingerprint,
                observedManifestBinding);

        if (args.Confirm && !rolledBack)
        {
            string? driftRefusal = DriftRefusal(drift, args.ReapprovalToken);
            if (driftRefusal is not null)
            {
                // The token is deliberately withheld here: reapproval requires reading --preview.
                return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.DriftUnapproved, new CliFailure(driftRefusal, [.. drift.Items.Select(item => item.Code)])).ConfigureAwait(false);
            }

            if (args.PhysicalReconciliationToken is not null)
            {
                (int Code, CliFailure Failure)? refusal = await RecordPhysicalReconciliationAsync(provider, request, existingOutcome, args.PhysicalReconciliationToken, cancellationToken).ConfigureAwait(false);
                if (refusal is not null)
                {
                    return await EmitAsync(output, args.Json, refusal.Value.Code, refusal.Value.Failure).ConfigureAwait(false);
                }
            }
        }

        using IServiceScope scope = provider.CreateScope();
        try
        {
            if (args.Confirm)
            {
                provider.GetRequiredService<ApprovalBoundInstalledHostStateStore>().Bind(HostUpdateRecoveryDrift.InstalledStateHash(installed));
            }

            if (!args.Confirm)
            {
                HostUpdateRecoveryPlan plan = await scope.ServiceProvider.GetRequiredService<IHostUpdateRecoveryPlanner>()
                    .PlanAsync(request, activities, cancellationToken).ConfigureAwait(false);
                (HostUpdateBackupManifest Manifest, string RunDirectory)? backup = await scope.ServiceProvider.GetRequiredService<IHostUpdateBackupManifestLocator>()
                    .FindLatestAsync(request.ReleaseId, cancellationToken).ConfigureAwait(false);
                bool admissionClosed = await provider.GetRequiredService<IHostUpdateAdmissionGate>().IsClosedAsync(cancellationToken).ConfigureAwait(false);
                IEnumerable<string> writers = provider.GetRequiredService<IReadOnlyList<IFenceableWriter>>().Select(writer => writer.Name);
                HostUpdatePhysicalReconciliationPreview physical = await HostUpdatePhysicalReconciliationPreviewBuilder.BuildAsync(
                    plan,
                    request,
                    provider.GetRequiredService<IHostUpdatePhysicalReconciliationStore>(),
                    provider.GetRequiredService<IHostUpdatePrinterCommandInventoryReader>(),
                    cancellationToken).ConfigureAwait(false);
                int planCode = plan.Kind == HostUpdateRecoveryPlanKind.NeedsOperator ? HostUpdateCliExitCodes.NeedsOperator : HostUpdateCliExitCodes.Success;
                var preview = new PreviewReport(
                    args.ReleaseId!,
                    request.RequestId,
                    plan,
                    proofFailures,
                    HostUpdateRecoveryPreview.Identity(request, installed, currentPlatform),
                    HostUpdateRecoveryPreview.Downtime(plan, installed, backup?.Manifest, options),
                    HostUpdateRecoveryPreview.Backup(request.ReleaseId, backup),
                    HostUpdateRecoveryPreview.Recovery(activities, existingOutcome),
                    HostUpdateRecoveryPreview.WriterFence(plan, admissionClosed, writers),
                    HostUpdateRecoveryPreview.Drift(drift),
                    physical);
                return await EmitAsync(output, args.Json, planCode, preview).ConfigureAwait(false);
            }

            HostUpdateRecoveryResult result = await scope.ServiceProvider.GetRequiredService<IHostUpdateRecoveryCoordinator>()
                .RecoverAsync(request, activities, cancellationToken).ConfigureAwait(false);
            if (string.Equals(result.Detail, nameof(HostUpdateRecoveryApprovalStaleException), StringComparison.Ordinal))
            {
                // The installed state changed after evaluation; the coordinator refused before any
                // restore or apply and recorded NeedsOperator. Re-run --preview to re-evaluate.
                return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.DriftUnapproved, new CliFailure("drift_reapproval_stale", [HostUpdateRecoveryDrift.PriorStateChanged])).ConfigureAwait(false);
            }

            int resultCode = HostUpdateAvailabilityCodes.IsDurableUnavailable(result.Detail)
                ? HostUpdateCliExitCodes.StateUnreadable
                : result.Outcome switch
                {
                    HostUpdateRecoveryOutcome.RolledBack => HostUpdateCliExitCodes.Success,
                    HostUpdateRecoveryOutcome.FenceReleasePending when HostUpdatePhysicalReconciliationCodes.IsBlockedDetail(result.Detail)
                        => HostUpdateCliExitCodes.PhysicalReconciliationPending,
                    HostUpdateRecoveryOutcome.FenceReleasePending => HostUpdateCliExitCodes.FenceReleasePending,
                    _ => HostUpdateCliExitCodes.NeedsOperator,
                };
            return await EmitAsync(output, args.Json, resultCode, new RecoveryReport(args.ReleaseId!, resolution.Request!.RequestId, result.Outcome, result.Detail)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.LockHeld, new CliFailure("host_update_lock_held", [])).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The coordinator has already durably recorded NeedsOperator recovery_canceled.
            return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.NeedsOperator, new CliFailure("recovery_canceled", [])).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStateFailure(exception))
        {
            return await EmitAsync(output, args.Json, HostUpdateCliExitCodes.StateUnreadable, new CliFailure(StateFailureCode(exception), [])).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records the operator's physical reconciliation only when the rollback is already durable
    /// and the token matches the inventory as it is right now. Returns a refusal, or null to
    /// continue into the coordinator's fence release. Reads the database; never writes it.
    /// </summary>
    private static async Task<(int Code, CliFailure Failure)?> RecordPhysicalReconciliationAsync(
        IServiceProvider provider,
        HostUpdateExecutionRequest request,
        HostUpdateRecoveryOutcomeRecord? existingOutcome,
        string token,
        CancellationToken cancellationToken)
    {
        if (existingOutcome is not { Outcome: HostUpdateRecoveryOutcome.FenceReleasePending })
        {
            // Nothing may be recorded before the rollback itself is durable.
            return (HostUpdateCliExitCodes.Refused, new CliFailure("physical_reconciliation_not_ready", []));
        }

        HostUpdatePrinterCommandInventory inventory;
        try
        {
            inventory = await provider.GetRequiredService<IHostUpdatePrinterCommandInventoryReader>().ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (HostUpdateCliExitCodes.StateUnreadable, new CliFailure("physical_inventory_unavailable:" + exception.GetType().Name, []));
        }

        string expected = inventory.Token(request.ReleaseId, request.RequestId);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(token),
            System.Text.Encoding.UTF8.GetBytes(expected)))
        {
            // The inventory changed since --preview (or the token is wrong); nothing is recorded.
            return (HostUpdateCliExitCodes.PhysicalReconciliationPending, new CliFailure("physical_reconciliation_mismatch", []));
        }

        try
        {
            await provider.GetRequiredService<IHostUpdatePhysicalReconciliationStore>().WriteAsync(
                HostUpdatePhysicalReconciliationRecord.Create(request.ReleaseId, request.RequestId, inventory, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStateFailure(exception))
        {
            return (HostUpdateCliExitCodes.StateUnreadable, new CliFailure("physical_reconciliation_unwritable:" + exception.GetType().Name, []));
        }

        return null;
    }

    private static string? DriftRefusal(HostUpdateDriftReport drift, string? token)
    {
        if (!drift.HasDrift)
        {
            return token is null ? null : "drift_reapproval_unexpected";
        }

        if (token is null)
        {
            return "drift_reapproval_required";
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(token),
            System.Text.Encoding.UTF8.GetBytes(drift.ReapprovalToken!))
            ? null
            : "drift_reapproval_mismatch";
    }

    /// <summary>
    /// Takes the execution lock for the CLI's own reads without rewriting the lock file when it
    /// already exists; only a host that has never taken the lock gets the empty sentinel created.
    /// </summary>
    internal static IHostUpdateExecutionLease? TryAcquireLock(IServiceProvider provider)
    {
        try
        {
            string lockPath = Path.Join(provider.GetRequiredService<HostUpdateExecutionOptions>().StateDirectory, FileHostUpdateExecutionLock.FileName);
            return FileHostUpdateExecutionLock.TryAcquireExisting(lockPath)
                ?? provider.GetRequiredService<IHostUpdateExecutionLock>().Acquire(TimeSpan.Zero, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the database manifest binding read-only (issue #3050). Any failure becomes an
    /// <c>unreadable:&lt;type&gt;</c> observation, which drift detection always treats as drift; the
    /// message is dropped because provider errors can carry connection details.
    /// </summary>
    private static async Task<string> ReadManifestBindingAsync(IServiceProvider provider, string releaseId, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetRequiredService<IHostUpdateManifestBindingReader>().ReadAsync(releaseId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HostUpdateRecoveryDrift.ManifestBindingUnreadablePrefix + exception.GetType().Name;
        }
    }

    internal static bool IsStateFailure(Exception exception) =>
        exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException
            or HostUpdateSubsystemUnavailableException or HostUpdateInstalledStateCorruptException
            or NotSupportedException or System.Security.SecurityException;

    internal static string StateFailureCode(Exception exception) => exception switch
    {
        InvalidDataException data when !string.IsNullOrWhiteSpace(data.Message) && JournalCode().IsMatch(data.Message) => data.Message,
        HostUpdateSubsystemUnavailableException => "state_unavailable",
        HostUpdateInstalledStateCorruptException corrupt => "installed_state_corrupt:" + corrupt.Code,
        UnauthorizedAccessException => "state_access_denied",
        _ => "state_unreadable:" + exception.GetType().Name,
    };

    /// <summary>Phases whose <c>:before</c> marker has no matching <c>:after</c>: side effects that may or may not have happened.</summary>
    internal static string[] FindUncertainPhases(IReadOnlyList<HostUpdateExecutionActivity> history)
    {
        var completed = history.Select(a => a.Phase)
            .Where(p => p.EndsWith(":after", StringComparison.Ordinal))
            .Select(p => p[..^":after".Length])
            .ToHashSet(StringComparer.Ordinal);
        return [.. history.Select(a => a.Phase)
            .Where(p => p.EndsWith(":before", StringComparison.Ordinal))
            .Select(p => p[..^":before".Length])
            .Where(p => !completed.Contains(p))
            .Distinct(StringComparer.Ordinal)];
    }

    internal static async Task<int> EmitAsync(TextWriter output, bool json, int exitCode, object report)
    {
        if (json)
        {
            var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["exitCode"] = exitCode,
                ["result"] = report,
            };
            await output.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonOptions)).ConfigureAwait(false);
            return exitCode;
        }

        await output.WriteLineAsync($"exitCode: {exitCode}").ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(report, JsonOptions));
        await WriteTextAsync(output, document.RootElement, string.Empty).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task WriteTextAsync(TextWriter output, JsonElement element, string prefix)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string key = prefix + property.Name;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    await WriteTextAsync(output, property.Value, key + ".").ConfigureAwait(false);
                    break;
                case JsonValueKind.Array:
                    int index = 0;
                    foreach (JsonElement item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            await WriteTextAsync(output, item, $"{key}[{index}].").ConfigureAwait(false);
                        }
                        else
                        {
                            await output.WriteLineAsync($"{key}[{index}]: {item}").ConfigureAwait(false);
                        }

                        index++;
                    }

                    if (index == 0)
                    {
                        await output.WriteLineAsync($"{key}: (none)").ConfigureAwait(false);
                    }

                    break;
                default:
                    await output.WriteLineAsync($"{key}: {property.Value}").ConfigureAwait(false);
                    break;
            }
        }
    }

    // Only fixed snake_case journal and replay-store codes pass through; any other message
    // (which could embed paths or detail) falls back to the exception type.
    [GeneratedRegex(@"\A(journal|host_update_replay)_[a-z_]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex JournalCode();

    internal sealed record CliFailure(string Code, string[] Details);

    private sealed record ReleaseSummary(string ReleaseId, HostUpdateExecutionState State, string Phase, DateTimeOffset RecordedAt);

    private sealed record ActivitySummary(HostUpdateExecutionState State, string Phase, DateTimeOffset RecordedAt);

    private sealed record OutcomeSummary(HostUpdateRecoveryOutcome Outcome, string Detail, DateTimeOffset RecordedAt);

    private sealed record ReleaseDetail(
        string ReleaseId,
        HostUpdateExecutionState State,
        string Phase,
        DateTimeOffset RecordedAt,
        string? RequestId,
        string[] UncertainPhases,
        OutcomeSummary? RecoveryOutcome,
        ActivitySummary[] Activities);

    private sealed record StatusReport(bool LockHeld, bool AdmissionClosed, ReleaseSummary[]? Releases, ReleaseDetail? Release);

    private sealed record PreviewReport(
        string ReleaseId,
        string RequestId,
        HostUpdateRecoveryPlan Plan,
        IReadOnlyList<string> NamespaceProofFailures,
        HostUpdateRecoveryIdentity Identity,
        HostUpdateDowntimePreview Downtime,
        HostUpdateBackupEvidence BackupEvidence,
        HostUpdateRecoveryEvidence RecoveryEvidence,
        HostUpdateWriterFencePreview WriterFence,
        HostUpdateDriftPreview Drift,
        HostUpdatePhysicalReconciliationPreview PhysicalReconciliation);

    private sealed record RecoveryReport(string ReleaseId, string RequestId, HostUpdateRecoveryOutcome Outcome, string Detail);
}
