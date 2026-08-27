using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.AutoDispatch;

/// <summary>
/// Regression coverage for issue #1495: <see cref="AutoDispatchService.GetAllStatusAsync"/> must
/// score each unassigned queued job exactly once (J scorer calls total) rather than once per
/// (printer, job) pair (P x J), while producing an output that is byte-identical to the
/// per-printer <see cref="AutoDispatchService.GetStatusAsync"/> path — which still rebuilds its
/// own scoring context per call and therefore reflects the pre-refactor, unbatched semantics.
/// </summary>
public sealed class AutoDispatchStatusScoringEquivalenceTests : IDisposable
{
    private const int PrinterCount = 10;
    private const int UnassignedJobCount = 20;

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public AutoDispatchStatusScoringEquivalenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetAllStatusAsync_ScoresEachUnassignedJobOnce_AndMatchesPerPrinterReference()
    {
        List<Printer> printers = [];
        for (int i = 0; i < PrinterCount; i++)
        {
            printers.Add(await CreatePrinterAsync(i));
        }

        List<PrintJob> unassignedJobs = [];
        for (int i = 0; i < UnassignedJobCount; i++)
        {
            unassignedJobs.Add(await CreateUnassignedQueuedJobAsync($"unassigned-job-{i}", queuePosition: i + 1));
        }

        DispatchSettings? existingSettings = _db.DispatchSettings.FirstOrDefault();
        if (existingSettings is not null)
        {
            existingSettings.MinimumScoreThreshold = 10;
        }
        else
        {
            _db.DispatchSettings.Add(new DispatchSettings { MinimumScoreThreshold = 10 });
        }

        await _db.SaveChangesAsync();

        // A deterministic, invocation-counting scorer: every printer/job pair gets a stable
        // score derived from the job/printer ordinal so eligibility varies across jobs and
        // printers (proving the batched map is actually consulted per printer, not just for the
        // first one), while every call to ScorePrintersForJobAsync(jobId, ...) returns the exact
        // same result regardless of who is asking, mirroring real scorer behavior.
        Dictionary<Guid, int> invocationsPerJob = [];
        Mock<IDispatchScorer> countingScorer = CreateDeterministicScorer(printers, unassignedJobs, invocationsPerJob);

        var (hubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService batchedService = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            dispatchScorer: countingScorer.Object);

        AutoDispatchGlobalStatusDto batchedResult = await batchedService.GetAllStatusAsync();

        // Correctness gate #1: each unassigned job scored exactly once (J calls), not once per
        // (printer, job) pair (P x J).
        invocationsPerJob.Should().HaveCount(UnassignedJobCount);
        invocationsPerJob.Values.Should().OnlyContain(count => count == 1);

        // Reference path: loop GetStatusAsync per printer. This still rebuilds its own scoring
        // context on every call (via the single-printer GetQueuedJobSelectionAsync overload), so
        // it reproduces the pre-refactor, unbatched (P x J) computation exactly.
        Dictionary<Guid, int> referenceInvocationsPerJob = [];
        Mock<IDispatchScorer> referenceScorer = CreateDeterministicScorer(printers, unassignedJobs, referenceInvocationsPerJob);
        var (referenceHubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService referenceService = new(
            _db,
            referenceHubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            dispatchScorer: referenceScorer.Object);

        List<AutoDispatchStatusDto> referenceStatuses = [];
        foreach (Printer printer in printers.OrderBy(p => p.Id))
        {
            referenceStatuses.Add(await referenceService.GetStatusAsync(printer.Id));
        }

        AutoDispatchGlobalStatusDto referenceResult = new()
        {
            GlobalEnabled = printers.Any(p => p.AutoDispatchEnabled),
            Printers = referenceStatuses,
        };

        // The reference (unbatched) path re-scores every job once per printer: P x J calls.
        referenceInvocationsPerJob.Should().HaveCount(UnassignedJobCount);
        referenceInvocationsPerJob.Values.Should().OnlyContain(count => count == PrinterCount);

        // Correctness gate #2: byte-identical DTO between the batched and unbatched paths for
        // the same seeded state, once both are ordered by printer id so ordering differences in
        // list construction don't produce a spurious mismatch.
        string batchedJson = SerializeOrderedByPrinter(batchedResult);
        string referenceJson = SerializeOrderedByPrinter(referenceResult);
        batchedJson.Should().Be(referenceJson);
    }

    private static string SerializeOrderedByPrinter(AutoDispatchGlobalStatusDto dto)
    {
        AutoDispatchGlobalStatusDto ordered = new()
        {
            GlobalEnabled = dto.GlobalEnabled,
            Printers = [.. dto.Printers.OrderBy(p => p.PrinterId)],
        };

        // CheckedAt is a wall-clock timestamp captured independently by each call and is not
        // part of the scoring/selection logic under test, so it is normalized out before the
        // byte-identical comparison to avoid a spurious mismatch from real elapsed time between
        // the batched and reference calls.
        foreach (AutoDispatchStatusDto printerStatus in ordered.Printers)
        {
            foreach (ReadyGateCheckDto gateCheck in printerStatus.ReadyGateChecks)
            {
                gateCheck.CheckedAt = string.Empty;
            }
        }

        return JsonSerializer.Serialize(ordered);
    }

    private static Mock<IDispatchScorer> CreateDeterministicScorer(
        List<Printer> printers,
        List<PrintJob> unassignedJobs,
        Dictionary<Guid, int> invocationsPerJob)
    {
        Mock<IDispatchScorer> scorer = new();
        foreach (PrintJob job in unassignedJobs)
        {
            int jobOrdinal = unassignedJobs.IndexOf(job);
            List<DispatchScore> scores = [];
            for (int printerOrdinal = 0; printerOrdinal < printers.Count; printerOrdinal++)
            {
                Printer printer = printers[printerOrdinal];

                // Deterministic score/elimination pattern that varies by (job, printer) pair so
                // the equivalence test actually exercises different eligibility outcomes.
                bool eliminated = (jobOrdinal + printerOrdinal) % 5 == 0;
                double totalScore = eliminated ? 0 : 5 + ((jobOrdinal * 3 + printerOrdinal * 7) % 90);
                scores.Add(new DispatchScore(
                    printer.Id,
                    printer.Name,
                    totalScore,
                    ScoreBreakdown: new Dictionary<string, FactorScore>(),
                    Eliminated: eliminated,
                    EliminationReasons: eliminated ? ["deterministic-test-elimination"] : []));
            }

            scorer
                .Setup(s => s.ScorePrintersForJobAsync(job.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(scores)
                .Callback(() =>
                {
                    invocationsPerJob[job.Id] = invocationsPerJob.GetValueOrDefault(job.Id) + 1;
                });
        }

        return scorer;
    }

    private async Task<Printer> CreatePrinterAsync(int index)
    {
        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Test Manufacturer {index}",
        };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Test Model {index}",
            ManufacturerId = manufacturer.Id,
        };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Equivalence Test Printer {index}",
            ServerUrl = $"http://autodispatch-equivalence-test-{Guid.NewGuid():N}.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            AutoDispatchEnabled = true,
            IsEnabled = true,
            IsAvailable = true,
        };

        _db.Manufacturers.Add(manufacturer);
        _db.PrinterModels.Add(model);
        _db.Printers.Add(printer);
        await _db.SaveChangesAsync();

        return printer;
    }

    private async Task<PrintJob> CreateUnassignedQueuedJobAsync(string name, int queuePosition)
    {
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            AssignedPrinterId = null,
            Status = PrintJobStatus.Queued,
            Priority = 0,
            QueuePosition = queuePosition,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };

        _db.PrintJobs.Add(job);
        await _db.SaveChangesAsync();
        return job;
    }

    private static (Mock<IHubContext<PrinterHub>> Hub, Mock<IClientProxy> Proxy) CreateHubContextMockWithProxy()
    {
        Mock<IClientProxy> proxy = new();
        proxy.Setup(x => x.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IHubClients> clients = new();
        clients.Setup(x => x.Group(It.IsAny<string>())).Returns(proxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(x => x.Clients).Returns(clients.Object);
        return (hub, proxy);
    }
}
