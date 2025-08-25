using Farm.Web.Server.Services;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SpoolmanController(SpoolmanService spoolman) : ControllerBase
{
    [HttpGet("config")]
    public ActionResult<SpoolmanConfigDto?> GetConfig() => spoolman.GetConfig();

    [HttpPost("config")]
    public IActionResult SetConfig(SpoolmanConfigDto config)
    {
        spoolman.SetConfig(config);
        return NoContent();
    }

    [HttpGet("spools")]
    public async Task<ActionResult<IEnumerable<SpoolmanSpoolDto>>> GetSpools(CancellationToken ct)
        => Ok(await spoolman.ListSpoolsAsync(ct));
}
