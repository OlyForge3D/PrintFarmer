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

/// <summary>
/// Public wire projection of a per-tool material requirement, exposed on every job response
/// surface (queue, detail, recent, project/approval) as the <c>toolRequirements[]</c> array
/// (GitHub issue OlyForge3D/PrintFarmer#710, B5). Distinct from the internal
/// <see cref="PrintJobToolMaterialRequirement"/> so the public contract can use the
/// operator-facing <c>toolIndex</c> name while the persisted metadata keeps <c>tool</c>.
/// The legacy scalar <c>requiredMaterialType</c> is preserved alongside this array.
/// </summary>
/// <param name="ToolIndex">Zero-based toolhead index matching G-code T-commands (T0 = 0, T1 = 1, ...).</param>
/// <param name="MaterialType">Material family required for this tool (e.g., "PLA", "PETG").</param>
/// <param name="ColorHint">Optional hex color hint captured from the slicer (e.g., "#FF0000").</param>
/// <param name="EstimatedGrams">Optional estimated grams of filament this tool will consume.</param>
public sealed record PrintJobToolRequirementDto(
    [property: JsonPropertyName("toolIndex")] int ToolIndex,
    [property: JsonPropertyName("materialType")] string MaterialType,
    [property: JsonPropertyName("colorHint")] string? ColorHint,
    [property: JsonPropertyName("estimatedGrams")] double? EstimatedGrams);
