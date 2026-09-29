using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Repository for querying and mutating <see cref="MachineProfile"/> entities from OrcaSlicer.
/// </summary>
public interface IMachineProfileRepository
{
    /// <summary>Gets a machine profile by its unique identifier.</summary>
    /// <param name="id">The profile identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MachineProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets machine profiles for a slicer engine with optional user filtering.</summary>
    /// <param name="engine">The slicer engine type.</param>
    /// <param name="includeSystem">Whether to include system profiles.</param>
    /// <param name="userId">Optional user ID; when set, only profiles the user owns, public profiles, and system profiles are returned.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MachineProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem = true, Guid? userId = null, CancellationToken ct = default);

    /// <summary>Gets a machine profile by its content hash.</summary>
    /// <param name="hash">The profile content hash.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MachineProfile?> GetByHashAsync(string hash, CancellationToken ct = default);

    /// <summary>
    /// Returns whether <paramref name="ownerUserId"/> already owns a machine profile with the given
    /// identity, the key of the per-owner unique index (#3198). Reads only the caller's own rows,
    /// so it never reveals another user's private profile names. Name comparison follows the
    /// database collation, matching the index it mirrors.
    /// </summary>
    /// <param name="ownerUserId">The owning user.</param>
    /// <param name="name">The profile name.</param>
    /// <param name="slicerType">The slicer engine type.</param>
    /// <param name="excludeProfileId">Optional profile to ignore (the row being renamed).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> OwnerHasNameAsync(Guid ownerUserId, string name, SlicerType slicerType, Guid? excludeProfileId = null, CancellationToken ct = default);

    /// <summary>Adds a new machine profile.</summary>
    /// <param name="profile">The profile to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(MachineProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Adds a batch of machine profiles in a single <c>SaveChangesAsync</c> call.
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
    Task<int> AddRangeAsync(IEnumerable<MachineProfile> profiles, CancellationToken ct = default);

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

    /// <summary>Updates an existing machine profile.</summary>
    /// <param name="profile">The profile to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(MachineProfile profile, CancellationToken ct = default);

    /// <summary>Deletes a machine profile.</summary>
    /// <param name="profile">The profile to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(MachineProfile profile, CancellationToken ct = default);

    /// <summary>Deletes all system profiles for a given slicer engine.</summary>
    /// <param name="engine">The slicer engine type.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of profiles deleted.</returns>
    Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default);

    /// <summary>
    /// Checks if any machine profiles exist for the given printer model.
    /// Soft reference — no FK constraint; printer model lives in core domain.
    /// </summary>
    /// <param name="printerModelId">The printer model identifier (soft reference).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> HasAnyForPrinterModelAsync(Guid printerModelId, CancellationToken ct = default);
}
