using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Attention.Sources;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.Queue;
using FluentAssertions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Attention;

/// <summary>
/// Unit tests for <see cref="FailureAttentionSource"/> composition suppression (issue #707,
/// review R2). A failure card must only surface while it can be truthfully acted on: the
/// referenced job must still exist, remain on the same printer, and still be active, and the
/// incident must not already be resolved by a successful action.
/// </summary>
public sealed class FailureAttentionSourceTests
{
    private static readonly DateTime Now = new(2026, 07, 10, 12, 00, 00, DateTimeKind.Utc);

    private readonly Mock<IFailureDetectionIncidentHistoryService> _history = new(MockBehavior.Strict);
    private readonly Mock<IQueueDataService> _queue = new(MockBehavior.Loose);
    private readonly FixedTimeProvider _clock = new(Now);

    private FailureAttentionSource CreateSource() => new(_history.Object, _queue.Object, _clock);

    private void SetupIncidents(params FailureDetectionDto[] incidents)
    {
        _history.Setup(h => h.GetRecentAsync(null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(incidents.ToList());
    }

    private static FailureDetectionDto Incident(
        Guid incidentId,
        Guid printerId,
        Guid? jobId,
        DateTime detectedAt,
        DateTime? resolvedAtUtc = null,
        bool autoPaused = true)
        => new()
        {
            Id = incidentId,
            PrinterId = printerId,
            PrinterName = "P",
            JobId = jobId,
            Confidence = 0.9m,
            DetectedAt = detectedAt,
            AutoPaused = autoPaused,
            ResolvedAtUtc = resolvedAtUtc,
        };

    private static PrintJob Job(Guid jobId, Guid printerId, PrintJobStatus status)
        => new() { Id = jobId, AssignedPrinterId = printerId, Status = status, Name = "job" };

    private void SetupJob(Guid jobId, PrintJob? job)
        => _queue.Setup(q => q.GetPrintJobByIdAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);

    [Fact]
    public async Task ActiveMatchingJob_SurfacesCard()
    {
        Guid incidentId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        Guid job = Guid.NewGuid();
        SetupIncidents(Incident(incidentId, printer, job, Now.AddMinutes(-5)));
        SetupJob(job, Job(job, printer, PrintJobStatus.Paused));

        IReadOnlyList<AttentionItemDto> items = await CreateSource().GetItemsAsync(CancellationToken.None);

        items.Should().ContainSingle();
        items[0].JobId.Should().Be(job);
    }

    [Fact]
    public async Task ResolvedIncident_IsSuppressed()
    {
        Guid incidentId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        Guid job = Guid.NewGuid();
        SetupIncidents(Incident(incidentId, printer, job, Now.AddMinutes(-5), resolvedAtUtc: Now.AddMinutes(-1)));

        IReadOnlyList<AttentionItemDto> items = await CreateSource().GetItemsAsync(CancellationToken.None);

        items.Should().BeEmpty("a resolved incident must not surface even if the job is still active");
    }

    [Fact]
    public async Task MissingJobId_IsSuppressed()
    {
        Guid incidentId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        SetupIncidents(Incident(incidentId, printer, jobId: null, Now.AddMinutes(-5)));

        IReadOnlyList<AttentionItemDto> items = await CreateSource().GetItemsAsync(CancellationToken.None);

        items.Should().BeEmpty("an incident with no JobId cannot be verified and must be suppressed");
    }

    [Fact]
    public async Task MissingJob_IsSuppressed()
    {
        Guid incidentId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        Guid job = Guid.NewGuid();
        SetupIncidents(Incident(incidentId, printer, job, Now.AddMinutes(-5)));
        SetupJob(job, null);

        IReadOnlyList<AttentionItemDto> items = await CreateSource().GetItemsAsync(CancellationToken.None);

        items.Should().BeEmpty("a card whose job no longer exists is no longer actionable");
    }

    [Fact]
    public async Task MovedJob_IsSuppressed()
    {
        Guid incidentId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        Guid otherPrinter = Guid.NewGuid();
        Guid job = Guid.NewGuid();
        SetupIncidents(Incident(incidentId, printer, job, Now.AddMinutes(-5)));
        SetupJob(job, Job(job, otherPrinter, PrintJobStatus.Printing));

        IReadOnlyList<AttentionItemDto> items = await CreateSource().GetItemsAsync(CancellationToken.None);

        items.Should().BeEmpty("acting on a job that moved printers would mutate the wrong plate");
    }

    [Fact]
    public async Task SupersededJob_IsSuppressed()
    {
        Guid incidentId = Guid.NewGuid();
        Guid printer = Guid.NewGuid();
        Guid job = Guid.NewGuid();
        SetupIncidents(Incident(incidentId, printer, job, Now.AddMinutes(-5)));
        SetupJob(job, Job(job, printer, PrintJobStatus.Completed));

        IReadOnlyList<AttentionItemDto> items = await CreateSource().GetItemsAsync(CancellationToken.None);

        items.Should().BeEmpty("a completed/superseded job is no longer actionable and must not linger");
    }

    [Fact]
    public async Task GetItemsWithOriginAsync_ExtraRowSentinel_MarksCappedObservationIncomplete()
    {
        List<FailureDetectionDto> incidents = [];
        for (int index = 0; index < 51; index++)
        {
            Guid incidentId = Guid.NewGuid();
            Guid printerId = Guid.NewGuid();
            Guid jobId = Guid.NewGuid();
            incidents.Add(Incident(incidentId, printerId, jobId, Now.AddMinutes(-index)));
            SetupJob(jobId, Job(jobId, printerId, PrintJobStatus.Printing));
        }

        SetupIncidents([.. incidents]);
        FailureAttentionSource source = new(
            _history.Object,
            _queue.Object,
            _clock,
            new ConstantWatermarkReader(12));

        AttentionSourceResult result =
            await source.GetItemsWithOriginAsync(CancellationToken.None);

        result.Items.Should().HaveCount(50);
        result.IsAuthoritativeComplete.Should().BeFalse();
        result.IncompleteReasons.Should().Contain("failure-incident-cap");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTime nowUtc) => _now = new DateTimeOffset(nowUtc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class ConstantWatermarkReader(long value) : IMutationWatermarkReader
    {
        public Task<long> GetCurrentAsync(CancellationToken ct = default)
            => Task.FromResult(value);
    }
}
