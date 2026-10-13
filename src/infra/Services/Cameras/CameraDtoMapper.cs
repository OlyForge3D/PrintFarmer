using System;
using System.Collections.Generic;
using System.Linq;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Maps <see cref="Camera"/> entities (with printer links, linked printers and locations loaded) to API DTOs.
/// </summary>
public static class CameraDtoMapper
{
    /// <summary>
    /// Maps a camera to a <see cref="CameraDto"/>.
    /// </summary>
    public static CameraDto ToDto(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        CameraDto dto = new()
        {
            Id = camera.Id,
            Name = camera.Name,
            Description = camera.Description,
            StreamUrl = camera.StreamUrl,
            SnapshotUrl = camera.SnapshotUrl,
            StreamConfigured = !string.IsNullOrWhiteSpace(camera.StreamUrl),
            SnapshotConfigured = !string.IsNullOrWhiteSpace(camera.SnapshotUrl),
            IsEnabled = camera.IsEnabled,
            SortOrder = camera.SortOrder,
            Location = camera.Location,
            CreatedAt = camera.CreatedAt,
            UpdatedAt = camera.UpdatedAt,
            Source = camera.Source,
            CameraType = camera.CameraType,
            HealthStatus = camera.HealthStatus,
            LastHealthCheck = camera.LastHealthCheck,
            IsStandalone = camera.PrinterLinks.Count == 0,
            UserModified = camera.UserModified,
        };
        ApplyLinks(camera, dto);
        ApplyLocation(camera, dto);
        return dto;
    }

    /// <summary>
    /// Maps a camera to a <see cref="DisplayCameraDto"/>. Proxy URLs are left for the caller to set.
    /// </summary>
    public static DisplayCameraDto ToDisplayDto(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        DisplayCameraDto dto = new()
        {
            Id = camera.Id,
            Name = camera.Name,
            Description = camera.Description,
            StreamUrl = camera.StreamUrl,
            SnapshotUrl = camera.SnapshotUrl,
            IsEnabled = camera.IsEnabled,
            SortOrder = camera.SortOrder,
            Location = camera.Location,
            IsStandalone = camera.PrinterLinks.Count == 0,
            Source = camera.Source,
            CameraType = camera.CameraType,
            HealthStatus = camera.HealthStatus,
        };
        ApplyLinks(camera, dto);
        dto.LocationId = camera.LocationId;
        dto.EffectiveLocationId = camera.GetEffectiveLocationId();
        dto.EffectiveLocationName = camera.GetEffectiveLocation()?.Name;
        dto.IsLocationDerived = camera.IsLocationDerivedFromPrinter();
        return dto;
    }

    private static void ApplyLinks(Camera camera, ICameraPrinterLinksDto dto)
    {
        List<CameraPrinter> links = camera.OrderedPrinterLinks().ToList();
        CameraPrinter? primary = links.FirstOrDefault();
        dto.PrinterId = primary?.PrinterId;
        dto.PrinterName = primary?.Printer?.Name;
        dto.PrinterIds = links.Select(l => l.PrinterId).ToList();
        dto.Printers = links
            .Select(l => new CameraPrinterSummaryDto
            {
                Id = l.PrinterId,
                Name = l.Printer?.Name ?? string.Empty,
                IsPrimary = l.IsPrimary,
            })
            .ToList();
    }

    private static void ApplyLocation(Camera camera, CameraDto dto)
    {
        dto.LocationId = camera.LocationId;
        dto.EffectiveLocationId = camera.GetEffectiveLocationId();
        dto.EffectiveLocationName = camera.GetEffectiveLocation()?.Name;
        dto.IsLocationDerived = camera.IsLocationDerivedFromPrinter();
    }
}
