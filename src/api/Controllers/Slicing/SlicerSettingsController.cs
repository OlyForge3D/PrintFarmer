using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Slicing;

[ApiController]
[Route("api/slicer/settings")]
public class SlicerSettingsController : ControllerBase
{
    private readonly ISlicerSettingsService _settingsService;

    public SlicerSettingsController(ISlicerSettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    [HttpGet]
    public ActionResult<SlicerSettingsDto> Get()
    {
        var s = _settingsService.GetSettings();
        return Ok(s);
    }

    [HttpPost]
    public IActionResult Save([FromBody] SlicerSettingsDto dto)
    {
        if (dto is null)
            return BadRequest("Settings payload required");

        // Server-side validation: jitter percent must be within 0..100
        if (dto.JitterPercent < 0.0 || dto.JitterPercent > 100.0)
        {
            return BadRequest($"JitterPercent must be between 0 and 100 (received {dto.JitterPercent}).");
        }

        _settingsService.SaveSettings(dto);
        return NoContent();
    }
}
