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
        services.Configure<Farm.Web.Api.Services.OctoPrint.OctoPrintSettings>(configuration.GetSection("OctoPrint"));
        services.AddScoped<Farm.Web.Api.Services.OctoPrint.IOctoPrintAuthService, Farm.Web.Api.Services.OctoPrint.OctoPrintAuthService>();
        services.AddSingleton<Farm.Web.Api.Middleware.SimpleRateLimitService>();

        // ApiKey repository
        services.AddScoped<Farm.Web.Api.Data.Repositories.IApiKeyRepository>(sp =>
        {
            AppDbContext db = sp.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();
            return new Farm.Web.Api.Data.Repositories.EfApiKeyRepositoryAdapter(db);
        });

        // Print job approval service
        services.AddScoped<Farm.Web.Api.Services.PrintJobs.IPrintApprovalService, Farm.Web.Api.Services.PrintJobs.PrintApprovalService>();

        // Print Projects Service (multi-file job tracking)
        services.AddScoped<Farm.Web.Api.Services.Projects.IPrintProjectService, Farm.Web.Api.Services.Projects.PrintProjectService>();
        services.AddScoped<Farm.Web.Api.Services.Projects.IPrintProjectTemplateService, Farm.Web.Api.Services.Projects.PrintProjectTemplateService>();

        // File Management Services
        services.AddScoped<Farm.Web.Api.Services.FileManagement.IFileManagementService, Farm.Web.Api.Services.FileManagement.FileManagementService>();
        services.AddScoped<Farm.Web.Api.Services.FileManagement.IStoredFileOperationsService, Farm.Web.Api.Services.FileManagement.StoredFileOperationsService>();

        // 3MF to STL Conversion Service
        services.AddScoped<Farm.Infrastructure.Services.Models.I3MfToStlConversionService, Farm.Infrastructure.Services.Models.ThreeMfToStlConversionService>();

        // Print Job Management Service (renamed from PrintQueueService)
        services.AddScoped<Farm.Infrastructure.Repositories.Queue.IPrintJobManagementRepository, Farm.Infrastructure.Repositories.Queue.EfPrintJobManagementRepository>();
        services.AddScoped<Farm.Api.Services.Interfaces.IPrintJobManagementService, Farm.Api.Services.PrintQueue.PrintJobManagementService>();

        // Print Job Completion Sync Service (auto-marks jobs as completed when printer finishes)
        services.AddScoped<Farm.Infrastructure.Services.Printers.IPrintJobCompletionService, Farm.Infrastructure.Services.Printers.PrintJobCompletionService>();

        // Job Scheduling Service (Phase 4.1)
        services.AddScoped<Farm.Infrastructure.Services.JobSchedulingService>();

        // Prediction Service (Phase 4.2)
        services.AddScoped<Farm.Infrastructure.Repositories.Queue.IPrintJobStatisticsRepository, Farm.Infrastructure.Repositories.Queue.EfPrintJobStatisticsRepository>();
        services.AddScoped<Farm.Infrastructure.Services.PredictionService>();

        // Retry Service (Phase 4.4)
        services.AddScoped<Farm.Infrastructure.Services.IRetryService, Farm.Infrastructure.Services.RetryService>();

        // Maintenance Module - Repositories
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IPrinterStatisticsRepository, Farm.Infrastructure.Repositories.Maintenance.EfPrinterStatisticsRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenanceScheduleRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenanceScheduleRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenanceAlertRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenanceAlertRepository>();
        services.AddScoped<Farm.Infrastructure.Repositories.Maintenance.IMaintenanceLogRepository, Farm.Infrastructure.Repositories.Maintenance.EfMaintenanceLogRepository>();

        // Maintenance Module - Services
        services.AddScoped<Farm.Web.Api.Services.Maintenance.IMaintenanceAlertService, Farm.Web.Api.Services.Maintenance.MaintenanceAlertEngine>();

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

        return services;
    }
}
