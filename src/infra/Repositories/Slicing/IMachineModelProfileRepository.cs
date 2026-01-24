using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Slicing;

/// <summary>
/// Repository abstraction for querying and mutating machine model profiles from OrcaSlicer.
/// Machine model profiles are base/template profiles from machine_model_list that define
/// the printer model (e.g., "Sovol SV08") without nozzle-specific configurations.
/// </summary>
public interface IMachineModelProfileRepository
{
    /// <summary>
    /// Gets a machine model profile by its unique identifier.
    /// </summary>
    Task<MachineModelProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets a machine model profile by its name and manufacturer.
    /// </summary>
    Task<MachineModelProfile?> GetByNameAsync(string name, string manufacturer, CancellationToken ct = default);

    /// <summary>
    /// Gets all machine model profiles for a given slicer engine.
    /// </summary>
    Task<IReadOnlyList<MachineModelProfile>> GetByEngineAsync(SlicerType engine, CancellationToken ct = default);

    /// <summary>
    /// Gets a machine model profile by its content hash.
    /// </summary>
    Task<MachineModelProfile?> GetByHashAsync(string hash, CancellationToken ct = default);

    /// <summary>
    /// Gets a machine model profile by its linked printer model ID.
    /// Used to check if profiles have been imported for a specific printer model.
    /// </summary>
    Task<MachineModelProfile?> GetByPrinterModelIdAsync(Guid printerModelId, CancellationToken ct = default);

    /// <summary>
    /// Adds a new machine model profile.
    /// </summary>
    Task AddAsync(MachineModelProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing machine model profile.
    /// </summary>
    Task UpdateAsync(MachineModelProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Deletes a machine model profile.
    /// </summary>
    Task DeleteAsync(MachineModelProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Deletes all system machine model profiles for a given slicer engine.
    /// Used during re-seeding to clear old profiles.
    /// </summary>
    Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default);
}
