using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/systemlogs")]
public class SystemLogsController : ControllerBase
{
    private readonly AppDbContext _db;
    public SystemLogsController(AppDbContext db)
    {
        _db = db;
    }

    // GET: api/systemlogs?correlationId=...&level=...&from=...&to=...&metadata=...
    [HttpGet]
    public async Task<IActionResult> GetLogsAsync(
        [FromQuery] string? correlationId,
        [FromQuery] string? level,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? metadata)
    {
        var query = _db.SystemLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(l => l.CorrelationId == correlationId);
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            query = query.Where(l => l.Level == level);
        }

        if (from.HasValue)
        {
            query = query.Where(l => l.Timestamp >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(l => l.Timestamp <= to.Value);
        }

        if (!string.IsNullOrWhiteSpace(metadata))
        {
            query = query.Where(l => l.Metadata != null && l.Metadata.Contains(metadata));
        }

        var logs = await query.OrderByDescending(l => l.Timestamp).Take(500).ToListAsync();
        return Ok(logs);
    }

    // GET: api/systemlogs/export?correlationId=...&level=...&from=...&to=...&metadata=...
    [HttpGet("export")]
    public async Task<IActionResult> ExportLogsAsync(
        [FromQuery] string? correlationId,
        [FromQuery] string? level,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? metadata)
    {
        var query = _db.SystemLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(l => l.CorrelationId == correlationId);
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            query = query.Where(l => l.Level == level);
        }

        if (from.HasValue)
        {
            query = query.Where(l => l.Timestamp >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(l => l.Timestamp <= to.Value);
        }

        if (!string.IsNullOrWhiteSpace(metadata))
        {
            query = query.Where(l => l.Metadata != null && l.Metadata.Contains(metadata));
        }

        var logs = await query.OrderByDescending(l => l.Timestamp).ToListAsync();
        var json = System.Text.Json.JsonSerializer.Serialize(logs);
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"systemlogs_{DateTime.UtcNow:yyyyMMddHHmmss}.json");
    }
}
