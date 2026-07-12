using Farm.Infrastructure;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Attention.Sources;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Attention;

public class FilamentRunoutAttentionSourceTests
{
    [Fact]
    public async Task GetItemsAsync_ActiveRunout_MapsCriticalDeadlineWithoutSwapAction()
    {
        Guid printerId = Guid.NewGuid();
        DateTime runoutAt = new(2026, 7, 11, 4, 30, 0, DateTimeKind.Utc);
        Mock<IFilamentCoverageAttentionSource> coverage = Source(
            new FilamentRunoutWarningDto(
                printerId,
                "Printer One",
                ToolheadIndex: 1,
                SpoolId: 42,
                Material: "PLA",
                RemainingGrams: 12.5,
                PredictedRunoutAt: runoutAt,
                Reason: "runout-during-active-job"));
        FilamentRunoutAttentionSource source = new(coverage.Object);

        AttentionItemDto item = (await source.GetItemsAsync(CancellationToken.None)).Should().ContainSingle().Which;

        item.Id.Should().Be($"runout:{printerId:D}:toolhead:1");
        item.Kind.Should().Be(AttentionKind.Runout);
        item.Severity.Should().Be(AttentionSeverity.Critical);
        item.PrinterId.Should().Be(printerId);
        item.ToolheadIndex.Should().Be(1);
        item.DeadlineAt.Should().Be(runoutAt);
        item.OccurredAt.Should().Be(FilamentRunoutAttentionSource.StableRunoutOccurredAt);
        item.AllowFreshOccurrenceBypass.Should().BeFalse();
        item.Detail.Should().Contain("12.5 g").And.Contain("PLA");
        item.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(AttentionActionKind.Snooze);
    }

    [Fact]
    public async Task GetItemsAsync_AssignedQueueShortage_MapsWarningWithoutFabricatedDeadline()
    {
        Mock<IFilamentCoverageAttentionSource> coverage = Source(
            new FilamentRunoutWarningDto(
                Guid.NewGuid(),
                "Printer Two",
                ToolheadIndex: 0,
                SpoolId: null,
                Material: null,
                RemainingGrams: null,
                PredictedRunoutAt: null,
                Reason: "insufficient-for-assigned-queue"));
        FilamentRunoutAttentionSource source = new(coverage.Object);

        AttentionItemDto item = (await source.GetItemsAsync(CancellationToken.None)).Should().ContainSingle().Which;

        item.Severity.Should().Be(AttentionSeverity.Warning);
        item.DeadlineAt.Should().BeNull();
        item.Title.Should().Be("Queued filament shortage");
        item.Detail.Should().Contain("assigned queue");
    }

    [Fact]
    public async Task GetItemsAsync_UnknownOrMalformedWarning_SkipsItem()
    {
        Mock<IFilamentCoverageAttentionSource> coverage = Source(
            new FilamentRunoutWarningDto(
                Guid.NewGuid(),
                "Printer",
                ToolheadIndex: 0,
                SpoolId: 1,
                Material: "PLA",
                RemainingGrams: 5,
                PredictedRunoutAt: null,
                Reason: "runout-during-active-job"),
            new FilamentRunoutWarningDto(
                Guid.NewGuid(),
                "Printer",
                ToolheadIndex: 0,
                SpoolId: 1,
                Material: "PLA",
                RemainingGrams: 5,
                PredictedRunoutAt: null,
                Reason: "unknown"));
        FilamentRunoutAttentionSource source = new(coverage.Object);

        IReadOnlyList<AttentionItemDto> items = await source.GetItemsAsync(CancellationToken.None);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetItemsAsync_ForwardsCancellationToken()
    {
        using CancellationTokenSource cancellation = new();
        Mock<IFilamentCoverageAttentionSource> coverage = new(MockBehavior.Strict);
        coverage.Setup(x => x.GetRunoutWarningsAsync(cancellation.Token))
            .ReturnsAsync([]);
        FilamentRunoutAttentionSource source = new(coverage.Object);

        IReadOnlyList<AttentionItemDto> items = await source.GetItemsAsync(cancellation.Token);

        items.Should().BeEmpty();
        coverage.VerifyAll();
    }

    private static Mock<IFilamentCoverageAttentionSource> Source(params FilamentRunoutWarningDto[] warnings)
    {
        Mock<IFilamentCoverageAttentionSource> coverage = new(MockBehavior.Strict);
        coverage.Setup(x => x.GetRunoutWarningsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(warnings);
        return coverage;
    }
}
