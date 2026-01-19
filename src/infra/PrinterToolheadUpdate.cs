using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// SignalR event for toolhead updates (position, homed_axes)
/// </summary>
public record PrinterToolheadUpdate(
    Guid PrinterId,
    double? X,
    double? Y,
    double? Z,
    string? HomedAxes);
