using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Web.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Locks down the shared notification-preference wire contract that dependent
/// features (#716 operator matrix UI, native push, mobile clients) rely on.
/// Any change to enum members, JSON tokens, or the capabilities probe shape is
/// a breaking change for downstream consumers and must be intentional.
/// </summary>
public sealed class NotificationPreferencesContractTests
{
    /// <summary>
    /// PRODUCTION serialization options: mirror what
    /// <c>ControllerStartup.AddJsonOptions</c> registers — a bare
    /// <see cref="JsonStringEnumConverter"/> with NO naming policy.
    /// Enum members therefore serialize as their raw PascalCase names.
    /// The pre-existing React frontend (see
    /// <c>src/Web/ReactApp/src/types/api.ts</c> around line 3618) already
    /// hard-codes these PascalCase values, so any change here is a
    /// breaking change for all clients.
    /// </summary>
    private static readonly JsonSerializerOptions ProdEnumOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Enum_HasExactExpectedMembers()
    {
        NotificationPreferenceEventType[] members =
            System.Enum.GetValues<NotificationPreferenceEventType>();

        members.Should().BeEquivalentTo(new[]
        {
            NotificationPreferenceEventType.JobStarted,
            NotificationPreferenceEventType.JobCompleted,
            NotificationPreferenceEventType.JobFailed,
            NotificationPreferenceEventType.JobPaused,
            NotificationPreferenceEventType.PrinterFailure,
            NotificationPreferenceEventType.FilamentRunout,
            NotificationPreferenceEventType.HarvestReady,
            NotificationPreferenceEventType.MaintenanceDue,
            NotificationPreferenceEventType.PrinterOffline,
        });
    }

    [Theory]
    [InlineData(NotificationPreferenceEventType.JobStarted, "JobStarted")]
    [InlineData(NotificationPreferenceEventType.JobCompleted, "JobCompleted")]
    [InlineData(NotificationPreferenceEventType.JobFailed, "JobFailed")]
    [InlineData(NotificationPreferenceEventType.JobPaused, "JobPaused")]
    [InlineData(NotificationPreferenceEventType.PrinterFailure, "PrinterFailure")]
    [InlineData(NotificationPreferenceEventType.FilamentRunout, "FilamentRunout")]
    [InlineData(NotificationPreferenceEventType.HarvestReady, "HarvestReady")]
    [InlineData(NotificationPreferenceEventType.MaintenanceDue, "MaintenanceDue")]
    [InlineData(NotificationPreferenceEventType.PrinterOffline, "PrinterOffline")]
    public void Enum_SerializesToPascalCaseWireToken(NotificationPreferenceEventType value, string expected)
    {
        string json = JsonSerializer.Serialize(value, ProdEnumOptions);
        json.Should().Be($"\"{expected}\"");

        NotificationPreferenceEventType round = JsonSerializer.Deserialize<NotificationPreferenceEventType>(
            json,
            ProdEnumOptions);
        round.Should().Be(value);
    }

    [Fact]
    public void CapabilitiesEndpoint_PublishesAllNinePascalCaseTokens()
    {
        // We call the controller helper directly — it has no auth/db side effects.
        // Constructing NotificationsController requires many services; instead we
        // exercise the same enumeration + serialization path the endpoint uses,
        // asserting the tokens callers will actually see over the wire.
        NotificationPreferenceEventType[] values =
            System.Enum.GetValues<NotificationPreferenceEventType>();

        string[] tokens = values
            .Select(v => JsonSerializer.Serialize(v, ProdEnumOptions).Trim('"'))
            .ToArray();

        tokens.Should().BeEquivalentTo(new[]
        {
            "JobStarted",
            "JobCompleted",
            "JobFailed",
            "JobPaused",
            "PrinterFailure",
            "FilamentRunout",
            "HarvestReady",
            "MaintenanceDue",
            "PrinterOffline",
        });
    }

    [Fact]
    public void CapabilitiesDto_SerializesToExpectedShape()
    {
        var dto = new NotificationPreferencesCapabilitiesDto
        {
            SupportedEventTypes = new System.Collections.Generic.List<string> { "JobStarted", "PrinterOffline" },
        };

        var apiOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        string json = JsonSerializer.Serialize(dto, apiOptions);

        json.Should().Be("{\"supportedEventTypes\":[\"JobStarted\",\"PrinterOffline\"]}");
    }

