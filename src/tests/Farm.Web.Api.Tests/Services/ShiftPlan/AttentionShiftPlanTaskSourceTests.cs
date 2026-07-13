using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.ShiftPlan;
using Farm.Infrastructure.Services.ShiftPlan.Sources;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.ShiftPlan;

public class AttentionShiftPlanTaskSourceTests
{
    private static readonly Guid PrinterId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task ProduceAsync_Runout_AnchorAtIsDeadlineMinusRunoutLeadMinutes()
    {
        DateTime deadline = DateTime.UtcNow.AddHours(3);
        AttentionItemDto runout = new(
            Id: "runout:printer:toolhead:0",
            Kind: AttentionKind.Runout,
            Severity: AttentionSeverity.Warning,
            PrinterId: PrinterId,
            PrinterName: "P1",
            Title: "Runout",
            Detail: "Predicted",
            OccurredAt: deadline.AddHours(-2),
            Actions: Array.Empty<AttentionActionDto>(),
            DeadlineAt: deadline);

        AttentionShiftPlanTaskSource src = BuildSource(new SpoolCoverageSettings { RunoutWarningLeadMinutes = 30 }, runout);

        IReadOnlyList<ShiftPlanTaskSpec> specs = await src.ProduceAsync(CancellationToken.None);

        ShiftPlanTaskSpec spec = Assert.Single(specs);
        Assert.Equal(UserTaskType.FilamentRunout, spec.TaskType);
        Assert.Equal(UserTaskSourceKind.FilamentCoverage, spec.SourceKind);
        Assert.Equal(UserTaskAnchorKind.At, spec.AnchorKind);
        Assert.Equal(deadline.AddMinutes(-30), spec.AnchorAtUtc);
        Assert.Equal(deadline, spec.DueAt);
    }

    [Fact]
    public async Task ProduceAsync_Runout_FallsBackToNow_WhenAnchorWouldBeInPast()
    {
        AttentionItemDto runout = new(
            Id: "runout:printer:toolhead:0",
            Kind: AttentionKind.Runout,
            Severity: AttentionSeverity.Critical,
            PrinterId: PrinterId,
            PrinterName: "P1",
            Title: "Runout imminent",
            Detail: "Very soon",
            OccurredAt: DateTime.UtcNow.AddMinutes(-5),
            Actions: Array.Empty<AttentionActionDto>(),
            DeadlineAt: DateTime.UtcNow.AddMinutes(5));

        AttentionShiftPlanTaskSource src = BuildSource(new SpoolCoverageSettings { RunoutWarningLeadMinutes = 30 }, runout);

        ShiftPlanTaskSpec spec = Assert.Single(await src.ProduceAsync(CancellationToken.None));
        Assert.Equal(UserTaskAnchorKind.Now, spec.AnchorKind);
        Assert.Null(spec.AnchorAtUtc);
    }

    [Fact]
    public async Task ProduceAsync_Failure_MapsToFailureClearAtAnchorNow()
    {
        AttentionItemDto failure = new(
            Id: "failure:incident1",
            Kind: AttentionKind.Failure,
            Severity: AttentionSeverity.Critical,
            PrinterId: PrinterId,
            PrinterName: "P1",
            Title: "Failure",
            Detail: "Clear jam",
            OccurredAt: DateTime.UtcNow,
            Actions: Array.Empty<AttentionActionDto>());

        AttentionShiftPlanTaskSource src = BuildSource(new SpoolCoverageSettings(), failure);

        ShiftPlanTaskSpec spec = Assert.Single(await src.ProduceAsync(CancellationToken.None));
        Assert.Equal(UserTaskType.FailureClear, spec.TaskType);
        Assert.Equal(UserTaskSourceKind.FailureIncident, spec.SourceKind);
        Assert.Equal(UserTaskAnchorKind.Now, spec.AnchorKind);
        Assert.Equal(UserTaskPriority.High, spec.Priority);
    }

    [Fact]
    public async Task ProduceAsync_Maintenance_IsNotEmitted_HandledElsewhere()
    {
        AttentionItemDto maint = new(
            Id: "maintenance:1",
            Kind: AttentionKind.Maintenance,
            Severity: AttentionSeverity.Warning,
            PrinterId: PrinterId,
            PrinterName: "P1",
            Title: "M",
            Detail: "",
            OccurredAt: DateTime.UtcNow,
            Actions: Array.Empty<AttentionActionDto>());

        AttentionShiftPlanTaskSource src = BuildSource(new SpoolCoverageSettings(), maint);
        Assert.Empty(await src.ProduceAsync(CancellationToken.None));
    }

    /// <summary>
    /// Fix 4: a failing inner IAttentionSource must propagate its exception outward so the
    /// compiler can suppress auto-complete for the whole attention source's OwnedKinds.
    /// Previous behavior swallowed the exception and continued with the remaining sources.
    /// </summary>
    [Fact]
    public async Task ProduceAsync_FailingAttentionSource_PropagatesException()
    {
        Mock<IAttentionSource> throwing = new();
        throwing.SetupGet(s => s.SourceName).Returns("bad");
        throwing.Setup(s => s.GetItemsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated"));

        Mock<IAttentionSource> good = new();
        good.SetupGet(s => s.SourceName).Returns("good");
        good.Setup(s => s.GetItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AttentionItemDto>());

        Mock<ISettingsService> settings = new();
        settings.Setup(s => s.Get<SpoolCoverageSettings>()).Returns(new SpoolCoverageSettings());

        AttentionShiftPlanTaskSource src = new(
            new[] { throwing.Object, good.Object },
            settings.Object,
            NullLogger<AttentionShiftPlanTaskSource>.Instance);

        // The compiler expects the exception to propagate so it can suppress auto-complete.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => src.ProduceAsync(CancellationToken.None));
    }

    /// <summary>Fix 4: OwnedKinds must include the three attention-source kinds.</summary>
    [Fact]
    public void OwnedKinds_ContainsExpectedSourceKinds()
    {
        AttentionShiftPlanTaskSource src = BuildSource(new SpoolCoverageSettings());
        Assert.Contains(UserTaskSourceKind.FailureIncident, src.OwnedKinds);
        Assert.Contains(UserTaskSourceKind.Harvest, src.OwnedKinds);
        Assert.Contains(UserTaskSourceKind.FilamentCoverage, src.OwnedKinds);
    }

    private static AttentionShiftPlanTaskSource BuildSource(SpoolCoverageSettings settings, params AttentionItemDto[] items)
    {
        Mock<IAttentionSource> attn = new();
        attn.SetupGet(s => s.SourceName).Returns("test");
        attn.Setup(s => s.GetItemsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(items);

        Mock<ISettingsService> svc = new();
        svc.Setup(s => s.Get<SpoolCoverageSettings>()).Returns(settings);

        return new AttentionShiftPlanTaskSource(
            new[] { attn.Object },
            svc.Object,
            NullLogger<AttentionShiftPlanTaskSource>.Instance);
    }
}
