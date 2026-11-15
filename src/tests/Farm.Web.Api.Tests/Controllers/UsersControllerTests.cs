using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Users;
using Farm.Modules.Identity.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class UsersControllerTests
{
    [Theory]
    [InlineData("wrapped-database")]
    [InlineData("wrapped-timeout")]
    [InlineData("invalid-operation")]
    [InlineData("disposed")]
    [InlineData("database")]
    [InlineData("update")]
    [InlineData("timeout")]
    [InlineData("internal-cancellation")]
    [InlineData("caller-cancellation")]
    [InlineData("unexpected")]
    public async Task AdminChangeUserPasswordAsync_PostCommitAuditFailure_HandlesOnlyExpectedFailures(string scenario)
    {
        using CancellationTokenSource cancellation = new();
        Exception failure = scenario switch
        {
            "wrapped-database" => new InvalidOperationException("Provider wrapper.", new SqliteException("Audit store unavailable.", 14)),
            "wrapped-timeout" => new InvalidOperationException("Provider wrapper.", new TimeoutException("Audit store timeout.")),
            "disposed" => new ObjectDisposedException("audit-store"),
            "database" => new SqliteException("Audit store unavailable.", 14),
            "update" => new DbUpdateException("Audit store unavailable."),
            "timeout" => new TimeoutException("Audit store unavailable."),
            "internal-cancellation" => new OperationCanceledException("Audit store cancelled."),
            "caller-cancellation" => new OperationCanceledException(cancellation.Token),
            "unexpected" => new ArgumentException("Audit implementation fault."),
            _ => new InvalidOperationException("Audit store unavailable.")
        };
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
            .Returns(async () =>
            {
                if (scenario == "caller-cancellation")
                {
                    await cancellation.CancelAsync();
                }
                await Task.FromException(failure);
            });
        Mock<ILogger<UsersController>> logger = new();
        UsersController controller = new(
            usersService.Object,
            Mock.Of<IAuthenticationService>(),
            tokenRevocationService.Object,
            authAuditService.Object,
            logger.Object)
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

        AdminChangeUserPasswordRequest request = new()
        {
            NewPassword = "new-password",
            ConfirmNewPassword = "new-password",
        };

        if (scenario is "caller-cancellation" or "unexpected" or "invalid-operation" or "disposed")
        {
            Assert.Same(failure, await Record.ExceptionAsync(() =>
                controller.AdminChangeUserPasswordAsync(targetUserId, request, cancellation.Token)));
        }
        else
        {
            Assert.IsType<OkObjectResult>(await controller.AdminChangeUserPasswordAsync(targetUserId, request, cancellation.Token));
            logger.Verify(log => log.Log(
                LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.Is<Exception>(exception => ReferenceEquals(exception, failure)),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }
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
