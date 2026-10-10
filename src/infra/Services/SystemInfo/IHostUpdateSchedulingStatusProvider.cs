using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.SystemStatus;

/// <summary>Read-only source for automatic update status; it must never start work.</summary>
public interface IHostUpdateSchedulingStatusProvider
{
    /// <summary>Returns scheduler status, or null when automatic execution is not wired.</summary>
    HostUpdateSchedulingStatusDto? GetStatus();
}

/// <summary>Safe default used until production scheduler and executor adapters are installed.</summary>
public sealed class UnwiredHostUpdateSchedulingStatusProvider : IHostUpdateSchedulingStatusProvider
{
    /// <inheritdoc />
    public HostUpdateSchedulingStatusDto? GetStatus() => null;
}
