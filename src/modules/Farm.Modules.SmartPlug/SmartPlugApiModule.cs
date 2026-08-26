using Farm.Modules.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.SmartPlug;

/// <summary>
/// Vertical-slice module for smart-plug electricity monitoring (issue #2036, epic #2019).
/// Owns the <see cref="Farm.Web.Api.Controllers.Admin.AdminPowerMonitorsController"/>
/// controller, the <see cref="Farm.Web.Api.Services.SmartPlug.ISmartPlugProvider"/>
/// implementations, and the <see cref="Farm.Web.Api.Services.PowerMonitor.PowerMonitorPollingService"/>
/// background poller. This is the pilot module for the Farm.Web.Api decomposition epic --
/// the smallest module, chosen to prove the move-a-module pattern before it is repeated for
/// the remaining ten modules (see docs/MODULE_MIGRATION_PATTERN.md).
/// </summary>
public sealed class SmartPlugApiModule : IApiModule
{
    /// <inheritdoc />
    public string Name => "SmartPlug";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        _ = services.AddSingleton<Farm.Web.Api.Services.SmartPlug.ISmartPlugProvider, Farm.Web.Api.Services.SmartPlug.KasaSmartPlugProvider>();
        _ = services.AddSingleton<Farm.Web.Api.Services.SmartPlug.ISmartPlugProvider, Farm.Web.Api.Services.SmartPlug.TasmotaSmartPlugProvider>();
        _ = services.AddSingleton<Farm.Web.Api.Services.SmartPlug.ISmartPlugProvider, Farm.Web.Api.Services.SmartPlug.ShellySmartPlugProvider>();
        _ = services.AddSingleton<Farm.Web.Api.Services.SmartPlug.ISmartPlugProvider, Farm.Web.Api.Services.SmartPlug.HomeAssistantSmartPlugProvider>();

        // Smart plug HTTP client shared by Tasmota, Shelly, and HomeAssistant providers (5s
        // timeout for LAN devices).
        _ = services.AddHttpClient("SmartPlug", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        // Electricity Module - poll enabled PowerMonitor records, persist PowerReading rows,
        // and aggregate KwhUsed for completed print jobs.
        _ = services.AddHostedService<Farm.Web.Api.Services.PowerMonitor.PowerMonitorPollingService>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No minimal-API endpoints -- AdminPowerMonitorsController is attribute-routed and
        // discovered via the ApplicationPart added during module discovery.
    }
}
