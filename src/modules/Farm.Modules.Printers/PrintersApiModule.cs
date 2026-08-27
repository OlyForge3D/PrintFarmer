using Farm.Modules.Abstractions;
using FluentValidation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Printers;

/// <summary>
/// Vertical-slice module for printers (issue #2046, epic #2019). Owns
/// <see cref="Farm.Web.Api.Controllers.PrintersController"/>,
/// <see cref="Farm.Web.Api.Controllers.PrinterGroupsController"/>,
/// <see cref="Farm.Web.Api.Controllers.CatalogController"/>,
/// <see cref="Farm.Web.Api.Controllers.Admin.CatalogUpdateController"/>,
/// <see cref="Farm.Web.Api.Controllers.BedTypeController"/>,
/// <see cref="Farm.Web.Api.Controllers.InternalDiscoveryEventsController"/>,
/// <see cref="Farm.Web.Api.Controllers.LocationsController"/>, and
/// <see cref="Farm.Web.Api.Controllers.FilaManController"/>.
/// Also owns the catalog facade
/// (<see cref="Farm.Web.Api.Services.Catalog.ICatalogService"/>), the
/// discovery proxy and its authenticator
/// (<see cref="Farm.Web.Api.Services.Discovery.DiscoveryProxyService"/>,
/// <see cref="Farm.Web.Api.Services.Discovery.DiscoveryServiceAuthenticator"/>),
/// and the SignalR printer status broadcaster
/// (<see cref="Farm.Web.Api.Services.Printers.SignalRPrinterStatusBroadcaster"/>).
/// Phase 18 of the Farm.Web.Api decomposition epic (see
/// docs/MODULE_MIGRATION_PATTERN.md). Namespaces are intentionally unchanged
/// from their prior Farm.Web.Api location (move-first-rename-last).
/// <see cref="Farm.Web.Api.Controllers.PrintersController"/> moves as-is with
/// no internal decomposition (out of scope for this phase; a follow-up
/// epic). The underlying printer/catalog/discovery services remain
/// registered by Farm.Infrastructure / Farm.Web.Api host-wide DI and are not
/// part of this move.
/// </summary>
public sealed class PrintersApiModule : IApiModule
{
    /// <inheritdoc />
    public string Name => "Printers";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // CreatePrinterValidator moved into this assembly along with
        // PrintersController. Program.cs's AddValidatorsFromAssemblyContaining<Program>()
        // only scans the Farm.Web.Api assembly, so this module must register
        // its own validators to keep IValidator<CreatePrinterFromDiscoveryDto>
        // resolvable via DI.
        _ = services.AddValidatorsFromAssemblyContaining<PrintersApiModule>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No minimal-API endpoints or SignalR hubs -- all printers/catalog/
        // discovery endpoints are attribute-routed controllers discovered via
        // the ApplicationPart added during module discovery.
    }
}
