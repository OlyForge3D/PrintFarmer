using Farm.Infrastructure.Data;
using Farm.Web.Api.Services.SchemaHealth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

[Route("api/schema-health")]
[ApiController]
public class SchemaHealthController(ISchemaHealthService health) : ControllerBase
{
    private readonly ISchemaHealthService _health = health;

    [HttpGet("ready")]
    public async Task<IActionResult> SchemaReadyAsync(CancellationToken ct)
    {
        var ready = await _health.IsSchemaReadyAsync(ct);
        if (ready)
        {
            return Ok(new { ready = true });
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new { ready = false });
    }
}
