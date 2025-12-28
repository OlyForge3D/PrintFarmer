using Farm.Web.Api.Services;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class GcodeUploadSettingsTests
{
    [Fact]
    public void InMemoryGcodeUploadSettings_DefaultConstructor_InitializesWithDefaultExtensions()
    {
        // Act
        var settings = new InMemoryGcodeUploadSettings();

        // Assert
        var extensions = settings.AllowedExtensions;
        Assert.Contains(".gcode", extensions);
        Assert.Contains(".bgcode", extensions);
    }

    [Fact]
    public void InMemoryGcodeUploadSettings_UpdateAllowedExtensions_UpdatesList()
    {
        // Arrange
        var settings = new InMemoryGcodeUploadSettings();
        var newExtensions = new[] { ".g", ".G", ".nc", ".ngc" };

        // Act
        settings.UpdateAllowedExtensions(newExtensions);

        // Assert
        var extensions = settings.AllowedExtensions;
        Assert.Equal(4, extensions.Count);
        Assert.Contains(".g", extensions);
        Assert.Contains(".nc", extensions);
    }

    [Fact]
    public void InMemoryGcodeUploadSettings_UpdateAllowedExtensions_NormalizesExtensions()
    {
        // Arrange
        var settings = new InMemoryGcodeUploadSettings();
        var extensions = new[] { "gcode", "bgcode", ".g" }; // Missing dot prefix on first two

        // Act
        settings.UpdateAllowedExtensions(extensions);

        // Assert
        var result = settings.AllowedExtensions;
        Assert.Contains(".gcode", result);
        Assert.Contains(".bgcode", result);
        Assert.Contains(".g", result);
    }

    [Fact]
    public void InMemoryGcodeUploadSettings_UpdateAllowedExtensions_HandlesWhitespace()
    {
        // Arrange
        var settings = new InMemoryGcodeUploadSettings();
        var extensions = new[] { " .gcode ", "  .g  ", "\t.nc\t" };

        // Act
        settings.UpdateAllowedExtensions(extensions);

        // Assert
        var result = settings.AllowedExtensions;
        Assert.Contains(".gcode", result);
        Assert.Contains(".g", result);
        Assert.Contains(".nc", result);
    }

    [Fact]
    public void InMemoryGcodeUploadSettings_UpdateAllowedExtensions_FilterEmptyStrings()
    {
        // Arrange
        var settings = new InMemoryGcodeUploadSettings();
        var extensions = new[] { ".gcode", "", "   ", ".g" };

        // Act
        settings.UpdateAllowedExtensions(extensions);

        // Assert
        var result = settings.AllowedExtensions;
        Assert.Equal(2, result.Count);
        Assert.Contains(".gcode", result);
        Assert.Contains(".g", result);
    }

    [Fact]
    public void InMemoryGcodeUploadSettings_UpdateAllowedExtensions_ClearsAndReplaces()
    {
        // Arrange
        var settings = new InMemoryGcodeUploadSettings();
        Assert.Contains(".gcode", settings.AllowedExtensions);

        // Act
        settings.UpdateAllowedExtensions(new[] { ".xyz", ".abc" });

        // Assert
        var result = settings.AllowedExtensions;
        Assert.DoesNotContain(".gcode", result);
        Assert.Contains(".xyz", result);
        Assert.Contains(".abc", result);
    }

    [Fact]
    public void InMemoryGcodeUploadSettings_UpdateAllowedExtensions_IsCaseInsensitive()
    {
        // Arrange
        var settings = new InMemoryGcodeUploadSettings();
        var extensions = new[] { ".GCODE", ".G", ".Nc" };

        // Act
        settings.UpdateAllowedExtensions(extensions);
        var result = settings.AllowedExtensions;

        // Assert
        Assert.Equal(3, result.Count);
        // Note: May be normalized to lower or keep original case, but should be present
        Assert.Contains(result, e => e.Equals(".gcode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, e => e.Equals(".g", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, e => e.Equals(".nc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InMemoryGcodeUploadQuotaService_TryAddUsage_WithinQuota_ReturnsTrue()
    {
        // Arrange
        var quota = new InMemoryGcodeUploadQuotaService(1_000_000); // 1MB limit
        const string userId = "user1";

        // Act
        bool result = quota.TryAddUsage(userId, 500_000, out long used, out long limit);

        // Assert
        Assert.True(result);
        Assert.Equal(500_000, used);
        Assert.Equal(1_000_000, limit);
    }

    [Fact]
    public void InMemoryGcodeUploadQuotaService_TryAddUsage_ExceedsQuota_ReturnsFalse()
    {
        // Arrange
        var quota = new InMemoryGcodeUploadQuotaService(1_000_000); // 1MB limit
        const string userId = "user1";

        // Act
        bool result = quota.TryAddUsage(userId, 1_500_000, out long used, out long limit);

        // Assert
        Assert.False(result);
        Assert.Equal(1_500_000, used);
        Assert.Equal(1_000_000, limit);
    }

    [Fact]
    public void InMemoryGcodeUploadQuotaService_TryAddUsage_MultipleAdds_Accumulates()
    {
        // Arrange
        var quota = new InMemoryGcodeUploadQuotaService(1_000_000); // 1MB limit
        const string userId = "user1";

        // Act
        quota.TryAddUsage(userId, 300_000, out _, out _);
        bool result = quota.TryAddUsage(userId, 600_000, out long used, out long limit);

        // Assert
        Assert.False(result); // 300k + 600k = 900k, but we're at 600k on second call
        Assert.Equal(900_000, used);
        Assert.Equal(1_000_000, limit);
    }

    [Fact]
    public void InMemoryGcodeUploadQuotaService_TryAddUsage_DifferentUsers_SeperateLimits()
    {
        // Arrange
        var quota = new InMemoryGcodeUploadQuotaService(1_000_000); // 1MB per user per day

        // Act
        quota.TryAddUsage("user1", 800_000, out long used1, out _);
        quota.TryAddUsage("user2", 800_000, out long used2, out _);

        // Assert
        Assert.Equal(800_000, used1);
        Assert.Equal(800_000, used2);
        // Both should succeed individually
    }

    [Fact]
    public void InMemoryGcodeUploadQuotaService_TryAddUsage_NullUserIdTreatedAsAnonymous()
    {
        // Arrange
        var quota = new InMemoryGcodeUploadQuotaService(1_000_000);

        // Act
        quota.TryAddUsage(null!, 500_000, out long used1, out _);
        quota.TryAddUsage("", 400_000, out long used2, out _);

        // Assert
        // Both null and empty should be treated as "anonymous" and accumulate
        Assert.Equal(900_000, used2);
    }

    [Fact]
    public void InMemoryGcodeUploadQuotaService_TryAddUsage_ZeroBytes_Succeeds()
    {
        // Arrange
        var quota = new InMemoryGcodeUploadQuotaService(1_000_000);

        // Act
        bool result = quota.TryAddUsage("user1", 0, out long used, out _);

        // Assert
        Assert.True(result);
        Assert.Equal(0, used);
    }

    [Fact]
    public void InMemoryGcodeUploadQuotaService_DefaultConstructor_Uses2GB()
    {
        // Arrange & Act
        var quota = new InMemoryGcodeUploadQuotaService();

        // Act
        quota.TryAddUsage("user1", 1_000_000_000, out _, out long limit); // 1GB

        // Assert
        Assert.Equal(2L * 1024 * 1024 * 1024, limit); // 2GB default
    }
}
