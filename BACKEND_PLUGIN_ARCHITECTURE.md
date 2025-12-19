# Backend Client Plugin Architecture

## Overview

PrintFarmer now supports a modular, plugin-based architecture for backend clients. Backend clients (Moonraker, OctoPrint, PrusaLink, SDCP) are now packaged as separate plugin libraries that are dynamically discovered and registered at runtime.

## Architecture Goals

1. **Separation of Concerns**: Each backend client is in its own library
2. **Dynamic Discovery**: The API discovers plugins at runtime without direct references
3. **Easy Extension**: New backends can be added without modifying the API
4. **Loose Coupling**: API depends only on the plugin core interfaces
5. **Pluggable**: Plugins can be loaded from separate files/directories in the future

## Project Structure

```
src/
├── api/                                    # Main API (only depends on Core)
│   └── Extensions/
│       └── BackendPluginExtensions.cs      # Plugin discovery & registration
├── backends/                               # Plugin directory
│   ├── Farm.Backend.Plugin.Core/           # Base plugin interfaces (no API reference)
│   │   ├── IBackendClientPlugin.cs
│   │   ├── IBackendPluginRegistry.cs
│   │   ├── IBackendPluginLoader.cs
│   │   ├── BackendPluginRegistry.cs
│   │   └── BackendPluginLoader.cs
│   ├── Farm.Backend.Plugin.Moonraker/      # Moonraker plugin descriptor
│   │   └── MoonrakerBackendPlugin.cs
│   ├── Farm.Backend.Plugin.OctoPrint/      # OctoPrint plugin descriptor
│   │   └── OctoPrintBackendPlugin.cs
│   ├── Farm.Backend.Plugin.PrusaLink/      # PrusaLink plugin descriptor
│   │   └── PrusaLinkBackendPlugin.cs
│   └── Farm.Backend.Plugin.Sdcp/           # SDCP plugin descriptor
│       └── SdcpBackendPlugin.cs
```

## How It Works

### 1. Plugin Definition

Each plugin library exports a plugin descriptor that implements `IBackendClientPlugin`:

```csharp
public class MoonrakerBackendPlugin : IBackendClientPlugin
{
    public string BackendType => "moonraker";
    public string DisplayName => "Moonraker";
    public Type ClientType => GetTypeFromApi("Farm.Web.Api.Services.MoonrakerClient");
    public Type ClientInterfaceType => GetTypeFromApi("Farm.Web.Api.Services.Interfaces.IMoonrakerClient");
    public IEnumerable<Type> GetCapabilities() => [ /* capability types */ ];
}
```

### 2. Dynamic Discovery

When the API starts, `BackendPluginExtensions.AddBackendClientPlugins()` is called:

```csharp
// In Program.cs
builder.Services.AddBackendClientPlugins();
```

This triggers automatic discovery:

1. Scans all loaded assemblies for types implementing `IBackendClientPlugin`
2. Instantiates plugins using their default constructors
3. Registers each plugin in the `IBackendPluginRegistry`
4. Stores plugin metadata (backend type, capabilities, client types)

### 3. Plugin Descriptor Pattern

Plugin descriptors use **lazy type resolution** via reflection:

```csharp
private static Type GetTypeFromApi(string fullyQualifiedTypeName)
{
    var assembly = AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => a.GetName().Name == "Farm.Web.Api");
    
    return assembly?.GetType(fullyQualifiedTypeName)
        ?? throw new InvalidOperationException($"Type {fullyQualifiedTypeName} not found");
}
```

**Why?** Plugins don't directly reference the API project, so types are resolved at runtime when the API assembly is loaded.

### 4. No Circular Dependencies

- **Core Plugin Library** (`Farm.Backend.Plugin.Core`): Only depends on `Microsoft.Extensions.DependencyInjection`
- **Plugin Libraries** (e.g., `Farm.Backend.Plugin.Moonraker`): Depend only on Core
- **API** (`Farm.Web.Api`): Depends only on Core (not on individual plugins)
- **solution file**: Links all projects but compiler handles dependency order automatically

This eliminates circular reference issues.

## Key Interfaces

### IBackendClientPlugin

Every plugin implements this interface:

```csharp
public interface IBackendClientPlugin
{
    string BackendType { get; }              // Unique identifier (e.g., "moonraker")
    string DisplayName { get; }              // Human-readable name
    string Description { get; }              // What this backend does
    Type ClientType { get; }                 // Concrete client class
    Type ClientInterfaceType { get; }        // Client interface
    Version Version { get; }                 // Plugin version
    void RegisterServices(IServiceCollection services);
    IEnumerable<Type> GetCapabilities();     // Supported capabilities
}
```

### IBackendPluginRegistry

Central registry for all plugins:

```csharp
public interface IBackendPluginRegistry
{
    void Register(IBackendClientPlugin plugin);
    IBackendClientPlugin? GetPlugin(string backendType);
    IEnumerable<IBackendClientPlugin> GetAllPlugins();
    bool IsRegistered(string backendType);
}
```

### IBackendPluginLoader

Discovers and loads plugins:

