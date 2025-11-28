namespace Farm.Infrastructure.Contracts.Slicing.Libraries;

/// <summary>
/// Marks an assembly as containing a slicer library plugin.
/// This attribute is used for automatic discovery and registration of slicer libraries.
/// </summary>
/// <remarks>
/// Usage: Add this attribute at the assembly level in your slicer library project.
/// Example in AssemblyInfo.cs or at the top of any file:
/// 
/// [assembly: SlicerPlugin(typeof(OrcaSlicerLibrary_v2_3_1), typeof(OrcaSlicerUIProvider_v2_3_1))]
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class SlicerPluginAttribute : Attribute
{
    /// <summary>
    /// Gets the type of the slicer library implementation (must implement ISlicerLibrary).
    /// </summary>
    public Type LibraryType { get; }

    /// <summary>
    /// Gets the type of the slicer UI provider implementation (must implement ISlicerUIProvider).
    /// </summary>
    public Type UIProviderType { get; }

    /// <summary>
    /// Initializes a new instance of the SlicerPluginAttribute.
    /// </summary>
    /// <param name="libraryType">The type implementing ISlicerLibrary</param>
    /// <param name="uiProviderType">The type implementing ISlicerUIProvider</param>
    public SlicerPluginAttribute(Type libraryType, Type uiProviderType)
    {
        LibraryType = libraryType;
        UIProviderType = uiProviderType;
    }
}
