using Farm.Slicer.Module.Contracts.Libraries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module.Api;

/// <summary>
/// Runtime-discovered implementation of <see cref="ISlicerModule"/> and
/// <see cref="ISlicerHubRegistrar"/> for the slicer API layer (controllers, hubs,
/// services, adapters).
/// Discovered by the Farm.Slicer.Integration shim via assembly scanning; no
/// compile-time reference from the API project is required.
/// </summary>
public sealed class SlicerApiModuleRegistrar : ISlicerModule, ISlicerHubRegistrar
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSlicerApiServices(configuration);

    /// <inheritdoc />
    public void Configure(WebApplication app) => app.ConfigureSlicerMetrics();

    /// <inheritdoc />
    public void MapHubs(IEndpointRouteBuilder endpoints) => endpoints.MapSlicerHubs();
}