```csharp
public interface IBackendPluginLoader
{
    Task LoadPluginsAsync(string pluginDirectory, IBackendPluginRegistry registry, IServiceCollection services);
    void LoadPlugin<T>(IBackendPluginRegistry registry, IServiceCollection services) where T : IBackendClientPlugin, new();
}
```

## Usage in the API

### Getting Plugin Information

```csharp
// Inject the registry
public class MyService
{
    private readonly IBackendPluginRegistry _pluginRegistry;
    
    public MyService(IBackendPluginRegistry pluginRegistry)
    {
        _pluginRegistry = pluginRegistry;
    }
    
    public void Example()
    {
        // Get all registered backends
        var allPlugins = _pluginRegistry.GetAllPlugins();
        
        // Get a specific plugin
        var moonrakerPlugin = _pluginRegistry.GetPlugin("moonraker");
        
        // Check if backend is supported
        if (_pluginRegistry.IsRegistered("moonraker"))
        {
            var capabilities = moonrakerPlugin.GetCapabilities();
        }
    }
}
```

### Extension Methods

The API provides helper extension methods:

```csharp
// Get capabilities for a backend
var moonrakerCapabilities = registry.GetCapabilities("moonraker");

// Get client type
var clientType = registry.GetClientType("moonraker");

// Get interface type
var interfaceType = registry.GetClientInterfaceType("moonraker");

// Get all plugins
var allPlugins = registry.GetAllPlugins();
```

## Adding a New Backend

To add support for a new backend (e.g., "MyPrinter3D"):

### 1. Create Plugin Library

```bash
mkdir src/backends/Farm.Backend.Plugin.MyPrinter3D
cd src/backends/Farm.Backend.Plugin.MyPrinter3D
dotnet new classlib -f net9.0
```

### 2. Add Core Reference

In `Farm.Backend.Plugin.MyPrinter3D.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="../Farm.Backend.Plugin.Core/Farm.Backend.Plugin.Core.csproj" />
</ItemGroup>
```

### 3. Implement Client

In API, create `src/api/Services/MyPrinter3DClient.cs` with proper interfaces.

### 4. Create Plugin Descriptor

In plugin library, create `MyPrinter3DBackendPlugin.cs`:

```csharp
public class MyPrinter3DBackendPlugin : IBackendClientPlugin
{
    public string BackendType => "myprinter3d";
    public string DisplayName => "MyPrinter 3D";
    // ... implement interface
}
```

### 5. Register in Solution

Add project to `src/farm-web.sln`.

### 6. Done!

The API will automatically discover and register it at startup.

## Runtime Behavior

### Startup

```
API Start
  ↓
AddBackendClientPlugins() called
  ↓
Scan all loaded assemblies
  ↓
Find all IBackendClientPlugin implementations
  ↓
Instantiate plugins (default constructor)
  ↓
Register in IBackendPluginRegistry
  ↓
API ready to use plugins
```

### Client Selection

When a printer needs to communicate with its backend:

```csharp
// Get the plugin for this printer's backend
var plugin = registry.GetPlugin(printer.BackendType);

// Check if it supports needed capability
if (plugin?.GetCapabilities().Contains(typeof(ISupportsFileUpload)) == true)
{
    // Safe to use file upload
    var clientType = plugin.ClientType;
}
```

## Benefits

✅ **Extensibility**: Add new backends without modifying API  
✅ **No Circular Dependencies**: Core plugin system is separate  
✅ **Dynamic Discovery**: Plugins are found automatically  
✅ **Type-Safe**: Full compile-time type checking  
✅ **Testable**: Easy to mock plugin registry in tests  
✅ **Future-Proof**: Can load plugins from files/directories later  
✅ **Clean Architecture**: Clear separation of concerns  

## Testing

Plugins are discovered automatically during tests:

```csharp
[Fact]
public void Test_PluginDiscovery()
{
    var registry = new BackendPluginRegistry();
    // Plugins will be discovered when assemblies are scanned
    Assert.True(registry.GetAllPlugins().Count() > 0);
}
```

## Migration Notes

The plugin system replaced the old `BackendCapabilityFactory` pattern. The factory still exists but now uses the plugin registry internally:

```csharp
public class BackendCapabilityFactory
{
    public BackendCapabilityFactory(IBackendPluginRegistry pluginRegistry)
    {
        _pluginRegistry = pluginRegistry;
    }
    // ... uses registry to determine capabilities
}
```

## Future Enhancements

1. **File-Based Plugin Loading**: Load plugins from `plugins/` directory at runtime
2. **Plugin Configuration**: TOML/JSON config files for plugin settings
3. **Plugin Versioning**: Check plugin versions, warn on mismatches
4. **Plugin Hot-Reload**: Support reloading plugins without restarting
5. **Plugin Marketplace**: Share community-created backends

## References

- [Plugin Architecture Pattern](https://en.wikipedia.org/wiki/Plug-in_(computing))
- [Dependency Injection](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection)
- [Reflection in .NET](https://learn.microsoft.com/en-us/dotnet/fundamentals/reflection/reflection)
