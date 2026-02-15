namespace Farm.Slicer.Module.Contracts.Libraries;

/// <summary>
/// Marks an assembly as containing a slicer library plugin.
/// Used for automatic discovery and registration of slicer libraries.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class SlicerPluginAttribute(Type libraryType, Type uiProviderType) : Attribute
{
    public Type LibraryType { get; } = libraryType;

    public Type UIProviderType { get; } = uiProviderType;
}
