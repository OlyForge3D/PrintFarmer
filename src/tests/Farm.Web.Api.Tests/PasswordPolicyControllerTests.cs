using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Controllers;
using Farm.Web.Shared;
using Moq;
using Xunit;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Tests;

public class PasswordPolicyControllerTests
{
    [Fact]
    public async Task GetAsync_ReturnsDto_FromService()
    {
        // Arrange
        var svc = new Mock<Farm.Web.Api.Services.PasswordPolicy.IPasswordPolicyService>();
        var expected = new PasswordPolicyDto { MinLength = 10, RequireDigit = true };
        svc.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new PasswordPolicyController(svc.Object);

        // Act
        var result = await controller.GetAsync(CancellationToken.None);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<PasswordPolicyDto>(ok.Value!);
        Assert.Equal(10, dto.MinLength);
        Assert.True(dto.RequireDigit);
    }

    [Fact]
    public async Task UpdateAsync_Delegates_ToService_AndReturnsOk()
    {
        var svc = new Mock<Farm.Web.Api.Services.PasswordPolicy.IPasswordPolicyService>();
        var request = new UpdatePasswordPolicyRequest { MinLength = 12 };
        var updated = new PasswordPolicyDto { MinLength = 12 };
        svc.Setup(s => s.UpdateAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(updated);
        var controller = new PasswordPolicyController(svc.Object);

        var result = await controller.UpdateAsync(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<PasswordPolicyDto>(ok.Value!);
        Assert.Equal(12, dto.MinLength);
    }
}
