using Farm.Infrastructure.Services;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Tests for InMemoryGcodeUploadQuotaService which tracks per-user upload quotas
/// and reads limits from ISettingsService.
/// </summary>
public class InMemoryGcodeUploadQuotaServiceTests
{
    private readonly Mock<ISettingsService> _mockSettingsService;

    public InMemoryGcodeUploadQuotaServiceTests()
    {
        _mockSettingsService = new Mock<ISettingsService>();
    }

    private InMemoryGcodeUploadQuotaService CreateService(long dailyLimitBytes = 2L * 1024 * 1024 * 1024)
    {
        var settings = new GcodeUploadSettings
        {
            DailyUploadLimitBytes = dailyLimitBytes,
            AllowedExtensions = [".gcode"]
        };
        _mockSettingsService
            .Setup(s => s.Get<GcodeUploadSettings>())
            .Returns(settings);
        return new InMemoryGcodeUploadQuotaService(_mockSettingsService.Object);
    }

    [Fact]
    public void TryAddUsage_WithinQuota_ReturnsTrue()
    {
        // Arrange
        var quota = CreateService(1_000_000); // 1MB limit
        const string userId = "user1";

        // Act
        bool result = quota.TryAddUsage(userId, 500_000, out long used, out long limit);

        // Assert
        Assert.True(result);
        Assert.Equal(500_000, used);
        Assert.Equal(1_000_000, limit);
    }

    [Fact]
    public void TryAddUsage_ExceedsQuota_ReturnsFalse()
    {
        // Arrange
        var quota = CreateService(1_000_000); // 1MB limit
        const string userId = "user1";

        // Act
        bool result = quota.TryAddUsage(userId, 1_500_000, out long used, out long limit);

        // Assert
        Assert.False(result);
        Assert.Equal(1_500_000, used);
        Assert.Equal(1_000_000, limit);
    }

    [Fact]
    public void TryAddUsage_MultipleAdds_Accumulates()
    {
        // Arrange
        var quota = CreateService(1_000_000); // 1MB limit
        const string userId = "user1";

        // Act
        quota.TryAddUsage(userId, 300_000, out _, out _);
        bool result = quota.TryAddUsage(userId, 600_000, out long used, out long limit);

        // Assert
        Assert.True(result); // 300k + 600k = 900k, which is under 1MB limit
        Assert.Equal(900_000, used);
        Assert.Equal(1_000_000, limit);
    }

    [Fact]
    public void TryAddUsage_DifferentUsers_SeparateLimits()
    {
        // Arrange
        var quota = CreateService(1_000_000); // 1MB per user per day

        // Act
        quota.TryAddUsage("user1", 800_000, out long used1, out _);
        quota.TryAddUsage("user2", 800_000, out long used2, out _);

        // Assert
        Assert.Equal(800_000, used1);
        Assert.Equal(800_000, used2);
        // Both should succeed individually
    }

    [Fact]
    public void TryAddUsage_NullUserIdTreatedAsAnonymous()
    {
        // Arrange
        var quota = CreateService(1_000_000);

        // Act
        quota.TryAddUsage(null!, 500_000, out long used1, out _);
        quota.TryAddUsage("", 400_000, out long used2, out _);

        // Assert
        // Both null and empty should be treated as "anonymous" and accumulate
        Assert.Equal(900_000, used2);
    }

    [Fact]
    public void TryAddUsage_ZeroBytes_Succeeds()
    {
        // Arrange
        var quota = CreateService(1_000_000);

        // Act
        bool result = quota.TryAddUsage("user1", 0, out long used, out _);

        // Assert
        Assert.True(result);
        Assert.Equal(0, used);
    }

    [Fact]
    public void TryAddUsage_WhenSettingsServiceReturnsNull_UsesDefaultLimit()
    {
        // Arrange
        _mockSettingsService
            .Setup(s => s.Get<GcodeUploadSettings>())
            .Returns((GcodeUploadSettings?)null);
        var quota = new InMemoryGcodeUploadQuotaService(_mockSettingsService.Object);

        // Act
        quota.TryAddUsage("user1", 1_000_000_000, out _, out long limit); // 1GB

        // Assert
        Assert.Equal(2L * 1024 * 1024 * 1024, limit); // 2GB default
    }

    [Fact]
    public void TryAddUsage_ReadsLimitFromSettingsService()
    {
        // Arrange
        var settings = new GcodeUploadSettings
        {
            DailyUploadLimitBytes = 5_000_000_000, // 5GB custom limit
            AllowedExtensions = [".gcode"]
        };
        _mockSettingsService
            .Setup(s => s.Get<GcodeUploadSettings>())
            .Returns(settings);
        var quota = new InMemoryGcodeUploadQuotaService(_mockSettingsService.Object);

        // Act
        quota.TryAddUsage("user1", 1_000_000, out _, out long limit);

        // Assert
        Assert.Equal(5_000_000_000, limit);
    }
}

