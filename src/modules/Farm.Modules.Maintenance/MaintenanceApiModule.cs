using Farm.Modules.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Maintenance;

/// <summary>
/// Vertical-slice module for printer maintenance (issue #2037, epic #2019). Owns the five
/// <c>Maintenance*Controller</c> attribute-routed controllers, the
/// <see cref="Farm.Modules.Maintenance.Services.Maintenance.MaintenanceAlertEngine"/> and
/// <see cref="Farm.Modules.Maintenance.Services.Maintenance.MaintenanceResolutionNotifier"/> services, the
/// <see cref="Farm.Modules.Maintenance.Services.Maintenance.PrintStatsSyncHostedService"/> background
/// sync job, and the <see cref="Farm.Modules.Maintenance.Hubs.MaintenanceHub"/> SignalR hub. This is the
/// first module in the decomposition epic to move a hub out of the host -- see
/// <see cref="MapEndpoints"/> -- proving <c>IEndpointRouteBuilder.MapHub</c> works the same
/// way from a module as it does from <c>Program.cs</c> (see docs/MODULE_MIGRATION_PATTERN.md).
/// </summary>
public sealed class MaintenanceApiModule : IApiModule
{
    /// <inheritdoc />
    public string Name => "Maintenance";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        _ = services.AddScoped<Farm.Infrastructure.Services.Maintenance.IMaintenanceAlertService, Farm.Modules.Maintenance.Services.Maintenance.MaintenanceAlertEngine>();
        _ = services.AddScoped<Farm.Infrastructure.Services.Maintenance.IMaintenanceResolutionNotifier, Farm.Modules.Maintenance.Services.Maintenance.MaintenanceResolutionNotifier>();

        // Periodically syncs printer print-stats totals into maintenance component/task
        // wear tracking (toolhead hours attribution).
        _ = services.Configure<Farm.Infrastructure.Services.Maintenance.PrintStatsSyncSettings>(configuration.GetSection(Farm.Infrastructure.Services.Maintenance.PrintStatsSyncSettings.SectionName));
        _ = services.AddHostedService<Farm.Modules.Maintenance.Services.Maintenance.PrintStatsSyncHostedService>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Maintenance*Controller endpoints are attribute-routed and discovered via the
        // ApplicationPart added during module discovery -- only the hub needs an explicit map.
        endpoints.MapHub<Farm.Modules.Maintenance.Hubs.MaintenanceHub>("/hubs/maintenance");
    }
}
