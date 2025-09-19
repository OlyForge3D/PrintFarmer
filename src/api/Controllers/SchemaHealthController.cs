using Farm.Web.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class SchemaHealthController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    public SchemaHealthController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("ready")]
    public async Task<IActionResult> SchemaReadyAsync(CancellationToken ct)
    {
        try
        {
            // Check for a critical table (Printers) existence
            var conn = _dbContext.Database.GetDbConnection();
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Printers';";
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result != null && result.ToString() == "Printers")
            {
                return Ok(new { ready = true });
            }

            return StatusCode(503, new { ready = false, reason = "Printers table missing" });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { ready = false, error = ex.Message });
        }
    }
}
