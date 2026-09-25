using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Issue #2999: after a rollback the coordinator must keep admission fenced until an operator
/// has recorded physical printer reconciliation, must never re-run restore/apply while waiting,
/// and must treat an unreadable or tampered record as not reconciled.
/// </summary>
public sealed class HostUpdatePhysicalReconciliationTests : IDisposable
{
    private static readonly HostUpdateExecutionRequest Request = new(
        "release-1", 1, "sha256:manifest", "abc123", HostUpdateExecutionChannel.Stable,
        [new HostUpdateExecutionTarget("api", "linux/amd64", "sha256:api")])
    {
        RequestId = "request-1",
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pf-physical-reconciliation-tests-" + Guid.NewGuid().ToString("N"));

    public HostUpdatePhysicalReconciliationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public async Task Unrecorded_reconciliation_keeps_the_fence_closed_after_rollback()
    {
        var outcomes = new FileHostUpdateRecoveryOutcomeStore(Path.Join(_root, "outcomes"));
        var fence = new CountingFence();

        HostUpdateRecoveryResult result = await Coordinator(outcomes, fence, new FileHostUpdatePhysicalReconciliationStore(Path.Join(_root, "physical")), new FakeDigestApplier())
            .RecoverAsync(Request, [], CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.FenceReleasePending);
        result.Detail.Should().Be("image_only_rollback|" + HostUpdatePhysicalReconciliationCodes.Pending);
        fence.ReleaseCount.Should().Be(0, "admission must not reopen before physical reconciliation is recorded");
        (await outcomes.ReadAsync(Request.ReleaseId, CancellationToken.None))!.Detail.Should().Be(result.Detail);
        HostUpdatePhysicalReconciliationCodes.IsBlockedDetail(result.Detail).Should().BeTrue();
    }

    [Fact]
    public async Task Recorded_reconciliation_releases_the_fence_once_without_replaying_rollback()
    {
        var outcomes = new FileHostUpdateRecoveryOutcomeStore(Path.Join(_root, "outcomes"));
        var physical = new FileHostUpdatePhysicalReconciliationStore(Path.Join(_root, "physical"));
        await Coordinator(outcomes, new CountingFence(), physical, new FakeDigestApplier()).RecoverAsync(Request, [], CancellationToken.None);

        // A second pending retry re-checks the gate only and does not stack diagnostics.
        HostUpdateRecoveryResult stillPending = await Coordinator(outcomes, new CountingFence(), physical, new ThrowingDigestApplier())
            .RecoverAsync(Request, [], CancellationToken.None);
        stillPending.Detail.Split('|').Should().HaveCount(2);

        await physical.WriteAsync(
            HostUpdatePhysicalReconciliationRecord.Create(Request.ReleaseId, Request.RequestId, Inventory(), DateTimeOffset.UtcNow),
            CancellationToken.None);
        var fence = new CountingFence();

        HostUpdateRecoveryResult released = await Coordinator(outcomes, fence, physical, new ThrowingDigestApplier())
            .RecoverAsync(Request, [], CancellationToken.None);

        released.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        released.Detail.Should().Be("image_only_rollback");
        fence.ReleaseCount.Should().Be(1);
    }

    [Fact]
    public async Task A_record_for_another_request_does_not_release_the_fence()
    {
        var physical = new FileHostUpdatePhysicalReconciliationStore(Path.Join(_root, "physical"));
        await physical.WriteAsync(
            HostUpdatePhysicalReconciliationRecord.Create(Request.ReleaseId, "request-other", Inventory(), DateTimeOffset.UtcNow),
            CancellationToken.None);
        var fence = new CountingFence();

        HostUpdateRecoveryResult result = await Coordinator(new FileHostUpdateRecoveryOutcomeStore(Path.Join(_root, "outcomes")), fence, physical, new FakeDigestApplier())
            .RecoverAsync(Request, [], CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.FenceReleasePending);
        fence.ReleaseCount.Should().Be(0);
    }

    [Fact]
    public async Task A_tampered_record_is_unreadable_and_keeps_the_fence_closed()
    {
        string physicalRoot = Path.Join(_root, "physical");
        var physical = new FileHostUpdatePhysicalReconciliationStore(physicalRoot);
        await physical.WriteAsync(
            HostUpdatePhysicalReconciliationRecord.Create(Request.ReleaseId, Request.RequestId, Inventory(), DateTimeOffset.UtcNow),
            CancellationToken.None);
        string file = Directory.EnumerateFiles(physicalRoot).Single();
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("Printing", "Completed", StringComparison.Ordinal));
        var fence = new CountingFence();

        HostUpdateRecoveryResult result = await Coordinator(new FileHostUpdateRecoveryOutcomeStore(Path.Join(_root, "outcomes")), fence, physical, new FakeDigestApplier())
            .RecoverAsync(Request, [], CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.FenceReleasePending);
        result.Detail.Should().EndWith("|" + HostUpdatePhysicalReconciliationCodes.Unreadable + ":InvalidDataException");
        fence.ReleaseCount.Should().Be(0);
        HostUpdatePhysicalReconciliationCodes.IsBlockedDetail(result.Detail).Should().BeTrue();
    }

