using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Attention;

/// <summary>
/// Unit tests for <see cref="AttentionService"/>. Covers composition, dedupe,
/// severity + deadline ordering, per-user snooze isolation and expiry, healthy-printer
/// derivation, typed action validation, and action dispatch.
/// </summary>
public class AttentionServiceTests
{
    private static readonly DateTime Now = new(2026, 07, 10, 12, 00, 00, DateTimeKind.Utc);

    private readonly Mock<IAttentionSnoozeRepository> _snoozeRepo = new(MockBehavior.Strict);
    private readonly Mock<IPrintersService> _printers = new(MockBehavior.Strict);
    private readonly Mock<IMaintenanceAlertService> _maintenance = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _clock = new(Now);

    private AttentionService CreateService(IEnumerable<IAttentionSource> sources)
    {
        return new AttentionService(
            sources,
            _snoozeRepo.Object,
            _printers.Object,
            _maintenance.Object,
            NullLogger<AttentionService>.Instance,
            _clock);
    }

    private void SetupNoSnoozes(Guid userId)
    {
        _snoozeRepo.Setup(r => r.GetActiveForUserAsync(userId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Array.Empty<AttentionSnooze>());
    }

    private void SetupPrinters(params Printer[] printers)
    {
        _printers.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(printers.ToList());
    }

    private static Printer NewPrinter(Guid id, string name, bool enabled = true)
        => new() { Id = id, Name = name, IsEnabled = enabled };

    private static AttentionItemDto BuildItem(
        string id,
        AttentionKind kind,
        AttentionSeverity severity,
        Guid printerId,
        DateTime occurredAt,
        DateTime? deadlineAt = null,
        IReadOnlyList<AttentionActionDto>? actions = null)
    {
        return new AttentionItemDto(
            id,
            kind,
            severity,
            printerId,
            "printer",
            "title",
            "detail",
            occurredAt,
            actions ?? Array.Empty<AttentionActionDto>(),
            ToolheadIndex: null,
            DeadlineAt: deadlineAt);
    }

    [Fact]
    public async Task GetFeedAsync_MergesItemsFromAllSources_AndReturnsDedupedList()
    {
        Guid userId = Guid.NewGuid();
        Guid printerA = Guid.NewGuid();
        Guid printerB = Guid.NewGuid();
        SetupNoSnoozes(userId);
        SetupPrinters(NewPrinter(printerA, "A"), NewPrinter(printerB, "B"));

        AttentionItemDto item1 = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printerA, Now.AddMinutes(-5));
        AttentionItemDto item2 = BuildItem("maintenance:1", AttentionKind.Maintenance, AttentionSeverity.Warning, printerB, Now.AddMinutes(-10));
        AttentionItemDto duplicate = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printerA, Now.AddMinutes(-5));

        AttentionService svc = CreateService(new[]
        {
            new StubSource("failure", new[] { item1 }),
            new StubSource("maintenance", new[] { item2 }),
            new StubSource("dup", new[] { duplicate }),
        });

        AttentionFeedDto feed = await svc.GetFeedAsync(userId, cancellationToken: CancellationToken.None);

