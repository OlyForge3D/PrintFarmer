using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Toolhead model catalog entry.
/// </summary>
public record ToolheadModelDto(
    Guid Id,
    string Name,
    Guid ManufacturerId,
    string? ManufacturerName = null,
    string? Description = null,
    string? Url = null);
