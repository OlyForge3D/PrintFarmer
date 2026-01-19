using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Nozzle model catalog entry.
/// </summary>
public record NozzleModelDto(
    Guid Id,
    string Name,
    Guid ManufacturerId,
    string? ManufacturerName = null,
    int? MaxTemp = null,
    bool IsHardened = false,
    string? Description = null,
    string? Url = null);
