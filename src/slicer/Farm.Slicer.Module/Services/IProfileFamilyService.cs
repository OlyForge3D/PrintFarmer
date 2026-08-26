using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Creates and installs farm-wide custom OrcaSlicer profile families.
/// </summary>
public interface IProfileFamilyService
{
    /// <summary>Creates one family and all selected nozzle variants.</summary>
    Task<CloneProfileFamilyResponseDto> CloneFamilyAsync(
        CloneProfileFamilyRequestDto request,
        Guid userId,
        CancellationToken ct);

    /// <summary>
    /// Lists persisted custom OrcaSlicer families (never stock model rows), newest first,
    /// optionally filtered to a single render status.
    /// </summary>
    /// <param name="renderStatus">Optional render-status filter; <see langword="null"/> returns all.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ProfileFamilySummaryDto>> ListFamiliesAsync(
        ProfileFamilyRenderStatus? renderStatus,
        CancellationToken ct);

    /// <summary>
    /// Reads one persisted custom OrcaSlicer family by id.
    /// </summary>
    /// <param name="familyId">Family identity.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The family read model.</returns>
    /// <exception cref="ProfileFamilyNotFoundException">
    /// Thrown when no custom family with that id exists (including when the row is a stock model).
    /// </exception>
    Task<ProfileFamilySummaryDto> GetFamilyAsync(Guid familyId, CancellationToken ct);

    /// <summary>
    /// Deletes one custom OrcaSlicer family: its worker bundle, its OrcaSlicer alias, and all
    /// authoritative DB rows (the family plus every derived variant).
    /// </summary>
    /// <remarks>
    /// <para><b>Ordering (partial-failure safety).</b> The worker bundle is removed first
    /// (idempotent on a worker <c>404</c>), then the OrcaSlicer alias is dropped and the catalog
    /// alias cache invalidated, then the DB rows are deleted inside a single transaction. A worker
    /// failure therefore aborts <em>before</em> any DB or alias mutation, leaving the family fully
    /// listed and usable. This deliberately never produces the forbidden state where the family has
    /// vanished from the API while its worker bundle lingers; the only residual state a late DB
    /// failure can leave is a still-listed family whose bundle is already gone, which is safe to
    /// resolve by re-running the delete.</para>
    /// <para><b>Refusal.</b> Deletion is refused (<see cref="ProfileFamilyInUseException"/>) when a
    /// registered printer's template profile, or a non-terminal slice job, still references one of
    /// the family's variant machine profiles.</para>
    /// </remarks>
    /// <param name="familyId">Family identity.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ProfileFamilyNotFoundException">Thrown when the family does not exist.</exception>
    /// <exception cref="ProfileFamilyInUseException">Thrown when a live reference holds the family.</exception>
    /// <exception cref="HttpRequestException">Thrown when the OrcaSlicer worker is unavailable.</exception>
    Task DeleteFamilyAsync(Guid familyId, CancellationToken ct);
}
