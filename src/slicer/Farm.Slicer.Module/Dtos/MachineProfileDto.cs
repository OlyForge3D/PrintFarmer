using System.Text.Json.Serialization;
using Farm.Slicer.Module.Json;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Machine/Printer profile DTO for OrcaSlicer.
/// High-value fields are promoted from the opaque <see cref="Settings"/> dictionary
/// for type safety, documentation, and server-side filtering. The Settings dictionary
/// still contains the full profile for forward compatibility.
/// </summary>
public class MachineProfileDto
{
    // ── Identity ──────────────────────────────────────────────────────────
    public string Name { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;

    public string? Description { get; set; }

    [JsonPropertyName("printer_model")]
    public string? PrinterModel { get; set; }

    public string? PrinterVariant { get; set; }

    [JsonConverter(typeof(StringToBoolJsonConverter))]
    public bool Instantiation { get; set; } = true;

    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }

    // ── Nozzle ────────────────────────────────────────────────────────────
    public double? NozzleDiameter { get; set; }

    public string? NozzleType { get; set; }

    // ── Build volume ──────────────────────────────────────────────────────
    public double? BuildVolumeX { get; set; }

    public double? BuildVolumeY { get; set; }

    public double? BuildVolumeZ { get; set; }

    public string? PrintableArea { get; set; }

    // ── Capabilities ──────────────────────────────────────────────────────
    public int? MaxPrintSpeed { get; set; }

    public string? MotionType { get; set; }

    public string? GcodeDialect { get; set; }

    public bool? HasHeatedBed { get; set; }

    public bool? HasHeatedChamber { get; set; }

    public int? MaxBedTemperature { get; set; }

    public int? MaxHotendTemperature { get; set; }

    public int ExtruderCount { get; set; } = 1;

    public bool? SupportMultiMaterial { get; set; }

    // ── Retraction ────────────────────────────────────────────────────────
    public double? RetractionLength { get; set; }

    public double? RetractionSpeed { get; set; }

    public double? RetractionLiftZ { get; set; }

    public double? DetractionSpeed { get; set; }

    // ── Bed ───────────────────────────────────────────────────────────────
    public string? BedType { get; set; }

    public string? BedShape { get; set; }

    // ── G-code ────────────────────────────────────────────────────────────
    public string? StartGcode { get; set; }

    public string? EndGcode { get; set; }

    // ── Motion limits ─────────────────────────────────────────────────────
    public double? MaxAccelerationX { get; set; }

    public double? MaxAccelerationY { get; set; }

    public double? MaxFeedrateX { get; set; }

    public double? MaxFeedrateY { get; set; }

    // ── Full settings bag (forward compatibility) ─────────────────────────
    public Dictionary<string, object> Settings { get; set; } = new();
}
