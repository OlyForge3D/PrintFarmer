using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Abstractions;

/// <summary>
/// Contract for a vertical-slice API module discovered and registered by the host
/// (<c>Farm.Web.Api</c>) at startup. A module packages a self-contained slice of the
/// monolith -- controllers, DI services, and the endpoints they expose -- into its own
/// <c>Farm.Modules.*</c> assembly so it can be developed, tested, and eventually deployed
/// independently of the rest of the API.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are discovered via <see cref="ApiModuleHostExtensions.AddApiModules"/>,
/// which the host calls with an explicit list of module assemblies -- discovery is never an
/// implicit whole-process scan (see that method's remarks for why). Discovery adds the
/// module's declaring assembly as an MVC <c>ApplicationPart</c> (so any controllers it
/// contains are found by routing) and invokes <see cref="ConfigureServices"/> before the host
/// builds its service provider. <see cref="MapEndpoints"/> runs afterward, once
/// <see cref="ApiModuleHostExtensions.MapApiModules"/> is called during endpoint
/// configuration -- controller actions are already routed via <c>MapControllers()</c> by
/// that point, so this hook exists for modules that additionally expose minimal-API style
/// endpoints alongside their controllers.
/// </para>
/// <para>
/// As of this phase (#2035) no assembly implements this interface -- it is a pure seam.
/// The first controller move happens in a later phase of epic #2019.
/// </para>
/// </remarks>
public interface IApiModule
{
    /// <summary>
    /// Stable, human-readable module identifier used in host startup logging and
    /// diagnostics (e.g. <c>"PartsInventory"</c>). Must be unique across all loaded modules.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Registers the module's DI services. Called before the host's
    /// <see cref="IServiceCollection"/> is built into a service provider, so
    /// implementations must not resolve services here -- only register them.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's configuration root.</param>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Maps any endpoints the module exposes outside of attribute-routed MVC controllers
    /// (which are already discovered via the <c>ApplicationPart</c> added during
    /// discovery). Called once, after the host application has been built.
    /// </summary>
    /// <param name="endpoints">The host's endpoint route builder.</param>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
