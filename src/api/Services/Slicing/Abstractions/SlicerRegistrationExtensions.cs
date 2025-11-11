using Microsoft.Extensions.DependencyInjection;
using Farm.Web.Shared.Contracts.Slicing.Libraries;

namespace Farm.Web.Api.Services.Slicing.Abstractions;

/// <summary>
/// Extension methods for registering slicer libraries in dependency injection.
/// </summary>
public static class SlicerRegistrationExtensions
{
    private static readonly List<ISlicerLibrary> RegisteredLibraries = [];
    private static readonly List<ISlicerUIProvider> RegisteredUIProviders = [];

    /// <summary>
    /// Registers a slicer library in the DI container and slicing registry.
    /// </summary>
    public static IServiceCollection AddSlicerLibrary<TLibrary>(this IServiceCollection services)
        where TLibrary : class, ISlicerLibrary, new()
    {
        var library = new TLibrary();
        RegisteredLibraries.Add(library);
        return services;
    }

    /// <summary>
    /// Registers a slicer UI provider in the DI container.
    /// </summary>
    public static IServiceCollection AddSlicerUIProvider<TUIProvider>(this IServiceCollection services)
        where TUIProvider : class, ISlicerUIProvider, new()
    {
        var provider = new TUIProvider();
        RegisteredUIProviders.Add(provider);
        return services;
    }

    /// <summary>
    /// Registers the slicer registry with all previously registered libraries and UI providers.
    /// Call this after adding all individual slicer libraries.
    /// </summary>
    public static IServiceCollection AddSlicerRegistry(this IServiceCollection services)
    {
        // Register all collected libraries and UI providers
        foreach (var library in RegisteredLibraries)
        {
            services.AddSingleton(library);
        }

        foreach (var provider in RegisteredUIProviders)
        {
            services.AddSingleton(provider);
        }

        // Register the registry itself
        services.AddSingleton<ISlicerRegistry>(sp =>
        {
            var libraries = sp.GetRequiredService<IEnumerable<ISlicerLibrary>>();
            var uiProviders = sp.GetRequiredService<IEnumerable<ISlicerUIProvider>>();
            return new SlicerRegistry(libraries, uiProviders);
        });

        // Clear for potential re-registration in tests
        RegisteredLibraries.Clear();
        RegisteredUIProviders.Clear();

        return services;
    }
}
