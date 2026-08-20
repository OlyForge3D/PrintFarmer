using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Queue;

/// <summary>
/// Relational tests for <see cref="QueueRetentionPruneService"/> executed against
/// SQLite in-memory (the EF Core InMemory provider does not support
/// <c>ExecuteDeleteAsync</c>). Covers per-table independent retention windows,
/// boundary correctness, batching caps, and the reconciliation-pending exemption
/// for dispatch attempts, per issue #1728.
/// </summary>
public class QueueRetentionPruneServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public QueueRetentionPruneServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using AppDbContext db = new(_options);
        _ = db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ServiceProvider BuildRootProvider()
    {
        ServiceCollection services = new();
        _ = services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(_connection));
        return services.BuildServiceProvider();
    }

    private static QueueRetentionPruneService CreateSut(
        ServiceProvider sp,
        QueueRetentionSettings? settings = null)
    {
        settings ??= new QueueRetentionSettings();
        return new QueueRetentionPruneService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings),
            NullLogger<QueueRetentionPruneService>.Instance);
    }

    private static QueueDispatchOutbox MakeOutboxEvent(
        long sequence,
        QueueOutboxEventStatus status,
        DateTime? completedAtUtc) => new()
        {
            Id = Guid.NewGuid(),
            Sequence = sequence,
            AggregateType = nameof(PrintJob),
            AggregateId = Guid.NewGuid(),
            EventType = "PrintFarmer.Queue.Test.v1",
            Status = status,
            CreatedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = completedAtUtc,
        };

    private static QueueDispatchAttempt MakeAttempt(
        DateTime? terminalAtUtc,
        bool requiresReconciliation) => new()
        {
            Id = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ActorSubject = "user-A",
            StartPathKind = "Manual",
            ClaimedAtUtc = DateTime.UtcNow,
            Outcome = DispatchAttemptOutcome.Accepted,
            RequiresReconciliation = requiresReconciliation,
            TerminalAtUtc = terminalAtUtc,
            UpdatedAtUtc = DateTime.UtcNow,
        };

    private static QueueOperationAudit MakeAudit(DateTime occurredAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        OccurredAtUtc = occurredAtUtc,
        ResourceType = "PrintJob",
    };

    [Fact]
    public async Task RunOnce_Outbox_OnlyPrunesRowsOlderThanWindow_BoundaryRespected()
    {
        DateTime now = DateTime.UtcNow;
        QueueRetentionSettings settings = new() { OutboxRetentionDays = 14 };

        using (AppDbContext seed = new(_options))
        {
            // Just inside the window: must survive.
            seed.QueueDispatchOutbox.Add(MakeOutboxEvent(
                1, QueueOutboxEventStatus.Published, now.AddDays(-13)));
            // Just outside the window: must be pruned.
            seed.QueueDispatchOutbox.Add(MakeOutboxEvent(
                2, QueueOutboxEventStatus.Published, now.AddDays(-15)));
            // Not terminal: must never be pruned regardless of age.
            seed.QueueDispatchOutbox.Add(MakeOutboxEvent(
                3, QueueOutboxEventStatus.Pending, null));
            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        using ServiceProvider sp = BuildRootProvider();
        QueueRetentionPruneService svc = CreateSut(sp, settings);
        await svc.RunOnceAsync(CancellationToken.None);

        using AppDbContext verify = new(_options);
        List<long> remaining = await verify.QueueDispatchOutbox
            .Select(e => e.Sequence)
            .OrderBy(s => s)
            .ToListAsync(CancellationToken.None);
        _ = remaining.Should().BeEquivalentTo([1L, 3L], "only the row past the retention window should be pruned");
    }

    [Fact]
    public async Task RunOnce_DispatchAttempts_NeverPrunesReconciliationPending()
    {
        DateTime now = DateTime.UtcNow;
        QueueRetentionSettings settings = new() { DispatchAttemptRetentionDays = 30 };

        QueueDispatchAttempt oldReconciliationPending = MakeAttempt(
            now.AddDays(-90), requiresReconciliation: true);
        QueueDispatchAttempt oldTerminal = MakeAttempt(
            now.AddDays(-90), requiresReconciliation: false);
        QueueDispatchAttempt recentTerminal = MakeAttempt(
            now.AddDays(-1), requiresReconciliation: false);

        using (AppDbContext seed = new(_options))
        {
            seed.QueueDispatchAttempts.AddRange(oldReconciliationPending, oldTerminal, recentTerminal);
            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        using ServiceProvider sp = BuildRootProvider();
        QueueRetentionPruneService svc = CreateSut(sp, settings);
        await svc.RunOnceAsync(CancellationToken.None);

        using AppDbContext verify = new(_options);
        List<Guid> remaining = await verify.QueueDispatchAttempts
            .Select(a => a.Id)
            .ToListAsync(CancellationToken.None);
        _ = remaining.Should().BeEquivalentTo(
            [oldReconciliationPending.Id, recentTerminal.Id],
            "an attempt pending reconciliation must never be pruned regardless of age");
    }

    [Fact]
    public async Task RunOnce_DispatchAttempts_OnlyPrunesRowsOlderThanWindow_BoundaryRespected()
    {
        DateTime now = DateTime.UtcNow;
        QueueRetentionSettings settings = new() { DispatchAttemptRetentionDays = 30 };

        // Just inside the window: must survive.
        QueueDispatchAttempt insideWindow = MakeAttempt(now.AddDays(-29), requiresReconciliation: false);
        // Just outside the window: must be pruned.
        QueueDispatchAttempt outsideWindow = MakeAttempt(now.AddDays(-31), requiresReconciliation: false);

        using (AppDbContext seed = new(_options))
        {
            seed.QueueDispatchAttempts.AddRange(insideWindow, outsideWindow);
            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        using ServiceProvider sp = BuildRootProvider();
        QueueRetentionPruneService svc = CreateSut(sp, settings);
        await svc.RunOnceAsync(CancellationToken.None);

        using AppDbContext verify = new(_options);
        List<Guid> remaining = await verify.QueueDispatchAttempts
            .Select(a => a.Id)
            .ToListAsync(CancellationToken.None);
        _ = remaining.Should().BeEquivalentTo(
            [insideWindow.Id],
            "only the attempt past the retention window should be pruned");
    }

    [Fact]
    public async Task RunOnce_OperationAudits_UsesIndependentWindow_NotOutboxWindow()
    {
        DateTime now = DateTime.UtcNow;
        // Outbox window is short; audits must NOT inherit it.
        QueueRetentionSettings settings = new()
        {
            OutboxRetentionDays = 1,
            OperationAuditRetentionDays = 180,
        };

        QueueOperationAudit recentAudit = MakeAudit(now.AddDays(-30));
        QueueOperationAudit veryOldAudit = MakeAudit(now.AddDays(-200));

        using (AppDbContext seed = new(_options))
        {
            seed.QueueOperationAudits.AddRange(recentAudit, veryOldAudit);
            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        using ServiceProvider sp = BuildRootProvider();
        QueueRetentionPruneService svc = CreateSut(sp, settings);
        await svc.RunOnceAsync(CancellationToken.None);

        using AppDbContext verify = new(_options);
        List<Guid> remaining = await verify.QueueOperationAudits
            .Select(a => a.Id)
            .ToListAsync(CancellationToken.None);
        _ = remaining.Should().BeEquivalentTo(
            [recentAudit.Id],
            "operation audits must use their own 180-day window, independent of the outbox window");
    }

    [Fact]
    public async Task RunOnce_OperationAudits_OnlyPrunesRowsOlderThanWindow_BoundaryRespected()
    {
        DateTime now = DateTime.UtcNow;
        QueueRetentionSettings settings = new() { OperationAuditRetentionDays = 180 };

        // Just inside the window: must survive.
        QueueOperationAudit insideWindow = MakeAudit(now.AddDays(-179));
        // Just outside the window: must be pruned.
        QueueOperationAudit outsideWindow = MakeAudit(now.AddDays(-181));

        using (AppDbContext seed = new(_options))
        {
            seed.QueueOperationAudits.AddRange(insideWindow, outsideWindow);
            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        using ServiceProvider sp = BuildRootProvider();
        QueueRetentionPruneService svc = CreateSut(sp, settings);
        await svc.RunOnceAsync(CancellationToken.None);

        using AppDbContext verify = new(_options);
        List<Guid> remaining = await verify.QueueOperationAudits
            .Select(a => a.Id)
            .ToListAsync(CancellationToken.None);
        _ = remaining.Should().BeEquivalentTo(
            [insideWindow.Id],
            "only the audit past the retention window should be pruned");
    }

    [Fact]
    public async Task RunOnce_HonorsMaxDeletesPerTablePerPass()
    {
        DateTime now = DateTime.UtcNow;
        QueueRetentionSettings settings = new()
        {
            OutboxRetentionDays = 1,
            DeleteBatchSize = 10,
            MaxDeletesPerTablePerPass = 25,
        };

        using (AppDbContext seed = new(_options))
        {
            for (int i = 0; i < 100; i++)
            {
                seed.QueueDispatchOutbox.Add(MakeOutboxEvent(
                    i + 1, QueueOutboxEventStatus.Published, now.AddDays(-10)));
            }

            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        using ServiceProvider sp = BuildRootProvider();
        QueueRetentionPruneService svc = CreateSut(sp, settings);
        await svc.RunOnceAsync(CancellationToken.None);

        using AppDbContext verify = new(_options);
        int remainingCount = await verify.QueueDispatchOutbox.CountAsync(CancellationToken.None);
        _ = remainingCount.Should().Be(
            100 - 25,
            "a single prune pass must delete at most MaxDeletesPerTablePerPass rows, " +
            "bounding lock hold time against a large backlog");
    }

    [Fact]
    public async Task RunOnce_RepeatedPasses_DrainsBacklogAndStabilizes()
    {
        DateTime now = DateTime.UtcNow;
        QueueRetentionSettings settings = new()
        {
            OutboxRetentionDays = 1,
            DeleteBatchSize = 10,
            MaxDeletesPerTablePerPass = 25,
        };

        using (AppDbContext seed = new(_options))
        {
            for (int i = 0; i < 60; i++)
            {
                seed.QueueDispatchOutbox.Add(MakeOutboxEvent(
                    i + 1, QueueOutboxEventStatus.Published, now.AddDays(-10)));
            }

            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        using ServiceProvider sp = BuildRootProvider();
        QueueRetentionPruneService svc = CreateSut(sp, settings);

        // Three passes fully drain 60 rows at 25/pass; a fourth pass must be a no-op,
        // proving table size stabilizes across repeated prune passes.
        await svc.RunOnceAsync(CancellationToken.None);
        await svc.RunOnceAsync(CancellationToken.None);
        await svc.RunOnceAsync(CancellationToken.None);

        using (AppDbContext verify = new(_options))
        {
            _ = (await verify.QueueDispatchOutbox.CountAsync(CancellationToken.None)).Should().Be(0);
        }

        await svc.RunOnceAsync(CancellationToken.None);

        using AppDbContext finalVerify = new(_options);
        _ = (await finalVerify.QueueDispatchOutbox.CountAsync(CancellationToken.None)).Should().Be(
            0, "table size must remain stable once the backlog is drained");
    }

    [Fact]
    public async Task RunOnce_SwallowsDbExceptions()
    {
        // A transient database failure must not tear down the host.
        ServiceCollection services = new();
        _ = services.AddDbContext<AppDbContext>(builder =>
            builder.UseSqlite("DataSource=file:does-not-exist?mode=memory&cache=private"));
        using ServiceProvider sp = services.BuildServiceProvider();

        QueueRetentionPruneService svc = CreateSut(sp);

        Func<Task> act = () => svc.RunOnceAsync(CancellationToken.None);
        _ = await act.Should().NotThrowAsync("the prune loop must tolerate transient database failures");
    }

    [Fact]
    public async Task RunOnce_CancelledToken_ReturnsCleanlyWithoutThrowing()
    {
        // Graceful shutdown mid-pass must not surface OperationCanceledException to the
        // BackgroundService host loop; RunOnceAsync must swallow it and return.
        using ServiceProvider sp = BuildRootProvider();
        QueueRetentionPruneService svc = CreateSut(sp);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = () => svc.RunOnceAsync(cts.Token);
        _ = await act.Should().NotThrowAsync("a cancelled token must be swallowed for graceful shutdown");
    }
}
