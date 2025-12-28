namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Lightweight abstraction for plugin registry metadata.
/// Used by factories to discover available backends without depending on the full plugin system.
/// This allows factories to be shared across different UIs (API, WPF, CLI, etc.)
/// </summary>
public interface IBackendPluginProvider
{
    /// <summary>
    /// Gets all registered backend plugin metadata.
    /// </summary>
    IEnumerable<IBackendPluginMetadata> GetAllPlugins();
}
