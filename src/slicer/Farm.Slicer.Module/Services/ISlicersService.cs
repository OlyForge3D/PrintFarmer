using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Service for managing slicer worker registration, heartbeat, and profile import.
/// </summary>
public interface ISlicersService
{
    /// <summary>Lists all registered slicer services.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SlicerService>> ListAsync(CancellationToken ct);

    /// <summary>Registers a new slicer service.</summary>
    /// <param name="dto">Registration data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (assigned ID, generated API key).</returns>
    Task<(Guid Id, string ApiKey)> RegisterAsync(RegisterSlicerDto dto, CancellationToken ct);

    /// <summary>Gets a slicer service by identifier.</summary>
    /// <param name="id">The slicer service identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SlicerService?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Records a heartbeat for a slicer service.</summary>
    /// <param name="id">The slicer service identifier.</param>
    /// <param name="dto">Heartbeat data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the heartbeat was recorded successfully.</returns>
    Task<bool> HeartbeatAsync(Guid id, HeartbeatDto dto, CancellationToken ct);

    /// <summary>Deregisters a slicer service.</summary>
    /// <param name="id">The slicer service identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the service was successfully deregistered.</returns>
    Task<bool> DeregisterAsync(Guid id, CancellationToken ct);

    /// <summary>Rotates the API key for a slicer service.</summary>
    /// <param name="id">The slicer service identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="isAdminForced">Whether the rotation is forced by an administrator.</param>
    /// <returns>The new API key, or null if the service was not found.</returns>
    Task<string?> RotateApiKeyAsync(Guid id, CancellationToken ct, bool isAdminForced = false);

    /// <summary>
    /// Import slicer profiles for a specific printer model on-demand.
    /// Called when a printer is added with a model that doesn't have profiles yet.
    /// </summary>
    /// <param name="printerModelId">The catalog PrinterModel ID to import profiles for.</param>
    /// <param name="printerModelName">The model name (for logging and alias resolution).</param>
    /// <param name="manufacturerName">The manufacturer name (for profile filtering).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of profiles imported, or 0 if no worker available or profiles already exist.</returns>
    Task<int> ImportProfilesForModelAsync(Guid printerModelId, string printerModelName, string manufacturerName, CancellationToken ct);
}
