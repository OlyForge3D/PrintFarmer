using Farm.Infrastructure;

namespace Farm.Slicer.Worker.Core;

/// <summary>
/// Generic interface for slicer profile discovery services.
/// Each slicer worker implements this interface to expose profiles from its local installation.
/// </summary>
public interface ISlicerProfilesService
{
    /// <summary>
    /// Discover and list all available machine model profiles (base templates from machine_model_list).
    /// These are NOT directly instantiatable - they define base printer models like "Sovol SV08".
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<IList<MachineModelProfileDto>> ListAvailableMachineModelProfilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Discover and list all available machine profiles from the slicer's local installation.
    /// These are the actual selectable profiles with nozzle sizes like "Sovol SV08 0.4 nozzle".
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<IList<MachineProfileDto>> ListAvailableMachineProfilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Discover and list all available filament profiles from the slicer's local installation.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<IList<FilamentProfileDto>> ListAvailableFilamentProfilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Discover and list all available process profiles from the slicer's local installation.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<IList<ProcessProfileDto>> ListAvailableProcessProfilesAsync(CancellationToken ct = default);
}
