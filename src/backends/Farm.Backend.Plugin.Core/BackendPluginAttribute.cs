namespace Farm.Backend.Plugin.Core;

/// <summary>
/// Attribute that marks an assembly as containing backend plugins.
/// This attribute is used by the plugin discovery system to identify valid plugin assemblies.
/// Stores metadata about the plugin including its unique backend identifier for use in service registration.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class BackendPluginAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the BackendPluginAttribute.
    /// </summary>
    /// <param name="backendId">The unique backend identifier as an integer (maps to PrinterBackend enum: Moonraker=1, PrusaLink=2, SDCP=3, OctoPrint=4).</param>
    /// <param name="name">The human-readable name of the plugin package.</param>
    /// <param name="version">The semantic version of the plugin.</param>
    public BackendPluginAttribute(int backendId, string name, string version)
    {
        BackendId = backendId;
        Name = name;
        Version = version;
    }

    /// <summary>
    /// Gets the unique backend identifier as an integer for use in the status client registry.
    /// This maps to the PrinterBackend enum value (Moonraker=1, PrusaLink=2, SDCP=3, OctoPrint=4).
    /// </summary>
    public int BackendId { get; }

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
