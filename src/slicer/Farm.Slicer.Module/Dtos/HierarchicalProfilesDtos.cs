namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Profiles organized by printer model with separate lists for each profile type.
/// </summary>
#pragma warning disable SA1402
public record HierarchicalPrinterModelProfilesDto
{
    /// <summary>Gets the printer model name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the model identifier.</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>Gets machine profiles for this model.</summary>
    public List<MachineProfileListItemDto> MachineProfiles { get; init; } = new();

    /// <summary>Gets filament profiles compatible with this model.</summary>
    public List<FilamentProfileListItemDto> FilamentProfiles { get; init; } = new();

    /// <summary>Gets process profiles compatible with this model.</summary>
    public List<ProcessProfileListItemDto> ProcessProfiles { get; init; } = new();
}

/// <summary>
/// Profiles grouped by manufacturer, containing models and their associated profiles.
/// </summary>
public record HierarchicalManufacturerProfilesDto
{
    /// <summary>Gets the manufacturer name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets models and their profiles for this manufacturer.</summary>
    public Dictionary<string, HierarchicalPrinterModelProfilesDto> Models { get; init; } = new();
}

/// <summary>
/// Response containing the full profile hierarchy organized by manufacturer and model.
/// </summary>
public record HierarchicalProfilesResponseDto
{
    /// <summary>Gets profiles organized as manufacturer → model → profile type.</summary>
    public Dictionary<string, HierarchicalManufacturerProfilesDto> ByHierarchy { get; init; } = new();

    /// <summary>Gets machine profiles grouped by manufacturer.</summary>
    public Dictionary<string, List<MachineProfileListItemDto>> MachineProfiles { get; init; } = new();

    /// <summary>Gets filament profiles grouped by manufacturer.</summary>
    public Dictionary<string, List<FilamentProfileListItemDto>> FilamentProfiles { get; init; } = new();

    /// <summary>Gets process profiles grouped by manufacturer.</summary>
    public Dictionary<string, List<ProcessProfileListItemDto>> ProcessProfiles { get; init; } = new();
}
#pragma warning restore SA1402
