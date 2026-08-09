using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.Notifications;
using Farm.Web.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class NotificationsControllerVapidKeyTests
{
    [Fact]
    public void GetVapidKey_WhenConfigured_ReturnsThePublicKeyFromOptions()
    {
        NotificationsController controller = BuildController(new VapidOptions
        {
            VapidPublicKey = "test-public-key",
            VapidPrivateKey = "test-private-key"
        });

        ActionResult<VapidKeyResponse> result = controller.GetVapidKey();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<VapidKeyResponse>().Subject;
        body.PublicKey.Should().Be("test-public-key");
    }

    [Fact]
    public void GetVapidKey_WhenNotConfigured_ReturnsEmptyPublicKey()
    {
        NotificationsController controller = BuildController(new VapidOptions());

        ActionResult<VapidKeyResponse> result = controller.GetVapidKey();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<VapidKeyResponse>().Subject;
        body.PublicKey.Should().BeEmpty();
    }

    private static NotificationsController BuildController(VapidOptions vapidOptions)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        using var dbContext = new AppDbContext(options);

        var service = new NotificationService(
            notificationRepository: null!,
            usersRepository: null!,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance,
            dbContext: dbContext);

        return new NotificationsController(service, vapidOptions);
    }
}
