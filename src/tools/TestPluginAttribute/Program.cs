using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Farm.Backend.Plugin.Core;
using Farm.Web.Api.Extensions;
using Microsoft.Extensions.DependencyInjection;

class Program 
{
    static void Main() 
    {
        Console.WriteLine("=== Testing Plugin Discovery ===\n");
        
        // Create a service collection
        var services = new ServiceCollection();
        
        // Add backend plugins using the extension method
        services.AddBackendClientPlugins();
        
        // Build the service provider
        var provider = services.BuildServiceProvider();
        
        // Get the registry
        var registry = provider.GetRequiredService<IBackendPluginRegistry>();
        
        var plugins = registry.GetAllPlugins().ToList();
        Console.WriteLine($"\nPlugins registered: {plugins.Count}");
        foreach (var plugin in plugins)
        {
            Console.WriteLine($"  - {plugin.BackendType}: {plugin.GetType().Name}");
        }
    }
}
