using System;
using System.Security.Claims;
using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Shared wiring for the per-owner profile-name tests (#3192, #3198): a real
/// <see cref="ProfilesController"/> over a real <see cref="ProfilesService"/> and SQLite-backed
/// repositories, authenticated as a specific user.
/// </summary>
internal static class ProfilesControllerNameConflictHarness
{
    public static ProfilesController CreateController(SlicerDbContext db, Guid userId)
    {
        ProfilesService service = new(
            new EfProfilesRepository(db),
            NullLogger<ProfilesService>.Instance,
            new EfProcessProfileRepository(db),
            new EfMachineProfileRepository(db),
            new EfFilamentProfileRepository(db),
            new Mock<IUnitOfWork>(MockBehavior.Loose).Object,
            new Mock<ICatalogService>(MockBehavior.Loose).Object,
            new Mock<IProfileParsingService>(MockBehavior.Loose).Object,
            new Mock<IHubContext<SlicerHub>>(MockBehavior.Loose).Object,
            new Mock<ISlicersService>(MockBehavior.Loose).Object,
            new Mock<IPrinterModelAliasService>(MockBehavior.Loose).Object);

        ClaimsPrincipal user = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        return new ProfilesController(
            NullLogger<ProfilesController>.Instance,
            service,
            new Mock<ICatalogServiceAdapter>(MockBehavior.Loose).Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };
    }

    public static CustomProfileDto AssertCreated(IActionResult result) =>
        Assert.IsType<CustomProfileDto>(Assert.IsType<CreatedResult>(result).Value);

    public static CustomProfileDto AssertOk(IActionResult result) =>
        Assert.IsType<CustomProfileDto>(Assert.IsType<OkObjectResult>(result).Value);

    public static CloneSingleProfileResponseDto AssertCloned(IActionResult result) =>
        Assert.IsType<CloneSingleProfileResponseDto>(Assert.IsType<CreatedResult>(result).Value);

    public static void AssertNameConflict(IActionResult result, string name)
    {
        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.Equal(ProfileNameConflictException.Code, body.RootElement.GetProperty("code").GetString());
        Assert.Contains(name, body.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }
}
