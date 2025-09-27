using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/settings")]
public class UnifiedSettingsController : ControllerBase
{

    private readonly IAppSettingsService _settingsService;
    private readonly SettingsService _modularSettingsService;

    public UnifiedSettingsController(IAppSettingsService settingsService, SettingsService modularSettingsService)
    {
        _settingsService = settingsService;
        _modularSettingsService = modularSettingsService;
    }


    [HttpGet]
    public ActionResult<AppSettings> Get()
    {
        return Ok(_settingsService.Current);
    }

    /// <summary>
    /// Returns metadata for all discovered settings classes for dynamic UI generation.
    /// </summary>
    [HttpGet("metadata")]
    public ActionResult<IEnumerable<SettingMetadata>> GetMetadata()
    {
        var metadata = _modularSettingsService.GetAllMetadata();
        return Ok(metadata);
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
