using Farm.Infrastructure.Data;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Services;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Configures feature-specific services (OctoPrint, File Management, Print Jobs, Maintenance, SPA).
/// </summary>
public static class FeatureServicesStartup
{
    /// <summary>
    /// Adds PrintFarmer feature services (repositories, business logic, SPA).
    /// </summary>
    public static IServiceCollection AddPrintFarmerFeatureServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        // OctoPrint compatibility settings. Bound here (rather than in Farm.Modules.Devices)
        // because OctoPrintCompatController, which remains in Farm.Web.Api, injects
        // IOptions<OctoPrintSettings> directly. (UserApiKeysController, also in Farm.Web.Api,
        // reads the same settings via ISettingsService.Get<OctoPrintSettings>() instead, so it
        // does not depend on this binding.) IOctoPrintAuthService itself is registered by
        // Farm.Modules.Devices.DevicesApiModule (issue #2043, Phase 15).
        services.Configure<Farm.Infrastructure.Settings.OctoPrintSettings>(configuration.GetSection("OctoPrint"));

        // ApiKey repository
        services.AddScoped<Farm.Infrastructure.Repositories.Api.IApiKeyRepository, Farm.Infrastructure.Repositories.Api.EfApiKeyRepository>();

        // Desktop API-key exchange (issue #838)
        services.AddScoped<Farm.Infrastructure.Services.Authentication.IApiKeyExchangeService, Farm.Infrastructure.Services.Authentication.ApiKeyExchangeService>();

        // Print job approval service
        services.AddScoped<Farm.Infrastructure.Services.PrintJobs.IPrintApprovalService, Farm.Infrastructure.Services.PrintJobs.PrintApprovalService>();

        // Print Projects Service (multi-file job tracking)
        services.AddScoped<Farm.Infrastructure.Services.Projects.IPrintProjectService, Farm.Infrastructure.Services.Projects.PrintProjectService>();
        services.AddScoped<Farm.Infrastructure.Services.Projects.IPrintProjectTemplateService, Farm.Infrastructure.Services.Projects.PrintProjectTemplateService>();

        // File Management Services
        services.AddScoped<Farm.Infrastructure.Services.FileManagement.IFileManagementService, Farm.Infrastructure.Services.FileManagement.FileManagementService>();
        services.AddScoped<Farm.Infrastructure.Services.FileManagement.IStoredFileOperationsService, Farm.Infrastructure.Services.FileManagement.StoredFileOperationsService>();

        // Print Job Management repository (the IPrintJobManagementService implementation itself
        // is registered by Farm.Modules.PrintQueue's PrintQueueApiModule -- issue #2040, epic #2019)
        services.AddScoped<Farm.Infrastructure.Repositories.Queue.IPrintJobManagementRepository, Farm.Infrastructure.Repositories.Queue.EfPrintJobManagementRepository>();

        // Print Job Completion Sync Service (auto-marks jobs as completed when printer finishes)
        services.AddScoped<Farm.Infrastructure.Services.Printers.IPrintJobCompletionService, Farm.Infrastructure.Services.Printers.PrintJobCompletionService>();

        // Auto-tag service for completed print jobs
        services.AddScoped<Farm.Infrastructure.Services.AutoTagging.IAutoTagService, Farm.Infrastructure.Services.AutoTagging.AutoTagService>();

        // Print Cost Calculator (calculates job costs from Spoolman spool price and filament usage)
        services.AddScoped<Farm.Infrastructure.Services.Printers.IPrintCostCalculator, Farm.Infrastructure.Services.Printers.PrintCostCalculator>();

