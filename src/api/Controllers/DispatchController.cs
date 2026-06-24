using Farm.Infrastructure.Services.Queue.Dispatch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Dispatch dashboard endpoints: queue status and dispatch history.
/// </summary>
[ApiController]
[Route("api/dispatch")]
[Tags("Dispatch Dashboard")]
[Authorize]
public class DispatchController(
    IBatchDispatchService batchDispatchService) : ControllerBase
{
    /// <summary>
    /// Returns current queue status: pending unassigned jobs, per-printer queue depth,
    /// idle/busy printer counts, and 24-hour dispatch statistics.
    /// </summary>
    [HttpGet("queue-status")]
    [ProducesResponseType(typeof(DispatchQueueStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueueStatusAsync(CancellationToken ct)
    {
        DispatchQueueStatusDto status = await batchDispatchService.GetQueueStatusAsync(ct);
        return Ok(status);
    }

    /// <summary>
    /// Returns paginated dispatch history log entries, most recent first.
    /// </summary>
    /// <param name="page">1-based page number (default: 1).</param>
    /// <param name="pageSize">Items per page (default: 20, max: 100).</param>
    /// <param name="dateFrom">Optional inclusive lower bound for log entries (UTC).</param>
    /// <param name="dateTo">Optional inclusive upper bound for log entries (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("history")]
    [ProducesResponseType(typeof(DispatchHistoryPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDispatchHistoryAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken ct = default)
    {
        (List<DispatchHistoryDto> items, int totalCount) = await batchDispatchService.GetDispatchHistoryAsync(page, pageSize, dateFrom, dateTo, ct);

        return Ok(new DispatchHistoryPageDto
        {
            Items = items,
            TotalCount = totalCount,
            Page = Math.Max(1, page),
            PageSize = Math.Clamp(pageSize, 1, 100),
        });
    }
}

/// <summary>
/// Paginated dispatch history response.
/// </summary>
public class DispatchHistoryPageDto
{
    public List<DispatchHistoryDto> Items { get; set; } = [];

    public int TotalCount { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }
}
