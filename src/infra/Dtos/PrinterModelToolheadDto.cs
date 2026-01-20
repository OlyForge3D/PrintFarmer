using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Toolhead template for a printer model (used when creating new printers from this model).
/// </summary>
public record PrinterModelToolheadDto(
    Guid Id,
    string Name,
    int Index,
    double? NozzleDiameter = null,
    NozzleType? NozzleType = null,
    int? MaxHotendTemp = null,
    double? MaxFlowRate = null,
    ToolheadType? ToolheadType = null,

    // Component references - IDs for saving, Names for display
    Guid? HotendModelId = null,
    string? HotendModelName = null,
    Guid? ExtruderModelId = null,
    string? ExtruderModelName = null,
    Guid? ToolheadModelDefId = null,
    string? ToolheadModelDefName = null,
    Guid? NozzleModelId = null,
    string? NozzleModelName = null,
    string[]? SupportedMaterials = null,
    bool IsPrimary = false);
