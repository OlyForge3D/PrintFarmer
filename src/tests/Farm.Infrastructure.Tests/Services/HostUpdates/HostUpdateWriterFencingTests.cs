using System.Collections.Concurrent;
using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Electricity;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.Queue;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Proves the host-update fence coverage extended in issue #2663 beyond the outbox publisher:
/// <see cref="PowerReadingPruneService"/> and <see cref="QueueRetentionPruneService"/> both
/// honor a pending <see cref="IHostUpdateWriterActivityFlag"/> pause request by skipping their
/// delete pass entirely (never opening a DB scope for it) and acknowledging quiescence, rather
/// than racing an in-flight <c>ExecuteDeleteAsync</c> against a coordinated backup/migration.
/// A counting <see cref="IServiceScopeFactory"/> wrapper is used instead of seeding full
/// relational fixtures: what matters here is whether the writer opens a scope to do work at
/// all while paused, not the specific rows it would have deleted (that behavior is already
/// covered by each service's own pre-existing prune tests).
/// </summary>
public class HostUpdateWriterFencingTests : IDisposable
{
    // Each paused outer-loop iteration acknowledges at its top and interval boundary.
    private const int AcknowledgementsPerPausedIteration = 2;

    private readonly SqliteConnection _connection;

    public HostUpdateWriterFencingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using AppDbContext db = new(options);
        _ = db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Counts how many times a fresh scope was actually opened, without changing behavior.</summary>
    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _scopesOpened;

