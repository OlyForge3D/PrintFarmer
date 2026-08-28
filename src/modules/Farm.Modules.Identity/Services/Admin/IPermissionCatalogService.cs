using Farm.Infrastructure.Dtos;

namespace Farm.Modules.Identity.Services.Admin;

/// <summary>
/// Derives the permission catalog from routed endpoint metadata rather than a hardcoded
/// list, so the admin UI's permission matrix (#1455) always reflects what the API actually
/// enforces.
/// </summary>
public interface IPermissionCatalogService
{
    /// <summary>
    /// Enumerates every <c>[RequirePermission]</c> attribute across routed endpoints,
    /// joins each permission's resource/action against the seeded database catalog for
    /// display metadata, and reports database catalog rows that no endpoint enforces.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The derived permission catalog.</returns>
    Task<PermissionCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default);
}
