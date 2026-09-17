using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.SystemStatus;

/// <summary>
/// Local read-only observation adapter. Only a trusted verifier may supply Identity and verification
/// fields; service self-reports must leave them and running digests null. No HTTP import/write surface.
/// </summary>
public interface IServiceInventorySource
{
    /// <summary>Reads available observations without discovering releases or inspecting Docker.</summary>
    Task<IReadOnlyList<ServiceReplicaObservationDto>> ReadAsync(CancellationToken cancellationToken);
}
