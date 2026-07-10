using System.Text.Json.Serialization;

namespace Farm.Infrastructure;

/// <summary>
/// Per-tool material requirement extracted from slicer / G-code metadata at queue time.
/// Provides authoritative multi-material validation input for the guided filament
/// swap flow. Preserving <see cref="Domain.PrintJob.RequiredMaterialType"/> keeps
/// existing single-material dispatch and reporting behaviour unchanged.
/// </summary>
/// <param name="Tool">Zero-based toolhead index matching gcode T-commands (T0 = 0, T1 = 1, ...).</param>
/// <param name="MaterialType">Material family required for this tool (e.g., "PLA", "PETG").</param>
/// <param name="ColorHint">Optional hex color hint captured from the slicer (e.g., "#FF0000").</param>
/// <param name="EstimatedGrams">Optional estimated grams of filament this tool will consume.</param>
public sealed record PrintJobToolMaterialRequirement(
    [property: JsonPropertyName("tool")] int Tool,
    [property: JsonPropertyName("materialType")] string MaterialType,
    [property: JsonPropertyName("colorHint")] string? ColorHint,
    [property: JsonPropertyName("estimatedGrams")] double? EstimatedGrams);
