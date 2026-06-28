using System.Runtime.CompilerServices;

namespace Farm.Web.Api.Tests.TestInfrastructure;

/// <summary>
/// Forces the slicer plugin assemblies to load into the current <see cref="AppDomain"/>
/// before any test runs.
/// </summary>
/// <remarks>
/// <para>
/// In production the slicer module is discovered by loading plugin DLLs from
/// <c>Slicer:PluginsPath</c>. In the test host that path is not configured, so
/// <c>Farm.Slicer.Integration.SlicerIntegrationExtensions.AddSlicerIntegration</c> falls back to
/// scanning <see cref="AppDomain.CurrentDomain"/> for assemblies that expose
/// <c>ISlicerModule</c> implementations.
/// </para>
/// <para>
/// .NET loads referenced assemblies lazily — only when one of their types is first used. Under
/// parallel test execution a <c>CustomWebApplicationFactory</c> can build its host before any test
/// has touched a <c>Farm.Slicer.Module</c> type, so the scan finds nothing and the slicer module
/// (including <c>SlicerDbContext</c>) is never registered. This produced intermittent
/// "No service for type 'SlicerDbContext' has been registered" failures depending on test order.
/// </para>
/// <para>
/// A <see cref="ModuleInitializerAttribute"/> runs once when this test assembly is loaded — before
/// any test or factory — guaranteeing the slicer assemblies are present in the AppDomain for the
/// fallback scan.
/// </para>
/// </remarks>
internal static class SlicerModulePreloader
{
    [ModuleInitializer]
    internal static void Preload()
    {
        // Touching a type from each assembly forces the runtime to load it into the AppDomain.
        // Farm.Slicer.Module hosts SlicerModuleRegistrar (registers SlicerDbContext, repositories).
        EnsureFullyLoaded(typeof(Farm.Slicer.Module.SlicerModuleRegistrar));

        // Farm.Slicer.Module.Api hosts SlicerApiModuleRegistrar (controllers, hubs, adapters).
        EnsureFullyLoaded(typeof(Farm.Slicer.Module.Api.SlicerApiModuleRegistrar));
    }

    /// <summary>
    /// Forces <paramref name="type"/>'s assembly — and the dependency closure required to
    /// enumerate its types — to load on this single thread.
    /// </summary>
    /// <remarks>
    /// The slicer-discovery fallback (<c>SlicerIntegrationExtensions.HasPluginTypes</c>) calls
    /// <see cref="System.Reflection.Assembly.GetTypes"/> and silently swallows
    /// <see cref="System.Reflection.ReflectionTypeLoadException"/>. When several
    /// <c>CustomWebApplicationFactory</c> hosts build concurrently, <c>GetTypes()</c> on a slicer
    /// assembly can throw transiently while its dependencies are still being loaded on another
    /// thread, so the slicer module is skipped and <c>SlicerDbContext</c> is never registered.
    /// Eagerly enumerating the types here (single-threaded, before any test) loads the closure once
    /// so the later concurrent scans read cached metadata and never throw.
    /// </remarks>
    private static void EnsureFullyLoaded(Type type)
    {
        try
        {
            _ = type.Assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException)
        {
            // Dependency closure could not be fully enumerated even single-threaded; the discovery
            // scan handles this the same way. Loading the assembly itself is still beneficial.
        }
    }
}
