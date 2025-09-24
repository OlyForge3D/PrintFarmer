using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Controllers;

public class SystemLogSettingsDto
{
    [Range(1, 365)]
    public int RetentionDays { get; set; } = 30;
    public List<string> PersistedLogTypes { get; set; } = new() { "Info", "Warning", "Error" };
}

[ApiController]
[Route("api/systemlogsettings")]
public class SystemLogSettingsController : ControllerBase
{
    private static SystemLogSettingsDto _settings = new(); // Replace with persistent storage in production

    [HttpGet]
    public ActionResult<SystemLogSettingsDto> GetSettings()
    {
        return Ok(_settings);
    }

    [HttpPost]
    public ActionResult SetSettings([FromBody] SystemLogSettingsDto dto)
    {
        _settings.RetentionDays = dto.RetentionDays;
        _settings.PersistedLogTypes = dto.PersistedLogTypes;
        return Ok();
    }
}
