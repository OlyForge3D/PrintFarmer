using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// SignalR event for print state and progress updates
/// </summary>
public record PrinterStateUpdate(
    Guid PrinterId,
    string? State,
    double? Progress,
    string? JobName);
