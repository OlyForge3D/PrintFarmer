using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.SystemLogs;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/systemlogs")]
public class SystemLogsController(Services.SystemLogs.ISystemLogService systemLogService) : ControllerBase
{
    private readonly Services.SystemLogs.ISystemLogService _service = systemLogService;

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
        
        var filter = LuceneLogQueryParser.Parse(q);
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
}
