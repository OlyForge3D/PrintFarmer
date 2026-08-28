using Farm.Modules.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Administration;

/// <summary>
/// Vertical-slice module for administration: the Admin Control Center overview aggregation,
/// admin data export/import, Home Assistant, and Telegram admin controllers, plus the
/// settings and unified-settings controllers (issue #2042, epic #2019). Owns
/// <see cref="Farm.Modules.Administration.Controllers.Admin.AdminOverviewController"/>,
/// <see cref="Farm.Modules.Administration.Controllers.Admin.AdminDataController"/>,
/// <see cref="Farm.Modules.Administration.Controllers.Admin.AdminHomeAssistantController"/>,
/// <see cref="Farm.Modules.Administration.Controllers.Admin.AdminTelegramController"/>,
/// <see cref="Farm.Modules.Administration.Controllers.SettingsController"/>, and
/// <see cref="Farm.Modules.Administration.Controllers.UnifiedSettingsController"/>, plus the overview
/// aggregation service and the discovery heartbeat monitor hosted service. Phase 14 of the
/// Farm.Web.Api decomposition epic (see docs/MODULE_MIGRATION_PATTERN.md). Namespaces were
/// renamed from Farm.Web.Api.* to Farm.Modules.Administration.* by Phase 19 (issue #2047),
/// completing the move-first-rename-last strategy.
/// <see cref="Farm.Modules.Administration.Services.Workers.DiscoveryHeartbeatMonitorService"/> is not
/// explicitly named by issue #2042, but moved alongside
/// <see cref="Farm.Modules.Administration.Controllers.UnifiedSettingsController"/> (its only consumer) to
/// avoid a circular Farm.Web.Api -&gt; Farm.Modules.Administration -&gt; Farm.Web.Api dependency.
/// </summary>
public sealed class AdministrationApiModule : IApiModule
{
    /// <inheritdoc />
    public string Name => "Administration";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Admin Control Center overview aggregation (issue #933). Composes the existing
        // health-check pipeline; does not run its own probes.
        _ = services.AddScoped<
            Farm.Modules.Administration.Services.Admin.IAdminOverviewService,
            Farm.Modules.Administration.Services.Admin.AdminOverviewService>();

        // Discovery heartbeat monitor - tracks external discovery microservice status.
        // Disabled under TEST_DISABLE_BACKGROUND_SERVICES, mirroring the guard the service
        // was registered under in ServiceCollectionExtensions.RegisterBackgroundServices
        // before this move.
        if (!ShouldDisableBackgroundServices(configuration))
        {
            _ = services.AddSingleton<Farm.Modules.Administration.Services.Workers.DiscoveryHeartbeatMonitorService>();
            _ = services.AddHostedService(sp =>
                sp.GetRequiredService<Farm.Modules.Administration.Services.Workers.DiscoveryHeartbeatMonitorService>());
        }
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No minimal-API endpoints or SignalR hubs -- all six controllers are attribute-routed
        // and discovered via the ApplicationPart added during module discovery.
    }

    private static bool ShouldDisableBackgroundServices(IConfiguration configuration)
    {
        string? value = configuration["TEST_DISABLE_BACKGROUND_SERVICES"];
        return !string.IsNullOrEmpty(value)
            && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1");
    }
}
