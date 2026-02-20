using Farm.Infrastructure.Services;
using Farm.Slicer.Module.Services;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Composition-root wiring that bridges slicer-module interfaces to
/// host-specific (API project) implementations. These registrations
/// cannot live inside <c>Farm.Slicer.Module.Api</c> because they depend
/// on types defined in <c>Farm.Web.Api</c> (which would be circular).
/// </summary>
public static class SlicerHostAdapterRegistrations
{
    /// <summary>
    /// Registers host-side service implementations that the slicer module
    /// requires but that reference API-project types.
    /// </summary>
    public static IServiceCollection AddSlicerHostAdapters(this IServiceCollection services)
    {
        // API temp-path provider (used by Program.cs diagnostic logging, not slicer-specific)
        _ = services.AddSingleton<Infrastructure.Temp.ITempPathProvider, Infrastructure.Temp.DefaultTempPathProvider>();

        // Model file service (now lives in Farm.Slicer.Module)
        _ = services.AddScoped<IModel3DFileService, Model3DFileService>();

        // 3MF to STL conversion (infrastructure implementation)
        _ = services.AddScoped<I3MfToStlConversionService, Farm.Infrastructure.Services.Models.ThreeMfToStlConversionService>();

        // IHostedServiceMonitor — forward to the same BackgroundServiceMonitor singleton
        _ = services.AddSingleton<IHostedServiceMonitor>(sp =>
            (IHostedServiceMonitor)sp.GetRequiredService<Farm.Infrastructure.Services.Background.IBackgroundServiceMonitor>());

        return services;
    }
}
