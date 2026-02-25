using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.SystemLogs;
using Farm.Infrastructure.Services.SystemLogs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/systemlogs")]
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

    [HttpGet("query")]
    public async Task<IActionResult> QueryLogsAsync(
        [FromQuery] string? q,
        CancellationToken ct)
    {
        // Get all logs and apply Lucene query filter
        IReadOnlyList<SystemLog> allLogs = await _service.QueryAllLogsAsync(null, null, null, null, null, ct);

        Func<SystemLog, bool> filter = LuceneLogQueryParser.Parse(q);
        var filteredLogs = allLogs.Where(filter).ToList();

        return Ok(filteredLogs);
    }

    [HttpGet("export")]
    public async Task<IActionResult> ExportLogsAsync(
        [FromQuery] string? correlationId,
        [FromQuery] string? level,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? metadata,
        CancellationToken ct)
    {
        IReadOnlyList<SystemLog> logs = await _service.QueryAllLogsAsync(correlationId, level, from, to, metadata, ct);
        string json = JsonSerializer.Serialize(logs);
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"systemlogs_{DateTime.UtcNow:yyyyMMddHHmmss}.json");
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStatsAsync(CancellationToken ct)
    {
        int rowCount = await _repository.GetRowCountAsync(ct);
        return Ok(new { rowCount });
    }
}
