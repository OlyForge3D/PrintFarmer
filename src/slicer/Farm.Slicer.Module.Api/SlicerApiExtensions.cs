using Farm.Slicer.Module.Api.Hubs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
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
