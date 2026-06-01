using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.SystemStatus;

/// <summary>
/// Provides aggregated runtime, storage, and database information for the system status UI.
/// </summary>
public interface ISystemInfoService
{
    /// <summary>
    /// Collects current system information for the running PrintFarmer instance.
    /// </summary>
    Task<SystemInfoDto> GetSystemInfoAsync(CancellationToken cancellationToken = default);
}
