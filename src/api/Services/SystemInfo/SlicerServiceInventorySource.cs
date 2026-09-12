using System.Text.Json;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.SystemStatus;
using Farm.Slicer.Module.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.SystemInfo;

/// <summary>Projects existing slicer registrations, including every replica, without reading worker endpoints.</summary>
public sealed class SlicerServiceInventorySource(SlicerDbContext? db, ILogger<SlicerServiceInventorySource> logger) : IServiceInventorySource
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<ServiceReplicaObservationDto>> ReadAsync(CancellationToken cancellationToken)
    {
        if (db is null)
        {
            return [Missing(InventoryObservationState.Unknown, "RegistryNotAvailableInThisHost")];
        }

        try
        {
            // Deliberately exclude host, API key, tags and arbitrary metadata from the public projection.
            var registrations = await db.SlicerServices.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.Version, row.LastSeen, row.Status, row.CapabilitiesJson })
                .ToListAsync(cancellationToken);
            if (registrations.Count == 0)
            {
                return [Missing(InventoryObservationState.NotInstalled, "NoRegisteredOptionalWorkers")];
            }

            return registrations.Select(row =>
            {
                (string? build, string? commit) = ReadApplicationBuild(row.CapabilitiesJson);
                DateTimeOffset observedAt = new(DateTime.SpecifyKind(row.LastSeen, DateTimeKind.Utc));
                return new ServiceReplicaObservationDto
                {
                    ServiceId = "slicer-worker", InstanceId = row.Id.ToString(), Component = "slicer-worker", Required = false,
                    ApplicationVersion = build, SourceCommit = commit, EngineVersion = ApplicationBuildObservation.Parse(row.Version).Version,
                    ObservedAt = observedAt, LastSuccessAt = observedAt, Source = "SelfReport",
                    ObservationState = row.Status == "Offline" ? InventoryObservationState.Unavailable : InventoryObservationState.Observed,
                    ReasonCode = build is null ? "LegacyRegistrationHasNoApplicationBuild" : "RegistrationNotDigestAttestation",
                };
            }).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Unable to collect slicer registry inventory");
            return [Missing(InventoryObservationState.Unavailable, "RegistryReadFailed")];
        }
    }

    private static ServiceReplicaObservationDto Missing(InventoryObservationState state, string reason) => new()
    {
        ServiceId = "slicer-worker", Component = "slicer-worker", Required = false,
        ObservationState = state, Source = "LocalRegistry", ReasonCode = reason,
    };

    // Only this allowlisted application field is consumed. Existing engine/container claims do not attest the app image.
    private static (string? Version, string? Commit) ReadApplicationBuild(string? capabilities)
    {
        if (string.IsNullOrWhiteSpace(capabilities))
        {
            return (null, null);
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(capabilities);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("applicationBuild", out JsonElement build)
                && build.ValueKind == JsonValueKind.String
                ? ApplicationBuildObservation.Parse(build.GetString()) : (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
