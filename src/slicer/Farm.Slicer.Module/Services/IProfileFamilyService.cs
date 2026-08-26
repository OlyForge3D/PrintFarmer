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

    /// <summary>
    /// Applies an in-place edit to one custom OrcaSlicer family and re-renders it. Supports three
    /// independent facets — rename, family-shared overrides, and nozzle-variant set — plus §5 source
    /// re-bind, each of which is optional and absent-aware (see <see cref="EditProfileFamilyRequestDto"/>).
    /// </summary>
    /// <remarks>
    /// <para>Every accepted edit re-renders, because the derived worker bundle embeds the family name,
    /// shared overrides and variant set. Surviving nozzle variants keep their <c>MachineProfile.Id</c>
    /// so a facet edit never silently orphans a printer template or slice-job reference.</para>
    /// <para><b>Validation-time failures</b> (bad name, forbidden override key, absent source, a nozzle
    /// the source lacks) are detected before any worker or DB mutation, so the family is left exactly
    /// as it was. <b>Install-time failures</b> mark the family <c>Failed</c> and restore the previous
    /// good worker bundle (see <see cref="RenderFamilyAsync"/>).</para>
    /// </remarks>
    /// <param name="familyId">Family identity.</param>
    /// <param name="request">The absent-aware edit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated family in the same read shape as <see cref="GetFamilyAsync"/>.</returns>
    /// <exception cref="ProfileFamilyNotFoundException">Thrown when the family does not exist.</exception>
    /// <exception cref="ProfileFamilyConflictException">Thrown on a rename name/alias collision.</exception>
    /// <exception cref="ProfileFamilyInUseException">Thrown when removing a still-referenced variant.</exception>
    /// <exception cref="ProfileFamilySourceException">Thrown when the source preset cannot be resolved.</exception>
    /// <exception cref="ArgumentException">Thrown when the edit is malformed (e.g. empty nozzle set).</exception>
    /// <exception cref="HttpRequestException">Thrown when the OrcaSlicer worker is unavailable.</exception>
    Task<ProfileFamilySummaryDto> EditFamilyAsync(
        Guid familyId,
        EditProfileFamilyRequestDto request,
        CancellationToken ct);

    /// <summary>
    /// Re-renders one custom OrcaSlicer family against the live worker and reinstalls its bundle.
    /// Idempotent and safe to retry: calling it twice yields the same variant set and never duplicates
    /// or corrupts the bundle. Accepts a <c>Stale</c> or <c>Failed</c> family (recovery) and a
    /// <c>Healthy</c> family (forced re-render).
    /// </summary>
    /// <remarks>
    /// <para><b>Previous-good-bundle preservation.</b> A re-render can only replace the live worker
    /// bundle once the new bundle has been fully rendered in memory; a source/validation failure throws
    /// before any worker mutation, so the previous good bundle is untouched. For the narrower window
    /// where the render succeeds but the worker rejects the install at load (the worker removes the
    /// bundle on a blocking failure), the previous good bundle — rendered from the pre-change persisted
    /// state — is re-installed so the farm is never left worse off. On any failure the family is marked
    /// <c>Failed</c>; because slice-time lookups resolve through the alias and worker bundle rather than
    /// the render status, a <c>Failed</c> family whose bundle was preserved still slices.</para>
    /// </remarks>
    /// <param name="familyId">Family identity.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The re-rendered family in the same read shape as <see cref="GetFamilyAsync"/>.</returns>
    /// <exception cref="ProfileFamilyNotFoundException">Thrown when the family does not exist.</exception>
    /// <exception cref="ProfileFamilySourceException">Thrown when the source preset cannot be resolved.</exception>
    /// <exception cref="HttpRequestException">Thrown when the OrcaSlicer worker is unavailable.</exception>
    Task<ProfileFamilySummaryDto> RenderFamilyAsync(Guid familyId, CancellationToken ct);

    /// <summary>
    /// Re-renders every family whose render status is <c>Stale</c> or <c>Failed</c>, returning one
    /// result per family so a single failure never hides the others. The batch is bounded and one bad
    /// family never aborts the rest.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-family outcomes; failures carry an actionable <c>{code,detail}</c>.</returns>
    Task<IReadOnlyList<ProfileFamilyRenderResultDto>> RenderStaleFamiliesAsync(CancellationToken ct);
}
