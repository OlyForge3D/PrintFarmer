using Farm.Infrastructure.Authorization;
using Farm.Web.Api.Services.Startup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides isolated application-state controls for the local Moonraker validation stack.
/// </summary>
[ApiController]
[Route("api/test/moonraker-emulator")]
[RequirePermission("diagnostics", "admin")]
public sealed class MoonrakerEmulatorControlController(
    IWebHostEnvironment environment,
    IOptions<MoonrakerEmulatorSeedSettings> options,
    MoonrakerEmulatorSeeder seeder) : ControllerBase
{
    /// <summary>
    /// Restores seeded jobs and dispatch state and removes printers added from deterministic discovery fixtures.
    /// </summary>
    [HttpPost("reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetAsync(CancellationToken cancellationToken)
    {
        MoonrakerEmulatorSeedSettings settings = options.Value;
        if (!environment.IsDevelopment() ||
            !settings.Enabled ||
            !settings.EnableControlApi)
        {
            return NotFound();
        }

        return await seeder.ResetAsync(cancellationToken)
            ? NoContent()
            : NotFound();
    }
}
