using Farm.Infrastructure.Domain;

namespace Farm.Slicer.Module.Api.Repositories;

/// <summary>
/// Narrow, slicer-host-scoped abstraction for reading printer data needed by
/// <see cref="Farm.Slicer.Module.Api.HostedServices.ProfileTaskCheckService"/>.
/// Deliberately avoids depending on <c>Farm.Infrastructure.Services.Printers.IPrintersService</c>,
/// whose implementation pulls in main-API-only infrastructure (backend client factories, camera
/// services, Spoolman, sensitive-data protection, etc.) that is never registered in the
/// standalone slicer-host DI container.
/// </summary>
public interface IPrinterProfileCheckRepository
{
    /// <summary>
    /// Returns all printers so the profile task check can group them by model and detect
    /// which models are missing imported slicer profiles.
    /// </summary>
    Task<List<Printer>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Returns the first registered printer whose <see cref="Printer.TemplateMachineProfileId"/>
    /// references any of the supplied machine-profile identities, or <see langword="null"/> when
    /// none do. Used by profile-family deletion to refuse removing a family whose derived variant
    /// is still bound as a printer's template profile.
    /// </summary>
    /// <param name="machineProfileIds">Candidate machine-profile identities to match against.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Printer?> FindByTemplateMachineProfileIdsAsync(
        IReadOnlyCollection<Guid> machineProfileIds,
        CancellationToken ct);

    /// <summary>
    /// Returns the first registered printer whose <see cref="Printer.ModelId"/> matches the supplied
    /// catalog model identity, or <see langword="null"/> when none do. Used by profile-family deletion
    /// to detect the <em>indirect</em> binding: removing a family's OrcaSlicer alias would strip a
    /// model's last profile coverage, orphaning every printer of that model even though no variant is
    /// bound as a template profile.
    /// </summary>
    /// <param name="modelId">Catalog model identity to match against.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Printer?> FindByModelIdAsync(Guid modelId, CancellationToken ct);
}
