using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.SystemStatus;
using Farm.Infrastructure.Settings;

namespace Farm.Web.Api.Services.SystemInfo;

/// <summary>Reads this API process and configured topology without network or host inspection.</summary>
public sealed class LocalServiceInventorySource(ISettingsService settings, IConfiguration configuration) : IServiceInventorySource
{
    private static readonly string ProcessInstanceId = Guid.NewGuid().ToString("N");

    /// <inheritdoc/>
    public Task<IReadOnlyList<ServiceReplicaObservationDto>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (string? version, string? commit) = ApplicationBuildObservation.FromAssembly(typeof(LocalServiceInventorySource).Assembly);
        NetworkDiscoverySettings? discovery = settings.GetByKey(NetworkDiscoverySettings.SectionName) as NetworkDiscoverySettings;
        bool split = Farm.Modules.Calibration.Startup.CalibrationProfileResolutionStartup.IsSplitDeployment(configuration);
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
                ServiceId = "slicer-host", Component = "slicer-host", Required = split,
                ObservationState = split ? InventoryObservationState.Unknown : InventoryObservationState.NotInstalled,
                Source = "TopologyConfiguration", ReasonCode = split ? "ExternalHostNotObserved" : "NoSeparateHostConfigured",
            },
        ];
        return Task.FromResult(rows);
    }
}
