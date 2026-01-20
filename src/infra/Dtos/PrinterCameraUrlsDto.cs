using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Camera URLs for all printers (static configuration without external API calls)
/// <summary>
/// Lightweight camera URL information for printers without external API overhead.
/// </summary>
public record PrinterCameraUrlsDto(
    Guid Id,
    string Name,
    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null);
