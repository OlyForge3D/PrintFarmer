using Farm.Modules.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Inventory;

/// <summary>
/// Vertical-slice module for inventory (issue #2044, epic #2019). Owns
/// <see cref="Farm.Modules.Inventory.Controllers.SpoolmanController"/>,
/// <see cref="Farm.Modules.Inventory.Controllers.FilamentTypeController"/>,
/// <see cref="Farm.Modules.Inventory.Controllers.BinsController"/>,
/// <see cref="Farm.Modules.Inventory.Controllers.PartsInventoryController"/>,
/// <see cref="Farm.Modules.Inventory.Controllers.MaterialClusterController"/>,
/// <see cref="Farm.Modules.Inventory.Controllers.TagsController"/>,
/// <see cref="Farm.Modules.Inventory.Controllers.CustomFieldsController"/>,
/// <see cref="Farm.Modules.Inventory.Controllers.ModelCollectionsController"/>,
/// <see cref="Farm.Modules.Inventory.Controllers.PrintProjectsController"/>,
/// <see cref="Farm.Modules.Inventory.Controllers.FilamentCoverageController"/>, and
/// <see cref="Farm.Modules.Inventory.Controllers.FilamentFallbackGroupsController"/>.
/// Phase 16 of the Farm.Web.Api decomposition epic (see
/// docs/MODULE_MIGRATION_PATTERN.md). Namespaces were renamed from Farm.Web.Api.* to
/// Farm.Modules.Inventory.* by Phase 19 (issue #2047), completing the
/// move-first-rename-last strategy. The
/// underlying inventory services remain registered by Farm.Infrastructure /
/// Farm.Web.Api host-wide DI and are not part of this move.
/// </summary>
public sealed class InventoryApiModule : IApiModule
{
    /// <inheritdoc />
    public string Name => "Inventory";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // No module-local service registrations moved in this phase. All
        // controller dependencies are already registered by the host's
        // Farm.Infrastructure-backed DI wiring.
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No minimal-API endpoints or SignalR hubs -- all inventory endpoints
        // are attribute-routed controllers discovered via the ApplicationPart
        // added during module discovery.
    }
}
