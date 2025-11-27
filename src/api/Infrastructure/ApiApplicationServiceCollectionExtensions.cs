using AutoMapper;
using Farm.Web.Api.Services.JobDispatch;
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
        RegisterArtifactServices(services, configuration);
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
        _ = services.AddScoped<Importing.Services.Import.IImportParserService, Importing.Services.Import.ImportParserService>();
        _ = services.AddScoped<Importing.Services.Import.IImportProcessorService, Importing.Services.Import.ImportProcessorService>();
        _ = services.AddScoped<Importing.Services.Adapters.IPrinterCapabilityDiscoveryAdapter, Services.Adapters.PrinterCapabilityDiscoveryAdapter>();
        _ = services.AddScoped<Importing.Services.Adapters.IDefaultCatalogAdapter, Services.Adapters.DefaultCatalogAdapter>();
    }

    private static void RegisterArtifactServices(IServiceCollection services, IConfiguration configuration)
    {
        _ = services.Configure<Farm.Infrastructure.Settings.ArtifactStorageSettings>(configuration.GetSection(Farm.Infrastructure.Settings.ArtifactStorageSettings.SectionName));
        _ = services.AddScoped<Services.Artifacts.IArtifactCleanupService, Services.Artifacts.ArtifactCleanupService>();
        _ = services.AddHostedService<Services.Artifacts.ArtifactCleanupHostedService>();

        _ = services.AddSingleton<Services.Slicing.SliceJobMetrics>();
        _ = services.AddSingleton<Services.Slicing.SlicerServiceMetrics>();
        _ = services.Configure<Services.Workers.WorkerAuthSettings>(configuration.GetSection(Farm.Web.Api.Services.Workers.WorkerAuthSettings.SectionName));
        _ = services.AddSingleton<Services.Workers.IWorkerAuthService, Services.Workers.WorkerAuthService>();
        _ = services.Configure<Farm.Infrastructure.Settings.SlicerSettings>(configuration.GetSection(Farm.Infrastructure.Settings.SlicerSettings.SectionName));
    }

    private static void RegisterAuthenticationAndSecurityServices(IServiceCollection services)
    {
        _ = services.AddScoped<Services.PasswordPolicy.IPasswordPolicyService, Services.PasswordPolicy.PasswordPolicyService>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.PasswordPolicy.IPasswordPolicyRepository, Farm.Infrastructure.Repositories.PasswordPolicy.PasswordPolicyRepository>();
        _ = services.AddScoped<Services.Authentication.IAccountLockoutService, Services.Authentication.AccountLockoutService>();
        _ = services.AddScoped<Services.Authentication.IAuthAuditService, Services.Authentication.AuthAuditService>();
        _ = services.AddScoped<Services.Authentication.ITokenRevocationService, Services.Authentication.TokenRevocationService>();
        _ = services.AddHostedService<Services.Authentication.TokenRevocationCleanupService>();
        _ = services.AddScoped<Services.Users.IUsersService, Services.Users.UsersService>();
    }

    private static void RegisterCatalogAndSlicingServices(IServiceCollection services)
    {
        // Catalog services
        _ = services.AddScoped<Services.Catalog.ICatalogService, Services.Catalog.CatalogService>();

        // Filament services (API-specific)
        _ = services.AddScoped<Services.Filament.IFilamentTypeService, Services.Filament.FilamentTypeService>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Filament.IFilamentTypeRepository, Farm.Infrastructure.Repositories.Filament.FilamentTypeRepository>();

        // Slicing-specific services
        _ = services.AddScoped<Services.Slicing.IProfileParsingService, Services.Slicing.ProfileParsingService>();
        _ = services.AddScoped<Services.Slicing.IOrcaBundleParsingService, Services.Slicing.OrcaBundleParsingService>();
        _ = services.AddScoped<Services.Slicing.IOrcaPresetMappingService, Services.Slicing.OrcaPresetMappingService>();
        _ = services.AddScoped<Services.Slicing.IOrcaBundleExportService, Services.Slicing.OrcaBundleExportService>();
        _ = services.AddScoped<Services.Slicing.IProfileDuplicateFilter, Services.Slicing.ProfileDuplicateFilter>();
        _ = services.AddScoped<Shared.ISlicerJobQueue, Services.SlicerServices.DbSlicerJobQueue>();
        _ = services.AddSingleton<Shared.ISlicerProgressNotifier, Services.SlicerServices.SignalRSlicerProgressNotifier>();
        _ = services.AddScoped<Shared.ISlicerOrchestrator, Services.SlicerServices.SlicerOrchestrator>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Slicing.ISliceJobRepository, Farm.Infrastructure.Repositories.Slicing.EfSliceJobRepository>();
        _ = services.AddScoped<Services.Slicing.ISliceJobEventService, Services.Slicing.SliceJobEventService>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Workers.IWorkerRepository, Farm.Infrastructure.Repositories.Workers.EfWorkerRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Queue.IQueueRepository, Farm.Infrastructure.Repositories.Queue.EfQueueRepository>();
        _ = services.AddScoped<Services.Queue.IJobQueueService, Services.Queue.JobQueueService>();
        _ = services.AddScoped<IJobDispatcherService, JobDispatcherService>();
        _ = services.AddSingleton(sp =>
        {
            IConfiguration cfg = sp.GetRequiredService<IConfiguration>();
            RetryOptions opts = new RetryOptions();
            cfg.GetSection("JobDispatchRetry").Bind(opts);
            return opts;
        });
        _ = services.AddScoped<Services.Slicing.ISlicingSubmissionService, Services.Slicing.SlicingSubmissionService>();
        _ = services.AddScoped<Services.SlicerServices.LocalSlicerFileStorage>();
        _ = services.AddScoped<Shared.ISlicerFileStorage>(sp => sp.GetRequiredService<Services.SlicerServices.LocalSlicerFileStorage>());
    }

    private static void RegisterModelAndGcodeServices(IServiceCollection services)
    {
        // Tag services and repositories (API-specific)
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Tags.ITagRepository, Farm.Infrastructure.Repositories.Tags.EfTagRepository>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.Tags.IModelTagMappingRepository, Farm.Infrastructure.Repositories.Tags.EfModelTagMappingRepository>();
        _ = services.AddScoped<Services.Tags.ITagService, Services.Tags.TagService>();

        // SystemLogs service (API-specific)
        _ = services.AddScoped<Services.SystemLogs.ISystemLogService, Services.SystemLogs.SystemLogService>();
    }

    private static void RegisterPrinterAndSetupServices(IServiceCollection services)
    {
        _ = services.AddScoped<Services.Setup.ISetupService, Services.Setup.SetupService>();
        _ = services.AddScoped<Services.Printers.IPrintersService, Services.Printers.PrintersService>();
        _ = services.AddScoped<Services.Interfaces.IMoonrakerDiagnosticsService, Services.MoonrakerDiagnosticsService>();
        _ = services.AddScoped<Services.Interfaces.IPrinterCapabilityDiscoveryService, Services.PrinterCapabilityDiscoveryService>();
        _ = services.AddScoped<Services.PrinterCapabilities.IPrinterCapabilitiesService, Services.PrinterCapabilities.PrinterCapabilitiesService>();
    }

    private static void RegisterSchemaAndSignalRServices(IServiceCollection services)
    {
        _ = services.AddScoped<Services.SchemaHealth.ISchemaHealthService, Services.SchemaHealth.SchemaHealthService>();
        _ = services.AddScoped<Farm.Infrastructure.Repositories.SchemaHealth.ISchemaHealthRepository, Farm.Infrastructure.Repositories.SchemaHealth.SchemaHealthRepository>();
        _ = services.AddScoped<Services.SignalR.ISignalRTestService, Services.SignalR.SignalRTestService>();
        _ = services.AddSingleton<Services.Interfaces.IStartupStatus, Services.StartupStatus>();
        _ = services.AddSingleton<Services.IDiscoveryProgressCache, Services.DiscoveryProgressCache>();
    }
}
