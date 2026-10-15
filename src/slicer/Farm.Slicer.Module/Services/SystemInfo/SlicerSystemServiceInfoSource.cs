using System.Data.Common;
using System.Text.Json;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.SystemStatus;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Services.SystemInfo;

/// <summary>Projects worker application/engine versions and health from the local registry.</summary>
public sealed class SlicerSystemServiceInfoSource(SlicerDbContext? db, ILogger<SlicerSystemServiceInfoSource> logger) : ISystemServiceInfoSource
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<SystemServiceInfoDto>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (db is null)
        {
            return [];
        }

        try
        {
            var registrations = await db.SlicerServices.AsNoTracking().OrderBy(row => row.Name).ThenBy(row => row.Id)
                .Select(row => new { row.Name, row.Version, row.LastSeen, row.Status, row.CapabilitiesJson })
                .ToListAsync(cancellationToken);
            DateTime now = DateTime.UtcNow;
            return registrations.Select(row => new SystemServiceInfoDto
            {
                Name = string.IsNullOrWhiteSpace(row.Name) ? "Slicer worker" : $"Slicer worker ({row.Name})",
                Version = ReadApplicationVersion(row.CapabilitiesJson) ?? "Unknown",
                EngineVersion = ApplicationBuildObservation.Parse(row.Version).Version,
                Health = row.Status == WorkerStatus.Error
                    ? SystemServiceHealth.Critical
                    : NormalizeUtc(row.LastSeen) <= now && now - NormalizeUtc(row.LastSeen) <= TimeSpan.FromSeconds(WorkerStatus.LiveHeartbeatTimeoutSeconds)
                        && row.Status is WorkerStatus.Online or WorkerStatus.Busy or WorkerStatus.Draining
                        ? SystemServiceHealth.Healthy
                        : SystemServiceHealth.Degraded,
            }).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read slicer worker registry for system status");
            return [UnavailableRegistry()];
        }
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static SystemServiceInfoDto UnavailableRegistry() => new()
    {
        Name = "Slicer workers (registry unavailable)",
        Version = "Unknown",
        Health = SystemServiceHealth.Degraded,
    };

    private string? ReadApplicationVersion(string? capabilities)
    {
        if (string.IsNullOrWhiteSpace(capabilities))
        {
            return null;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(capabilities);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("applicationBuild", out JsonElement build)
                && build.ValueKind == JsonValueKind.String
                ? ApplicationBuildObservation.Parse(build.GetString()).Version : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Unable to read slicer worker application build from registry capabilities");
            return null;
        }
    }
}
