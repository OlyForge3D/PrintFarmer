using Farm.Infrastructure.Services.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Runtime diagnostic channel management.
/// Enables/disables verbose logging for specific subsystems without restart.
/// </summary>
[ApiController]
[Route("api/diagnostics/channels")]
[Authorize(Roles = "farm_admin")]
public class DiagnosticChannelsController(IDiagnosticChannelService channelService) : ControllerBase
{
    private readonly IDiagnosticChannelService _channels = channelService;

    /// <summary>
    /// List all diagnostic channels and their current state.
    /// </summary>
    [HttpGet]
    public IActionResult GetAll()
    {
        return Ok(_channels.GetAllChannels());
    }

    /// <summary>
    /// Enable a diagnostic channel. Verbose logging will be emitted for that area.
    /// </summary>
    /// <param name="name">Channel name (e.g., "orphaned-job-sync").</param>
    /// <param name="request">Optional configuration for auto-expiry.</param>
    [HttpPost("{name}/enable")]
    public IActionResult Enable(string name, [FromBody] EnableChannelRequest? request = null)
    {
        TimeSpan? expiry = request?.AutoDisableAfterMinutes is > 0
            ? TimeSpan.FromMinutes(request.AutoDisableAfterMinutes.Value)
            : null;

        _channels.Enable(name, expiry);
        return Ok(_channels.GetAllChannels());
    }

    /// <summary>
    /// Disable a diagnostic channel.
    /// </summary>
    /// <param name="name">Channel name.</param>
    [HttpPost("{name}/disable")]
    public IActionResult Disable(string name)
    {
        _channels.Disable(name);
        return Ok(_channels.GetAllChannels());
    }
}

/// <summary>
/// Request body for enabling a diagnostic channel.
/// </summary>
public class EnableChannelRequest
{
    /// <summary>
    /// If set, the channel will automatically disable after this many minutes.
    /// Useful for time-boxed debugging sessions.
    /// </summary>
    public int? AutoDisableAfterMinutes { get; set; }
}
