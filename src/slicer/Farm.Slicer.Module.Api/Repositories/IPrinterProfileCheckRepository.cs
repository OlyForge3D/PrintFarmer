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
}
