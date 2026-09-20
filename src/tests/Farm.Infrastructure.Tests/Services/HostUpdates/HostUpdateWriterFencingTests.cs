using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.Electricity;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.Queue;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    private CountingScopeFactory BuildCountingScopeFactory()
    {
        ServiceCollection services = new();
        _ = services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(_connection));
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
}