    [Fact]
    public async Task A_throwing_gate_keeps_the_fence_closed_even_without_a_fence_coordinator()
    {
        var outcomes = new FileHostUpdateRecoveryOutcomeStore(Path.Join(_root, "outcomes"));

        HostUpdateRecoveryResult result = await Coordinator(outcomes, fence: null, new ThrowingGate(), new FakeDigestApplier())
            .RecoverAsync(Request, [], CancellationToken.None);

        result.Outcome.Should().Be(HostUpdateRecoveryOutcome.FenceReleasePending);
        result.Detail.Should().EndWith("|" + HostUpdatePhysicalReconciliationCodes.Unreadable + ":IOException");
    }

    [Fact]
    public async Task Unconfigured_store_never_reports_reconciliation_and_refuses_writes()
    {
        var store = new UnconfiguredHostUpdatePhysicalReconciliationStore();

        (await store.IsRecordedAsync(Request.ReleaseId, Request.RequestId, CancellationToken.None)).Should().BeFalse();
        Func<Task> write = () => store.WriteAsync(
            HostUpdatePhysicalReconciliationRecord.Create(Request.ReleaseId, Request.RequestId, Inventory(), DateTimeOffset.UtcNow),
            CancellationToken.None);
        await write.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Token_is_bound_to_release_request_and_inventory_but_not_ordering()
    {
        HostUpdatePrinterCommandInventory inventory = Inventory();
        var reordered = new HostUpdatePrinterCommandInventory([.. inventory.Printers.Reverse()]);
        HostUpdatePrinterCommandInventory settled = new([inventory.Printers[0] with { UncertainOutcomes = [] }, inventory.Printers[1]]);

        string token = inventory.Token(Request.ReleaseId, Request.RequestId);

        token.Should().MatchRegex("^physical-[0-9a-f]{32}$");
        reordered.Token(Request.ReleaseId, Request.RequestId).Should().Be(token);
        inventory.Token(Request.ReleaseId, "request-2").Should().NotBe(token);
        inventory.Token("release-2", Request.RequestId).Should().NotBe(token);
        settled.Token(Request.ReleaseId, Request.RequestId).Should().NotBe(token);
    }

    [Fact]
    public async Task Database_inventory_lists_every_printer_with_its_uncertain_outcomes_and_writes_nothing()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        Guid printing = Guid.NewGuid(), unknown = Guid.NewGuid(), idle = Guid.NewGuid(), orphan = Guid.NewGuid();
        Guid job = Guid.NewGuid(), command = Guid.NewGuid(), published = Guid.NewGuid(), attempt = Guid.NewGuid(), settledAttempt = Guid.NewGuid();
        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            await seed.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            seed.Printers.AddRange(new Printer { Id = printing, Name = "A", ServerUrl = "http://a" }, new Printer { Id = unknown, Name = "B", ServerUrl = "http://b" }, new Printer { Id = idle, Name = "C", ServerUrl = "http://c" });
            seed.PrintJobs.AddRange(
                new PrintJob { Id = job, Name = "j", AssignedPrinterId = printing, Status = PrintJobStatus.Printing },
                new PrintJob { Id = Guid.NewGuid(), Name = "done", AssignedPrinterId = idle, Status = PrintJobStatus.Completed });
            seed.QueueDispatchOutbox.AddRange(
                new QueueDispatchOutbox { Id = command, Sequence = 1, AggregateType = "PrintJob", AggregateId = job, PrinterId = printing, EventType = "start.v1", Status = QueueOutboxEventStatus.Processing },
                new QueueDispatchOutbox { Id = published, Sequence = 2, AggregateType = "PrintJob", AggregateId = job, PrinterId = idle, EventType = "start.v1", Status = QueueOutboxEventStatus.Published });
            seed.QueueDispatchAttempts.AddRange(
                new QueueDispatchAttempt { Id = attempt, PrinterId = unknown, AttemptNumber = 1, Outcome = DispatchAttemptOutcome.Unknown },
                new QueueDispatchAttempt { Id = Guid.NewGuid(), PrinterId = orphan, AttemptNumber = 1, Outcome = DispatchAttemptOutcome.Rejected, RequiresReconciliation = true },
                new QueueDispatchAttempt { Id = settledAttempt, PrinterId = idle, AttemptNumber = 1, Outcome = DispatchAttemptOutcome.Accepted });
            await seed.SaveChangesAsync();
        }

