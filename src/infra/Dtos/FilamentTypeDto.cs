using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Filament type management
/// <summary>
/// Filament type with default temperature targets and material properties.
/// </summary>
/// <param name="Id">Unique identifier for the filament type.</param>
/// <param name="Name">Display name of the filament type (e.g., "PLA", "PETG-CF").</param>
/// <param name="DefaultTemperatures">Default hotend and bed temperatures.</param>
/// <param name="IsAbrasive">True if the filament contains abrasive materials (e.g., carbon fiber, glass fiber) that require hardened nozzles.</param>
/// <param name="NeedsEnclosure">True if the filament requires an enclosure for optimal printing (e.g., ABS, ASA, Nylon).</param>
public record FilamentTypeDto(Guid Id, string Name, TempTargets DefaultTemperatures, bool IsAbrasive, bool NeedsEnclosure);
