using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Controllers.Admin;
using Farm.Web.Api.Services.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Verifies the admin overview controller is a thin pass-through to the aggregation service:
/// it must delegate, wrap the DTO in a 200 OK, and never suppress exceptions itself.
/// </summary>
public class AdminOverviewControllerTests
{
    [Fact]
    public async Task GetOverview_DelegatesToServiceAndReturnsOkWithDto()
    {
        Mock<IAdminOverviewService> service = new(MockBehavior.Strict);
        AdminOverviewDto expected = new()
        {
            CheckedAt = new DateTime(2026, 7, 25, 17, 4, 0, DateTimeKind.Utc),
            Subsystems = new[]
            {
                new SubsystemHealthDto { Key = "api", Name = "API", Status = SubsystemStatus.Healthy, Detail = "Responding" },
            },
            Attention = System.Array.Empty<AttentionItemDto>(),
        };

        service.Setup(s => s.GetOverviewAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        AdminOverviewController controller = new(service.Object);

        ActionResult<AdminOverviewDto> result = await controller.GetOverviewAsync(default);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Which;
        ok.Value.Should().BeSameAs(expected);
        service.VerifyAll();
    }

    [Fact]
    public async Task GetOverview_PropagatesCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Mock<IAdminOverviewService> service = new();
        service.Setup(s => s.GetOverviewAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        AdminOverviewController controller = new(service.Object);

        Func<Task> act = () => controller.GetOverviewAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
