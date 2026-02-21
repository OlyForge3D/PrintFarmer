using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// OrcaSlicer manufacturer bundle structure.
/// Contains lists of machine, process, and filament profiles for a manufacturer.
/// </summary>
public class ManufacturerBundleDto
{
    /// <summary>
    /// Gets or sets the manufacturer name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the bundle version.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the bundle description.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of machine model profile entries.
    /// </summary>
    [JsonPropertyName("machine_model_list")]
    public IList<ManufacturerBundleProfileEntry> MachineModelList { get; set; } = [];

    /// <summary>
    /// Gets or sets the list of machine profile entries.
    /// </summary>
    [JsonPropertyName("machine_list")]
    public IList<ManufacturerBundleProfileEntry> MachineList { get; set; } = [];

    /// <summary>
    /// Gets or sets the list of process profile entries.
    /// </summary>
    [JsonPropertyName("process_list")]
    public IList<ManufacturerBundleProfileEntry> ProcessList { get; set; } = [];

    /// <summary>
    /// Gets or sets the list of filament profile entries.
    /// </summary>
    [JsonPropertyName("filament_list")]
    public IList<ManufacturerBundleProfileEntry> FilamentList { get; set; } = [];
}