        public TaskCompletionSource FirstScopeOpened { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ScopesOpened => Volatile.Read(ref _scopesOpened);

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _scopesOpened) == 1)
            {
                FirstScopeOpened.TrySetResult();
            }

            return inner.CreateScope();
        }
    }

    private CountingScopeFactory BuildCountingScopeFactory(params IInterceptor[] interceptors)
    {
        ServiceCollection services = new();
        _ = services.AddDbContext<AppDbContext>(
            builder => builder
                .UseSqlite(_connection)
                .AddInterceptors(interceptors));
        ServiceProvider sp = services.BuildServiceProvider();
        return new CountingScopeFactory(sp.GetRequiredService<IServiceScopeFactory>());
    }

    private static async Task WaitForPauseAcknowledgementsAsync(
        IHostUpdateWriterActivityFlag fence,
        Func<int> acknowledgementCount,
        int expectedAcknowledgements)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!await fence.IsPausedAsync(timeout.Token) || acknowledgementCount() < expectedAcknowledgements)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            int observed = acknowledgementCount();
            throw new Xunit.Sdk.XunitException(
                $"Writer acknowledged the pause {observed} times, expected {expectedAcknowledgements}; " +
                $"missing {expectedAcknowledgements - observed} acknowledgement(s) within 10 seconds.");
        }
    }

    [Fact]
    public async Task PowerReadingPruneService_WhilePauseRequested_NeverOpensScope()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new PowerReadingPruneFenceFlag();
        await fence.RequestPauseAsync(CancellationToken.None);

        var sut = new PowerReadingPruneService(
            scopeFactory,
            NullLogger<PowerReadingPruneService>.Instance,
            fence);
        await sut.StartAsync(CancellationToken.None);
        await WaitForPauseAcknowledgementsAsync(
            fence,
            () => fence.AcknowledgementCount,
            AcknowledgementsPerPausedIteration + 1);
        await sut.StopAsync(CancellationToken.None);

        (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
        scopeFactory.ScopesOpened.Should().Be(0, "the fenced writer must not touch the database while a pause is pending");
    }

    [Fact]
    public async Task PowerReadingPruneService_WithoutPauseRequested_OpensScopeNormally()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new PowerReadingPruneFenceFlag();

        var sut = new PowerReadingPruneService(
            scopeFactory,
            NullLogger<PowerReadingPruneService>.Instance,
            fence);

        await sut.StartAsync(CancellationToken.None);
        await scopeFactory.FirstScopeOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sut.StopAsync(CancellationToken.None);

        scopeFactory.ScopesOpened.Should().BeGreaterThan(0, "an unpaused fence must not block normal pruning");
    }

    [Fact]
    public async Task QueueRetentionPruneService_WhilePauseRequested_NeverRunsOncePass()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new QueueRetentionPruneFenceFlag();
        await fence.RequestPauseAsync(CancellationToken.None);

        var settings = new QueueRetentionSettings { PruneInterval = TimeSpan.FromHours(6) };
        var sut = new QueueRetentionPruneService(
            scopeFactory,
            Options.Create(settings),
            NullLogger<QueueRetentionPruneService>.Instance,
            fence);
        await sut.StartAsync(CancellationToken.None);
        await WaitForPauseAcknowledgementsAsync(
            fence,
            () => fence.AcknowledgementCount,
            AcknowledgementsPerPausedIteration + 1);
        await sut.StopAsync(CancellationToken.None);

        (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
        scopeFactory.ScopesOpened.Should().Be(0, "the fenced writer must not touch the database while a pause is pending");
    }

    [Fact]
    public async Task QueueRetentionPruneService_WithoutPauseRequested_RunsOncePassNormally()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new QueueRetentionPruneFenceFlag();

        var settings = new QueueRetentionSettings { PruneInterval = TimeSpan.FromHours(6) };
        var sut = new QueueRetentionPruneService(
            scopeFactory,
            Options.Create(settings),
            NullLogger<QueueRetentionPruneService>.Instance,
            fence);

        await sut.StartAsync(CancellationToken.None);
        await scopeFactory.FirstScopeOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sut.StopAsync(CancellationToken.None);

        scopeFactory.ScopesOpened.Should().BeGreaterThan(0, "an unpaused fence must not block normal pruning");
    }

    [Fact]
    public async Task QueueReconciliationFence_AfterProcessRestart_RejectsReconciliation()
    {
        string root = Directory.CreateTempSubdirectory("pf-host-update-restart-fence-").FullName;
        try
        {
            HostUpdateExecutionOptions options = new() { RootDirectory = root };
            await new FileHostUpdateAdmissionGate(options).CloseAsync(CancellationToken.None);

            CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
            var restartedFence = new QueueReconciliationFenceFlag(new FileHostUpdateAdmissionGate(options));
            var service = new QueueReconciliationService(
                scopeFactory,
                NullLogger<QueueReconciliationService>.Instance,
                restartedFence);

            await service.ReconcileStaleAttemptsAsync(CancellationToken.None);

            (await restartedFence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
            scopeFactory.ScopesOpened.Should().Be(0, "a restarted reconciler must not touch queue state while the durable fence is closed");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QueueReconciliationFence_WhenDurableMarkerIsCorrupt_RefusesReconciliation()
    {
        string root = Directory.CreateTempSubdirectory("pf-host-update-corrupt-fence-").FullName;
        try
        {
            HostUpdateExecutionOptions options = new() { RootDirectory = root };
            Directory.CreateDirectory(options.StateDirectory);
            await File.WriteAllTextAsync(Path.Combine(options.StateDirectory, "admission.closed"), "{truncated");

            CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
            var fence = new QueueReconciliationFenceFlag(new FileHostUpdateAdmissionGate(options));
            var service = new QueueReconciliationService(
                scopeFactory,
                NullLogger<QueueReconciliationService>.Instance,
                fence);

            await service.ReconcileStaleAttemptsAsync(CancellationToken.None);

            (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
            scopeFactory.ScopesOpened.Should().Be(0, "an unreadable durable fence must block reconciliation");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QueueReconciliationService_PauseDuringInterval_AcknowledgesWithinFenceWindow()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new QueueReconciliationFenceFlag();
        var service = new QueueReconciliationService(
            scopeFactory,
            NullLogger<QueueReconciliationService>.Instance,
            fence);

        await service.StartAsync(CancellationToken.None);
        await scopeFactory.FirstScopeOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fence.RequestPauseAsync(CancellationToken.None);

        await WaitForPauseAcknowledgementsAsync(fence, () => fence.AcknowledgementCount, 1);
        await service.StopAsync(CancellationToken.None);

        (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task QueueReconciliationService_PauseAfterAttempt_SavesBeforeAcknowledging()
    {
        await DisableForeignKeysAsync();
        await SeedStaleAttemptsAsync(2);

        var fence = new QueueReconciliationFenceFlag();
        var saveObserver = new FenceAwareSaveChangesInterceptor(fence);
        var pauseTrigger = new PauseOnDispatchStateReadInterceptor(fence);
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory(saveObserver, pauseTrigger);
        var service = new QueueReconciliationService(
            scopeFactory,
            NullLogger<QueueReconciliationService>.Instance,
            fence);
        saveObserver.IsEnabled = true;
        pauseTrigger.IsEnabled = true;

        await service.ReconcileStaleAttemptsAsync(CancellationToken.None);

        saveObserver.AcknowledgementCounts.Should().NotBeEmpty();
        saveObserver.AcknowledgementCounts.Should().OnlyContain(
            count => count == 0,
            "all staged queue mutations must commit before the writer acknowledges quiescence");
        fence.AcknowledgementCount.Should().Be(1);
    }

    [Fact]
    public async Task QueueReconciliationService_PauseAfterNullAttemptRecovery_SavesBeforeAcknowledging()
    {
        await DisableForeignKeysAsync();
        Guid printerId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        await using (AppDbContext seed = new(
                         new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options))
        {
            seed.PrinterDispatchStates.Add(new PrinterDispatchState
            {
                PrinterId = printerId,
                ActiveJobId = jobId,
                ActiveDispatchAttemptId = attemptId,
            });
            seed.QueueDispatchOutbox.Add(new QueueDispatchOutbox
            {
                Id = Guid.NewGuid(),
                Sequence = 1,
                AggregateType = nameof(PrintJob),
                AggregateId = jobId,
                EventType = BedClearAcknowledgementService.BackendStartCommandEventType,
                Status = QueueOutboxEventStatus.Processing,
                FailureCode = "backend_outcome_unknown",
                PrinterId = printerId,
                AttemptId = null,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var fence = new QueueReconciliationFenceFlag();
        var saveObserver = new FenceAwareSaveChangesInterceptor(fence);
        var pauseTrigger = new PauseOnDispatchStateReadInterceptor(fence);
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory(saveObserver, pauseTrigger);
        var service = new QueueReconciliationService(
            scopeFactory,
            NullLogger<QueueReconciliationService>.Instance,
            fence);
        saveObserver.IsEnabled = true;
        pauseTrigger.IsEnabled = true;

        await service.ReconcileStaleAttemptsAsync(CancellationToken.None);

        saveObserver.AcknowledgementCounts.Should().ContainSingle();
        saveObserver.AcknowledgementCounts.Should().OnlyContain(
            count => count == 0,
            "null-attempt recovery must commit before the writer acknowledges quiescence");
        fence.AcknowledgementCount.Should().Be(1);
    }

    private async Task DisableForeignKeysAsync()
    {
        await using DbCommand command = _connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF;";
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedStaleAttemptsAsync(int count)
    {
        await using AppDbContext seed = new(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        seed.QueueDispatchAttempts.AddRange(
            Enumerable.Range(0, count).Select(_ => new QueueDispatchAttempt
            {
                Id = Guid.NewGuid(),
                PrinterId = Guid.NewGuid(),
                ActorSubject = "host-update-fence-test",
                StartPathKind = "Manual",
                ClaimedAtUtc = DateTime.UtcNow.AddMinutes(-15),
                Outcome = DispatchAttemptOutcome.InProgress,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-15),
            }));
        await seed.SaveChangesAsync();
    }

    private sealed class FenceAwareSaveChangesInterceptor(QueueReconciliationFenceFlag fence)
        : SaveChangesInterceptor
    {
        private readonly ConcurrentQueue<int> _acknowledgementCounts = new();

        public bool IsEnabled { get; set; }

        public IReadOnlyCollection<int> AcknowledgementCounts => _acknowledgementCounts.ToArray();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsEnabled)
            {
                _acknowledgementCounts.Enqueue(fence.AcknowledgementCount);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class PauseOnDispatchStateReadInterceptor(QueueReconciliationFenceFlag fence)
        : DbCommandInterceptor
    {
        private int _pauseRequested;

        public bool IsEnabled { get; set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (IsEnabled &&
                command.CommandText.Contains("PrinterDispatchStates", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _pauseRequested, 1) == 0)
            {
                await fence.RequestPauseAsync(cancellationToken);
            }

            return result;
        }
    }
}
