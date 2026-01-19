using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Filament type management
/// <summary>
/// Filament type with default temperature targets.
/// </summary>
public record FilamentTypeDto(Guid Id, string Name, TempTargets DefaultTemperatures);
