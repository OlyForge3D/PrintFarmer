using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Notifications.NativePush;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications.NativePush;

public sealed class AttentionPushCategoryPreferencesTests
{
    [Fact]
    public void IsEnabled_UnsetKey_ReturnsTrue()
    {
        var prefs = new AttentionPushCategoryPreferences();
        prefs.IsEnabled(AttentionKind.Failure).Should().BeTrue();
        prefs.IsEnabled(AttentionKind.Runout).Should().BeTrue();
    }

    [Fact]
    public void Set_ThenIsEnabled_RoundTrips()
    {
        var prefs = new AttentionPushCategoryPreferences();
        prefs.Set(AttentionKind.Failure, false);
        prefs.IsEnabled(AttentionKind.Failure).Should().BeFalse();
        prefs.IsEnabled(AttentionKind.Offline).Should().BeTrue();
    }

    [Fact]
    public void ToJson_UsesCamelCaseKindKeys()
    {
        var prefs = new AttentionPushCategoryPreferences();
        prefs.Set(AttentionKind.Failure, false);
        prefs.Set(AttentionKind.Harvest, true);

        string json = prefs.ToJson();

        json.Should().Contain("\"failure\":false");
        json.Should().Contain("\"harvest\":true");
    }

    [Fact]
    public void FromJson_NullOrWhitespace_ReturnsAllEnabled()
    {
        AttentionPushCategoryPreferences.FromJson(null).IsEnabled(AttentionKind.Failure).Should().BeTrue();
        AttentionPushCategoryPreferences.FromJson(string.Empty).IsEnabled(AttentionKind.Failure).Should().BeTrue();
        AttentionPushCategoryPreferences.FromJson("   ").IsEnabled(AttentionKind.Failure).Should().BeTrue();
    }

    [Fact]
    public void FromJson_MalformedJson_ReturnsAllEnabled()
    {
        AttentionPushCategoryPreferences prefs = AttentionPushCategoryPreferences.FromJson("{not-json");
        prefs.IsEnabled(AttentionKind.Failure).Should().BeTrue();
        prefs.IsEnabled(AttentionKind.Maintenance).Should().BeTrue();
    }

    [Fact]
    public void FromJson_ValidRoundTrip_PreservesDisabledFlags()
    {
        var original = new AttentionPushCategoryPreferences();
        original.Set(AttentionKind.Failure, false);
        original.Set(AttentionKind.Runout, false);

        AttentionPushCategoryPreferences roundTripped = AttentionPushCategoryPreferences.FromJson(original.ToJson());

        roundTripped.IsEnabled(AttentionKind.Failure).Should().BeFalse();
        roundTripped.IsEnabled(AttentionKind.Runout).Should().BeFalse();
        roundTripped.IsEnabled(AttentionKind.Offline).Should().BeTrue();
    }

    [Fact]
    public void FromJson_ExplicitNullCategories_DoesNotThrowAndAllowsAllKinds()
    {
        // Regression: System.Text.Json bypasses field initializers via the setter,
        // so {"categories": null} used to leave the backing dictionary null and
        // IsEnabled/Set/ToJson blew up with NullReferenceException.
        AttentionPushCategoryPreferences prefs =
            AttentionPushCategoryPreferences.FromJson("{\"categories\": null}");

        prefs.IsEnabled(AttentionKind.Failure).Should().BeTrue();
        prefs.Set(AttentionKind.Failure, false);
        prefs.IsEnabled(AttentionKind.Failure).Should().BeFalse();
        prefs.ToJson().Should().Contain("\"failure\":false");
    }
}
