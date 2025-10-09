using Farm.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

[Route("api/schema-health")]
[ApiController]
public class SchemaHealthController(AppDbContext dbContext) : ControllerBase
{
    private readonly AppDbContext _dbContext = dbContext;

    [HttpGet("ready")]
    public async Task<IActionResult> SchemaReadyAsync(CancellationToken ct)
    {
        try
        {
            // Check for a critical table (Printers) existence
            System.Data.Common.DbConnection conn = _dbContext.Database.GetDbConnection();
            await conn.OpenAsync(ct);
            using System.Data.Common.DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Printers';";
            object? result = await cmd.ExecuteScalarAsync(ct);
            if (result != null && result.ToString() == "Printers")
            {
                return Ok(new { ready = true });
            }

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { ready = false, reason = "Printers table missing" });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { ready = false, error = ex.Message });
        }
    }
}
