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
/// <remarks>
/// Initializes a new instance of the SlicerPluginAttribute.
/// </remarks>
/// <param name="libraryType">The type implementing ISlicerLibrary</param>
/// <param name="uiProviderType">The type implementing ISlicerUIProvider</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class SlicerPluginAttribute(Type libraryType, Type uiProviderType) : Attribute
{
    /// <summary>
    /// Gets the type of the slicer library implementation (must implement ISlicerLibrary).
    /// </summary>
    public Type LibraryType { get; } = libraryType;

    /// <summary>
    /// Gets the type of the slicer UI provider implementation (must implement ISlicerUIProvider).
    /// </summary>
    public Type UIProviderType { get; } = uiProviderType;
}
