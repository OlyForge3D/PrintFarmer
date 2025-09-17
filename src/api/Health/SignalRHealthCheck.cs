using Farm.Web.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.Web.Api.Health;

/// <summary>
/// Health check that validates SignalR hub functionality
/// </summary>
public class SignalRHealthCheck(
    IHubContext<PrinterHub> hubContext,
    ILogger<SignalRHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        Dictionary<string, object> checks = new();
        bool overallHealthy = true;
        List<string> issues = new();

        // Test SignalR Hub Context
        try
        {
            // Test if we can access the hub context
            if (hubContext == null)
            {
                overallHealthy = false;
                issues.Add("SignalR Hub context is null");
                checks["SignalRHub"] = new { Status = "Unhealthy", Error = "Hub context is null" };
            }
            else
            {
                // Try to send a test message to a non-existent group (should not fail)
                string testGroupName = $"health-check-{Guid.NewGuid()}";
                await hubContext.Clients.Group(testGroupName).SendAsync("HealthCheck", new
                {
                    Timestamp = DateTime.UtcNow,
                    Message = "Health check test message"
                }, cancellationToken);

                checks["SignalRHub"] = new
                {
                    Status = "Healthy",
                    HubName = nameof(PrinterHub),
                    TestGroupName = testGroupName,
                    Message = "Hub context accessible and can send messages"
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SignalR Hub health check failed");
            overallHealthy = false;
            issues.Add($"SignalR Hub test failed: {ex.Message}");
            checks["SignalRHub"] = new { Status = "Unhealthy", Error = ex.Message };
        }

        // Test discovery group functionality
        try
        {
            if (hubContext != null)
            {
                string testSessionId = Guid.NewGuid().ToString();
                string discoveryGroupName = $"discovery-{testSessionId}";

                // Test sending discovery messages
                await hubContext.Clients.Group(discoveryGroupName).SendAsync("DiscoveryProgress", new
                {
                    SessionId = testSessionId,
                    CurrentIP = "127.0.0.1",
                    ScannedCount = 1,
                    TotalCount = 1,
                    Progress = 100.0
                }, cancellationToken);

                checks["DiscoveryGroups"] = new
                {
                    Status = "Healthy",
                    TestSessionId = testSessionId,
                    GroupName = discoveryGroupName,
                    Message = "Discovery group messaging functional"
                };
            }
            else
            {
                checks["DiscoveryGroups"] = new { Status = "Skipped", Reason = "Hub context unavailable" };
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discovery group health check failed");
            overallHealthy = false;
            issues.Add($"Discovery group test failed: {ex.Message}");
            checks["DiscoveryGroups"] = new { Status = "Unhealthy", Error = ex.Message };
        }

        HealthCheckResult result = new(
            overallHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy,
            description: overallHealthy ? "SignalR fully operational" : string.Join("; ", issues),
            data: checks
        );

        return result;
    }
}
