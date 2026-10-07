using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.SystemStatus;

/// <summary>Supplies module-specific service versions and health for system status.</summary>
public interface ISystemServiceInfoSource
{
    /// <summary>Reads service health without making requests to worker endpoints.</summary>
    Task<IReadOnlyList<SystemServiceInfoDto>> ReadAsync(CancellationToken cancellationToken);
}
