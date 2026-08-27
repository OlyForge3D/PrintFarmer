using Farm.Modules.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.PrintQueue;

/// <summary>
/// Vertical-slice module for the print job queue (issue #2040, epic #2019). Owns
/// <see cref="Farm.Modules.PrintQueue.Services.PrintQueue.PrintJobManagementService"/> (moved as-is, no
/// internal decomposition -- that is a follow-up epic), the job queue/analytics/scheduling
/// controllers, the dispatch/auto-dispatch/dispatch-settings/retries controllers, and the
/// slice-to-print bridge controller (<see cref="Farm.Modules.PrintQueue.Controllers.SlicePrintBridgeController"/>).
/// Phase 12 of the Farm.Web.Api decomposition epic, following the pattern established by
/// Phase 7 (#2035) and piloted by Phase 8 (#2036) -- see docs/MODULE_MIGRATION_PATTERN.md.
/// </summary>
public sealed class PrintQueueApiModule : IApiModule
{
    /// <inheritdoc />
    public string Name => "PrintQueue";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Print Job Management Service (renamed from PrintQueueService). The repository
        // registration stays in Farm.Web.Api's FeatureServicesStartup because
        // EfPrintJobManagementRepository has not moved -- only the service implementation did.
        _ = services.AddScoped<Farm.Infrastructure.Services.Interfaces.IPrintJobManagementService, Farm.Modules.PrintQueue.Services.PrintQueue.PrintJobManagementService>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No minimal-API endpoints -- all PrintQueue controllers are attribute-routed and
        // discovered via the ApplicationPart added during module discovery.
    }
}
