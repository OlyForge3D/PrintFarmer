using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Result of scanning a network address for a Spoolman instance.
/// </summary>
public record SpoolmanDiscoveryResult(
    string Url,
    bool IsAvailable,
    string? Error = null,
    string? Version = null,
    TimeSpan? ResponseTime = null);
