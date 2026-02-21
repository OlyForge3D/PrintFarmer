using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Repository for querying and mutating <see cref="MachineModelProfile"/> entities from OrcaSlicer.
/// Machine model profiles are base/template profiles from machine_model_list that define
/// the printer model without nozzle-specific configurations.
/// </summary>
public interface IMachineModelProfileRepository
{
    /// <summary>Gets a machine model profile by its unique identifier.</summary>
    /// <param name="id">The profile identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MachineModelProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets a machine model profile by its name and manufacturer.</summary>
    /// <param name="name">The profile name.</param>
    /// <param name="manufacturer">The manufacturer name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MachineModelProfile?> GetByNameAsync(string name, string manufacturer, CancellationToken ct = default);

    /// <summary>Gets all machine model profiles for a given slicer engine.</summary>
    /// <param name="engine">The slicer engine type.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MachineModelProfile>> GetByEngineAsync(SlicerType engine, CancellationToken ct = default);

    /// <summary>Gets a machine model profile by its content hash.</summary>
    /// <param name="hash">The profile content hash.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MachineModelProfile?> GetByHashAsync(string hash, CancellationToken ct = default);

    /// <summary>
    /// Gets a machine model profile by its linked printer model identifier.
    /// Soft reference — no FK constraint; printer model lives in core domain.
    /// </summary>
    /// <param name="printerModelId">The printer model identifier (soft reference).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MachineModelProfile?> GetByPrinterModelIdAsync(Guid printerModelId, CancellationToken ct = default);

    /// <summary>Adds a new machine model profile.</summary>
    /// <param name="profile">The profile to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(MachineModelProfile profile, CancellationToken ct = default);

    /// <summary>Updates an existing machine model profile.</summary>
    /// <param name="profile">The profile to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(MachineModelProfile profile, CancellationToken ct = default);

    /// <summary>Deletes a machine model profile.</summary>
    /// <param name="profile">The profile to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(MachineModelProfile profile, CancellationToken ct = default);

    /// <summary>Deletes all system machine model profiles for a given slicer engine.</summary>
    /// <param name="engine">The slicer engine type.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of profiles deleted.</returns>
    Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default);
}
