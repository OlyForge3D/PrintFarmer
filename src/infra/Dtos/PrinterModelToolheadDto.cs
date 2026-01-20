using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Toolhead template for a printer model (used when creating new printers from this model).
/// Nozzle diameter is now derived from the referenced NozzleModel, not stored on the toolhead.
/// </summary>
public record PrinterModelToolheadDto(
    Guid Id,
    string Name,
    int Index,
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
    double? NozzleDiameter = null,  // Derived from NozzleModel.Diameter
    string[]? SupportedMaterials = null,
    bool IsPrimary = false);
