using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// OrcaSlicer manufacturer bundle entry that points to a profile JSON file.
/// Used in {manufacturer}.json bundle files to reference profiles.
/// </summary>
public class ManufacturerBundleProfileEntry
{
    /// <summary>
    /// Gets or sets the profile name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the relative path like "machine/Prusa MK4S.json".
    /// </summary>
    [JsonPropertyName("sub_path")]
    public string SubPath { get; set; } = string.Empty;
}
