using Farm.Infrastructure;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Attention.Sources;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Attention;

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

    [Fact]
    public async Task GetItemsAsync_ActiveRunout_NoBackup_StaysCritical()
    {
        Mock<IFilamentCoverageAttentionSource> coverage = Source(ActiveRunout());
        Mock<IFilamentRunoutSwitchEvaluator> evaluator = Evaluator(RunoutSwitchAssessment.NoBackup);
        FilamentRunoutAttentionSource source = new(coverage.Object, evaluator.Object, EnabledGate());

        AttentionItemDto item = (await source.GetItemsAsync(CancellationToken.None)).Should().ContainSingle().Which;

        item.Severity.Should().Be(AttentionSeverity.Critical);
        item.Title.Should().Be("Filament runout predicted");
        item.DeadlineAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetItemsAsync_ActiveRunout_BackupAvailableButNoTelemetry_DowngradesToWarning()
    {
        Mock<IFilamentCoverageAttentionSource> coverage = Source(ActiveRunout());
        Mock<IFilamentRunoutSwitchEvaluator> evaluator = Evaluator(RunoutSwitchAssessment.BackupAvailable);
        FilamentRunoutAttentionSource source = new(coverage.Object, evaluator.Object, EnabledGate());

        AttentionItemDto item = (await source.GetItemsAsync(CancellationToken.None)).Should().ContainSingle().Which;

        item.Severity.Should().Be(AttentionSeverity.Warning);
        item.Detail.Should().Contain("no switch has been confirmed");
        item.DeadlineAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetItemsAsync_ActiveRunout_ConfirmedSwitch_DowngradesToInformational()
    {
        Mock<IFilamentCoverageAttentionSource> coverage = Source(ActiveRunout());
        Mock<IFilamentRunoutSwitchEvaluator> evaluator = Evaluator(RunoutSwitchAssessment.SwitchConfirmed);
        FilamentRunoutAttentionSource source = new(coverage.Object, evaluator.Object, EnabledGate());

        AttentionItemDto item = (await source.GetItemsAsync(CancellationToken.None)).Should().ContainSingle().Which;

        item.Severity.Should().Be(AttentionSeverity.Info);
        item.Title.Should().Be("Filament auto-switch confirmed");
        item.DeadlineAt.Should().BeNull();
    }

    [Fact]
    public async Task GetItemsAsync_ActiveRunout_FeatureDisabled_StaysCriticalWithoutConsultingEvaluator()
    {
        Mock<IFilamentCoverageAttentionSource> coverage = Source(ActiveRunout());
        Mock<IFilamentRunoutSwitchEvaluator> evaluator = new(MockBehavior.Strict);
        Mock<IOperatorFeatureGate> gate = new(MockBehavior.Strict);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(false);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        FilamentRunoutAttentionSource source = new(coverage.Object, evaluator.Object, gate.Object);

        AttentionItemDto item = (await source.GetItemsAsync(CancellationToken.None)).Should().ContainSingle().Which;

        item.Severity.Should().Be(AttentionSeverity.Critical);
        evaluator.Verify(
            e => e.AssessAsync(It.IsAny<FilamentRunoutWarningDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetItemsAsync_ActiveRunout_MmuGateZeroBasedIndex_DisplaysToolZeroNotToolOne()
    {
        // Issue #711 round-19 Finding M19-2: warning.ToolheadIndex is now the 0-based G-code
        // index (matching the mapped ToolheadCoverageDto contract). MMU gate 1 maps to G-code T0,
        // so the attention text must say "tool 0" — the old "+1" double-add displayed "tool 2"
        // for gate 1 instead of the correct "tool 0".
        Guid printerId = Guid.NewGuid();
        DateTime runoutAt = new(2026, 7, 11, 4, 30, 0, DateTimeKind.Utc);
        Mock<IFilamentCoverageAttentionSource> coverage = Source(
            new FilamentRunoutWarningDto(
                printerId,
                "Printer One",
                ToolheadIndex: 0,
                SpoolId: 42,
                Material: "PLA",
                RemainingGrams: 12.5,
                PredictedRunoutAt: runoutAt,
                Reason: "runout-during-active-job"));
        FilamentRunoutAttentionSource source = new(coverage.Object);

        AttentionItemDto item = (await source.GetItemsAsync(CancellationToken.None)).Should().ContainSingle().Which;

        item.Detail.Should().Contain("tool 0");
        item.Detail.Should().NotContain("tool 1");
        item.ToolheadIndex.Should().Be(0);
    }

    [Fact]
    public async Task GetItemsWithOriginAsync_PreservesCoverageOrigin()
    {
        Mock<IFilamentCoverageAttentionSource> coverage = new(MockBehavior.Strict);
        coverage
            .Setup(x => x.GetRunoutWarningsWithOriginAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentCoverageResult<IReadOnlyList<FilamentRunoutWarningDto>>(
                [ActiveRunout()],
                OriginWatermark: 23));
        FilamentRunoutAttentionSource source = new(coverage.Object);

        AttentionSourceResult result =
            await source.GetItemsWithOriginAsync(CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.OriginWatermark.Should().Be(23);
        result.IsAuthoritativeComplete.Should().BeTrue();
        result.AuthorityKind.Should().Be(AttentionKind.Runout);
    }

    [Fact]
    public async Task GetItemsWithOriginAsync_NullCoverageOrigin_IsIncomplete()
    {
        Mock<IFilamentCoverageAttentionSource> coverage = new(MockBehavior.Strict);
        coverage
            .Setup(x => x.GetRunoutWarningsWithOriginAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentCoverageResult<IReadOnlyList<FilamentRunoutWarningDto>>(
                [],
                OriginWatermark: null));
        FilamentRunoutAttentionSource source = new(coverage.Object);

        AttentionSourceResult result =
            await source.GetItemsWithOriginAsync(CancellationToken.None);

        result.IsAuthoritativeComplete.Should().BeFalse();
        result.IncompleteReasons.Should().Contain("filament-coverage-origin-unproven");
    }

    private static FilamentRunoutWarningDto ActiveRunout()
        => new(
            Guid.NewGuid(),
            "Printer One",
            ToolheadIndex: 1,
            SpoolId: 42,
            Material: "PLA",
            RemainingGrams: 12.5,
            PredictedRunoutAt: new DateTime(2026, 7, 11, 4, 30, 0, DateTimeKind.Utc),
            Reason: "runout-during-active-job");

    private static Mock<IFilamentRunoutSwitchEvaluator> Evaluator(RunoutSwitchAssessment assessment)
    {
        Mock<IFilamentRunoutSwitchEvaluator> evaluator = new(MockBehavior.Strict);
        evaluator
            .Setup(e => e.AssessAsync(It.IsAny<FilamentRunoutWarningDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(assessment);
        return evaluator;
    }

    private static IOperatorFeatureGate EnabledGate()
    {
        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return gate.Object;
    }

    private static Mock<IFilamentCoverageAttentionSource> Source(params FilamentRunoutWarningDto[] warnings)
    {
        Mock<IFilamentCoverageAttentionSource> coverage = new(MockBehavior.Strict);
        coverage.Setup(x => x.GetRunoutWarningsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(warnings);
        return coverage;
    }
}
