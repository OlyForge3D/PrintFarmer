using Farm.Slicer.Module.Contracts.Libraries;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module;

/// <summary>
/// Runtime-discovered implementation of <see cref="ISlicerModule"/> for the core slicer module
/// (SlicerDbContext, repositories, background services, metrics).
/// Discovered by the Farm.Slicer.Integration shim via assembly scanning; no compile-time
/// reference from the API project is required.
/// </summary>
public sealed class SlicerModuleRegistrar : ISlicerModule
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSlicerModule(configuration);

    /// <inheritdoc />
    public void Configure(WebApplication app)
    { /* No post-build configuration for core module. */
    }
}
