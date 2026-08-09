using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Repository for querying and mutating <see cref="ProcessProfile"/> entities.
/// Process profiles contain quality/speed settings (layer height, infill, speeds, supports).
/// Supports deduplication via hash and user/system scoping.
/// </summary>
public interface IProcessProfileRepository
{
    /// <summary>Gets a process profile by its unique identifier.</summary>
    Task<ProcessProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets process profiles accessible to a specific user.</summary>
    Task<IReadOnlyList<ProcessProfile>> GetByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Gets all public process profiles.</summary>
    Task<IReadOnlyList<ProcessProfile>> GetPublicAsync(CancellationToken ct = default);

    /// <summary>Gets process profiles for a slicer engine with optional user filtering.</summary>
    Task<IReadOnlyList<ProcessProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem, Guid? userId = null, CancellationToken ct = default);

    /// <summary>Gets the default process profile for a slicer engine and optional user.</summary>
    Task<ProcessProfile?> GetDefaultAsync(SlicerType engine, Guid? userId = null, CancellationToken ct = default);

    /// <summary>Gets a process profile by its content hash.</summary>
    Task<ProcessProfile?> GetByHashAsync(string hash, CancellationToken ct = default);

    /// <summary>Adds a new process profile.</summary>
    Task AddAsync(ProcessProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Adds a batch of process profiles in a single <c>SaveChangesAsync</c> call.
    /// Used to avoid one DB commit per profile during bulk imports/seeding.
    /// </summary>
    /// <param name="profiles">The profiles to add.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of profiles added.</returns>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">
    /// Thrown if the batch fails to save (e.g. a unique-hash constraint violation). Callers
    /// should catch this and fall back to per-profile <see cref="AddAsync"/> to isolate the
    /// failing profile from the rest of the batch.
    /// </exception>
    Task<int> AddRangeAsync(IEnumerable<ProcessProfile> profiles, CancellationToken ct = default);

    /// <summary>
    /// Batch-checks which of the given content hashes already exist as system profiles for the
    /// given slicer engine. Equivalent to calling <see cref="GetByHashAsync"/> for each hash and
    /// checking <c>IsSystem &amp;&amp; SlicerType == engine</c>, but in a single query.
    /// </summary>
    /// <param name="hashes">The candidate content hashes to check.</param>
    /// <param name="engine">The slicer engine type.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The subset of <paramref name="hashes"/> that already exist as matching system profiles.</returns>
    Task<HashSet<string>> GetExistingSystemHashesAsync(IEnumerable<string> hashes, SlicerType engine, CancellationToken ct = default);

    /// <summary>Updates an existing process profile.</summary>
    Task UpdateAsync(ProcessProfile profile, CancellationToken ct = default);

    /// <summary>Deletes a process profile.</summary>
    Task DeleteAsync(ProcessProfile profile, CancellationToken ct = default);

    /// <summary>Sets a profile as the default for its engine and optional user scope.</summary>
    Task SetDefaultAsync(ProcessProfile profile, Guid? userId, CancellationToken ct = default);

    /// <summary>Adds a profile if hash not found, otherwise updates existing metadata if permitted.</summary>
    /// <param name="imported">The profile to import.</param>
    /// <param name="allowSystemOverride">Whether to allow overriding system profiles.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The added or updated profile.</returns>
    Task<ProcessProfile> AddOrUpdateFromImportAsync(ProcessProfile imported, bool allowSystemOverride, CancellationToken ct = default);

    /// <summary>Gets all system OrcaSlicer process profiles.</summary>
    Task<IReadOnlyList<ProcessProfile>> GetSystemOrcaProfilesAsync(CancellationToken ct = default);

    /// <summary>Deletes all system profiles for a given slicer engine.</summary>
    /// <returns>Number of profiles deleted.</returns>
    Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default);
}
