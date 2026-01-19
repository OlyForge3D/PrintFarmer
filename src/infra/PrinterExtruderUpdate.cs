using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// SignalR event for extruder temperature updates
/// </summary>
public record PrinterExtruderUpdate(
    Guid PrinterId,
    double? Temperature,
    double? Target);
