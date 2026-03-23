using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Toolhead template for a printer model (used when creating new printers from this model).
/// Nozzle diameter is derived from the referenced NozzleModel.
/// MaxFlowRate and MaxTemp are derived from the referenced HotendModel.
/// </summary>
public record PrinterModelToolheadDto(
    Guid Id,
    string Name,
    int Index,

    // Component references - IDs for saving, Names for display
    Guid? HotendModelId = null,
    string? HotendModelName = null,
    Guid? ExtruderModelId = null,
    string? ExtruderModelName = null,
    Guid? ToolheadModelDefId = null,
    string? ToolheadModelDefName = null,
    Guid? NozzleModelId = null,
    string? NozzleModelName = null,
    double? NozzleDiameter = null,   // Derived from NozzleModel.Diameter
    NozzleType? NozzleType = null,   // Derived from NozzleModel.NozzleType
    double? MaxFlowRate = null,      // Derived from HotendModel.MaxFlowRate
    int? MaxTemp = null,             // Derived from HotendModel.MaxTemp
    string[]? SupportedMaterials = null,
    bool IsPrimary = false);
