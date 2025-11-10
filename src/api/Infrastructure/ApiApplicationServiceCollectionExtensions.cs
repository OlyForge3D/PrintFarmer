using AutoMapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.Api.Infrastructure;

public static class ApiApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApiApplicationServices(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        _ = services.AddAutoMapper(typeof(Program).Assembly);

        RegisterImportingServices(services);
#pragma warning disable CA1062 // Validate arguments of public methods
        RegisterArtifactServices(services, configuration, environment);
#pragma warning restore CA1062 // Validate arguments of public methods
        RegisterAuthenticationAndSecurityServices(services);
        RegisterCatalogAndSlicingServices(services);
        RegisterModelAndGcodeServices(services);
        RegisterPrinterAndSetupServices(services);
        RegisterSchemaAndSignalRServices(services);

        return services;
    }

    private static void RegisterImportingServices(IServiceCollection services)
    {
        _ = services.AddScoped<Farm.Importing.Services.Import.IImportParserService, Farm.Importing.Services.Import.ImportParserService>();
        _ = services.AddScoped<Farm.Importing.Services.Import.IImportProcessorService, Farm.Importing.Services.Import.ImportProcessorService>();
        _ = services.AddScoped<Farm.Importing.Services.Adapters.IPrinterCapabilityDiscoveryAdapter, Farm.Web.Api.Services.Adapters.PrinterCapabilityDiscoveryAdapter>();
        _ = services.AddScoped<Farm.Importing.Services.Adapters.IDefaultCatalogAdapter, Farm.Web.Api.Services.Adapters.DefaultCatalogAdapter>();
    }

    private static void RegisterArtifactServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        _ = services.Configure<Farm.Infrastructure.Settings.ArtifactStorageSettings>(configuration.GetSection(Farm.Infrastructure.Settings.ArtifactStorageSettings.SectionName));
        if (!environment.IsEnvironment("Testing"))
        {
            _ = services.AddSingleton<Farm.Web.Api.Services.Artifacts.ArtifactsMetrics>();
        }
        _ = services.AddScoped<Farm.Web.Api.Services.Artifacts.IArtifactsService, Farm.Web.Api.Services.Artifacts.ArtifactsService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Artifacts.IArtifactCleanupService, Farm.Web.Api.Services.Artifacts.ArtifactCleanupService>();
        _ = services.AddHostedService<Farm.Web.Api.Services.Artifacts.ArtifactCleanupHostedService>();

        _ = services.AddSingleton<Farm.Web.Api.Services.Slicing.SliceJobMetrics>();
        _ = services.AddSingleton<Farm.Web.Api.Services.Slicing.SlicerServiceMetrics>();
        _ = services.Configure<Farm.Web.Api.Services.Workers.WorkerAuthSettings>(configuration.GetSection(Farm.Web.Api.Services.Workers.WorkerAuthSettings.SectionName));
        _ = services.AddSingleton<Farm.Web.Api.Services.Workers.IWorkerAuthService, Farm.Web.Api.Services.Workers.WorkerAuthService>();
        _ = services.Configure<Farm.Infrastructure.Settings.SlicerSettings>(configuration.GetSection(Farm.Infrastructure.Settings.SlicerSettings.SectionName));
    }

    private static void RegisterAuthenticationAndSecurityServices(IServiceCollection services)
    {
        _ = services.AddScoped<Farm.Web.Api.Services.PasswordPolicy.IPasswordPolicyService, Farm.Web.Api.Services.PasswordPolicy.PasswordPolicyService>();
        _ = services.AddScoped<Farm.Web.Api.Repositories.PasswordPolicy.IPasswordPolicyRepository, Farm.Web.Api.Repositories.PasswordPolicy.PasswordPolicyRepository>();
        _ = services.AddScoped<Farm.Web.Api.Services.Authentication.IAccountLockoutService, Farm.Web.Api.Services.Authentication.AccountLockoutService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Authentication.IAuthAuditService, Farm.Web.Api.Services.Authentication.AuthAuditService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Authentication.ITokenRevocationService, Farm.Web.Api.Services.Authentication.TokenRevocationService>();
        _ = services.AddHostedService<Farm.Web.Api.Services.Authentication.TokenRevocationCleanupService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Users.IUsersService, Farm.Web.Api.Services.Users.UsersService>();

        _ = services.AddSingleton<Farm.Web.Api.Services.Email.IEmailTemplateRenderer, Farm.Web.Api.Services.Email.EmailTemplateRenderer>();
        _ = services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var opts = new Farm.Web.Api.Services.Email.EmailOptions();
            cfg.GetSection("Email").Bind(opts);
            return opts;
        });
        _ = services.AddScoped<Farm.Web.Api.Services.Email.IEmailService>(sp =>
        {
            var logger = sp.GetRequiredService<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var opts = sp.GetRequiredService<Farm.Web.Api.Services.Email.EmailOptions>();
            var renderer = sp.GetRequiredService<Farm.Web.Api.Services.Email.IEmailTemplateRenderer>();
            return opts.Provider?.Equals("mailjet", StringComparison.OrdinalIgnoreCase) == true
                ? new Farm.Web.Api.Services.Email.MailjetEmailService(logger, opts, renderer)
                : new Farm.Web.Api.Services.Email.ConsoleEmailService(logger, renderer);
        });

        _ = services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var opts = new Farm.Web.Api.Services.RateLimiting.RateLimitOptions();
            cfg.GetSection("RateLimiting").Bind(opts);
            return opts;
        });
        _ = services.AddSingleton<Farm.Web.Api.Services.RateLimiting.IRateLimitService, Farm.Web.Api.Services.RateLimiting.InMemoryRateLimitService>();
    }

    private static void RegisterCatalogAndSlicingServices(IServiceCollection services)
    {
        _ = services.AddScoped<Farm.Web.Api.Services.Catalog.ICatalogService, Farm.Web.Api.Services.Catalog.CatalogService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Filament.IFilamentTypeService, Farm.Web.Api.Services.Filament.FilamentTypeService>();
        _ = services.AddScoped<Farm.Web.Api.Repositories.Filament.IFilamentTypeRepository, Farm.Web.Api.Repositories.Filament.FilamentTypeRepository>();

        _ = services.AddScoped<Farm.Web.Api.Repositories.Slicing.IProfilesRepository, Farm.Web.Api.Repositories.Slicing.ProfilesRepository>();
        _ = services.AddScoped<Farm.Web.Api.Services.Slicing.IProfilesService, Farm.Web.Api.Services.Slicing.ProfilesService>();
        _ = services.AddScoped<Farm.Web.Api.Repositories.Slicing.ISlicerProfileRepository, Farm.Web.Api.Repositories.Slicing.EfSlicerProfileRepository>();
        _ = services.AddScoped<Farm.Web.Api.Services.Slicing.IProfileParsingService, Farm.Web.Api.Services.Slicing.ProfileParsingService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Slicing.IOrcaBundleParsingService, Farm.Web.Api.Services.Slicing.OrcaBundleParsingService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Slicing.IOrcaPresetMappingService, Farm.Web.Api.Services.Slicing.OrcaPresetMappingService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Slicing.IOrcaBundleExportService, Farm.Web.Api.Services.Slicing.OrcaBundleExportService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Slicing.IProfileDuplicateFilter, Farm.Web.Api.Services.Slicing.ProfileDuplicateFilter>();
        _ = services.AddScoped<Farm.Web.Api.Repositories.Slicing.ISlicersRepository, Farm.Web.Api.Repositories.Slicing.EfSlicersRepository>();
        _ = services.AddScoped<Farm.Web.Api.Services.Slicing.ISlicersService, Farm.Web.Api.Services.Slicing.SlicersService>();
        _ = services.AddScoped<Farm.Web.Shared.ISlicerJobQueue, Farm.Web.Api.Services.SlicerServices.DbSlicerJobQueue>();
        _ = services.AddSingleton<Farm.Web.Shared.ISlicerProgressNotifier, Farm.Web.Api.Services.SlicerServices.SignalRSlicerProgressNotifier>();
        _ = services.AddScoped<Farm.Web.Shared.ISlicerOrchestrator, Farm.Web.Api.Services.SlicerServices.SlicerOrchestrator>();
        _ = services.AddScoped<Farm.Web.Api.Repositories.Slicing.ISliceJobRepository, Farm.Web.Api.Repositories.Slicing.EfSliceJobRepository>();
        _ = services.AddScoped<Farm.Web.Api.Services.Slicing.ISliceJobEventService, Farm.Web.Api.Services.Slicing.SliceJobEventService>();
        _ = services.AddScoped<Farm.Web.Api.Repositories.Workers.IWorkerRepository, Farm.Web.Api.Repositories.Workers.EfWorkerRepository>();
        _ = services.AddScoped<Farm.Web.Api.Repositories.Queue.IQueueRepository, Farm.Web.Api.Repositories.Queue.EfQueueRepository>();
        _ = services.AddScoped<Farm.Web.Api.Services.Queue.IJobQueueService, Farm.Web.Api.Services.Queue.JobQueueService>();
        _ = services.AddScoped<Farm.Web.Api.Repositories.Gcode.IGcodeRepository, Farm.Web.Api.Repositories.Gcode.EfGcodeRepository>();
        _ = services.AddScoped<Farm.Web.Api.Services.JobDispatch.IJobDispatcherService, Farm.Web.Api.Services.JobDispatch.JobDispatcherService>();
        _ = services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var opts = new Farm.Web.Api.Services.JobDispatch.RetryOptions();
            cfg.GetSection("JobDispatchRetry").Bind(opts);
            return opts;
        });
        _ = services.AddScoped<Farm.Web.Api.Services.Slicing.ISlicingSubmissionService, Farm.Web.Api.Services.Slicing.SlicingSubmissionService>();
        _ = services.AddScoped<Farm.Web.Api.Services.SlicerServices.LocalSlicerFileStorage>();
        _ = services.AddScoped<Farm.Web.Shared.ISlicerFileStorage>(sp => sp.GetRequiredService<Farm.Web.Api.Services.SlicerServices.LocalSlicerFileStorage>());
    }

    private static void RegisterModelAndGcodeServices(IServiceCollection services)
    {
        _ = services.AddScoped<Farm.Web.Api.Services.Model.IModelService, Farm.Web.Api.Services.Model.ModelService>();
        _ = services.AddSingleton<Farm.Web.Api.Services.IO.IFileSystem, Farm.Web.Api.Services.IO.SystemFileSystem>();
        _ = services.AddScoped<Farm.Web.Api.Repositories.Model.IModelRepository, Farm.Web.Api.Repositories.Model.EfModelRepository>();
        _ = services.AddScoped<Farm.Web.Api.Services.SystemLogs.ISystemLogService, Farm.Web.Api.Services.SystemLogs.SystemLogService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Gcode.IGcodeLibraryService, Farm.Web.Api.Services.Gcode.GcodeLibraryService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Gcode.IGcodeFilesService, Farm.Web.Api.Services.Gcode.GcodeFilesService>();

        // Tag services and repositories
        _ = services.AddScoped<Farm.Web.Api.Repositories.Tags.ITagRepository, Farm.Web.Api.Repositories.Tags.EfTagRepository>();
        _ = services.AddScoped<Farm.Web.Api.Repositories.Tags.IModelTagMappingRepository, Farm.Web.Api.Repositories.Tags.EfModelTagMappingRepository>();
        _ = services.AddScoped<Farm.Web.Api.Services.Tags.ITagService, Farm.Web.Api.Services.Tags.TagService>();
    }

    private static void RegisterPrinterAndSetupServices(IServiceCollection services)
    {
        _ = services.AddScoped<Farm.Web.Api.Services.Setup.ISetupService, Farm.Web.Api.Services.Setup.SetupService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Printers.IPrintersService, Farm.Web.Api.Services.Printers.PrintersService>();
        _ = services.AddScoped<Farm.Web.Api.Services.Interfaces.IMoonrakerDiagnosticsService, Farm.Web.Api.Services.MoonrakerDiagnosticsService>();
        _ = services.AddScoped<Farm.Web.Api.Services.PrinterCapabilities.IPrinterCapabilitiesService, Farm.Web.Api.Services.PrinterCapabilities.PrinterCapabilitiesService>();
    }

    private static void RegisterSchemaAndSignalRServices(IServiceCollection services)
    {
        _ = services.AddScoped<Farm.Web.Api.Services.SchemaHealth.ISchemaHealthService, Farm.Web.Api.Services.SchemaHealth.SchemaHealthService>();
        _ = services.AddScoped<Farm.Web.Api.Repositories.SchemaHealth.ISchemaHealthRepository, Farm.Web.Api.Repositories.SchemaHealth.SchemaHealthRepository>();
        _ = services.AddScoped<Farm.Web.Api.Services.SignalR.ISignalRTestService, Farm.Web.Api.Services.SignalR.SignalRTestService>();
        _ = services.AddSingleton<Farm.Web.Api.Services.Interfaces.IStartupStatus, Farm.Web.Api.Services.StartupStatus>();
    }
}
