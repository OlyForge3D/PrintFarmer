using Farm.Infrastructure.Services.Authentication;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Authentication;

public sealed class EffectivePermissionsRevocationServiceTests
{
    [Fact]
    public async Task RevokeUsersAsync_WhenAUserFails_ContinuesAndReportsPartialBatch()
    {
        Guid firstUserId = Guid.NewGuid();
        Guid failedUserId = Guid.NewGuid();
        Guid lastUserId = Guid.NewGuid();
        InvalidOperationException failure = new("revocation persistence failed");
        Mock<ITokenRevocationService> tokenRevocationService = new(MockBehavior.Strict);
        tokenRevocationService
            .Setup(service => service.RevokeAllUserTokensAsync(
                firstUserId,
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        tokenRevocationService
            .Setup(service => service.RevokeAllUserTokensAsync(
                failedUserId,
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        tokenRevocationService
            .Setup(service => service.RevokeAllUserTokensAsync(
                lastUserId,
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        EffectivePermissionsRevocationService service = new(tokenRevocationService.Object);

        EffectivePermissionsRevocationException exception =
            await Assert.ThrowsAsync<EffectivePermissionsRevocationException>(
                () => service.RevokeUsersAsync(
                    [firstUserId, failedUserId, lastUserId],
                    Guid.NewGuid(),
                    "role permissions changed",
                    ipAddress: null));

        Assert.Equal(2, exception.SuccessfullyRevokedUserCount);
        Assert.Collection(exception.FailedUsers.Keys, userId => Assert.Equal(failedUserId, userId));
        Assert.Same(failure, exception.FailedUsers[failedUserId]);
        tokenRevocationService.Verify(
            revocation => revocation.RevokeAllUserTokensAsync(
                lastUserId,
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