        feed.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { "failure:1", "maintenance:1" });
    }

    [Fact]
    public async Task GetFeedAsync_OrdersBySeverityThenDeadlineThenOccurredAt()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        SetupNoSnoozes(userId);
        SetupPrinters(NewPrinter(printer, "P"));

        AttentionItemDto oldest = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Warning, printer, Now.AddHours(-3));
        AttentionItemDto newerWarning = BuildItem("failure:2", AttentionKind.Failure, AttentionSeverity.Warning, printer, Now.AddHours(-1));
        AttentionItemDto critical = BuildItem("failure:3", AttentionKind.Failure, AttentionSeverity.Critical, printer, Now.AddHours(-2));
        AttentionItemDto infoWithDeadline = BuildItem("failure:4", AttentionKind.Failure, AttentionSeverity.Info, printer, Now.AddHours(-4), deadlineAt: Now.AddMinutes(5));

        AttentionService svc = CreateService(new[]
        {
            new StubSource("s", new[] { oldest, newerWarning, critical, infoWithDeadline }),
        });

        AttentionFeedDto feed = await svc.GetFeedAsync(userId, cancellationToken: CancellationToken.None);

        // Severity is the primary sort key ("severity × time-to-impact" from #707 with
        // severity primary). Within the same severity, nearest deadline wins, then oldest
        // OccurredAt breaks ties.
        feed.Items.Select(i => i.Id).Should().Equal("failure:3", "failure:1", "failure:2", "failure:4");
    }

    [Fact]
    public async Task GetFeedAsync_SuppressesActiveSnoozesForCallingUserOnly()
    {
        Guid userA = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        SetupPrinters(NewPrinter(printer, "P"));

        AttentionSnooze snooze = new()
        {
            Id = Guid.NewGuid(),
            UserId = userA,
            AttentionItemId = "failure:1",
            SnoozedUntilUtc = Now.AddHours(1),
            CreatedAtUtc = Now,
        };
        _snoozeRepo.Setup(r => r.GetActiveForUserAsync(userA, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { snooze });

        AttentionItemDto snoozed = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printer, Now);
        AttentionItemDto other = BuildItem("failure:2", AttentionKind.Failure, AttentionSeverity.Warning, printer, Now);

        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { snoozed, other }) });

        AttentionFeedDto feed = await svc.GetFeedAsync(userA, cancellationToken: CancellationToken.None);

        feed.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { "failure:2" });
    }

    [Fact]
    public async Task GetFeedAsync_PerUserSnoozeIsolation_UserBSeesItemUserASnoozed()
    {
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        SetupPrinters(NewPrinter(printer, "P"));

        AttentionSnooze aSnooze = new()
        {
            Id = Guid.NewGuid(),
            UserId = userA,
            AttentionItemId = "failure:1",
            SnoozedUntilUtc = Now.AddHours(1),
            CreatedAtUtc = Now,
        };
        _snoozeRepo.Setup(r => r.GetActiveForUserAsync(userA, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { aSnooze });
        _snoozeRepo.Setup(r => r.GetActiveForUserAsync(userB, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Array.Empty<AttentionSnooze>());

        AttentionItemDto item = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printer, Now);
        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { item }) });

        AttentionFeedDto feedA = await svc.GetFeedAsync(userA, cancellationToken: CancellationToken.None);
        AttentionFeedDto feedB = await svc.GetFeedAsync(userB, cancellationToken: CancellationToken.None);

        feedA.Items.Should().BeEmpty();
        feedB.Items.Should().HaveCount(1).And.ContainSingle(i => i.Id == "failure:1");
    }

    [Fact]
    public async Task GetFeedAsync_HealthyPrinters_ExcludeDisabledAndPrintersWithItems()
    {
        Guid userId = Guid.NewGuid();
        Guid pWithItem = Guid.NewGuid();
        Guid pHealthy = Guid.NewGuid();
        Guid pDisabled = Guid.NewGuid();
        SetupNoSnoozes(userId);
        SetupPrinters(
            NewPrinter(pWithItem, "busy"),
            NewPrinter(pHealthy, "healthy"),
            NewPrinter(pDisabled, "disabled", enabled: false));

        AttentionItemDto item = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, pWithItem, Now);
        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { item }) });

        AttentionFeedDto feed = await svc.GetFeedAsync(userId, cancellationToken: CancellationToken.None);

        feed.HealthyPrinterIds.Should().BeEquivalentTo(new[] { pHealthy });
    }

    [Fact]
    public async Task GetFeedAsync_ThrowingSourceIsLoggedAndSkipped_DoesNotBlankFeed()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        SetupNoSnoozes(userId);
        SetupPrinters(NewPrinter(printer, "P"));

        AttentionItemDto ok = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printer, Now);
        AttentionService svc = CreateService(new IAttentionSource[]
        {
            new ThrowingSource("boom"),
            new StubSource("ok", new[] { ok }),
        });

        AttentionFeedDto feed = await svc.GetFeedAsync(userId, cancellationToken: CancellationToken.None);

        feed.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { "failure:1" });
    }

    [Fact]
    public async Task SnoozeAsync_WithPastDeadline_ReturnsFailure()
    {
        Guid userId = Guid.NewGuid();
        AttentionService svc = CreateService(Array.Empty<IAttentionSource>());

        SnoozeResult result = await svc.SnoozeAsync(userId, "failure:1", Now.AddMinutes(-1), attentionItemAnchorAtUtc: null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Reason.Should().Contain("future");
        _snoozeRepo.Verify(
            r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SnoozeAsync_WithFutureDeadline_PersistsSnooze()
    {
        Guid userId = Guid.NewGuid();
        DateTime until = Now.AddHours(1);
        AttentionSnooze snooze = new() { Id = Guid.NewGuid(), UserId = userId, AttentionItemId = "failure:1", SnoozedUntilUtc = until, CreatedAtUtc = Now };
        _snoozeRepo.Setup(r => r.UpsertAsync(userId, "failure:1", until, It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(snooze);

        AttentionService svc = CreateService(Array.Empty<IAttentionSource>());

        SnoozeResult result = await svc.SnoozeAsync(userId, "failure:1", until, attentionItemAnchorAtUtc: null, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Snooze.Should().BeSameAs(snooze);
    }

    [Fact]
    public async Task ClearSnoozeAsync_WhenRepoReportsRemoved_ReturnsSuccess()
    {
        Guid userId = Guid.NewGuid();
        _snoozeRepo.Setup(r => r.RemoveAsync(userId, "failure:1", It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);

        AttentionService svc = CreateService(Array.Empty<IAttentionSource>());
        SnoozeResult result = await svc.ClearSnoozeAsync(userId, "failure:1", CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteActionAsync_UnknownItem_ReturnsNotFound()
    {
        Guid userId = Guid.NewGuid();
        AttentionService svc = CreateService(new[] { new StubSource("s", Array.Empty<AttentionItemDto>()) });

        AttentionActionResult result = await svc.ExecuteActionAsync(userId, "user", "failure:missing", AttentionActionKind.Pause, CancellationToken.None);

        result.Outcome.Should().Be(AttentionActionOutcome.NotFound);
    }

    [Fact]
    public async Task ExecuteActionAsync_ActionNotOfferedByItem_ReturnsInvalidAction()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        AttentionActionDto pauseOnly = new(AttentionActionKind.Pause, "Pause", false);
        AttentionItemDto item = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printer, Now, actions: new[] { pauseOnly });

        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { item }) });

        AttentionActionResult result = await svc.ExecuteActionAsync(userId, "user", "failure:1", AttentionActionKind.Cancel, CancellationToken.None);

        result.Outcome.Should().Be(AttentionActionOutcome.InvalidAction);
    }

    [Fact]
    public async Task ExecuteActionAsync_FailurePause_DelegatesToPrintersService()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        AttentionActionDto pause = new(AttentionActionKind.Pause, "Pause", true);
        AttentionItemDto item = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printer, Now, actions: new[] { pause });

        _printers.Setup(p => p.PauseAsync(printer, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { item }) });

        AttentionActionResult result = await svc.ExecuteActionAsync(userId, "user", "failure:1", AttentionActionKind.Pause, CancellationToken.None);

        result.Outcome.Should().Be(AttentionActionOutcome.Ok);
        _printers.Verify(p => p.PauseAsync(printer, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteActionAsync_FailurePauseWithBusyBackend_ReturnsConflict()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        AttentionActionDto pause = new(AttentionActionKind.Pause, "Pause", true);
        AttentionItemDto item = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printer, Now, actions: new[] { pause });

        _printers.Setup(p => p.PauseAsync(printer, It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new PrinterBackendBusyException("backend busy"));

        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { item }) });

        AttentionActionResult result = await svc.ExecuteActionAsync(userId, "user", "failure:1", AttentionActionKind.Pause, CancellationToken.None);

        result.Outcome.Should().Be(AttentionActionOutcome.Conflict);
    }

    [Fact]
    public async Task ExecuteActionAsync_MaintenanceResolve_ParsesGuidAndDelegates()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        Guid alertId = Guid.NewGuid();
        string itemId = AttentionIdPrefixes.Build(AttentionIdPrefixes.Maintenance, alertId);
        AttentionActionDto resolve = new(AttentionActionKind.Resolve, "Resolve", true);
        AttentionItemDto item = BuildItem(itemId, AttentionKind.Maintenance, AttentionSeverity.Warning, printer, Now, actions: new[] { resolve });

        _maintenance.Setup(m => m.ResolveAlertAsync(alertId, "user", It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { item }) });

        AttentionActionResult result = await svc.ExecuteActionAsync(userId, "user", itemId, AttentionActionKind.Resolve, CancellationToken.None);

        result.Outcome.Should().Be(AttentionActionOutcome.Ok);
        _maintenance.Verify(m => m.ResolveAlertAsync(alertId, "user", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteActionAsync_HarvestKind_ReturnsNotImplemented()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        AttentionActionDto harvest = new(AttentionActionKind.Harvest, "Harvest", false);
        AttentionItemDto item = BuildItem("harvest:1", AttentionKind.Harvest, AttentionSeverity.Info, printer, Now, actions: new[] { harvest });

        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { item }) });

        AttentionActionResult result = await svc.ExecuteActionAsync(userId, "user", "harvest:1", AttentionActionKind.Harvest, CancellationToken.None);

        result.Outcome.Should().Be(AttentionActionOutcome.NotImplemented);
    }

    [Fact]
    public async Task ExecuteActionAsync_SnoozeActionKind_RedirectsToDedicatedEndpoint()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        AttentionActionDto snooze = new(AttentionActionKind.Snooze, "Snooze", false);
        AttentionItemDto item = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Warning, printer, Now, actions: new[] { snooze });

        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { item }) });

        AttentionActionResult result = await svc.ExecuteActionAsync(userId, "user", "failure:1", AttentionActionKind.Snooze, CancellationToken.None);

        result.Outcome.Should().Be(AttentionActionOutcome.InvalidAction);
        result.Reason.Should().Contain("snooze");
    }

    [Fact]
    public async Task ExecuteActionAsync_OfflineKind_ReturnsInvalidAction()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        AttentionActionDto pause = new(AttentionActionKind.Pause, "Pause", true);
        AttentionItemDto item = BuildItem("offline:" + printer.ToString("D"), AttentionKind.Offline, AttentionSeverity.Warning, printer, Now, actions: new[] { pause });

        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { item }) });

        AttentionActionResult result = await svc.ExecuteActionAsync(userId, "user", item.Id, AttentionActionKind.Pause, CancellationToken.None);

        result.Outcome.Should().Be(AttentionActionOutcome.InvalidAction);
    }

    [Fact]
    public async Task GetFeedAsync_FreshOccurrenceBypass_ShowsItemWhenOccurredAtExceedsAnchor()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        SetupPrinters(NewPrinter(printer, "P"));

        // Snooze anchored at t-1h; fresh occurrence at t-5m must bypass the snooze.
        AttentionSnooze snooze = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AttentionItemId = "failure:1",
            SnoozedUntilUtc = Now.AddHours(2),
            CreatedAtUtc = Now.AddHours(-1),
            AttentionItemAnchorAtUtc = Now.AddHours(-1),
        };
        _snoozeRepo.Setup(r => r.GetActiveForUserAsync(userId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { snooze });

        AttentionItemDto fresh = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printer, Now.AddMinutes(-5));
        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { fresh }) });

        AttentionFeedDto feed = await svc.GetFeedAsync(userId, cancellationToken: CancellationToken.None);

        feed.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { "failure:1" });
    }

    [Fact]
    public async Task GetFeedAsync_FreshOccurrenceBypass_StaleOccurrenceStillSnoozed()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        SetupPrinters(NewPrinter(printer, "P"));

        // Anchor equals item OccurredAt exactly — bypass requires strictly greater.
        DateTime anchor = Now.AddMinutes(-30);
        AttentionSnooze snooze = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AttentionItemId = "failure:1",
            SnoozedUntilUtc = Now.AddHours(2),
            CreatedAtUtc = Now.AddMinutes(-30),
            AttentionItemAnchorAtUtc = anchor,
        };
        _snoozeRepo.Setup(r => r.GetActiveForUserAsync(userId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { snooze });

        AttentionItemDto stale = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printer, anchor);
        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { stale }) });

        AttentionFeedDto feed = await svc.GetFeedAsync(userId, cancellationToken: CancellationToken.None);

        feed.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFeedAsync_SnoozeWithoutAnchor_SuppressesItemUnconditionally()
    {
        // Legacy snoozes (no anchor recorded) always suppress. Regression guard so the
        // fresh-occurrence bypass never accidentally re-shows a legacy-snoozed item.
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        SetupPrinters(NewPrinter(printer, "P"));

        AttentionSnooze snooze = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AttentionItemId = "failure:1",
            SnoozedUntilUtc = Now.AddHours(2),
            CreatedAtUtc = Now,
            AttentionItemAnchorAtUtc = null,
        };
        _snoozeRepo.Setup(r => r.GetActiveForUserAsync(userId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { snooze });

        AttentionItemDto item = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printer, Now.AddHours(5));
        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { item }) });

        AttentionFeedDto feed = await svc.GetFeedAsync(userId, cancellationToken: CancellationToken.None);

        feed.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFeedAsync_Pagination_ClampsPageAndCapsPageSize()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        SetupNoSnoozes(userId);
        SetupPrinters(NewPrinter(printer, "P"));

        List<AttentionItemDto> items = new();
        for (int i = 0; i < 5; i++)
        {
            items.Add(BuildItem($"failure:{i}", AttentionKind.Failure, AttentionSeverity.Warning, printer, Now.AddMinutes(-i)));
        }
        AttentionService svc = CreateService(new[] { new StubSource("s", items) });

        AttentionFeedDto page = await svc.GetFeedAsync(userId, page: -3, pageSize: 2, cancellationToken: CancellationToken.None);

        // page <= 0 clamps to 1; pageSize honoured (2).
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(2);
        page.TotalCount.Should().Be(5);
        page.TotalPages.Should().Be(3);
        page.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetFeedAsync_Pagination_PageSizeAboveMaxIsCapped()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        SetupNoSnoozes(userId);
        SetupPrinters(NewPrinter(printer, "P"));

        AttentionService svc = CreateService(new[] { new StubSource("s", Array.Empty<AttentionItemDto>()) });

        AttentionFeedDto feed = await svc.GetFeedAsync(userId, page: 1, pageSize: 5_000, cancellationToken: CancellationToken.None);

        feed.PageSize.Should().Be(AttentionService.MaxPageSize);
    }

    [Fact]
    public async Task SnoozeAsync_DerivesAnchorFromCurrentSourcesWhenCallerOmitsIt()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        DateTime until = Now.AddHours(1);
        DateTime occurredAt = Now.AddMinutes(-15);

        AttentionItemDto item = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printer, occurredAt);
        AttentionSnooze persisted = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AttentionItemId = "failure:1",
            SnoozedUntilUtc = until,
            CreatedAtUtc = Now,
            AttentionItemAnchorAtUtc = occurredAt,
        };
        _snoozeRepo.Setup(r => r.UpsertAsync(userId, "failure:1", until, It.IsAny<DateTime>(), occurredAt, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(persisted);

        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { item }) });

        SnoozeResult result = await svc.SnoozeAsync(userId, "failure:1", until, attentionItemAnchorAtUtc: null, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Snooze!.AttentionItemAnchorAtUtc.Should().Be(occurredAt);
        _snoozeRepo.Verify(
            r => r.UpsertAsync(userId, "failure:1", until, It.IsAny<DateTime>(), occurredAt, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SnoozeAsync_UsesCallerSuppliedAnchorAsIs()
    {
        Guid userId = Guid.NewGuid();
        DateTime until = Now.AddHours(1);
        DateTime anchor = Now.AddMinutes(-42);
        AttentionSnooze persisted = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AttentionItemId = "failure:1",
            SnoozedUntilUtc = until,
            CreatedAtUtc = Now,
            AttentionItemAnchorAtUtc = anchor,
        };
        _snoozeRepo.Setup(r => r.UpsertAsync(userId, "failure:1", until, It.IsAny<DateTime>(), anchor, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(persisted);

        AttentionService svc = CreateService(Array.Empty<IAttentionSource>());

        SnoozeResult result = await svc.SnoozeAsync(userId, "failure:1", until, attentionItemAnchorAtUtc: anchor, CancellationToken.None);

        result.Success.Should().BeTrue();
        _snoozeRepo.Verify(
            r => r.UpsertAsync(userId, "failure:1", until, It.IsAny<DateTime>(), anchor, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ClearSnoozeAsync_WhenRepoReportsNothingRemoved_ReturnsNotFound()
    {
        Guid userId = Guid.NewGuid();
        _snoozeRepo.Setup(r => r.RemoveAsync(userId, "failure:1", It.IsAny<CancellationToken>()))
                   .ReturnsAsync(false);

        AttentionService svc = CreateService(Array.Empty<IAttentionSource>());
        SnoozeResult result = await svc.ClearSnoozeAsync(userId, "failure:1", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteActionAsync_MaintenanceMalformedItemId_ReturnsFailed()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        AttentionActionDto resolve = new(AttentionActionKind.Resolve, "Resolve", true);
        // Malformed id: prefix ok but suffix is not a Guid.
        AttentionItemDto item = BuildItem("maintenance:not-a-guid", AttentionKind.Maintenance, AttentionSeverity.Warning, printer, Now, actions: new[] { resolve });

        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { item }) });

        AttentionActionResult result = await svc.ExecuteActionAsync(userId, "user", item.Id, AttentionActionKind.Resolve, CancellationToken.None);

        result.Outcome.Should().Be(AttentionActionOutcome.Failed);
    }

    [Fact]
    public async Task FindItemAsync_HonoursSnoozeUnlessFreshOccurrenceBypasses()
    {
        Guid userId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();

        AttentionSnooze snooze = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AttentionItemId = "failure:1",
            SnoozedUntilUtc = Now.AddHours(2),
            CreatedAtUtc = Now.AddMinutes(-10),
            AttentionItemAnchorAtUtc = Now.AddMinutes(-10),
        };
        _snoozeRepo.Setup(r => r.GetActiveForUserAsync(userId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { snooze });

        AttentionItemDto stale = BuildItem("failure:1", AttentionKind.Failure, AttentionSeverity.Critical, printer, Now.AddMinutes(-15));
        AttentionService svc = CreateService(new[] { new StubSource("s", new[] { stale }) });

        AttentionItemDto? hidden = await svc.FindItemAsync(userId, "failure:1", CancellationToken.None);
        hidden.Should().BeNull();
    }

    private sealed class StubSource : IAttentionSource
    {
        private readonly IReadOnlyList<AttentionItemDto> _items;
        public StubSource(string name, IReadOnlyList<AttentionItemDto> items)
        {
            SourceName = name;
            _items = items;
        }

        public string SourceName { get; }

        public Task<IReadOnlyList<AttentionItemDto>> GetItemsAsync(CancellationToken cancellationToken)
            => Task.FromResult(_items);
    }

    private sealed class ThrowingSource : IAttentionSource
    {
        public ThrowingSource(string name) { SourceName = name; }
        public string SourceName { get; }
        public Task<IReadOnlyList<AttentionItemDto>> GetItemsAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTime nowUtc) { _now = new DateTimeOffset(nowUtc, TimeSpan.Zero); }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
