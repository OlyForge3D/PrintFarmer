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

    // Component model references
    Guid? HotendModelId = null,
    Guid? ExtruderModelId = null,
    Guid? ToolheadModelDefId = null,
    Guid? NozzleModelId = null,
    string[]? SupportedMaterials = null,
    bool? IsPrimary = null,
    ToolheadType? ToolheadType = null,
    double? OffsetX = null,
    double? OffsetY = null,
    double? OffsetZ = null,
    double? NozzleDiameter = null,
    NozzleType? NozzleType = null,
    string? NozzleMaterial = null,
    int? NozzleMaxTemperature = null,
    bool? NozzleIsHardened = null,
    int? HotendMaxTemperature = null,
    double? MaxVolumetricFlow = null,
    string? DriveType = null,
    bool? IsDirectDrive = null,
    string? ExtruderGearRatio = null);
