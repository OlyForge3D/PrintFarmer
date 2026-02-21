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
    public IActionResult CreateSession()
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
            && Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto)
            && string.Equals(proto.ToString().Split(',')[0].Trim(), "https", StringComparison.OrdinalIgnoreCase))
        {
            isSecure = true;
        }

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = isSecure,
            SameSite = SameSiteMode.Strict,
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
}
