using System.Reflection;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Services.SystemStatus;

/// <summary>Reads this API process and configured topology without network or host inspection.</summary>
/// <param name="settings">Configured discovery topology.</param>
/// <param name="applicationAssembly">The API assembly supplied by the host, not this infrastructure assembly.</param>
/// <param name="splitDeployment">The host's existing deployment-mode decision.</param>
public sealed class LocalServiceInventorySource(ISettingsService settings, Assembly applicationAssembly, bool splitDeployment) : IServiceInventorySource
{
    private static readonly string ProcessInstanceId = Guid.NewGuid().ToString("N");

    /// <inheritdoc/>
    public Task<IReadOnlyList<ServiceReplicaObservationDto>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (string? version, string? commit) = ApplicationBuildObservation.FromAssembly(applicationAssembly);
        NetworkDiscoverySettings? discovery = settings.GetByKey(NetworkDiscoverySettings.SectionName) as NetworkDiscoverySettings;
        IReadOnlyList<ServiceReplicaObservationDto> rows =
        [
            new()
            {
                ServiceId = "api", InstanceId = ProcessInstanceId, Component = "api", Required = true,
                ApplicationVersion = version, SourceCommit = commit, Source = "SelfReport",
                ObservationState = version is null ? InventoryObservationState.Unknown : InventoryObservationState.Observed,
                ObservedAt = now, LastSuccessAt = now, ReasonCode = "AssemblyMetadataNotDigestAttestation",
            },
            new()
            {
                ServiceId = "frontend", Component = "frontend", Required = true,
                ReasonCode = "FrontendAssetsObservedByBrowser",
            },
            new()
            {
                ServiceId = "discovery", Component = "discovery", Required = discovery?.EnableDiscovery == true,

                // The legacy anonymous heartbeat proves neither build nor replica identity.
                ObservationState = InventoryObservationState.Unknown,
                Source = "TopologyConfiguration", ReasonCode = discovery?.EnableDiscovery == false ? "OptionalDisabledInstallationUnknown" : "HeartbeatHasNoAuthenticatedBuildIdentity",
            },
            new()
            {
                ServiceId = "slicer-host", Component = "slicer-host", Required = splitDeployment,
                ObservationState = splitDeployment ? InventoryObservationState.Unknown : InventoryObservationState.NotInstalled,
                Source = "TopologyConfiguration", ReasonCode = splitDeployment ? "ExternalHostNotObserved" : "NoSeparateHostConfigured",
            },
        ];
        return Task.FromResult(rows);
    }
}
