using Farm.Web.Api.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for testing SignalR connectivity and functionality
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SignalRTestController(
    IHubContext<PrinterHub> hubContext,
    ILogger<SignalRTestController> logger) : ControllerBase
{
    /// <summary>
    /// Test endpoint to verify SignalR hub can send messages
    /// </summary>
    [HttpPost("send-test-message")]
    public async Task<IActionResult> SendTestMessageAsync([FromBody] SignalRTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var testMessage = new
            {
                Timestamp = DateTime.UtcNow,
                TestId = Guid.NewGuid().ToString(),
                Message = request.Message ?? "SignalR connectivity test",
                Source = "API Health Check"
            };

            // Send to specific connection if provided
            if (!string.IsNullOrEmpty(request.ConnectionId))
            {
                await hubContext.Clients.Client(request.ConnectionId).SendAsync("TestMessage", testMessage);
                return Ok(new { Success = true, Target = "Connection", ConnectionId = request.ConnectionId, TestMessage = testMessage });
            }

            // Send to specific group if provided
            if (!string.IsNullOrEmpty(request.GroupName))
            {
                await hubContext.Clients.Group(request.GroupName).SendAsync("TestMessage", testMessage);
                return Ok(new { Success = true, Target = "Group", GroupName = request.GroupName, TestMessage = testMessage });
            }

            // Send to all connected clients
            await hubContext.Clients.All.SendAsync("TestMessage", testMessage);
            return Ok(new { Success = true, Target = "All", TestMessage = testMessage });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send SignalR test message");
            return StatusCode(500, new { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Test discovery group functionality specifically
    /// </summary>
    [HttpPost("test-discovery-group")]
    public async Task<IActionResult> TestDiscoveryGroupAsync([FromBody] DiscoveryTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var sessionId = request.SessionId ?? Guid.NewGuid().ToString();
            var groupName = $"discovery-{sessionId}";

            // Send a series of discovery progress messages
            var testMessages = new[]
            {
                new { SessionId = sessionId, CurrentIP = "10.0.0.1", ScannedCount = 1, TotalCount = 254, Progress = 0.4 },
                new { SessionId = sessionId, CurrentIP = "10.0.0.10", ScannedCount = 10, TotalCount = 254, Progress = 3.9 },
                new { SessionId = sessionId, CurrentIP = "10.0.0.50", ScannedCount = 50, TotalCount = 254, Progress = 19.7 },
                new { SessionId = sessionId, CurrentIP = "10.0.0.100", ScannedCount = 100, TotalCount = 254, Progress = 39.4 },
            };

            foreach (var message in testMessages)
            {
                await hubContext.Clients.Group(groupName).SendAsync("DiscoveryProgress", message);

                // Small delay between messages for realistic simulation
                if (request.DelayBetweenMessages)
                {
                    await Task.Delay(100);
                }
            }

            // Send a test printer found message
            var testPrinter = new
            {
                SessionId = sessionId,
                IpAddress = "10.0.0.123",
                Name = "Test Printer",
                Backend = "Moonraker",
                ServerUrl = "http://10.0.0.123"
            };

            await hubContext.Clients.Group(groupName).SendAsync("DiscoveryPrinterFound", testPrinter);

            // Send completion message
            var completionMessage = new
            {
                SessionId = sessionId,
                TotalScanned = 254,
                PrintersFound = 1,
                Duration = TimeSpan.FromSeconds(10.5)
            };

            await hubContext.Clients.Group(groupName).SendAsync("DiscoveryCompleted", completionMessage);

            return Ok(new
            {
                Success = true,
                SessionId = sessionId,
                GroupName = groupName,
                MessagesSent = testMessages.Length + 2, // progress messages + printer found + completion
                TestPrinter = testPrinter,
                Completion = completionMessage
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to test discovery group functionality");
            return StatusCode(500, new { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Get current SignalR connection statistics
    /// </summary>
    [HttpGet("connection-stats")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public IActionResult GetConnectionStats()
    {
        try
        {
            // Note: Getting exact connection count requires additional setup
            // This is a basic implementation that provides available information

            var stats = new
            {
                Timestamp = DateTime.UtcNow,
                HubName = nameof(PrinterHub),
                AvailableMethods = new[]
                {
                    "PrinterStatusUpdated",
                    "HarvestProgress",
                    "JobQueueUpdated",
                    "DiscoveryProgress",
                    "DiscoveryPrinterFound",
                    "DiscoveryCompleted",
                    "TestMessage"
                },
                HealthStatus = "Hub context available and functional"
            };

            return Ok(stats);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get SignalR connection stats");
            return StatusCode(500, new { Success = false, Error = ex.Message });
        }
    }
}

public class SignalRTestRequest
{
    public string? ConnectionId { get; set; }
    public string? GroupName { get; set; }
    public string? Message { get; set; }
}

public class DiscoveryTestRequest
{
    public string? SessionId { get; set; }
    public bool DelayBetweenMessages { get; set; } = true;
}
