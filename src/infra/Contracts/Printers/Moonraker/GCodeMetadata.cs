using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class GCodeMetadata
{
    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("modified")]
    public double Modified { get; set; }

    [JsonPropertyName("slicer")]
    public string? Slicer { get; set; }

    [JsonPropertyName("slicer_version")]
    public string? SlicerVersion { get; set; }

    [JsonPropertyName("layer_height")]
    public double? LayerHeight { get; set; }

    [JsonPropertyName("first_layer_height")]
    public double? FirstLayerHeight { get; set; }

    [JsonPropertyName("object_height")]
    public double? ObjectHeight { get; set; }

    [JsonPropertyName("filament_total")]
    public double? FilamentTotal { get; set; }

    [JsonPropertyName("filament_weight_total")]
    public double? FilamentWeightTotal { get; set; }

    [JsonPropertyName("estimated_time")]
    public int? EstimatedTime { get; set; }

    [JsonPropertyName("thumbnails")]
    public ThumbnailInfo[] Thumbnails { get; set; } = Array.Empty<ThumbnailInfo>();

    [JsonPropertyName("first_layer_bed_temp")]
    public double? FirstLayerBedTemp { get; set; }

    [JsonPropertyName("first_layer_extr_temp")]
    public double? FirstLayerExtrTemp { get; set; }

    [JsonPropertyName("gcode_start_byte")]
    public long? GcodeStartByte { get; set; }

    [JsonPropertyName("gcode_end_byte")]
    public long? GcodeEndByte { get; set; }

    [JsonPropertyName("object_info")]
    public GCodeObjectInfo[] ObjectInfo { get; set; } = Array.Empty<GCodeObjectInfo>();
}

public class GCodeObjectInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("center")]
    public double[]? Center { get; set; }

    [JsonPropertyName("polygon")]
    public double[][]? Polygon { get; set; }
}
