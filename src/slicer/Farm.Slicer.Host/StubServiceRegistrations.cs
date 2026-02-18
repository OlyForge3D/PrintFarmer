using Farm.Slicer.Module.Services;

namespace Farm.Slicer.Host;

/// <summary>
/// Extension methods that register stub (no-op) implementations for all slicer
/// service interfaces. These stubs use <see cref="StubServiceProxy{T}"/> to
/// return sensible defaults (empty collections, null entities, completed tasks)
/// until real service implementations are migrated into <c>Farm.Slicer.Module</c>.
/// </summary>
public static class StubServiceRegistrations
{
    /// <summary>
    /// Registers stub implementations for all slicer service interfaces that
    /// have not yet been migrated into the module. Controllers will resolve
    /// successfully and return empty/default responses.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSlicerStubServices(this IServiceCollection services)
    {
        // Core slicer services (used by SlicersController, SlicerManagementController)
        RegisterStub<ISlicersService>(services);

        // Slicing submission (used by SlicingSubmissionController)
        RegisterStub<ISlicingSubmissionService>(services);

        // Slice job support (used by SliceJobController)
        RegisterStub<ISliceJobEventService>(services);
        RegisterStub<IArtifactsService>(services);
        RegisterStub<IRateLimitService>(services);
        RegisterStub<IWorkerAuthService>(services);
        RegisterStub<IWorkerCircuitBreakerService>(services);

        // Slicing orchestration (used by SlicingJobsController)
        RegisterStub<ISlicerOrchestrator>(services);
        RegisterStub<ISlicerFileManagementService>(services);
        RegisterStub<ISlicerStoredFileOpsService>(services);
        RegisterStub<ISlicerTempPathProvider>(services);

        // Model/file management (used by Model3DFilesController)
        RegisterStub<IModel3DFileService>(services);
        RegisterStub<I3MfToStlConversionService>(services);

        // Profiles (used by ProfilesController)
        RegisterStub<IProfilesService>(services);

        // NOTE: ICatalogServiceAdapter and IPrinterLookupService are registered
        // as real HTTP-backed implementations in AddCrossDomainLookupServices()
        // rather than as stubs, so they resolve cross-domain data from the main API.

        // Orca bundle services (used by ProfilesController overloads)
        RegisterStub<IOrcaBundleExportService>(services);
        RegisterStub<IOrcaBundleParsingService>(services);
        RegisterStub<IOrcaPresetMappingService>(services);

        // Background/infrastructure services (not controller-injected but may be resolved)
        RegisterStub<ISlicerJobDispatcherService>(services);
        RegisterStub<IArtifactCleanupService>(services);
        RegisterStub<ISlicerModelAnalysisService>(services);
        RegisterStub<ISlicerProfileParsingService>(services);
        RegisterStub<ISlicerJobQueue>(services);
        RegisterStub<ISlicerFileStorage>(services);
        RegisterStub<ISlicerProgressNotifier>(services);

        return services;
    }

    /// <summary>
    /// Registers a <see cref="StubServiceProxy{T}"/> as the scoped implementation
    /// for <typeparamref name="TInterface"/>.
    /// </summary>
    private static void RegisterStub<TInterface>(IServiceCollection services)
        where TInterface : class
    {
        services.AddScoped(_ => StubServiceProxy<TInterface>.CreateInstance());
    }
}
