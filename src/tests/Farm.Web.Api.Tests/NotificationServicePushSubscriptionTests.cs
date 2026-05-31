using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Services.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests;

public class NotificationServicePushSubscriptionTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly NotificationService _service;

    public NotificationServicePushSubscriptionTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _service = new NotificationService(
            null!,
            null!,
            NullLogger<NotificationService>.Instance,
            _dbContext);
    }

    [Fact]
    public async Task DeletePushSubscriptionAsync_OnlyRemovesMatchingEndpoint()
    {
        // Arrange: create 3 subscriptions for the same user
        var userId = Guid.NewGuid();
        var sub1 = new PushSubscription { UserId = userId, Endpoint = "https://push.example.com/sub1", P256dh = "k1", Auth = "a1" };
        var sub2 = new PushSubscription { UserId = userId, Endpoint = "https://push.example.com/sub2", P256dh = "k2", Auth = "a2" };
        var sub3 = new PushSubscription { UserId = userId, Endpoint = "https://push.example.com/sub3", P256dh = "k3", Auth = "a3" };

        _dbContext.PushSubscriptions.AddRange(sub1, sub2, sub3);
        await _dbContext.SaveChangesAsync();

        // Act: delete only sub2's endpoint
        await _service.DeletePushSubscriptionAsync(userId, "https://push.example.com/sub2");

        // Assert: sub1 and sub3 remain, sub2 is gone
        var remaining = await _dbContext.PushSubscriptions
            .Where(s => s.UserId == userId)
            .ToListAsync();

        remaining.Should().HaveCount(2);
        remaining.Select(s => s.Endpoint).Should().Contain("https://push.example.com/sub1");
        remaining.Select(s => s.Endpoint).Should().Contain("https://push.example.com/sub3");
        remaining.Select(s => s.Endpoint).Should().NotContain("https://push.example.com/sub2");
    }

    [Fact]
    public async Task DeletePushSubscriptionAsync_DoesNotAffectOtherUsers()
    {
        // Arrange
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        var endpoint = "https://push.example.com/shared-endpoint";

        _dbContext.PushSubscriptions.AddRange(
            new PushSubscription { UserId = userId1, Endpoint = endpoint, P256dh = "k1", Auth = "a1" },
            new PushSubscription { UserId = userId2, Endpoint = endpoint, P256dh = "k2", Auth = "a2" }
        );
        await _dbContext.SaveChangesAsync();

        // Act: delete for user1 only
        await _service.DeletePushSubscriptionAsync(userId1, endpoint);

        // Assert: user2's subscription remains
        var remaining = await _dbContext.PushSubscriptions.ToListAsync();
        remaining.Should().HaveCount(1);
        remaining[0].UserId.Should().Be(userId2);
    }

    [Fact]
    public async Task DeletePushSubscriptionAsync_NoOpWhenEndpointNotFound()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _dbContext.PushSubscriptions.Add(new PushSubscription
        {
            UserId = userId, Endpoint = "https://push.example.com/existing", P256dh = "k", Auth = "a"
        });
        await _dbContext.SaveChangesAsync();

        // Act: delete non-existent endpoint
        await _service.DeletePushSubscriptionAsync(userId, "https://push.example.com/nonexistent");

        // Assert: existing subscription untouched
        var remaining = await _dbContext.PushSubscriptions.Where(s => s.UserId == userId).ToListAsync();
        remaining.Should().HaveCount(1);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
