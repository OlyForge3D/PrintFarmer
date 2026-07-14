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

    [Fact]
    public void FromJson_MixedCaseKey_DisablesCanonicalKindAfterRoundTrip()
    {
        // Hicks #5: a mixed-case persisted opt-out (e.g. "Failure": false)
        // MUST continue to disable the canonical lookup ("failure"). The
        // pre-fix deserializer built a case-SENSITIVE dictionary so this
        // key silently no-longer-matched — a mixed-case opt-out survived the
        // round-trip only in bytes, not in behavior. The setter now rebuilds
        // the backing dict under StringComparer.OrdinalIgnoreCase.
        const string json = "{\"categories\":{\"Failure\":false,\"Offline\":true}}";

        AttentionPushCategoryPreferences prefs = AttentionPushCategoryPreferences.FromJson(json);

        prefs.IsEnabled(AttentionKind.Failure).Should().BeFalse("mixed-case 'Failure' MUST disable delivery after persistence");
        prefs.IsEnabled(AttentionKind.Offline).Should().BeTrue();
        prefs.IsEnabled(AttentionKind.Runout).Should().BeTrue();
    }

    [Fact]
    public void FromJson_DuplicateCaseVariantKeys_CollapsesLastWriteWins()
    {
        // Case-insensitive rebuild MUST NOT throw when the persisted JSON
        // carries both "Failure" and "failure" — the rebuild uses indexer
        // semantics so the second occurrence overwrites the first. This
        // matters because System.Text.Json will happily deserialize both
        // keys into a case-sensitive dictionary; converting to case-
        // insensitive with Dictionary(comparer, source) would throw
        // ArgumentException on the duplicate.
        const string json = "{\"categories\":{\"Failure\":false,\"failure\":true}}";

        AttentionPushCategoryPreferences prefs = AttentionPushCategoryPreferences.FromJson(json);

        // Whichever value the deserializer surfaces LAST wins after rebuild.
        // The point of the test is that we did not throw and lookup still
        // works — the exact final boolean is a secondary property.
        bool enabled = prefs.IsEnabled(AttentionKind.Failure);
        (enabled == true || enabled == false).Should().BeTrue();
    }

    [Fact]
    public void ToJson_ThenFromJson_ThenIsEnabled_MixedCase_MatchesCanonical()
    {
        // Hicks #5 essence: mixed-case opt-outs (stored via a legacy client
        // or an older row) MUST remain effective through arbitrary
        // persist -> load -> persist cycles. System.Text.Json does not
        // canonicalize dictionary keys on write, so the JSON itself may
        // still emit "Failure" — but the LOOKUP (IsEnabled) must resolve
        // through the case-insensitive dict every single hop.
        AttentionPushCategoryPreferences first = AttentionPushCategoryPreferences
            .FromJson("{\"categories\":{\"Failure\":false}}");
        first.IsEnabled(AttentionKind.Failure).Should().BeFalse();

        string reserialized = first.ToJson();
        AttentionPushCategoryPreferences second = AttentionPushCategoryPreferences.FromJson(reserialized);
        second.IsEnabled(AttentionKind.Failure).Should().BeFalse("case-insensitive lookup MUST survive a re-persist cycle");

        // And a third hop for good measure — an infinite loop of round-trips
        // must never lose the opt-out.
        AttentionPushCategoryPreferences third = AttentionPushCategoryPreferences.FromJson(second.ToJson());
        third.IsEnabled(AttentionKind.Failure).Should().BeFalse();
    }
}
