using Farm.Infrastructure.Services.SchemaHealth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[Route("api/schema-health")]
[ApiController]
[AllowAnonymous] // Readiness probes need anonymous access and return only a boolean schema-ready status.
public class SchemaHealthController(ISchemaHealthService health) : ControllerBase
{
    private readonly ISchemaHealthService _health = health;

    [HttpGet("ready")]
    public async Task<IActionResult> SchemaReadyAsync(CancellationToken ct)
    {
        bool ready = await _health.IsSchemaReadyAsync(ct);
        if (ready)
        {
            return Ok(new { ready = true });
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new { ready = false });
    }
}
