using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Test endpoints for PrusaLink client APIs
/// </summary>
[ApiController]
[Route("api/client-test/prusalink")]
public class PrusaLinkClientTestController : ControllerBase
{
    private readonly ILogger<PrusaLinkClientTestController> _logger;
    private readonly IPrusaLinkClient _prusaLinkClient;

    public PrusaLinkClientTestController(
        ILogger<PrusaLinkClientTestController> logger,
        IPrusaLinkClient prusaLinkClient)
    {
        _logger = logger;
        _prusaLinkClient = prusaLinkClient;
    }

    /// <summary>
    /// Test endpoint to fetch printer status from a PrusaLink instance
    /// </summary>
    [HttpGet("printer-info")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> TestGetPrinterInfoAsync(
        [FromQuery] string serverUrl,
        [FromQuery] string? apiKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return BadRequest("serverUrl is required");
        }

        try
        {
            _logger.LogInformation("Testing PrusaLinkClient.GetStatusAsync with serverUrl={ServerUrl}", serverUrl);
            PrusaStatus status = await _prusaLinkClient.GetStatusAsync(serverUrl, apiKey, ct);
            return Ok(new { success = true, result = status });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing PrusaLinkClient.GetStatusAsync");
            return StatusCode(500, new { success = false, error = ex.Message, stackTrace = ex.StackTrace });
        }
    }

    /// <summary>
    /// Test endpoint for PrusaLinkClient.GetFileListAsync
    /// </summary>
    [HttpGet("files")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> TestGetFileListAsync(
        [FromQuery] string serverUrl,
        [FromQuery] string? apiKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return BadRequest("serverUrl is required");
        }

        try
        {
            _logger.LogInformation("Testing PrusaLinkClient.GetFileListAsync with serverUrl={ServerUrl}", serverUrl);
            string[] files = await _prusaLinkClient.GetFileListAsync(serverUrl, apiKey, ct);
            return Ok(new { success = true, files, count = files.Length });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing PrusaLinkClient.GetFileListAsync");
            return StatusCode(500, new { success = false, error = ex.Message, stackTrace = ex.StackTrace });
        }
    }
}
