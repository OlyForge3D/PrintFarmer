using Farm.Infrastructure.Services.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Authentication;

public sealed class EffectivePermissionsRevocationServiceTests
{
    [Theory]
    [InlineData("wrapped-database")]
    [InlineData("wrapped-timeout")]
    [InlineData("database")]
    [InlineData("update")]
    [InlineData("timeout")]
    [InlineData("internal-cancellation")]
    public async Task RevokeUsersAsync_WhenAnExpectedUserFailureOccurs_ContinuesAndReportsPartialBatch(string scenario)
    {
        Guid firstUserId = Guid.NewGuid();
        Guid failedUserId = Guid.NewGuid();
        Guid lastUserId = Guid.NewGuid();
        Exception failure = scenario switch
        {
            "wrapped-database" => new InvalidOperationException("Provider wrapper.", new SqliteException("Store unavailable.", 14)),
            "wrapped-timeout" => new InvalidOperationException("Provider wrapper.", new TimeoutException("Store timeout.")),
            "database" => new SqliteException("Revocation persistence failed.", 14),
            "update" => new DbUpdateException("Revocation persistence failed."),
            "timeout" => new TimeoutException("Revocation persistence failed."),
            "internal-cancellation" => new OperationCanceledException("Revocation store cancelled."),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
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

    [Theory]
    [InlineData("unexpected")]
    [InlineData("invalid-operation")]
    [InlineData("disposed")]
    [InlineData("caller-cancellation")]
    public async Task RevokeUsersAsync_UnexpectedFailureOrCallerCancellation_StopsBatchAndPropagates(string scenario)
    {
        bool cancelCaller = scenario == "caller-cancellation";
        using CancellationTokenSource cancellation = new();
        Guid completedUserId = Guid.NewGuid();
        Guid failedUserId = Guid.NewGuid();
        Guid unprocessedUserId = Guid.NewGuid();
        Exception failure = scenario switch
        {
            "caller-cancellation" => new OperationCanceledException(cancellation.Token),
            "disposed" => new ObjectDisposedException("revocation-store"),
            "invalid-operation" => new InvalidOperationException("Invalid tracker state."),
            _ => new ArgumentException("Revocation implementation fault.")
        };
        Mock<ITokenRevocationService> revocation = new(MockBehavior.Strict);
        revocation.Setup(service => service.RevokeAllUserTokensAsync(
                completedUserId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), cancellation.Token))
            .ReturnsAsync(1);
        revocation.Setup(service => service.RevokeAllUserTokensAsync(
                failedUserId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), cancellation.Token))
            .Returns(async () =>
            {
                if (cancelCaller)
                {
                    await cancellation.CancelAsync();
                }
                return await Task.FromException<int>(failure);
            });
        EffectivePermissionsRevocationService service = new(revocation.Object);

        Exception? propagated = await Record.ExceptionAsync(() => service.RevokeUsersAsync(
            [completedUserId, failedUserId, unprocessedUserId], Guid.NewGuid(), "Test revocation", null, cancellation.Token));

        Assert.Same(failure, propagated);
        revocation.Verify(token => token.RevokeAllUserTokensAsync(
            completedUserId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), cancellation.Token), Times.Once);
        revocation.Verify(token => token.RevokeAllUserTokensAsync(
            unprocessedUserId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), cancellation.Token), Times.Never);
    }
}