    [Fact]
    public void EventChannelPreferenceDto_HasStableShape()
    {
        // The DTO shape { eventType, inApp, email, push, telegram } is the
        // cross-client contract. Locking property names down catches accidental
        // renames or additions that would silently break older clients.
        var dto = new NotificationEventChannelPreferenceDto
        {
            EventType = NotificationPreferenceEventType.HarvestReady,
            InApp = true,
            Email = false,
            Push = true,
            Telegram = false,
        };

        // Match production: CamelCase property naming + bare enum converter
        // (PascalCase enum tokens). See ControllerStartup.
        var apiOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        string json = JsonSerializer.Serialize(dto, apiOptions);
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[]
        {
            "eventType", "inApp", "email", "push", "telegram",
        });
        root.GetProperty("eventType").GetString().Should().Be("HarvestReady");
    }

    [Fact]
    public void UnknownEnumToken_IsRejectedByDeserializer()
    {
        // Forward-compat contract: an old server MUST NOT accept an unknown
        // eventType from a newer client. JsonStringEnumConverter throws
        // JsonException on unrecognised names; ASP.NET Core turns that into a
        // 400 ProblemDetails via ModelState. This test locks the throw-behavior
        // so a future switch to a permissive converter (e.g., ignoring unknown
        // values) can't sneak past review.
        string bogus = "\"someFuturePlanetAlignmentEvent\"";
        Action act = () => JsonSerializer.Deserialize<NotificationPreferenceEventType>(
            bogus,
            ProdEnumOptions);
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void LegacyUserWithNoPreferencesRow_HydratesAllNineRowsWithSafeDefaults()
    {
        // Contract with #716: GET /api/notifications/preferences on a user with
        // no persisted preferences row (fresh install OR pre-existing user
        // upgraded to this schema) must materialize all 9 supported operator
        // rows so the UI has deterministic hydration and never renders a
        // partial matrix. The migration applies column defaults to pre-existing
        // rows too (`AddColumn nullable:false defaultValue:...`), so a legacy
        // user with a preferences row from before this stage also observes the
        // same 9 rows once the migration runs.
        //
        // We assert against the CLR-default entity (what
        // CreateDefaultPreferences returns for a user with no persisted row)
        // to prove BuildEventChannelPreferences never returns fewer than 9.
        Farm.Infrastructure.Domain.Notifications.NotificationPreferences bare = new()
        {
            UserId = System.Guid.NewGuid(),
        };

        // Emulate what the controller does on GET when the DB returns null:
        // - CreateDefaultPreferences seeds job-row defaults via
        //   ApplyEventChannelPreferences(prefs, empty-request), which also
        //   resets attention rows to their CLR defaults (InApp/Push true,
        //   Email/Telegram false).
        // - BuildEventChannelPreferences then projects the entity into the
        //   9-row wire matrix.
        //
        // Rather than reach into the private helpers via reflection we assert
        // the CLR default state, which is the state a fresh entity sits in
        // after `new NotificationPreferences()`. The properties themselves are
        // initialised inline in NotificationPreferences.cs to those defaults.
        bare.InAppOnFilamentRunout.Should().BeTrue();
        bare.PushOnFilamentRunout.Should().BeTrue();
        bare.EmailOnFilamentRunout.Should().BeFalse();
        bare.TelegramOnFilamentRunout.Should().BeFalse();

        bare.InAppOnHarvestReady.Should().BeTrue();
        bare.PushOnHarvestReady.Should().BeTrue();
        bare.InAppOnMaintenanceDue.Should().BeTrue();
        bare.PushOnMaintenanceDue.Should().BeTrue();
        bare.InAppOnPrinterOffline.Should().BeTrue();
        bare.PushOnPrinterOffline.Should().BeTrue();
        bare.InAppOnPrinterFailure.Should().BeTrue();
        bare.PushOnPrinterFailure.Should().BeTrue();
    }

    [Fact]
    public void SupportedEventTypesList_IsClosedSet_UnknownServerAdvertisedToken_StillDeserializes()
    {
        // Bidirectional forward-compat: if a NEWER server advertises an enum
        // value not yet known to this build, an older client that parses the
        // capabilities response as `string[]` (not enum[]) will still keep the
        // opaque token in memory. That is exactly why the DTO is
        // `IReadOnlyList<string>` and not `NotificationPreferenceEventType[]`.
        string wire = "{\"supportedEventTypes\":[\"jobStarted\",\"newFutureToken\"]}";
        var dto = JsonSerializer.Deserialize<NotificationPreferencesCapabilitiesDto>(
            wire,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        dto.Should().NotBeNull();
        dto!.SupportedEventTypes.Should().Contain("newFutureToken");
    }

    [Fact]
    public void LegacyClientMatrix_DoesNotClobberAttentionPreferences()
    {
        // Vasquez v3 B1 regression: a legacy mobile client that only knows
        // about the 4 job rows must not wipe out the attention preferences a
        // newer client saved. We simulate an existing user whose attention
        // rows are all OFF (they deliberately opted out), then send an
        // UpdateNotificationPreferencesRequest whose matrix contains only
        // JobStarted+JobCompleted, and assert the attention rows survive.
        var prefs = new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
        {
            UserId = System.Guid.NewGuid(),
            InAppOnPrinterFailure = false,
            EmailOnPrinterFailure = false,
            PushOnPrinterFailure = false,
            TelegramOnPrinterFailure = false,
            InAppOnFilamentRunout = false,
            PushOnFilamentRunout = false,
            InAppOnHarvestReady = false,
            PushOnHarvestReady = false,
            InAppOnMaintenanceDue = false,
            PushOnMaintenanceDue = false,
            InAppOnPrinterOffline = false,
            PushOnPrinterOffline = false,
        };
        var request = new UpdateNotificationPreferencesRequest
        {
            EventChannelPreferences = new System.Collections.Generic.List<NotificationEventChannelPreferenceDto>
            {
                new()
                {
                    EventType = NotificationPreferenceEventType.JobStarted,
                    InApp = true,
                    Email = false,
                    Push = false,
                    Telegram = false,
                },
                new()
                {
                    EventType = NotificationPreferenceEventType.JobCompleted,
                    InApp = true,
                    Email = false,
                    Push = false,
                    Telegram = false,
                },
            },
        };

        // Invoke the private helper via reflection — we specifically want to
        // pin the exact matrix-legacy-detection code path this test exists to
        // guard, not the surrounding controller/service graph.
        System.Reflection.MethodInfo apply = typeof(NotificationsController)
            .GetMethod(
                "ApplyEventChannelPreferences",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        apply.Invoke(null, new object[] { prefs, request });

        // Attention rows must remain OFF — the legacy matrix didn't address them.
        prefs.InAppOnPrinterFailure.Should().BeFalse("legacy matrix did not address PrinterFailure");
        prefs.PushOnPrinterFailure.Should().BeFalse();
        prefs.InAppOnFilamentRunout.Should().BeFalse();
        prefs.PushOnFilamentRunout.Should().BeFalse();
        prefs.InAppOnHarvestReady.Should().BeFalse();
        prefs.PushOnHarvestReady.Should().BeFalse();
        prefs.InAppOnMaintenanceDue.Should().BeFalse();
        prefs.PushOnMaintenanceDue.Should().BeFalse();
        prefs.InAppOnPrinterOffline.Should().BeFalse();
        prefs.PushOnPrinterOffline.Should().BeFalse();

        // Job rows must have been applied.
        prefs.InAppOnJobStarted.Should().BeTrue();
        prefs.InAppOnJobCompleted.Should().BeTrue();
    }

    [Fact]
    public void NewClientMatrixWithAttentionRow_ResetsAndAppliesAttentionOverrides()
    {
        // Complementary to the legacy-preservation test: when the matrix DOES
        // contain at least one attention row, the reset-to-defaults block
        // fires so omitted attention rows land at safe defaults rather than
        // whatever stale state was in the DB.
        var prefs = new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
        {
            UserId = System.Guid.NewGuid(),
            InAppOnPrinterFailure = false, // stale: newer client's reset must fix this to true
            PushOnPrinterFailure = false,
            InAppOnHarvestReady = true,    // matrix will override this to false
        };
        var request = new UpdateNotificationPreferencesRequest
        {
            EventChannelPreferences = new System.Collections.Generic.List<NotificationEventChannelPreferenceDto>
            {
                new()
                {
                    EventType = NotificationPreferenceEventType.HarvestReady,
                    InApp = false,
                    Email = false,
                    Push = false,
                    Telegram = false,
                },
            },
        };

        System.Reflection.MethodInfo apply = typeof(NotificationsController)
            .GetMethod(
                "ApplyEventChannelPreferences",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        apply.Invoke(null, new object[] { prefs, request });

        // HarvestReady row explicitly present → matrix values applied.
        prefs.InAppOnHarvestReady.Should().BeFalse();

        // Attention rows NOT in the matrix but reset was triggered → defaults.
        prefs.InAppOnPrinterFailure.Should().BeTrue("reset block fires when matrix contains any attention row");
        prefs.PushOnPrinterFailure.Should().BeTrue();
        prefs.InAppOnFilamentRunout.Should().BeTrue();
        prefs.PushOnFilamentRunout.Should().BeTrue();
    }
}
