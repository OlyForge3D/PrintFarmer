using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;
/// <summary>
/// Creation payload for a filament type.
/// </summary>
/// <param name="Name">Display name of the filament type.</param>
/// <param name="DefaultTemperatures">Default hotend and bed temperatures.</param>
/// <param name="IsAbrasive">True if the filament contains abrasive materials requiring hardened nozzles.</param>
/// <param name="NeedsEnclosure">True if the filament requires an enclosure for optimal printing.</param>
public record CreateFilamentTypeRequest(string Name, TempTargets DefaultTemperatures, bool IsAbrasive = false, bool NeedsEnclosure = false);
