using Farm.Web.Shared.Contracts.Slicing.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Services.Slicing.Abstractions;

/// <summary>
/// DEPRECATED: This file is replaced by SlicerPluginDiscovery which uses assembly attributes.
/// 
/// Slicer library registration now uses a plugin discovery approach via SlicerPluginAttribute.
/// Each slicer library declares itself in AssemblyInfo.cs with:
/// 
/// [assembly: SlicerPlugin(typeof(MySlicerLibrary), typeof(MySlicerUIProvider))]
/// 
/// Then during application startup, call:
/// services.DiscoverAndRegisterSlicerPlugins().AddSlicerRegistry();
/// 
/// See SlicerPluginDiscovery for implementation details.
/// This file is kept for reference but is no longer used.
/// </summary>
[Obsolete("Use SlicerPluginDiscovery with assembly attributes instead")]
public static class SlicerRegistrationExtensions
{
    // Deprecated - do not use
}
