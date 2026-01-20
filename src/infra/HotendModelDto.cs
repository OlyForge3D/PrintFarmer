using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// ============ Component Model DTOs ============

/// <summary>
/// Hotend model catalog entry.
/// </summary>
public record HotendModelDto(
    Guid Id,
    string Name,
    Guid ManufacturerId,
    string? ManufacturerName = null,
    int? MaxTemp = null,
    bool IsHighFlow = false,
    NozzleInterfaceType NozzleInterface = NozzleInterfaceType.V6,
    string? Description = null,
    string? Url = null);
