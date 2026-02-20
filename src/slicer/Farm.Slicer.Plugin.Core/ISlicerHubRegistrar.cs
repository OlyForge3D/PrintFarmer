using Microsoft.AspNetCore.Routing;

namespace Farm.Slicer.Module.Contracts.Libraries;

/// <summary>
/// Contract for mapping slicer SignalR hubs to endpoint routes.
/// The implementation in Farm.Slicer.Module.Api has compile-time access to the
/// concrete hub types and calls <c>endpoints.MapHub&lt;T&gt;()</c> directly.
/// The API project obtains the implementation at runtime via DI without any
/// compile-time reference to the hub types.
/// </summary>
public interface ISlicerHubRegistrar
{
    /// <summary>
    /// Maps slicer SignalR hubs to their endpoint routes.
    /// </summary>
    void MapHubs(IEndpointRouteBuilder endpoints);
}
