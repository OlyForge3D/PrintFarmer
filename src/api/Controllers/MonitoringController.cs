using System.Security.Claims;
using Farm.Infrastructure.Services.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/monitoring")]
public class MonitoringController(
    IMonitoringSessionService sessionService,
    IMonitoringHealthService healthService) : ControllerBase
{
    /// <summary>
    /// Creates a short-lived monitoring session cookie for proxied Grafana/Jaeger access.
    /// </summary>
    [HttpPost("session")]
    [Authorize(Roles = "farm_admin")]
    public IActionResult CreateSession([FromHeader(Name = "X-Forwarded-Proto")] string? forwardedProto = null)
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        var token = sessionService.CreateMonitoringToken(username);

        // Check X-Forwarded-Proto for HTTPS behind reverse proxy (e.g., nginx TLS termination)
        var isSecure = Request.IsHttps;
        if (!isSecure
            && !string.IsNullOrEmpty(forwardedProto)
            && string.Equals(forwardedProto.Split(',')[0].Trim(), "https", StringComparison.OrdinalIgnoreCase))
        {
            isSecure = true;
        }

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromMinutes(15),
        };

        Response.Cookies.Append("pf_monitoring_session", token, cookieOptions);

        return Ok(new
        {
            success = true,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        });
    }

    /// <summary>
    /// Validates the monitoring session cookie. Called by nginx auth_request.
    /// Returns 200 with X-Monitoring-User header on success, 401 on failure.
    /// </summary>
    [HttpGet("verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifySessionAsync()
    {
        var cookie = Request.Cookies["pf_monitoring_session"];
        if (string.IsNullOrEmpty(cookie))
        {
            return Unauthorized();
        }

        var result = await sessionService.ValidateMonitoringTokenAsync(cookie);
        if (!result.IsValid || string.IsNullOrEmpty(result.Username))
        {
            return Unauthorized();
        }

        Response.Headers["X-Monitoring-User"] = result.Username;
        return Ok();
    }

    /// <summary>
    /// Returns availability status of monitoring services (Grafana, Jaeger, Prometheus).
    /// </summary>
    [HttpGet("status")]
    [Authorize(Roles = "farm_admin")]
    public async Task<IActionResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await healthService.GetStatusAsync(cancellationToken);
        return Ok(status);
    }

    /// <summary>
    /// Returns a curated summary of key application metrics from Prometheus.
    /// </summary>
    [HttpGet("metrics/summary")]
    [Authorize(Roles = "farm_admin")]
    public async Task<IActionResult> GetMetricsSummaryAsync(CancellationToken cancellationToken)
    {
        var summary = await healthService.GetMetricsSummaryAsync(cancellationToken);
        return Ok(summary);
    }

    /// <summary>
    /// Streams individual metric results as Server-Sent Events for progressive UI updates.
    /// Each metric is emitted as it resolves from Prometheus, allowing cards to render progressively.
    /// </summary>
    [HttpGet("metrics/stream")]
    [Authorize(Roles = "farm_admin")]
    public async Task StreamMetricsSseAsync(CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };

        await foreach (var evt in healthService.StreamMetricsAsync(cancellationToken))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(evt, jsonOptions);
            await Response.WriteAsync($"event: metric\ndata: {json}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }

        await Response.WriteAsync("event: done\ndata: {}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
