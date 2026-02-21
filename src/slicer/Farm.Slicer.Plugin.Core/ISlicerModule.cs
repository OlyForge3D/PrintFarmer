using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module.Contracts.Libraries;

/// <summary>
/// Contract for a slicer module registration component discovered at runtime.
/// Implementations live in Farm.Slicer.Module and Farm.Slicer.Module.Api;
/// they are loaded by the Farm.Slicer.Integration shim and invoked during startup
/// without any compile-time reference from the API project.
/// </summary>
public interface ISlicerModule
{
    /// <summary>
    /// Phase 1 – DI service registration. Called before the application is built.
    /// </summary>
    void RegisterServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Phase 2 – Post-build configuration (metrics thresholds, subscriptions, etc.).
    /// Called after <see cref="WebApplication"/> has been built.
    /// </summary>
    void Configure(WebApplication app);
}
