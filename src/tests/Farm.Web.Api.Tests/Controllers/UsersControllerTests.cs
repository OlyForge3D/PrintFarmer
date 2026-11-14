using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Users;
using Farm.Modules.Identity.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class UsersControllerTests
{
    [Fact]
    public async Task AdminChangeUserPasswordAsync_WhenPostCommitAuditFails_ReturnsSuccess()
    {
        Guid adminUserId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Mock<IUsersService> usersService = new(MockBehavior.Strict);
        usersService
            .Setup(service => service.ChangeUserPasswordAsync(
                targetUserId,
                "new-password",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        Mock<ITokenRevocationService> tokenRevocationService = new(MockBehavior.Strict);
        tokenRevocationService
            .Setup(service => service.RevokeAllUserTokensAsync(
                targetUserId,
                adminUserId,
                "Password changed by administrator",
                "unknown",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        Mock<IAuthAuditService> authAuditService = new(MockBehavior.Strict);
        authAuditService
            .Setup(service => service.LogPasswordChangeAsync(
                targetUserId,
                null,
                null,
                $"admin-password-change:{adminUserId}",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("audit store unavailable"));
        UsersController controller = new(
            usersService.Object,
            Mock.Of<IAuthenticationService>(),
            tokenRevocationService.Object,
            authAuditService.Object,
            Mock.Of<ILogger<UsersController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, adminUserId.ToString())],
                        "test")),
                },
            },
        };

        IActionResult result = await controller.AdminChangeUserPasswordAsync(
            targetUserId,
            new AdminChangeUserPasswordRequest
            {
                NewPassword = "new-password",
                ConfirmNewPassword = "new-password",
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        tokenRevocationService.Verify(
            service => service.RevokeAllUserTokensAsync(
                targetUserId,
                adminUserId,
                "Password changed by administrator",
                "unknown",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
