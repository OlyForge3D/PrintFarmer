using Farm.Web.Api.Settings;
using Farm.Web.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/settings")]
public class UnifiedSettingsController : ControllerBase
{
    private readonly IAppSettingsService _settingsService;

    public UnifiedSettingsController(IAppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    [HttpGet]
    public ActionResult<AppSettings> Get()
    {
        return Ok(_settingsService.Current);
    }

    [HttpPost]
    public async Task<IActionResult> SaveAsync([FromBody] AppSettings dto)
    {
        if (dto == null)
        {
            return BadRequest("Settings payload required");
        }
        await _settingsService.SaveAsync(dto);
        return NoContent();
    }
}
