using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Extruder model catalog entry.
/// </summary>
public record ExtruderModelDto(
    Guid Id,
    string Name,
    Guid ManufacturerId,
    string? ManufacturerName = null,
    string? GearRatio = null,
    bool IsDirectDrive = true,
    string? Description = null,
    string? Url = null);
