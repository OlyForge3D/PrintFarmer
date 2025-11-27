using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.PasswordPolicy;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests;

public class PasswordPolicyControllerTests
{
    [Fact]
    public async Task GetAsync_ReturnsDto_FromService()
    {
        // Arrange
        Mock<IPasswordPolicyService> svc = new Mock<Farm.Web.Api.Services.PasswordPolicy.IPasswordPolicyService>();
        PasswordPolicyDto expected = new PasswordPolicyDto { MinLength = 10, RequireDigit = true };
        svc.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        PasswordPolicyController controller = new PasswordPolicyController(svc.Object);

        // Act
        ActionResult<PasswordPolicyDto> result = await controller.GetAsync(CancellationToken.None);

        // Assert
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PasswordPolicyDto dto = Assert.IsType<PasswordPolicyDto>(ok.Value!);
        Assert.Equal(10, dto.MinLength);
        Assert.True(dto.RequireDigit);
    }

    [Fact]
    public async Task UpdateAsync_Delegates_ToService_AndReturnsOk()
    {
        Mock<IPasswordPolicyService> svc = new Mock<Farm.Web.Api.Services.PasswordPolicy.IPasswordPolicyService>();
        UpdatePasswordPolicyRequest request = new UpdatePasswordPolicyRequest { MinLength = 12 };
        PasswordPolicyDto updated = new PasswordPolicyDto { MinLength = 12 };
        svc.Setup(s => s.UpdateAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(updated);
        PasswordPolicyController controller = new PasswordPolicyController(svc.Object);

        ActionResult<PasswordPolicyDto> result = await controller.UpdateAsync(request, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PasswordPolicyDto dto = Assert.IsType<PasswordPolicyDto>(ok.Value!);
        Assert.Equal(12, dto.MinLength);
    }
}
