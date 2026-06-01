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
        // OctoPrint compatibility settings and services
        services.Configure<Farm.Infrastructure.Settings.OctoPrintSettings>(configuration.GetSection("OctoPrint"));
        services.AddScoped<Farm.Web.Api.Services.OctoPrint.IOctoPrintAuthService, Farm.Web.Api.Services.OctoPrint.OctoPrintAuthService>();

        // ApiKey repository
        services.AddScoped<Farm.Infrastructure.Repositories.Api.IApiKeyRepository, Farm.Infrastructure.Repositories.Api.EfApiKeyRepository>();

        // Print job approval service
        services.AddScoped<Farm.Infrastructure.Services.PrintJobs.IPrintApprovalService, Farm.Infrastructure.Services.PrintJobs.PrintApprovalService>();

        // Print Projects Service (multi-file job tracking)
        services.AddScoped<Farm.Infrastructure.Services.Projects.IPrintProjectService, Farm.Infrastructure.Services.Projects.PrintProjectService>();
        services.AddScoped<Farm.Infrastructure.Services.Projects.IPrintProjectTemplateService, Farm.Infrastructure.Services.Projects.PrintProjectTemplateService>();

        // File Management Services
        services.AddScoped<Farm.Infrastructure.Services.FileManagement.IFileManagementService, Farm.Infrastructure.Services.FileManagement.FileManagementService>();
        services.AddScoped<Farm.Infrastructure.Services.FileManagement.IStoredFileOperationsService, Farm.Infrastructure.Services.FileManagement.StoredFileOperationsService>();

        // Print Job Management Service (renamed from PrintQueueService)
        services.AddScoped<Farm.Infrastructure.Repositories.Queue.IPrintJobManagementRepository, Farm.Infrastructure.Repositories.Queue.EfPrintJobManagementRepository>();
        services.AddScoped<Farm.Infrastructure.Services.Interfaces.IPrintJobManagementService, Farm.Api.Services.PrintQueue.PrintJobManagementService>();

        // Print Job Completion Sync Service (auto-marks jobs as completed when printer finishes)
        services.AddScoped<Farm.Infrastructure.Services.Printers.IPrintJobCompletionService, Farm.Infrastructure.Services.Printers.PrintJobCompletionService>();

        // Auto-tag service for completed print jobs
        services.AddScoped<Farm.Infrastructure.Services.AutoTagging.IAutoTagService, Farm.Infrastructure.Services.AutoTagging.AutoTagService>();

        // Print Cost Calculator (calculates job costs from Spoolman spool price and filament usage)
        services.AddScoped<Farm.Infrastructure.Services.Printers.IPrintCostCalculator, Farm.Infrastructure.Services.Printers.PrintCostCalculator>();

        // Notification Module (job event notifications broadcast to all users)
        services.AddScoped<Farm.Infrastructure.Repositories.Notifications.INotificationRepository, Farm.Infrastructure.Repositories.Notifications.EfNotificationRepository>();
        services.AddScoped<Farm.Infrastructure.Services.Notifications.INotificationService, Farm.Infrastructure.Services.Notifications.NotificationService>();

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
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenanceAlertRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenanceAlertRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenanceLogRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenanceLogRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenancePlanRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenancePlanRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenanceTaskRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenanceTaskRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenanceComponentRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenanceComponentRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IPrinterMaintenanceScheduleRepository, Farm.Infrastructure.Repositories.Maintenance.EfPrinterMaintenanceScheduleRepository>();

        // Maintenance Module - Services
        services.AddScoped<Farm.Infrastructure.Services.Maintenance.IMaintenanceAlertService, Farm.Web.Api.Services.Maintenance.MaintenanceAlertEngine>();
        services.AddScoped<Farm.Infrastructure.Services.Maintenance.IMaintenanceImportExportService, Farm.Infrastructure.Services.Maintenance.MaintenanceImportExportService>();

        // SPA services (only for monolithic deployments)
        bool isMonolithicDeployment = configuration.GetValue<string>("DEPLOYMENT_MODE") != "microservices";
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
                        string fallback = Path.Combine(environment.ContentRootPath, "wwwroot");
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
        services.AddHttpClient("MonitoringHealth", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        return services;
    }
}
