using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Models;
using Farm.Slicer.Module.Services;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Host;

/// <summary>
/// Extension methods that register slicer service implementations for the
/// standalone slicer-host deployment.
/// </summary>
/// <remarks>
/// Both <see cref="IModel3DFileService"/> and <see cref="I3MfToStlConversionService"/>
/// now have real implementations. Model3DFileService was moved from the API project
/// to Farm.Slicer.Module; ThreeMfToStlConversionService lives in Farm.Infrastructure.
/// </remarks>
public static class StubServiceRegistrations
{
    /// <summary>
    /// Registers slicer service implementations:
    /// <list type="bullet">
    ///   <item><see cref="IModel3DFileService"/> – 3D model CRUD (Farm.Slicer.Module)</item>
    ///   <item><see cref="I3MfToStlConversionService"/> – 3MF-to-STL conversion (Farm.Infrastructure)</item>
    /// </list>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddUnimplementedSlicerServiceStubs(this IServiceCollection services)
    {
        // Real implementation — moved from API project to Farm.Slicer.Module.
        services.AddScoped<IModel3DFileService, Model3DFileService>();

        // Real implementation — self-contained in Farm.Infrastructure, only needs ILogger<StubServiceRegistrations>.
        services.AddScoped<I3MfToStlConversionService, ThreeMfToStlConversionService>();

        return services;
    }
}
