using System.Text.Json.Serialization;
using Farm.Slicer.Module.Json;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Filament/Material profile DTO for OrcaSlicer.
/// High-value fields are promoted from the opaque <see cref="Settings"/> dictionary
/// for type safety, documentation, and server-side filtering. The Settings dictionary
/// still contains the full profile for forward compatibility.
/// </summary>
public class FilamentProfileDto
{
    // ── Identity ──────────────────────────────────────────────────────────
    public string Name { get; set; } = string.Empty;

    public string Material { get; set; } = "PLA";

    public string? Manufacturer { get; set; }

    public string? Description { get; set; }

    public string? Color { get; set; }

    [JsonPropertyName("compatible_printers")]
    public IList<string> CompatiblePrinters { get; set; } = [];

    [JsonIgnore]
    public string? CompatiblePrintersCondition { get; set; }

    [JsonConverter(typeof(StringToBoolJsonConverter))]
    public bool Instantiation { get; set; } = true;

    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }

    // ── Temperature ───────────────────────────────────────────────────────
    public int NozzleTemperature { get; set; } = 210;

    public int BedTemperature { get; set; } = 60;

    public int? FirstLayerNozzleTemperature { get; set; }

    public int? FirstLayerBedTemperature { get; set; }

    public int? ChamberTemperature { get; set; }

    public double? MaxVolumetricSpeed { get; set; }

    // ── Flow ──────────────────────────────────────────────────────────────
    public double? FlowRatio { get; set; }

    public int PrintSpeed { get; set; } = 50;

    public bool? EnablePressureAdvance { get; set; }

    public double? PressureAdvance { get; set; }

    // ── Retraction ────────────────────────────────────────────────────────
    public double? RetractionLength { get; set; }

    public double? RetractionSpeed { get; set; }

    public double? DetractionSpeed { get; set; }

    // ── Cooling ───────────────────────────────────────────────────────────
    public bool? EnableFanCooling { get; set; }

    public int? MinFanSpeed { get; set; }

    public int? MaxFanSpeed { get; set; }

    public int? BridgeFanSpeed { get; set; }

    // ── Physical properties ───────────────────────────────────────────────
    public double? Density { get; set; }

    public double? Cost { get; set; }

    // ── G-code ────────────────────────────────────────────────────────────
    public string? StartGcode { get; set; }

    public string? EndGcode { get; set; }

    // ── Full settings bag (forward compatibility) ─────────────────────────
    public Dictionary<string, object> Settings { get; set; } = new();

    /// <summary>
    /// Shallow clone with independent <see cref="Settings"/> and
    /// <see cref="CompatiblePrinters"/> collections. Use before mutating a
    /// profile resolved from a shared cache (e.g. injecting a per-slice
    /// filament colour override) so the cached instance is never polluted.
    /// </summary>
    public FilamentProfileDto Clone()
    {
        FilamentProfileDto clone = (FilamentProfileDto)MemberwiseClone();
        clone.Settings = new Dictionary<string, object>(Settings);
        clone.CompatiblePrinters = new List<string>(CompatiblePrinters);
        return clone;
    }
}
