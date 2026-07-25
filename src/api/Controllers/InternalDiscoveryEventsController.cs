using Farm.Infrastructure;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Discovery;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Webhooks;
using Farm.Web.Api.Services.Discovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Receives authenticated discovery-service events and publishes redacted client events.
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/internal/discovery")]
public sealed class InternalDiscoveryEventsController(
    DiscoveryServiceAuthenticator authenticator,
    IDiscoverySessionRegistry sessions,
    IDiscoveryProgressCache progressCache,
    IHubContext<PrinterHub> hubContext,
    IWebhookService webhookService) : ControllerBase
{
    private readonly DiscoveryServiceAuthenticator _authenticator = authenticator;
    private readonly IDiscoverySessionRegistry _sessions = sessions;
    private readonly IDiscoveryProgressCache _progressCache = progressCache;
    private readonly IHubContext<PrinterHub> _hubContext = hubContext;
    private readonly IWebhookService _webhookService = webhookService;

    /// <summary>Publishes a redacted progress update for a live discovery session.</summary>
    [HttpPost("progress")]
    public async Task<IActionResult> ProgressAsync(
        [FromBody] InternalDiscoveryProgressDto progress,
        CancellationToken ct)
    {
        ObjectResult? authenticationFailure = GetAuthenticationFailure();
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        if (!_sessions.SessionExists(progress.SessionId))
        {
            return DiscoveryProblem(
                StatusCodes.Status404NotFound,
                "Discovery session not found",
                "resource_not_found");
        }

        var redacted = new DiscoveryProgressDto(
            progress.SessionId,
            string.Empty,
            string.Empty,
            progress.TotalIps,
            progress.ScannedIps,
            progress.PrintersFound,
            progress.PrintersExcluded,
            progress.ProgressPercentage,
            progress.Status,
            progress.Message,
            null,
            progress.AutoDetectedNetworks);

        _progressCache.Set(progress.SessionId, redacted);
        await _hubContext.Clients.Group(DiscoveryGroup(progress.SessionId))
            .SendAsync("discoveryprogress", redacted, ct);
        return NoContent();
    }

    /// <summary>Stores a network target server-side and publishes its redacted summary.</summary>
    [HttpPost("printer-found")]
    public async Task<IActionResult> PrinterFoundAsync(
        [FromBody] InternalDiscoveryPrinterFoundDto found,
        CancellationToken ct)
    {
        ObjectResult? authenticationFailure = GetAuthenticationFailure();
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        DiscoveryPrinterFoundDto? redacted = _sessions.StorePrinter(found);
        if (redacted is null)
        {
            return DiscoveryProblem(
                StatusCodes.Status404NotFound,
                "Discovery session not found",
                "resource_not_found");
        }

        await _hubContext.Clients.Group(DiscoveryGroup(found.SessionId))
            .SendAsync("discoveryprinterfound", redacted, ct);
        _webhookService.Enqueue("discovery.printer_found", new
        {
            sessionId = found.SessionId,
            printerName = found.Name,
            backend = found.Backend.ToString(),
        });
        return NoContent();
    }

    /// <summary>Publishes a redacted completion update for a live discovery session.</summary>
    [HttpPost("completed")]
    public async Task<IActionResult> CompletedAsync(
        [FromBody] InternalDiscoveryCompletedDto completed,
        CancellationToken ct)
    {
        ObjectResult? authenticationFailure = GetAuthenticationFailure();
        if (authenticationFailure is not null)
        {
            return authenticationFailure;
        }

        if (!_sessions.SessionExists(completed.SessionId))
        {
            return DiscoveryProblem(
                StatusCodes.Status404NotFound,
                "Discovery session not found",
                "resource_not_found");
        }

        var redacted = new DiscoveryCompletedDto(
            completed.SessionId,
            completed.TotalPrintersFound,
            completed.TotalPrintersExcluded,
            completed.Duration,
            completed.WasCancelled,
            null,
            completed.AutoDetectedNetworks);

        await _hubContext.Clients.Group(DiscoveryGroup(completed.SessionId))
            .SendAsync("discoverycompleted", redacted, ct);
        _webhookService.Enqueue("discovery.completed", new
        {
            sessionId = completed.SessionId,
            totalPrintersFound = completed.TotalPrintersFound,
            totalPrintersExcluded = completed.TotalPrintersExcluded,
            wasCancelled = completed.WasCancelled,
        });
        return NoContent();
    }

    private ObjectResult? GetAuthenticationFailure()
    {
        if (!_authenticator.IsConfigured)
        {
            return DiscoveryProblem(
                StatusCodes.Status503ServiceUnavailable,
                "Authentication service unavailable",
                "authentication_unavailable");
        }

        return _authenticator.IsAuthorized(Request)
            ? null
            : DiscoveryProblem(
                StatusCodes.Status401Unauthorized,
                "Authentication required",
                "authentication_required");
    }

    private ObjectResult DiscoveryProblem(int status, string title, string code)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://printfarmer.dev/problems/{code}",
            Instance = Request.Path,
        };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }

    private static string DiscoveryGroup(string sessionId) => $"discovery-{sessionId}";
}
