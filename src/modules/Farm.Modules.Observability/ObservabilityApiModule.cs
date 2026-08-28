using Farm.Modules.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Observability;

/// <summary>
/// Vertical-slice module for observability (issue #2045, epic #2019). Owns
/// <see cref="Farm.Modules.Observability.Controllers.NotificationsController"/>,
/// <see cref="Farm.Modules.Observability.Controllers.AttentionController"/>,
/// <see cref="Farm.Modules.Observability.Controllers.StatisticsController"/>,
/// <see cref="Farm.Modules.Observability.Controllers.CorrelationAnalyticsController"/>,
/// <see cref="Farm.Modules.Observability.Controllers.PredictiveAnalyticsController"/>,
/// <see cref="Farm.Modules.Observability.Controllers.MonitoringController"/>,
/// <see cref="Farm.Modules.Observability.Controllers.WebhooksController"/>,
/// <see cref="Farm.Modules.Observability.Controllers.ObicoServerController"/>,
/// <see cref="Farm.Modules.Observability.Controllers.FailureDetectionController"/>,
/// <see cref="Farm.Modules.Observability.Controllers.TasksController"/>,
/// <see cref="Farm.Modules.Observability.Controllers.BackgroundServicesController"/>,
/// <see cref="Farm.Modules.Observability.Controllers.SignalRTestController"/>,
/// <see cref="Farm.Modules.Observability.Controllers.DiagnosticChannelsController"/>, and
/// <see cref="Farm.Modules.Observability.Controllers.ConnectionDiagnosticsController"/>.
/// Also owns the history-seeding/active-external-job-sync background workers
/// (<see cref="Farm.Modules.Observability.Services.Workers.HistorySeedingBackgroundService"/>),
/// the SignalR task broadcaster
/// (<see cref="Farm.Modules.Observability.Services.Tasks.SignalRTaskBroadcaster"/>), and the
/// SignalR connectivity test service
/// (<see cref="Farm.Modules.Observability.Services.SignalR.SignalRTestService"/>).
/// Phase 17 of the Farm.Web.Api decomposition epic (see
/// docs/MODULE_MIGRATION_PATTERN.md). Namespaces were renamed from Farm.Web.Api.* to
/// Farm.Modules.Observability.* by Phase 19 (issue #2047), completing the
/// move-first-rename-last strategy. The
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
