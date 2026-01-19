using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;
/// <summary>
/// Creation payload for a filament type.
/// </summary>
public record CreateFilamentTypeRequest(string Name, TempTargets DefaultTemperatures);