        await using var db = new AppDbContext(options);
        HostUpdatePrinterCommandInventory inventory = await new DbHostUpdatePrinterCommandInventoryReader(db).ReadAsync(CancellationToken.None);

        inventory.Printers.Select(p => p.PrinterId).Should().Equal(new[] { printing, unknown, idle, orphan }.Order());
        inventory.Printers.Single(p => p.PrinterId == printing).UncertainOutcomes.Should().Equal(
            new HostUpdateUncertainPhysicalOutcome(HostUpdatePhysicalReconciliationCodes.PhysicalCommandInFlight, command.ToString("D"), "start.v1:Processing"),
            new HostUpdateUncertainPhysicalOutcome(HostUpdatePhysicalReconciliationCodes.PrintJobActive, job.ToString("D"), "Printing"));
        inventory.Printers.Single(p => p.PrinterId == unknown).UncertainOutcomes.Should().ContainSingle()
            .Which.Should().Be(new HostUpdateUncertainPhysicalOutcome(HostUpdatePhysicalReconciliationCodes.DispatchOutcomeUncertain, attempt.ToString("D"), "Unknown"));
        inventory.Printers.Single(p => p.PrinterId == idle).UncertainOutcomes.Should().BeEmpty();
        inventory.Printers.Single(p => p.PrinterId == orphan).PrinterName.Should().BeNull("an outcome on a deleted printer is still surfaced");
        inventory.UncertainOutcomeCount.Should().Be(4);
        db.ChangeTracker.Entries().Should().BeEmpty("the inventory is read with no tracking and never saved");
    }

    private static HostUpdatePrinterCommandInventory Inventory() => new(
    [
        new HostUpdatePrinterReconciliationItem(Guid.Parse("00000000-0000-0000-0000-000000000001"), "A", [new(HostUpdatePhysicalReconciliationCodes.PrintJobActive, "job-1", "Printing")]),
        new HostUpdatePrinterReconciliationItem(Guid.Parse("00000000-0000-0000-0000-000000000002"), "B", []),
    ]);

    private static HostUpdateRecoveryCoordinator Coordinator(
        IHostUpdateRecoveryOutcomeStore outcomes,
        IHostUpdateFenceCoordinator? fence,
        IHostUpdatePhysicalReconciliationGate gate,
        IHostUpdateDigestApplier applier) =>
        new(
            new FakeInstalledHostStateStore(),
            new AlwaysCompatibleEvaluator(),
            applier,
            new ThrowingRestoreExecutor(),
            new NeverFindsManifestLocator(),
            new FakeDigestVerifier(),
            outcomes,
            fence,
            executionLock: null,
            physicalReconciliationGate: gate);

    private sealed class CountingFence : IHostUpdateFenceCoordinator
    {
        public int ReleaseCount { get; private set; }

        public Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReleaseAsync(CancellationToken cancellationToken)
        {
            ReleaseCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingGate : IHostUpdatePhysicalReconciliationGate
    {
        public Task<bool> IsRecordedAsync(string releaseId, string requestId, CancellationToken cancellationToken) => throw new IOException("unreadable");
    }

    private sealed class FakeInstalledHostStateStore : IInstalledHostStateStore
    {
        public Task<InstalledHostState?> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<InstalledHostState?>(new InstalledHostState(
                "release-0", "sha256:prior", new Dictionary<string, string> { ["api"] = "sha256:prior-api" }, "monolith", DateTimeOffset.UtcNow));

        public Task WriteAsync(InstalledHostState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AlwaysCompatibleEvaluator : IHostUpdateRecoveryCompatibilityEvaluator
    {
        public bool SupportsImageOnlyRollback(InstalledHostState priorState, IReadOnlyList<HostUpdateExecutionActivity> activities) => true;
    }

    private sealed class FakeDigestApplier : IHostUpdateDigestApplier
    {
        public Task ApplyByDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? platformsByService = null) => Task.CompletedTask;
    }

    private sealed class ThrowingDigestApplier : IHostUpdateDigestApplier
    {
        public Task ApplyByDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? platformsByService = null) =>
            throw new InvalidOperationException("apply_replayed");
    }

    private sealed class FakeDigestVerifier : IHostUpdateDigestVerifier
    {
        public Task VerifyDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingRestoreExecutor : IHostUpdateRestoreExecutor
    {
        public Task RestoreAsync(HostUpdateBackupManifest manifest, string backupRunDirectory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("restore_replayed");
    }

    private sealed class NeverFindsManifestLocator : IHostUpdateBackupManifestLocator
    {
        public Task<(HostUpdateBackupManifest Manifest, string RunDirectory)?> FindLatestAsync(string releaseId, CancellationToken cancellationToken) =>
            Task.FromResult<(HostUpdateBackupManifest, string)?>(null);
    }
}
