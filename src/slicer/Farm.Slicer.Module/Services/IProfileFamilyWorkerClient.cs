using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Reads resolved profiles from and installs generated bundles into an OrcaSlicer worker.
/// </summary>
public interface IProfileFamilyWorkerClient
{
    /// <summary>Selects a fresh worker for the requested version and downloads its resolved catalog.</summary>
    Task<(ProfileFamilyWorkerTarget Target, AllProfilesResponseDto Catalog)> GetCatalogAsync(
        string sourceManufacturer,
        string? orcaVersion,
        CancellationToken ct);

    /// <summary>
    /// Reports the OrcaSlicer engine version of a fresh online worker, or <see langword="null"/> when
    /// no worker can currently be selected. Unlike <see cref="GetCatalogAsync"/> this never throws for
    /// an absent worker: staleness detection must degrade safely (leave statuses alone) when the farm
    /// cannot currently be reached, rather than guessing a version.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> GetActiveOrcaVersionAsync(CancellationToken ct);

    /// <summary>Atomically writes or replaces one family inside the worker's Custom bundle.</summary>
    Task WriteBundleAsync(
        ProfileFamilyWorkerTarget target,
        ProfileFamilyBundleDto bundle,
        CancellationToken ct);

    /// <summary>
    /// Removes the derived Custom bundle for one family from the worker and triggers its in-process
    /// profile reload. Idempotent: a worker <c>404</c> (bundle already absent) is treated as success.
    /// Selects a fresh online worker (optionally pinned to <paramref name="orcaVersion"/>) exactly as
    /// catalog reads and bundle writes do. Throws <see cref="HttpRequestException"/> when no worker can
    /// be selected or reached, or when the worker returns any other non-success status.
    /// </summary>
    /// <param name="orcaVersion">Version hint to pin worker selection, or <see langword="null"/> for any.</param>
    /// <param name="familyId">Family whose bundle (<c>PrintFarmer-{familyId:N}</c>) is removed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteBundleAsync(
        string? orcaVersion,
        Guid familyId,
        CancellationToken ct);
}
