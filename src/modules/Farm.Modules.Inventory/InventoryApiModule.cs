using Farm.Modules.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Inventory;

/// <summary>
/// Vertical-slice module for inventory (issue #2044, epic #2019). Owns
/// <see cref="Farm.Web.Api.Controllers.SpoolmanController"/>,
/// <see cref="Farm.Web.Api.Controllers.FilamentTypeController"/>,
/// <see cref="Farm.Web.Api.Controllers.BinsController"/>,
/// <see cref="Farm.Web.Api.Controllers.PartsInventoryController"/>,
/// <see cref="Farm.Web.Api.Controllers.MaterialClusterController"/>,
/// <see cref="Farm.Web.Api.Controllers.TagsController"/>,
/// <see cref="Farm.Web.Api.Controllers.CustomFieldsController"/>,
/// <see cref="Farm.Web.Api.Controllers.ModelCollectionsController"/>,
/// <see cref="Farm.Web.Api.Controllers.PrintProjectsController"/>,
/// <see cref="Farm.Web.Api.Controllers.FilamentCoverageController"/>, and
/// <see cref="Farm.Web.Api.Controllers.FilamentFallbackGroupsController"/>.
/// Phase 16 of the Farm.Web.Api decomposition epic (see
/// docs/MODULE_MIGRATION_PATTERN.md). Namespaces are intentionally unchanged
/// from their prior Farm.Web.Api location (move-first-rename-last). The
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
