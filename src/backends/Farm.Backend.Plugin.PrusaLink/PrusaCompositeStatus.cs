#pragma warning disable S1006, CS1998, S1939 // Default parameters, async methods, and explicit interface inheritance are intentional

using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Backend.Plugin.PrusaLink;

public record PrusaCompositeStatus(
    bool IsOnline,
    string? State,
    double? Progress,
    string? JobName,
    [property: SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Transport model for JSON/UI; keep string and provide Uri accessors in shared DTOs")] string? ThumbnailUrl,
    [property: SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Transport model for JSON/UI; keep string and provide Uri accessors in shared DTOs")] string? CameraStreamUrl,
    [property: SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Transport model for JSON/UI; keep string and provide Uri accessors in shared DTOs")] string? CameraSnapshotUrl,
    double? HotendTemp = null,
    double? BedTemp = null,
    double? HotendTarget = null,
    double? BedTarget = null,
    double? AxisX = null,
    double? AxisY = null,
    double? AxisZ = null);

#pragma warning restore CS1066
