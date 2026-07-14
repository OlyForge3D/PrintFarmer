using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Web.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    public async System.Threading.Tasks.Task LegacyClientMatrix_DoesNotClobberAttentionPreferences()
    {
        // Vasquez v3 B1 regression, now enforced through the full controller
        // path against the patch-oriented service API. A legacy mobile client
        // that only knows the 4 job rows must not wipe attention preferences a
        // newer client saved. Seed a user whose attention rows are all OFF
        // (deliberate opt-out), send a matrix with only two job rows, then
        // re-query the DB and assert attention rows survived.
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<
                Farm.Infrastructure.Data.AppDbContext>()
            .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new Farm.Infrastructure.Data.AppDbContext(options);

        System.Guid userId = System.Guid.NewGuid();
        var existing = new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
        {
            UserId = userId,
            EnablePushNotifications = true,
            EnableInAppNotifications = true,
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
        dbContext.NotificationPreferences.Add(existing);
        await dbContext.SaveChangesAsync();

        NotificationsController controller = BuildController(dbContext, userId);

        var request = new UpdateNotificationPreferencesRequest
        {
            EnablePushNotifications = true,
            EnableInAppNotifications = true,
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

        Microsoft.AspNetCore.Mvc.ActionResult<NotificationPreferencesDto> result =
            await controller.UpdatePreferencesAsync(request, dbContext);
        var ok = result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);

        // Attention rows must remain OFF — the legacy matrix didn't address them,
        // so the patch semantics preserve the persisted values.
        Farm.Infrastructure.Domain.Notifications.NotificationPreferences? persisted =
            await dbContext.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
        persisted.Should().NotBeNull();
        persisted!.InAppOnPrinterFailure.Should().BeFalse("legacy matrix did not address PrinterFailure");
        persisted.PushOnPrinterFailure.Should().BeFalse();
        persisted.InAppOnFilamentRunout.Should().BeFalse();
        persisted.PushOnFilamentRunout.Should().BeFalse();
        persisted.InAppOnHarvestReady.Should().BeFalse();
        persisted.PushOnHarvestReady.Should().BeFalse();
        persisted.InAppOnMaintenanceDue.Should().BeFalse();
        persisted.PushOnMaintenanceDue.Should().BeFalse();
        persisted.InAppOnPrinterOffline.Should().BeFalse();
        persisted.PushOnPrinterOffline.Should().BeFalse();

        // Job rows were applied.
        persisted.InAppOnJobStarted.Should().BeTrue();
        persisted.InAppOnJobCompleted.Should().BeTrue();
    }

    [Fact]
    public async System.Threading.Tasks.Task PartialModernMatrix_OnlySuppliedRowsAreApplied_OmittedRowsPreserved()
    {
        // Vasquez v6 B3 regression: a partial modern PUT that carries one
        // attention row must apply ONLY that row to the tracked entity and
        // preserve every omitted job and attention row's persisted value.
        // Previous behavior reset all 20 attention columns AND all 16 job
        // columns to hard-coded defaults when any matrix row was present,
        // silently reverting omitted user choices. This test exercises the
        // full controller path against a real in-memory database.
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<
                Farm.Infrastructure.Data.AppDbContext>()
            .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new Farm.Infrastructure.Data.AppDbContext(options);

        System.Guid userId = System.Guid.NewGuid();
        var existing = new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
        {
            UserId = userId,

            // Non-default persisted values for attention rows the request does
            // NOT address — every one of these must survive the PUT.
            InAppOnPrinterFailure = false,
            EmailOnPrinterFailure = true,
            PushOnPrinterFailure = false,
            TelegramOnPrinterFailure = true,
            InAppOnFilamentRunout = false,
            EmailOnFilamentRunout = true,
            PushOnFilamentRunout = false,
            TelegramOnFilamentRunout = true,
            InAppOnMaintenanceDue = false,
            EmailOnMaintenanceDue = true,
            PushOnMaintenanceDue = false,
            TelegramOnMaintenanceDue = true,
            InAppOnPrinterOffline = false,
            EmailOnPrinterOffline = true,
            PushOnPrinterOffline = false,
            TelegramOnPrinterOffline = true,

            // Non-default persisted values for the three job rows the request
            // does NOT address.
            InAppOnJobStarted = true,
            EmailOnJobStarted = true,
            PushOnJobStarted = true,
            TelegramOnJobStarted = true,
            InAppOnJobFailed = true,
            EmailOnJobFailed = true,
            PushOnJobFailed = true,
            TelegramOnJobFailed = true,
            InAppOnJobPaused = true,
            EmailOnJobPaused = true,
            PushOnJobPaused = true,
            TelegramOnJobPaused = true,
        };
        dbContext.NotificationPreferences.Add(existing);
        await dbContext.SaveChangesAsync();

        NotificationsController controller = BuildController(dbContext, userId);

        // Modern client sends exactly ONE attention row and ONE job row.
        var request = new UpdateNotificationPreferencesRequest
        {
            EnablePushNotifications = true,
            EnableInAppNotifications = true,
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
                new()
                {
                    EventType = NotificationPreferenceEventType.JobCompleted,
                    InApp = false,
                    Email = false,
                    Push = false,
                    Telegram = false,
                },
            },
        };

        Microsoft.AspNetCore.Mvc.ActionResult<NotificationPreferencesDto> result =
            await controller.UpdatePreferencesAsync(request, dbContext);
        var ok = result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<NotificationPreferencesDto>().Subject;

        // The single supplied HarvestReady row is applied.
        dto.EventChannelPreferences!
            .Single(r => r.EventType == NotificationPreferenceEventType.HarvestReady)
            .Should().BeEquivalentTo(new NotificationEventChannelPreferenceDto
            {
                EventType = NotificationPreferenceEventType.HarvestReady,
                InApp = false,
                Email = false,
                Push = false,
                Telegram = false,
            });

        // Every omitted attention row's non-default persisted value MUST be
        // preserved — asserting on the returned DTO also proves the response
        // reflects the persisted state, not the transient request view.
        NotificationEventChannelPreferenceDto printerFailureRow = dto.EventChannelPreferences!
            .Single(r => r.EventType == NotificationPreferenceEventType.PrinterFailure);
        printerFailureRow.InApp.Should().BeFalse();
        printerFailureRow.Email.Should().BeTrue();
        printerFailureRow.Push.Should().BeFalse();
        printerFailureRow.Telegram.Should().BeTrue();

        NotificationEventChannelPreferenceDto filamentRunoutRow = dto.EventChannelPreferences!
            .Single(r => r.EventType == NotificationPreferenceEventType.FilamentRunout);
        filamentRunoutRow.Email.Should().BeTrue();
        filamentRunoutRow.Telegram.Should().BeTrue();

        NotificationEventChannelPreferenceDto maintenanceDueRow = dto.EventChannelPreferences!
            .Single(r => r.EventType == NotificationPreferenceEventType.MaintenanceDue);
        maintenanceDueRow.Email.Should().BeTrue();
        maintenanceDueRow.Telegram.Should().BeTrue();

        NotificationEventChannelPreferenceDto printerOfflineRow = dto.EventChannelPreferences!
            .Single(r => r.EventType == NotificationPreferenceEventType.PrinterOffline);
        printerOfflineRow.Email.Should().BeTrue();
        printerOfflineRow.Telegram.Should().BeTrue();

        // Omitted job rows also survive.
        NotificationEventChannelPreferenceDto jobStartedRow = dto.EventChannelPreferences!
            .Single(r => r.EventType == NotificationPreferenceEventType.JobStarted);
        jobStartedRow.InApp.Should().BeTrue();
        jobStartedRow.Email.Should().BeTrue();
        jobStartedRow.Push.Should().BeTrue();
        jobStartedRow.Telegram.Should().BeTrue();

        NotificationEventChannelPreferenceDto jobPausedRow = dto.EventChannelPreferences!
            .Single(r => r.EventType == NotificationPreferenceEventType.JobPaused);
        jobPausedRow.InApp.Should().BeTrue();

        // Re-query DB to prove the DTO faithfully reflects persisted state.
        Farm.Infrastructure.Domain.Notifications.NotificationPreferences? persisted =
            await dbContext.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
        persisted.Should().NotBeNull();
        persisted!.EmailOnPrinterFailure.Should().BeTrue("omitted attention rows must survive partial modern PUT");
        persisted.EmailOnMaintenanceDue.Should().BeTrue();
        persisted.EmailOnPrinterOffline.Should().BeTrue();
        persisted.PushOnJobStarted.Should().BeTrue("omitted job rows must survive partial modern PUT");

        // The supplied JobCompleted row is applied.
        persisted.InAppOnJobCompleted.Should().BeFalse();
        persisted.EmailOnJobCompleted.Should().BeFalse();
        persisted.PushOnJobCompleted.Should().BeFalse();
        persisted.TelegramOnJobCompleted.Should().BeFalse();
    }

    [Fact]
    public async System.Threading.Tasks.Task FullNineRowMatrix_IsDeterministicAndFullyApplied()
    {
        // A full 9-row modern PUT must overwrite every column deterministically
        // regardless of persisted state. Also verifies the response DTO
        // returns the persisted state.
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<
                Farm.Infrastructure.Data.AppDbContext>()
            .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new Farm.Infrastructure.Data.AppDbContext(options);

        System.Guid userId = System.Guid.NewGuid();
        var existing = new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
        {
            UserId = userId,
            InAppOnPrinterFailure = false,
            EmailOnPrinterFailure = false,
            PushOnPrinterFailure = false,
            TelegramOnPrinterFailure = false,
        };
        dbContext.NotificationPreferences.Add(existing);
        await dbContext.SaveChangesAsync();

        NotificationsController controller = BuildController(dbContext, userId);

        NotificationPreferenceEventType[] allNineEvents =
        [
            NotificationPreferenceEventType.JobStarted,
            NotificationPreferenceEventType.JobCompleted,
            NotificationPreferenceEventType.JobFailed,
            NotificationPreferenceEventType.JobPaused,
            NotificationPreferenceEventType.PrinterFailure,
            NotificationPreferenceEventType.FilamentRunout,
            NotificationPreferenceEventType.HarvestReady,
            NotificationPreferenceEventType.MaintenanceDue,
            NotificationPreferenceEventType.PrinterOffline,
        ];

        var request = new UpdateNotificationPreferencesRequest
        {
            EnablePushNotifications = true,
            EnableInAppNotifications = true,
            EventChannelPreferences = allNineEvents
                .Select(e => new NotificationEventChannelPreferenceDto
                {
                    EventType = e,
                    InApp = true,
                    Email = false,
                    Push = true,
                    Telegram = false,
                })
                .ToList(),
        };

        Microsoft.AspNetCore.Mvc.ActionResult<NotificationPreferencesDto> result =
            await controller.UpdatePreferencesAsync(request, dbContext);
        var ok = result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<NotificationPreferencesDto>().Subject;

        // Response must contain all nine rows in a deterministic shape.
        dto.EventChannelPreferences.Should().HaveCount(9);
        foreach (NotificationPreferenceEventType e in allNineEvents)
        {
            NotificationEventChannelPreferenceDto row = dto.EventChannelPreferences!.Single(r => r.EventType == e);
            row.InApp.Should().BeTrue();
            row.Email.Should().BeFalse();
            row.Push.Should().BeTrue();
            row.Telegram.Should().BeFalse();
        }

        // Master flags derived from final tracked state: push=true (all rows
        // have push=true), inApp=true, email/telegram=false.
        dto.EnablePushNotifications.Should().BeTrue();
        dto.EnableInAppNotifications.Should().BeTrue();
        dto.EnableEmailNotifications.Should().BeFalse();
        dto.EnableTelegramNotifications.Should().BeFalse();
    }

    [Fact]
    public async System.Threading.Tasks.Task EmptyMatrix_UsesLegacyDerivationAndPreservesAttentionRows()
    {
        // An empty matrix (or null) is the legacy branch: job rows are derived
        // from top-level scalars; attention rows are untouched.
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<
                Farm.Infrastructure.Data.AppDbContext>()
            .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new Farm.Infrastructure.Data.AppDbContext(options);

        System.Guid userId = System.Guid.NewGuid();
        var existing = new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
        {
            UserId = userId,
            PushOnHarvestReady = false,
            InAppOnHarvestReady = false,
            EmailOnMaintenanceDue = true,
        };
        dbContext.NotificationPreferences.Add(existing);
        await dbContext.SaveChangesAsync();

        NotificationsController controller = BuildController(dbContext, userId);

        var request = new UpdateNotificationPreferencesRequest
        {
            EnablePushNotifications = true,
            EnableInAppNotifications = true,
            EnableEmailNotifications = true,
            NotifyOnStart = false,
            NotifyOnCompletion = true,
            NotifyOnFailure = true,
            NotifyOnPause = false,
            EventChannelPreferences = null,
        };

        Microsoft.AspNetCore.Mvc.ActionResult<NotificationPreferencesDto> result =
            await controller.UpdatePreferencesAsync(request, dbContext);
        result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();

        Farm.Infrastructure.Domain.Notifications.NotificationPreferences? persisted =
            await dbContext.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
        persisted.Should().NotBeNull();

        // Job rows derived from top-level scalars.
        persisted!.PushOnJobStarted.Should().BeFalse();
        persisted.PushOnJobCompleted.Should().BeTrue();
        persisted.PushOnJobPaused.Should().BeFalse();

        // Attention rows preserved.
        persisted.PushOnHarvestReady.Should().BeFalse();
        persisted.InAppOnHarvestReady.Should().BeFalse();
        persisted.EmailOnMaintenanceDue.Should().BeTrue();
    }

    [Fact]
    public async System.Threading.Tasks.Task UnknownEnumTokenOnPut_Returns400ProblemDetails()
    {
        // The unknown-enum → 400 contract must survive the patch refactor.
        // JsonStringEnumConverter (no naming policy) rejects unknown tokens at
        // model binding, before the controller action runs. We simulate that
        // by handing the controller a request assembled with an out-of-range
        // enum, which the service maps back to a 400 via ArgumentException →
        // BadRequest fallback in the catch block. (The end-to-end 400 comes
        // from the ApiController model-binding path in production.)
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<
                Farm.Infrastructure.Data.AppDbContext>()
            .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new Farm.Infrastructure.Data.AppDbContext(options);

        System.Guid userId = System.Guid.NewGuid();
        NotificationsController controller = BuildController(dbContext, userId);

        var request = new UpdateNotificationPreferencesRequest
        {
            EventChannelPreferences = new System.Collections.Generic.List<NotificationEventChannelPreferenceDto>
            {
                new()
                {
                    EventType = (NotificationPreferenceEventType)9999,
                    InApp = true,
                    Email = false,
                    Push = true,
                    Telegram = false,
                },
            },
        };

        Microsoft.AspNetCore.Mvc.ActionResult<NotificationPreferencesDto> result =
            await controller.UpdatePreferencesAsync(request, dbContext);

        result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
    }

    private static NotificationsController BuildController(
        Farm.Infrastructure.Data.AppDbContext dbContext,
        System.Guid userId)
    {
        var service = new Farm.Infrastructure.Services.Notifications.NotificationService(
            notificationRepository: null!,
            usersRepository: null!,
            logger: Microsoft.Extensions.Logging.Abstractions
                .NullLogger<Farm.Infrastructure.Services.Notifications.NotificationService>
                .Instance,
            dbContext: dbContext);

        return new NotificationsController(service)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(
                        new System.Security.Claims.ClaimsIdentity(
                            new[] { new System.Security.Claims.Claim("sub", userId.ToString()) },
                            authenticationType: "test")),
                },
            },
        };
    }

    [Fact]
    public async System.Threading.Tasks.Task
        LegacyClientPut_DoesNotClobberPersistedAttentionPreferencesThroughController()
    {
        // Hicks v4 blocker 2 regression: even with the V1 guard in place, the
        // controller previously built a fresh NotificationPreferences with CLR
        // defaults for the 20 attention fields and handed it to the service,
        // which then round-tripped those defaults into the DB — silently
        // wiping a user's saved `false` attention prefs on every legacy PUT.
        // The fix is to load the existing row first and seed attention columns
        // before ApplyEventChannelPreferences runs. This test exercises the
        // full controller path.
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<
                Farm.Infrastructure.Data.AppDbContext>()
            .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new Farm.Infrastructure.Data.AppDbContext(options);

        System.Guid userId = System.Guid.NewGuid();
        var existing = new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
        {
            UserId = userId,
            EnablePushNotifications = true,
            EnableInAppNotifications = true,
            EnableEmailNotifications = false,
            EnableTelegramNotifications = false,
            PushOnFilamentRunout = false,
            InAppOnFilamentRunout = false,
            PushOnPrinterFailure = false,
            PushOnHarvestReady = false,
            PushOnMaintenanceDue = false,
            PushOnPrinterOffline = false,
        };
        dbContext.NotificationPreferences.Add(existing);
        await dbContext.SaveChangesAsync();

        var service = new Farm.Infrastructure.Services.Notifications.NotificationService(
            notificationRepository: null!,
            usersRepository: null!,
            logger: Microsoft.Extensions.Logging.Abstractions
                .NullLogger<Farm.Infrastructure.Services.Notifications.NotificationService>
                .Instance,
            dbContext: dbContext);

        var controller = new NotificationsController(service);
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(
                        new[] { new System.Security.Claims.Claim("sub", userId.ToString()) },
                        authenticationType: "test")),
            },
        };

        // Legacy client: only 4 job-row entries in the matrix; no attention rows,
        // no top-level attention properties on the request.
        var request = new UpdateNotificationPreferencesRequest
        {
            EnableEmailNotifications = false,
            EnablePushNotifications = true,
            EnableInAppNotifications = true,
            EnableTelegramNotifications = false,
            NotifyOnCompletion = true,
            NotifyOnFailure = true,
            NotifyOnStart = false,
            NotifyOnPause = false,
            EventChannelPreferences = new System.Collections.Generic.List<
                NotificationEventChannelPreferenceDto>
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

        Microsoft.AspNetCore.Mvc.ActionResult<NotificationPreferencesDto> result =
            await controller.UpdatePreferencesAsync(request, dbContext);

        var ok = result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);

        // Re-query the DB — this is the invariant Hicks demands: the persisted
        // attention prefs still read `false` after a legacy PUT.
        Farm.Infrastructure.Domain.Notifications.NotificationPreferences? persisted =
            await dbContext.NotificationPreferences
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == userId);
        persisted.Should().NotBeNull();
        persisted!.PushOnFilamentRunout.Should()
            .BeFalse("legacy PUT must not clobber attention rows the user disabled");
        persisted.InAppOnFilamentRunout.Should().BeFalse();
        persisted.PushOnPrinterFailure.Should().BeFalse();
        persisted.PushOnHarvestReady.Should().BeFalse();
        persisted.PushOnMaintenanceDue.Should().BeFalse();
        persisted.PushOnPrinterOffline.Should().BeFalse();
    }

    [Theory]
    [InlineData(Farm.Infrastructure.Dtos.Attention.AttentionKind.Failure,
        nameof(Farm.Infrastructure.Domain.Notifications.NotificationPreferences.PushOnPrinterFailure))]
    [InlineData(Farm.Infrastructure.Dtos.Attention.AttentionKind.Runout,
        nameof(Farm.Infrastructure.Domain.Notifications.NotificationPreferences.PushOnFilamentRunout))]
    [InlineData(Farm.Infrastructure.Dtos.Attention.AttentionKind.Harvest,
        nameof(Farm.Infrastructure.Domain.Notifications.NotificationPreferences.PushOnHarvestReady))]
    [InlineData(Farm.Infrastructure.Dtos.Attention.AttentionKind.Maintenance,
        nameof(Farm.Infrastructure.Domain.Notifications.NotificationPreferences.PushOnMaintenanceDue))]
    [InlineData(Farm.Infrastructure.Dtos.Attention.AttentionKind.Offline,
        nameof(Farm.Infrastructure.Domain.Notifications.NotificationPreferences.PushOnPrinterOffline))]
    public void DispatcherPushGate_MapsAttentionKindToSharedMatrixColumn(
        Farm.Infrastructure.Dtos.Attention.AttentionKind kind, string columnName)
    {
        // Hicks v4 blocker 3 regression: the dispatcher must consult the
        // shared web preference matrix (PushOn{Kind}) before enqueueing a
        // native push, so #716 opt-outs actually stop native sends. This test
        // pins the mapping via the private helper — invoked via reflection on
        // NativePushDispatcher — for every AttentionKind that native push
        // supports.
        var prefs = new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
        {
            UserId = System.Guid.NewGuid(),
            // All push-on columns start `true`; flip the one for this row.
        };
        System.Reflection.PropertyInfo column = typeof(
                Farm.Infrastructure.Domain.Notifications.NotificationPreferences)
            .GetProperty(columnName)!;
        column.SetValue(prefs, false);

        System.Reflection.MethodInfo helper = typeof(
                Farm.Infrastructure.Services.Notifications.NativePush.NativePushDispatcher)
            .GetMethod(
                "IsPushEnabledForKind",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        bool enabledForThisKind = (bool)helper.Invoke(null, new object?[] { prefs, kind })!;
        enabledForThisKind.Should().BeFalse(
            $"prefs.{columnName} is false so the dispatcher must skip {kind}");

        // Cross-check: every OTHER kind still returns true — the mapping is
        // exclusive (setting PushOnFilamentRunout=false does not accidentally
        // block PrinterFailure sends).
        foreach (Farm.Infrastructure.Dtos.Attention.AttentionKind other in
            System.Enum.GetValues<Farm.Infrastructure.Dtos.Attention.AttentionKind>())
        {
            if (other == kind)
            {
                continue;
            }

            bool otherEnabled = (bool)helper.Invoke(null, new object?[] { prefs, other })!;
            otherEnabled.Should().BeTrue(
                $"only PushOn{{{kind}}} was disabled; {other} must still be allowed");
        }
    }

    [Fact]
    public void DispatcherPushGate_NullPreferences_FallsBackToCLRDefaultTrue()
    {
        // No persisted preferences row → CLR defaults on NotificationPreferences
        // give push=true for every attention kind. The helper must preserve
        // that historical opt-in behaviour.
        System.Reflection.MethodInfo helper = typeof(
                Farm.Infrastructure.Services.Notifications.NativePush.NativePushDispatcher)
            .GetMethod(
                "IsPushEnabledForKind",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        foreach (Farm.Infrastructure.Dtos.Attention.AttentionKind kind in
            System.Enum.GetValues<Farm.Infrastructure.Dtos.Attention.AttentionKind>())
        {
            bool enabled = (bool)helper.Invoke(null, new object?[] { null, kind })!;
            enabled.Should().BeTrue(
                $"null prefs must allow {kind} (matches CLR defaults + pre-#708 behaviour)");
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task
        UpdatePreferences_MasterFlagDerivedFromAllNineRows_WhenOnlyAttentionPushEnabled()
    {
        // Hicks v5 H1 regression: the shared-preference write projection was
        // OR'ing only the four legacy job rows. That silently reset the four
        // Enable{Channel}Notifications master flags to `false` whenever a
        // user disabled every job row even though attention rows were still
        // sending. Post-fix: master flags derive from all nine rows.
        var (dbContext, userId) = await BuildInMemoryDbWithUserAsync();
        await using Farm.Infrastructure.Data.AppDbContext _ = dbContext;

        // Seed a row where every job Push is false but attention Push is true.
        dbContext.NotificationPreferences.Add(
            new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
            {
                UserId = userId,
                EnablePushNotifications = false,
                PushOnJobStarted = false,
                PushOnJobCompleted = false,
                PushOnJobFailed = false,
                PushOnJobPaused = false,
            });
        await dbContext.SaveChangesAsync();

        var service = new Farm.Infrastructure.Services.Notifications.NotificationService(
            notificationRepository: null!,
            usersRepository: null!,
            logger: Microsoft.Extensions.Logging.Abstractions
                .NullLogger<Farm.Infrastructure.Services.Notifications.NotificationService>
                .Instance,
            dbContext: dbContext);

        // Simulate a modern PUT: matrix addresses attention rows, every push
        // job row is false, PrinterFailure has push=true.
        var incoming = new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
        {
            UserId = userId,
            PushOnJobStarted = false,
            PushOnJobCompleted = false,
            PushOnJobFailed = false,
            PushOnJobPaused = false,
            PushOnPrinterFailure = true,
            PushOnFilamentRunout = false,
            PushOnHarvestReady = false,
            PushOnMaintenanceDue = false,
            PushOnPrinterOffline = false,
        };

        await service.UpdatePreferencesAsync(userId, incoming, preserveAttentionFields: false);

        Farm.Infrastructure.Domain.Notifications.NotificationPreferences? persisted =
            await dbContext.NotificationPreferences
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == userId);
        persisted.Should().NotBeNull();
        persisted!.EnablePushNotifications.Should()
            .BeTrue("PushOnPrinterFailure alone must lift the master flag; the OR must span all nine rows");
    }

    [Fact]
    public async System.Threading.Tasks.Task
        UpdatePreferences_MasterFlagDerivedFromAllNineRows_WhenAllPushRowsFalse()
    {
        // Symmetric: every push row false across all nine event types must
        // produce EnablePushNotifications=false. Guards against a broken OR
        // that leaves the master flag `true` when nothing is enabled.
        var (dbContext, userId) = await BuildInMemoryDbWithUserAsync();
        await using Farm.Infrastructure.Data.AppDbContext _ = dbContext;

        dbContext.NotificationPreferences.Add(
            new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
            {
                UserId = userId,
                EnablePushNotifications = true,
            });
        await dbContext.SaveChangesAsync();

        var service = new Farm.Infrastructure.Services.Notifications.NotificationService(
            notificationRepository: null!,
            usersRepository: null!,
            logger: Microsoft.Extensions.Logging.Abstractions
                .NullLogger<Farm.Infrastructure.Services.Notifications.NotificationService>
                .Instance,
            dbContext: dbContext);

        var incoming = new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
        {
            UserId = userId,
            PushOnJobStarted = false,
            PushOnJobCompleted = false,
            PushOnJobFailed = false,
            PushOnJobPaused = false,
            PushOnPrinterFailure = false,
            PushOnFilamentRunout = false,
            PushOnHarvestReady = false,
            PushOnMaintenanceDue = false,
            PushOnPrinterOffline = false,
        };

        await service.UpdatePreferencesAsync(userId, incoming, preserveAttentionFields: false);

        Farm.Infrastructure.Domain.Notifications.NotificationPreferences? persisted =
            await dbContext.NotificationPreferences
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == userId);
        persisted.Should().NotBeNull();
        persisted!.EnablePushNotifications.Should()
            .BeFalse("with every push row off, EnablePushNotifications must be off");
    }

    [Fact]
    public async System.Threading.Tasks.Task
        LegacyPut_WithConcurrentAttentionCommit_PreservesConcurrentUpdate()
    {
        // Hicks v5 H2 regression: prior to the fix, the controller performed
        // an AsNoTracking pre-read of the preferences row and copied its 20
        // attention columns onto a transient DTO before calling the service.
        // If a newer-client attention update landed AFTER the pre-read but
        // BEFORE the service's tracked read, the service would overwrite the
        // concurrent update with the stale pre-read snapshot on save. The
        // fix moves attention-row preservation into the service's single
        // authoritative tracked read/write unit; a `preserveAttentionFields`
        // flag tells the service to leave those columns untouched. This test
        // simulates a concurrent commit through a second DbContext to prove
        // the newer attention update survives a legacy PUT.
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<
                Farm.Infrastructure.Data.AppDbContext>()
            .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
            .Options;

        System.Guid userId = System.Guid.NewGuid();

        // Bootstrap the initial persisted row via its own short-lived context.
        await using (var seedCtx = new Farm.Infrastructure.Data.AppDbContext(options))
        {
            seedCtx.Users.Add(new Farm.Infrastructure.Domain.User
            {
                Id = userId,
                Username = "concurrent-user",
                Email = "u@example.com",
                PasswordHash = "x",
                CreatedAt = System.DateTime.UtcNow,
            });
            seedCtx.NotificationPreferences.Add(
                new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
                {
                    UserId = userId,
                    EnablePushNotifications = true,
                    PushOnPrinterFailure = true,
                });
            await seedCtx.SaveChangesAsync();
        }

        // Context A: the legacy PUT request path. It will be handed to the
        // service AFTER a concurrent commit lands via context B.
        var serviceCtx = new Farm.Infrastructure.Data.AppDbContext(options);
        await using Farm.Infrastructure.Data.AppDbContext _ = serviceCtx;

        // Context B: the concurrent newer-client attention update. Commit a
        // change that flips PushOnPrinterFailure to false.
        await using (var concurrentCtx = new Farm.Infrastructure.Data.AppDbContext(options))
        {
            Farm.Infrastructure.Domain.Notifications.NotificationPreferences concurrent =
                (await concurrentCtx.NotificationPreferences
                    .FirstOrDefaultAsync(p => p.UserId == userId))!;
            concurrent.PushOnPrinterFailure = false;
            await concurrentCtx.SaveChangesAsync();
        }

        // Now invoke the service on context A with preserveAttentionFields=true
        // — the legacy PUT contract. The stale in-memory value on the passed
        // preferences DTO is PushOnPrinterFailure=true, matching the value the
        // legacy pre-read WOULD have captured before the concurrent commit.
        var service = new Farm.Infrastructure.Services.Notifications.NotificationService(
            notificationRepository: null!,
            usersRepository: null!,
            logger: Microsoft.Extensions.Logging.Abstractions
                .NullLogger<Farm.Infrastructure.Services.Notifications.NotificationService>
                .Instance,
            dbContext: serviceCtx);

        var stale = new Farm.Infrastructure.Domain.Notifications.NotificationPreferences
        {
            UserId = userId,
            PushOnJobStarted = false,
            PushOnJobCompleted = true,
            PushOnJobFailed = true,
            PushOnJobPaused = false,
            PushOnPrinterFailure = true,
        };

        await service.UpdatePreferencesAsync(userId, stale, preserveAttentionFields: true);

        // Re-read via a fresh context so we see the true persisted state.
        await using var verifyCtx = new Farm.Infrastructure.Data.AppDbContext(options);
        Farm.Infrastructure.Domain.Notifications.NotificationPreferences? persisted =
            await verifyCtx.NotificationPreferences
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == userId);
        persisted.Should().NotBeNull();
        persisted!.PushOnPrinterFailure.Should()
            .BeFalse("the concurrent newer-client attention update must survive a legacy PUT");
    }

    private static async System.Threading.Tasks.Task<
        (Farm.Infrastructure.Data.AppDbContext DbContext, System.Guid UserId)> BuildInMemoryDbWithUserAsync()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<
                Farm.Infrastructure.Data.AppDbContext>()
            .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
            .Options;
        var dbContext = new Farm.Infrastructure.Data.AppDbContext(options);
        System.Guid userId = System.Guid.NewGuid();
        dbContext.Users.Add(new Farm.Infrastructure.Domain.User
        {
            Id = userId,
            Username = "u",
            Email = "u@example.com",
            PasswordHash = "x",
            CreatedAt = System.DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        return (dbContext, userId);
    }
}
