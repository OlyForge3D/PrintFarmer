using System;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.SystemLogs;
using Farm.Infrastructure.Services.SystemLogs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/system-logs")]
[Authorize(Roles = "farm_admin")]
public class SystemLogsController(ISystemLogService systemLogService, ISystemLogRepository systemLogRepository) : ControllerBase
{
    private readonly ISystemLogService _service = systemLogService;
    private readonly ISystemLogRepository _repository = systemLogRepository;

    [HttpGet]
    public async Task<IActionResult> GetLogsAsync(
        [FromQuery] string? correlationId,
        [FromQuery] string? level,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? metadata,
        CancellationToken ct)
    {
        IReadOnlyList<SystemLog> logs = await _service.QueryLogsAsync(correlationId, level, from, to, metadata, ct);
        return Ok(logs);
    }

    [HttpGet("export")]
    public async Task ExportLogsAsync(
        [FromQuery] string? correlationId,
        [FromQuery] string? level,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? metadata,
        CancellationToken ct)
    {
        Response.ContentType = "application/json";
        Response.Headers.ContentDisposition = $"attachment; filename=\"systemlogs_{DateTime.UtcNow:yyyyMMddHHmmss}.json\"";

        IAsyncEnumerable<SystemLog> logs = _service.QueryAllLogsAsync(correlationId, level, from, to, metadata, ct);
        await JsonSerializer.SerializeAsync(Response.Body, logs, cancellationToken: ct);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStatsAsync(CancellationToken ct)
    {
        int rowCount = await _repository.GetRowCountAsync(ct);
        return Ok(new { rowCount });
    }
}
