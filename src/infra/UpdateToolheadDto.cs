using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Update payload for modifying toolhead settings.
/// </summary>
public record UpdateToolheadDto(
    Guid Id,
    string? Name = null,
    int? Index = null,
    double? NozzleDiameter = null,
    int? MaxHotendTemp = null,
    string[]? SupportedMaterials = null,
    bool? IsPrimary = null);
