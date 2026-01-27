using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Slicing;

/// <summary>
/// Repository abstraction for querying and mutating machine profiles from OrcaSlicer.
/// </summary>
public interface IMachineProfileRepository
{
    Task<MachineProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<MachineProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem = true, Guid? userId = null, CancellationToken ct = default);

    Task<MachineProfile?> GetByHashAsync(string hash, CancellationToken ct = default);

    Task AddAsync(MachineProfile profile, CancellationToken ct = default);

    Task UpdateAsync(MachineProfile profile, CancellationToken ct = default);

    Task DeleteAsync(MachineProfile profile, CancellationToken ct = default);

    Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default);

    /// <summary>
    /// Checks if any machine profiles exist for the given printer model.
    /// Used to determine if profiles have been imported for a printer model.
    /// </summary>
    Task<bool> HasAnyForPrinterModelAsync(Guid printerModelId, CancellationToken ct = default);
}
