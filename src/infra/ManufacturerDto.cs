using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Catalog (Manufacturers / Models)
/// <summary>
/// Printer manufacturer catalog entry.
/// </summary>
public record ManufacturerDto(Guid Id, string Name, string? Url = null, string? Description = null);
