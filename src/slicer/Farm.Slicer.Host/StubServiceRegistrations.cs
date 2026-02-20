using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Models;
using Farm.Slicer.Module.Services;

namespace Farm.Slicer.Host;

/// <summary>
/// Extension methods that register stub (no-op) implementations for slicer service
/// interfaces that do not yet have concrete implementations in
/// <c>Farm.Slicer.Module</c> or <c>Farm.Slicer.Module.Api</c>.
/// </summary>
/// <remarks>
/// <see cref="I3MfToStlConversionService"/> has a real implementation in Farm.Infrastructure
/// and is registered directly. <see cref="IModel3DFileService"/> remains stubbed because
/// its implementation (<c>Model3DFileService</c>) lives in the API project with deep
/// API-layer dependencies (UnitOfWork, tags, file management, thumbnails). It needs to
/// be refactored into the slicer module or exposed via HTTP cross-domain lookup.
/// </remarks>
public static class StubServiceRegistrations
{
    /// <summary>
    /// Registers real implementations where available and stub implementations for
    /// interfaces that have no standalone-compatible concrete implementation yet:
    /// <list type="bullet">
    ///   <item><see cref="IModel3DFileService"/> – 3D model CRUD (implementation in API project, needs refactoring)</item>
    ///   <item><see cref="I3MfToStlConversionService"/> – 3MF-to-STL conversion (real implementation from Farm.Infrastructure)</item>
    /// </list>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddUnimplementedSlicerServiceStubs(this IServiceCollection services)
    {
        RegisterStub<IModel3DFileService>(services);

        // Real implementation — self-contained in Farm.Infrastructure, only needs IUnifiedLoggingService.
        services.AddScoped<I3MfToStlConversionService, ThreeMfToStlConversionService>();

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
