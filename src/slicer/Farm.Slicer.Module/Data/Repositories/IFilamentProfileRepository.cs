using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Data.Repositories;

/// <summary>
/// Repository for querying and mutating <see cref="FilamentProfile"/> entities from OrcaSlicer.
/// </summary>
public interface IFilamentProfileRepository
{
    /// <summary>Gets a filament profile by its unique identifier.</summary>
    /// <param name="id">The profile identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FilamentProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets filament profiles for a slicer engine with optional user filtering.</summary>
    /// <param name="engine">The slicer engine type.</param>
    /// <param name="includeSystem">Whether to include system profiles.</param>
    /// <param name="userId">Optional user ID to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FilamentProfile>> GetByEngineAsync(SlicerType engine, bool includeSystem = true, Guid? userId = null, CancellationToken ct = default);

    /// <summary>Gets a filament profile by its content hash.</summary>
    /// <param name="hash">The profile content hash.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FilamentProfile?> GetByHashAsync(string hash, CancellationToken ct = default);

    /// <summary>
    /// Gets the filament profile, if any, that was promoted from the given calibration draft
    /// profile (#2180, gap 1). Used as the idempotency check backing
    /// <c>IProfilesService.PromoteCalibrationDraftProfileAsync</c>.
    /// </summary>
    /// <param name="sourceDraftProfileId">The calibration draft profile's stable identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FilamentProfile?> GetByPromotedFromCalibrationDraftProfileIdAsync(Guid sourceDraftProfileId, CancellationToken ct = default);

    /// <summary>Adds a new filament profile.</summary>
    /// <param name="profile">The profile to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(FilamentProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Adds a batch of filament profiles in a single <c>SaveChangesAsync</c> call.
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
    Task<int> AddRangeAsync(IEnumerable<FilamentProfile> profiles, CancellationToken ct = default);

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

    /// <summary>Updates an existing filament profile.</summary>
    /// <param name="profile">The profile to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(FilamentProfile profile, CancellationToken ct = default);

    /// <summary>Deletes a filament profile.</summary>
    /// <param name="profile">The profile to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(FilamentProfile profile, CancellationToken ct = default);

    /// <summary>Deletes all system profiles for a given slicer engine.</summary>
    /// <param name="engine">The slicer engine type.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of profiles deleted.</returns>
    Task<int> DeleteSystemProfilesAsync(SlicerType engine, CancellationToken ct = default);
}
