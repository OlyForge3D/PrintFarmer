using System.Text.Json.Serialization;
using Farm.Slicer.Module.Json;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Process/Quality profile DTO for OrcaSlicer.
/// High-value fields are promoted from the opaque <see cref="Settings"/> dictionary
/// for type safety, documentation, and server-side filtering. The Settings dictionary
/// still contains the full profile for forward compatibility.
/// </summary>
public class ProcessProfileDto
{
    // ── Identity ──────────────────────────────────────────────────────────
    public string Name { get; set; } = string.Empty;

    public string Quality { get; set; } = "standard";

    public string? Description { get; set; }

    [JsonPropertyName("compatible_printers")]
    public IList<string> CompatiblePrinters { get; set; } = [];

    [JsonIgnore]
    public string? CompatiblePrintersCondition { get; set; }

    [JsonConverter(typeof(StringToBoolJsonConverter))]
    public bool Instantiation { get; set; } = true;

    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }

    // ── Layer ─────────────────────────────────────────────────────────────
    public double LayerHeight { get; set; } = 0.2;

    public double FirstLayerHeight { get; set; } = 0.2;

    public int TopLayers { get; set; } = 4;

    public int BottomLayers { get; set; } = 3;

    // ── Walls ─────────────────────────────────────────────────────────────
    public int WallCount { get; set; } = 3;

    // ── Infill ────────────────────────────────────────────────────────────
    public int InfillPercentage { get; set; } = 20;

    public string? InfillPattern { get; set; }

    // ── Speed ─────────────────────────────────────────────────────────────
    public int PrintSpeed { get; set; } = 50;

    public int FirstLayerPrintSpeed { get; set; } = 50;

    public int? OuterWallSpeed { get; set; }

    public int? InnerWallSpeed { get; set; }

    public int? InfillSpeed { get; set; }

    public int? TopSurfaceSpeed { get; set; }

    public int? TravelSpeed { get; set; }

    // ── Adhesion ──────────────────────────────────────────────────────────
    public string? BedAdhesion { get; set; }

    // ── Supports ──────────────────────────────────────────────────────────
    public bool Supports { get; set; }

    public string? SupportType { get; set; }

    public int? SupportDensity { get; set; }

    public int? SupportAngle { get; set; }

    // ── Seam ──────────────────────────────────────────────────────────────
    public string? SeamPosition { get; set; }

    // ── Ironing ───────────────────────────────────────────────────────────
    public bool? EnableIroning { get; set; }

    // ── Temperature ───────────────────────────────────────────────────────
    public int? NozzleTemp { get; set; }

    public int? BedTemp { get; set; }

    public int? FirstLayerNozzleTemp { get; set; }

    public int? FirstLayerBedTemp { get; set; }

    // ── Retraction ────────────────────────────────────────────────────────
    public double? RetractionLength { get; set; }

    public double? RetractionSpeed { get; set; }

    // ── Line widths ───────────────────────────────────────────────────────
    public double? LineWidthDefault { get; set; }

    public double? LineWidthOuterWall { get; set; }

    public double? LineWidthInnerWall { get; set; }

    // ── Acceleration ──────────────────────────────────────────────────────
    public int? DefaultAcceleration { get; set; }

    public int? OuterWallAcceleration { get; set; }

    // ── Full settings bag (forward compatibility) ─────────────────────────
    public Dictionary<string, object> Settings { get; set; } = new();
}