        // Notification Module (job event notifications broadcast to all users)
        services.AddScoped<Farm.Infrastructure.Repositories.Notifications.INotificationRepository, Farm.Infrastructure.Repositories.Notifications.EfNotificationRepository>();
        services.AddSingleton(sp =>
        {
            var opts = new Farm.Infrastructure.Services.Notifications.VapidOptions();
            configuration.GetSection("WebPush").Bind(opts);

            // Backward compatibility: fall back to the legacy flat environment
            // variables used before the "WebPush" configuration section existed.
            opts.VapidPublicKey = string.IsNullOrWhiteSpace(opts.VapidPublicKey)
                ? Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY")
                : opts.VapidPublicKey;
            opts.VapidPrivateKey = string.IsNullOrWhiteSpace(opts.VapidPrivateKey)
                ? Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY")
                : opts.VapidPrivateKey;
            opts.VapidSubject = string.IsNullOrWhiteSpace(opts.VapidSubject)
                ? Environment.GetEnvironmentVariable("VAPID_SUBJECT")
                : opts.VapidSubject;

            return opts;
        });
        services.AddSingleton<Farm.Infrastructure.Services.Notifications.IWebPushNotificationSender, Farm.Infrastructure.Services.Notifications.WebPushNotificationSender>();
        services.AddSingleton<Farm.Infrastructure.Services.Notifications.ITelegramNotificationSender, Farm.Infrastructure.Services.Notifications.TelegramNotificationSender>();
        services.AddScoped<Farm.Infrastructure.Services.Notifications.INotificationChannel, Farm.Infrastructure.Services.Notifications.TelegramNotificationChannel>();
        services.AddScoped<Farm.Infrastructure.Services.Notifications.INotificationService, Farm.Infrastructure.Services.Notifications.NotificationService>();
        services.AddHttpClient("TelegramDelivery", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("User-Agent", "PrintFarmer-Telegram/1.0");
        });

        // Webhooks (event delivery via HTTP POST to external consumers)
        services.AddSingleton<Farm.Infrastructure.Services.Webhooks.WebhookService>();
        services.AddSingleton<Farm.Infrastructure.Services.Webhooks.IWebhookService>(sp =>
            sp.GetRequiredService<Farm.Infrastructure.Services.Webhooks.WebhookService>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<Farm.Infrastructure.Services.Webhooks.WebhookService>());
        services.AddHttpClient("WebhookDelivery", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("User-Agent", "PrintFarmer-Webhook/1.0");
        });

        // Job Scheduling Service (Phase 4.1)
        services.AddScoped<Farm.Infrastructure.Services.JobSchedulingService>();

        // Auto-dispatch ready gate (operator confirmation before dispatching the next queued job)
        services.AddScoped<Farm.Infrastructure.Services.AutoDispatch.IAutoDispatchService, Farm.Infrastructure.Services.AutoDispatch.AutoDispatchService>();

        // Prediction Service (Phase 4.2)
        services.AddScoped<Farm.Infrastructure.Repositories.Queue.IPrintJobStatisticsRepository, Farm.Infrastructure.Repositories.Queue.EfPrintJobStatisticsRepository>();
        services.AddScoped<Farm.Infrastructure.Services.PredictionService>();

        // Retry Service (Phase 4.4)
        services.AddScoped<Farm.Infrastructure.Services.IRetryService, Farm.Infrastructure.Services.RetryService>();

        // Maintenance Module - Repositories
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IPrinterStatisticsRepository, Farm.Infrastructure.Repositories.Maintenance.EfPrinterStatisticsRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IToolheadStatisticsRepository, Farm.Infrastructure.Repositories.Maintenance.EfToolheadStatisticsRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenanceAlertRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenanceAlertRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenanceLogRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenanceLogRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenancePlanRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenancePlanRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenanceTaskRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenanceTaskRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenanceComponentRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenanceComponentRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IPrinterMaintenanceScheduleRepository, Farm.Infrastructure.Repositories.Maintenance.EfPrinterMaintenanceScheduleRepository>();

        // Maintenance Module - Services
        // IMaintenanceAlertService and IMaintenanceResolutionNotifier are registered by
        // MaintenanceApiModule.ConfigureServices() -- their implementations moved to
        // Farm.Modules.Maintenance (issue #2037).
        services.AddScoped<Farm.Infrastructure.Services.Maintenance.IMaintenanceImportExportService, Farm.Infrastructure.Services.Maintenance.MaintenanceImportExportService>();

        // Atomic resolve-with-log to close the resolve TOCTOU (issue #711, round-7 Finding 5).
        services.AddScoped<Farm.Infrastructure.Services.Maintenance.IMaintenanceAlertResolutionService, Farm.Infrastructure.Services.Maintenance.MaintenanceAlertResolutionService>();

        // Filament fallback groups (issue #711, F6)
        services.AddScoped<Farm.Infrastructure.Services.Printers.IFilamentFallbackGroupService,
            Farm.Infrastructure.Services.Printers.FilamentFallbackGroupService>();

        // Persistent Idempotency-Key store and cleanup (issue #715). Store is
        // registered scoped because it uses IDbContextFactory internally and is
        // resolved per-request from the filter (and per-sweep from the cleanup
        // hosted service via its own scope). See docs/OFFLINE_WRITE_REPLAY.md.
        services.AddSingleton(Farm.Infrastructure.Services.Idempotency.IdempotencyOptions.Default);
        services.AddScoped<Farm.Infrastructure.Services.Idempotency.IIdempotencyStore,
            Farm.Infrastructure.Services.Idempotency.IdempotencyStore>();
        services.AddScoped<Farm.Web.Api.Infrastructure.Idempotency.IdempotencyFilter>();
        services.AddHostedService<Farm.Infrastructure.Services.Idempotency.IdempotencyRecordCleanupService>();

        // Printed-part inventory (see #714). Distinct from MaintenanceComponents
        // (replacement parts) — this module tracks parts produced by prints.
        services.AddScoped<Farm.Infrastructure.Repositories.PartsInventory.IPartInventoryRepository,
            Farm.Infrastructure.Repositories.PartsInventory.EfPartInventoryRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.PartsInventory.IBinRepository,
            Farm.Infrastructure.Repositories.PartsInventory.EfBinRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.PartsInventory.IPartInventoryAdjustmentRepository,
            Farm.Infrastructure.Repositories.PartsInventory.EfPartInventoryAdjustmentRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.PartsInventory.IPartOutputMappingRepository,
            Farm.Infrastructure.Repositories.PartsInventory.EfPartOutputMappingRepository>();
        services.AddScoped<Farm.Infrastructure.Services.PartsInventory.IPartInventoryService,
            Farm.Infrastructure.Services.PartsInventory.PartInventoryService>();
        services.AddScoped<Farm.Infrastructure.Services.PartsInventory.IPartHarvestService,
            Farm.Infrastructure.Services.PartsInventory.PartHarvestService>();
        services.AddScoped<Farm.Infrastructure.Services.PartsInventory.IPartOutputSnapshotService,
            Farm.Infrastructure.Services.PartsInventory.PartOutputSnapshotService>();
        services.AddScoped<Farm.Infrastructure.Services.PartsInventory.IReorderEvaluationService,
            Farm.Infrastructure.Services.PartsInventory.ReorderEvaluationService>();

        // Attention Feed (issue #707) — unified operator feed composed from failure,
        // maintenance, offline, harvest, and runout sources.
        services.AddScoped<Farm.Infrastructure.Repositories.Attention.IAttentionSnoozeRepository,
            Farm.Infrastructure.Repositories.Attention.EfAttentionSnoozeRepository>();
        services.AddScoped<Farm.Infrastructure.Services.Attention.IAttentionSource,
            Farm.Infrastructure.Services.Attention.Sources.FailureAttentionSource>();
        services.AddScoped<Farm.Infrastructure.Services.Attention.IAttentionSource,
            Farm.Infrastructure.Services.Attention.Sources.MaintenanceAttentionSource>();
        services.AddScoped<Farm.Infrastructure.Services.Attention.IAttentionSource,
            Farm.Infrastructure.Services.Attention.Sources.OfflineAttentionSource>();
        services.AddScoped<Farm.Infrastructure.Services.Attention.IAttentionSource,
            Farm.Infrastructure.Services.Attention.Sources.HarvestAttentionSource>();
        services.AddScoped<Farm.Infrastructure.Services.Attention.IAttentionService,
            Farm.Infrastructure.Services.Attention.AttentionService>();
        services.AddSingleton<Farm.Infrastructure.Services.Attention.IAttentionBroadcaster,
            Farm.Infrastructure.Services.Attention.AttentionBroadcaster>();

        // Native push (issue #708) — device-token registration, per-user category
        // preferences, and the dispatcher hooked from AttentionBroadcaster after the
        // SignalR broadcast. See docs/OPERATOR_NATIVE_PUSH.md.
        //
        // Hicks #6: fail-fast startup validation for credentials. The
        // validator enforces mode-specific requirements (Relay: absolute
        // HTTPS endpoint + api key; Direct: TeamId/KeyId/BundleId + a
        // readable .p8 file OR inline PEM). Disabled mode requires nothing.
        // Diagnostics NEVER echo the secret path, api key, PEM contents, or
        // full URI — only high-level shape errors surface so ops logs stay
        // safe.
        services.AddOptions<Farm.Infrastructure.Services.Notifications.NativePush.NativePushSettings>()
            .Bind(configuration.GetSection(Farm.Infrastructure.Services.Notifications.NativePush.NativePushSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Farm.Infrastructure.Services.Notifications.NativePush.NativePushSettings>,
            Farm.Infrastructure.Services.Notifications.NativePush.NativePushSettingsValidator>();

        services.AddScoped<Farm.Infrastructure.Repositories.Notifications.IDeviceTokenRepository,
            Farm.Infrastructure.Repositories.Notifications.EfDeviceTokenRepository>();
        services.AddSingleton<Farm.Infrastructure.Services.ServerIdentity.IServerIdentityService,
            Farm.Infrastructure.Services.ServerIdentity.ServerIdentityService>();
        services.AddSingleton<Farm.Infrastructure.Services.Notifications.NativePush.NativePushMetrics>();
        Farm.Infrastructure.Services.Notifications.NativePush.NativePushMode nativePushMode =
            configuration.GetSection(Farm.Infrastructure.Services.Notifications.NativePush.NativePushSettings.SectionName)
                .GetValue<Farm.Infrastructure.Services.Notifications.NativePush.NativePushMode>("Mode");
        switch (nativePushMode)
        {
            case Farm.Infrastructure.Services.Notifications.NativePush.NativePushMode.Relay:
                services.AddSingleton<Farm.Infrastructure.Services.Notifications.NativePush.INativePushSender,
                    Farm.Infrastructure.Services.Notifications.NativePush.RelayNativePushSender>();
                break;
            case Farm.Infrastructure.Services.Notifications.NativePush.NativePushMode.Direct:
                services.AddSingleton<Farm.Infrastructure.Services.Notifications.NativePush.INativePushSender,
                    Farm.Infrastructure.Services.Notifications.NativePush.DirectApnsNativePushSender>();
                break;
            default:
                services.AddSingleton<Farm.Infrastructure.Services.Notifications.NativePush.INativePushSender,
                    Farm.Infrastructure.Services.Notifications.NativePush.DisabledNativePushSender>();
                break;
        }

        services.AddSingleton<Farm.Infrastructure.Services.Notifications.NativePush.INativePushDispatcher,
            Farm.Infrastructure.Services.Notifications.NativePush.NativePushDispatcher>();
        services.AddHttpClient(
            Farm.Infrastructure.Services.Notifications.NativePush.RelayNativePushSender.HttpClientName,
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("User-Agent", "PrintFarmer-NativePush/1.0");
            })

            // Silence the default IHttpClientFactory request-logger for this
            // named client. It writes the outbound URI at Information — a raw
            // device token would end up in stdout logs (Bishop v3 B1). We still
            // get the OTel span with a redacted url.full via TelemetryStartup's
            // AddHttpClientInstrumentation enrich callbacks; that's the sole
            // audit trail for these requests.
            .RemoveAllLoggers();
        services.AddHttpClient(
            Farm.Infrastructure.Services.Notifications.NativePush.DirectApnsNativePushSender.HttpClientName,
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("User-Agent", "PrintFarmer-NativePush/1.0");

                // APNs REQUIRES HTTP/2. Default at the client level so a stray
                // request that forgets to set Version still negotiates HTTP/2.
                client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
                client.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrHigher;
            })

            // Same reasoning as the Relay client above: the token is embedded
            // in the APNs path `/3/device/<token>` and the default logger
            // writes it verbatim. Redaction on the OTel span alone is not
            // enough — the ILogger sink is a separate output.
            .RemoveAllLoggers();

        // NOTE: URL redaction of `/3/device/<token>` for OpenTelemetry spans is
        // handled in TelemetryStartup via `AddHttpClientInstrumentation(o =>
        // o.EnrichWithHttpRequestMessage = ...)`. A DelegatingHandler cannot
        // scrub the tag because the runtime creates the HTTP client Activity
        // in the primary handler, below every DelegatingHandler.

        // SPA services (only for monolithic deployments)
        string? deploymentMode =
            configuration.GetValue<string>("DEPLOYMENT_MODE") ??
            configuration.GetValue<string>("Deployment:Mode");
        bool isMonolithicDeployment =
            !string.Equals(deploymentMode, "microservices", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(deploymentMode, "split", StringComparison.OrdinalIgnoreCase);
        if (isMonolithicDeployment)
        {
            services.AddSpaStaticFiles(configuration =>
            {
                // Use relative path from content root to unified shared web root so SPA static files (prod) resolve.
                string shared = environment.WebRootPath;
                try
                {
                    if (string.IsNullOrWhiteSpace(shared) || !Directory.Exists(shared))
                    {
                        // Fallback: look for a local wwwroot under content root (publish scenario)
                        string fallback = Path.Join(environment.ContentRootPath, "wwwroot");
                        if (Directory.Exists(fallback))
                        {
                            shared = fallback;
                        }
                        else
                        {
                            // No static root available; skip configuring SPA static files.
                            return; // leaves configuration.RootPath unset -> no static file serving attempt
                        }
                    }

                    string relative = Path.GetRelativePath(environment.ContentRootPath, shared);
                    configuration.RootPath = relative; // e.g. ../../wwwroot or wwwroot
                }
                catch
                {
                    // Safety: if relative path resolution fails (null args, etc.), skip static file mapping to avoid container crash.
                }
            });
        }

        // Dynamic SPA dev proxy support (development only)
        if (isMonolithicDeployment && environment.IsDevelopment())
        {
            // Default dev server URL (configurable via SPA_DEV_URL); using widely adopted Vite default.
            string? devUrl = configuration.GetValue<string>("SPA_DEV_URL");
            if (string.IsNullOrWhiteSpace(devUrl))
            {
                devUrl = string.Concat("http://localhost:", "3000"); // constructed to avoid hardcoded analyzer warning
            }

            _ = services.AddSingleton(_ => new SpaProxyActivationState(devUrl));
            _ = services.AddHttpClient("SpaProxy");

            // SpaDevServerWatcher is implemented as a BackgroundService; register it as a hosted service
            _ = services.AddHostedService<SpaDevServerWatcher>();
        }

        // Monitoring services (Grafana/Jaeger auth proxy, Prometheus metrics)
        services.AddSingleton<Farm.Infrastructure.Services.Monitoring.IMonitoringSessionService, Farm.Infrastructure.Services.Monitoring.MonitoringSessionService>();
        services.AddScoped<Farm.Infrastructure.Services.Monitoring.IMonitoringHealthService, Farm.Infrastructure.Services.Monitoring.MonitoringHealthService>();
        services.AddScoped<Farm.Infrastructure.Services.SystemStatus.ISystemInfoService, Farm.Infrastructure.Services.SystemStatus.SystemInfoService>();

        // Admin Control Center overview aggregation (issue #933) moved to
        // Farm.Modules.Administration's IApiModule registration (issue #2042).

        // Permission catalog and role permission grant services (issues #1446/#1449) moved to
        // Farm.Modules.Identity's IApiModule registration (issue #2041).
        services.AddHttpClient("MonitoringHealth", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        return services;
    }
}
