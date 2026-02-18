using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module.Api;

/// <summary>
/// Extension methods for registering slicer API controllers and SignalR hubs.
/// </summary>
public static class SlicerApiExtensions
{
    /// <summary>
    /// Adds the slicer module API assembly as an MVC application part so its
    /// controllers are discovered by the routing infrastructure.
    /// </summary>
    /// <param name="builder">The MVC builder returned by <c>AddControllers</c>.</param>
    /// <returns>The MVC builder for chaining.</returns>
    public static IMvcBuilder AddSlicerControllers(this IMvcBuilder builder)
    {
        _ = builder.AddApplicationPart(typeof(SlicerApiExtensions).Assembly);
        return builder;
    }

    /// <summary>
    /// Registers slicer API-layer services (SignalR notifiers, job dispatch, profile mapping) into the DI container.
    /// </summary>
    public static IServiceCollection AddSlicerApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        // SignalR notifiers
        _ = services.AddSingleton<ISlicerProgressNotifier, SignalRSlicerProgressNotifier>();
        _ = services.AddScoped<ISliceJobEventService, SliceJobEventService>();

        // Profile mapping and export
        _ = services.AddScoped<IOrcaPresetMappingService, OrcaPresetMappingService>();
        _ = services.AddScoped<IOrcaBundleExportService, OrcaBundleExportService>();

        // Job dispatch
        _ = services.AddScoped<ISlicerJobDispatcherService, JobDispatcherService>();
        _ = services.AddSingleton(sp =>
        {
            IConfiguration cfg = sp.GetRequiredService<IConfiguration>();
            RetryOptions opts = new RetryOptions();
            cfg.GetSection("JobDispatchRetry").Bind(opts);
            return opts;
        });

        return services;
    }

    /// <summary>
    /// Maps slicer-module SignalR hubs to their endpoint routes.
    /// Maps <see cref="SlicerHub"/> to <c>/hubs/slicer-registry</c> and
    /// <see cref="SlicerProgressHub"/> to <c>/hubs/slicers</c>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (typically from <c>app.MapXxx</c>).</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapSlicerHubs(this IEndpointRouteBuilder endpoints)
    {
        _ = endpoints.MapHub<SlicerHub>("/hubs/slicer-registry");
        _ = endpoints.MapHub<SlicerProgressHub>("/hubs/slicers");
        return endpoints;
    }
}
