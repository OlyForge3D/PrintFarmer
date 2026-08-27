using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Controllers.Admin;
using Farm.Web.Api.Services.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Verifies the permission catalog controller is a thin pass-through to the derivation
/// service: it must delegate and wrap the DTO in a 200 OK.
/// </summary>
public class PermissionCatalogControllerTests
{
    [Fact]
    public async Task GetCatalog_DelegatesToServiceAndReturnsOkWithDto()
    {
        Mock<IPermissionCatalogService> service = new(MockBehavior.Strict);
        PermissionCatalogDto expected = new()
        {
            GeneratedAt = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc),
            Resources = System.Array.Empty<PermissionResourceGroupDto>(),
            OrphanedCatalogEntries = System.Array.Empty<OrphanedPermissionEntryDto>(),
        };

        service.Setup(s => s.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        PermissionCatalogController controller = new(service.Object);

        ActionResult<PermissionCatalogDto> result = await controller.GetCatalogAsync(default);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Which;
        ok.Value.Should().BeSameAs(expected);
        service.VerifyAll();
    }

    [Fact]
    public async Task GetCatalog_PropagatesCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Mock<IPermissionCatalogService> service = new();
        service.Setup(s => s.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        PermissionCatalogController controller = new(service.Object);

        Func<Task> act = async () => await controller.GetCatalogAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
