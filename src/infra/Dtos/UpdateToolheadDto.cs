using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Update payload for modifying toolhead settings.
/// Includes hardware tracking fields for component model references.
/// </summary>
public record UpdateToolheadDto(
    Guid Id,
    string? Name = null,
    int? Index = null,
    int? MaxHotendTemp = null,
    double? MaxFlowRate = null,
    ToolheadType? ToolheadType = null,

    // Component model references
    Guid? HotendModelId = null,
    Guid? ExtruderModelId = null,
    Guid? ToolheadModelDefId = null,
    Guid? NozzleModelId = null,
    string[]? SupportedMaterials = null,
    bool? IsPrimary = null);
