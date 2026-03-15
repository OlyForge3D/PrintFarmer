namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Service for monitoring camera health by probing snapshot URLs.
/// </summary>
public interface ICameraHealthMonitorService
{
    /// <summary>
    /// Runs health checks for all enabled cameras.
    /// Can be triggered manually for testing or diagnostics.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RunHealthCheckAsync(CancellationToken cancellationToken = default);
}
