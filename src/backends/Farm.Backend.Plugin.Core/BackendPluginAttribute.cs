namespace Farm.Backend.Plugin.Core;

/// <summary>
/// Attribute that marks an assembly as containing backend plugins.
/// This attribute is used by the plugin discovery system to identify valid plugin assemblies.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class BackendPluginAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the BackendPluginAttribute.
    /// </summary>
    /// <param name="name">The human-readable name of the plugin package.</param>
    /// <param name="version">The semantic version of the plugin.</param>
    public BackendPluginAttribute(string name, string version)
    {
        Name = name;
        Version = version;
    }

    /// <summary>
    /// Gets the name of the plugin package.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the version of the plugin.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets or sets an optional description of the plugin.
    /// </summary>
    public string? Description { get; set; }
}
