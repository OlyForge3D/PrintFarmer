using Farm.Modules.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Observability;

/// <summary>
/// Vertical-slice module for observability (issue #2045, epic #2019). Owns
/// <see cref="Farm.Web.Api.Controllers.NotificationsController"/>,
/// <see cref="Farm.Web.Api.Controllers.AttentionController"/>,
/// <see cref="Farm.Web.Api.Controllers.StatisticsController"/>,
/// <see cref="Farm.Web.Api.Controllers.CorrelationAnalyticsController"/>,
/// <see cref="Farm.Web.Api.Controllers.PredictiveAnalyticsController"/>,
/// <see cref="Farm.Web.Api.Controllers.MonitoringController"/>,
/// <see cref="Farm.Web.Api.Controllers.WebhooksController"/>,
/// <see cref="Farm.Web.Api.Controllers.ObicoServerController"/>,
/// <see cref="Farm.Web.Api.Controllers.FailureDetectionController"/>,
/// <see cref="Farm.Web.Api.Controllers.TasksController"/>,
/// <see cref="Farm.Web.Api.Controllers.BackgroundServicesController"/>,
/// <see cref="Farm.Web.Api.Controllers.SignalRTestController"/>,
/// <see cref="Farm.Web.Api.Controllers.DiagnosticChannelsController"/>, and
/// <see cref="Farm.Web.Api.Controllers.ConnectionDiagnosticsController"/>.
/// Also owns the history-seeding/active-external-job-sync background workers
/// (<see cref="Farm.Web.Api.Services.Workers.HistorySeedingBackgroundService"/>),
/// the SignalR task broadcaster
/// (<see cref="Farm.Web.Api.Services.Tasks.SignalRTaskBroadcaster"/>), and the
/// SignalR connectivity test service
/// (<see cref="Farm.Web.Api.Services.SignalR.SignalRTestService"/>).
/// Phase 17 of the Farm.Web.Api decomposition epic (see
/// docs/MODULE_MIGRATION_PATTERN.md). Namespaces are intentionally unchanged
/// from their prior Farm.Web.Api location (move-first-rename-last). The
/// underlying observability services remain registered by Farm.Infrastructure /
/// Farm.Web.Api host-wide DI and are not part of this move.
/// </summary>
public sealed class ObservabilityApiModule : IApiModule
{
    /// <inheritdoc />
    public string Name => "Observability";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // No module-local service registrations moved in this phase. All
        // controller/worker dependencies are already registered by the host's
        // Farm.Infrastructure-backed DI wiring (see FeatureServicesStartup and
        // BackgroundServicesStartup).
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No minimal-API endpoints or SignalR hubs -- all observability endpoints
        // are attribute-routed controllers discovered via the ApplicationPart
        // added during module discovery.
    }
}
