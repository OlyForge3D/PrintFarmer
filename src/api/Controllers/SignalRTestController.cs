using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for testing SignalR connectivity and functionality
/// </summary>
[ApiController]
[Route("api/signalr-test")]
public class SignalRTestController(
    Services.SignalR.ISignalRTestService testService,
    IUnifiedLoggingService logger) : ControllerBase
{
    /// <summary>
    /// Test endpoint to verify SignalR hub can send messages
    /// </summary>
    /// <param name="request">The test message request containing connection details and message.</param>
    [HttpPost("send-test-message")]
    public async Task<IActionResult> SendTestMessageAsync([FromBody] SignalRTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await testService.SendTestMessageAsync(request.ConnectionId, request.GroupName, request.Message, HttpContext?.RequestAborted ?? CancellationToken.None);
            return Ok(new { Success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send SignalR test message");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Test discovery group functionality specifically
    /// </summary>
    /// <param name="request">The discovery test request containing session and delay configuration.</param>
    [HttpPost("test-discovery-group")]
    public async Task<IActionResult> TestDiscoveryGroupAsync([FromBody] DiscoveryTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await testService.TestDiscoveryGroupAsync(request.SessionId, request.DelayBetweenMessages, HttpContext?.RequestAborted ?? CancellationToken.None);
            return Ok(new { Success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to test discovery group functionality");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Error = ex.Message });
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
            object stats = testService.GetConnectionStats();
            return Ok(stats);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get SignalR connection stats");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Error = ex.Message });
        }
    }
}
