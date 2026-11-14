using System.Data.Common;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.ShiftPlan;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.ShiftPlan;

public sealed class IdleWindowBatchTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task GetIdleWindowJobInputsAsync_TerminalHistory_TransfersOnlyLiveFacts(int terminalCount)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid printerId = await fixture.AddPrinterAsync();
        PrintJob[] live = Enum.GetValues<PrintJobStatus>()
            .Where(s => PrintJobOccupancy.Statuses.Contains(s) || s is PrintJobStatus.Queued or PrintJobStatus.Assigned)
            .Select(s => Job(printerId, s)).ToArray();
        fixture.Db.PrintJobs.AddRange(live);
        fixture.Db.PrintJobs.AddRange(Enumerable.Range(0, terminalCount).Select(_ => Job(printerId, PrintJobStatus.Completed)));
        fixture.Db.PrintJobs.Add(Job(null, PrintJobStatus.Queued));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        fixture.Commands.Clear();

        List<IdleWindowJobInput> inputs = await fixture.Repository.GetIdleWindowJobInputsAsync([printerId], default);

        Assert.Equal(live.Select(j => j.Id).Order(), inputs.Select(j => j.Id).Order());
        Assert.All(inputs, j => Assert.Equal(printerId, j.AssignedPrinterId));
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
        string sql = Assert.Single(fixture.Commands.Sql);
        Assert.DoesNotContain("JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GcodeFileId", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("EstimatedPrintTime", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
        Assert.Contains("Status", sql, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Commands.JobEntities);
    }

    [Fact]
    public async Task GetIdleWindowJobInputsAsync_MoreThanProviderLimit_DeduplicatesAndLosesNoLiveRows()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid[] ids = Enumerable.Range(0, 2201).Select(_ => Guid.NewGuid()).ToArray();
        fixture.Db.Printers.AddRange(ids.Select(fixture.Printer));
        fixture.Db.PrintJobs.AddRange(ids.Select(id => Job(id, PrintJobStatus.Printing)));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        fixture.Commands.Clear();

        List<IdleWindowJobInput> inputs = await fixture.Repository.GetIdleWindowJobInputsAsync([.. ids, ids[0]], default);

        Assert.Equal(ids.Order(), inputs.Select(j => j.AssignedPrinterId!.Value).Order());
        Assert.Equal(5, fixture.Commands.Sql.Count);
        Assert.All(fixture.Commands.ParameterCounts, n => Assert.InRange(n, 0, 510));
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetIdleWindowJobInputsAsync_EmptyOrCancelled_DoesNotQuery()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Commands.Clear();
        Assert.Empty(await fixture.Repository.GetIdleWindowJobInputsAsync([], default));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Repository.GetIdleWindowJobInputsAsync([], cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Repository.GetIdleWindowJobInputsAsync([Guid.NewGuid()], cancelled.Token));
        Assert.Empty(fixture.Commands.Sql);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public async Task GetIdleWindowJobInputsAsync_SupportedProvider_TranslatesBoundedProjectionWithoutConnecting(string provider)
    {
        var commands = new CommandObserver { FailOnCommand = 1 };
        var options = new DbContextOptionsBuilder<AppDbContext>().AddInterceptors(commands, new SuppressConnection());
        if (provider == "postgres")
        {
            options.UseNpgsql("Host=unused.invalid;Database=synthetic");
        }
        else
        {
            options.UseSqlServer("Server=unused.invalid;Database=synthetic;Integrated Security=true");
        }

        await using var db = new AppDbContext(options.Options);
        Guid[] ids = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EfQueueRepository(db).GetIdleWindowJobInputsAsync(ids, default));
        string sql = Assert.Single(commands.Sql);
        Assert.Contains("AssignedPrinterId", sql, StringComparison.Ordinal);
        Assert.Contains("Status", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
        Assert.True(sql.Contains(" IN ", StringComparison.Ordinal) || sql.Contains("ANY", StringComparison.Ordinal), sql);
        Assert.DoesNotContain("JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GcodeFileId", sql, StringComparison.Ordinal);
        Assert.InRange(Assert.Single(commands.ParameterCounts), 0, 510);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetIdleWindowJobInputsAsync_SecondChunkFailureOrCancellation_ReturnsNoPartialResult(bool cancel)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Commands.Clear();
        fixture.Commands.FailOnCommand = 2;
        fixture.Commands.Cancel = cancel;
        Guid[] ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Repository.GetIdleWindowJobInputsAsync(ids, default));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.GetIdleWindowJobInputsAsync(ids, default));
        }

        Assert.Equal(2, fixture.Commands.Sql.Count);
    }

    [Fact]
    public async Task GetIdleWindowsWithIndeterminateAsync_BatchFails_PropagatesWithoutSafeLookingWindows()
    {
        var queue = new Mock<IQueueDataService>();
        queue.Setup(q => q.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Printer { Id = Guid.NewGuid(), Name = "synthetic" }]);
        queue.Setup(q => q.GetIdleWindowJobInputsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("batch failed"));
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var factory = new Mock<IDbContextFactory<AppDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new AppDbContext(options));
        var service = new IdleWindowService(queue.Object, Mock.Of<IDispatchScorer>(), factory.Object, NullLogger<IdleWindowService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetIdleWindowsWithIndeterminateAsync(TimeSpan.Zero));
        queue.Verify(q => q.GetPrintJobsForPrinterAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    internal static PrintJob Job(Guid? printerId, PrintJobStatus status) => new()
    {
        Id = Guid.NewGuid(),
        AssignedPrinterId = printerId,
        Status = status,
        QueuePosition = (int)status,
        QueuedAt = new DateTime(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc),
    };

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly PrinterModel _model;
        internal AppDbContext Db { get; }
        internal CommandObserver Commands { get; }
        internal EfQueueRepository Repository => new(Db);

        private Fixture(SqliteConnection connection, AppDbContext db, CommandObserver commands, PrinterModel model)
        {
            _connection = connection;
            Db = db;
            Commands = commands;
            _model = model;
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var commands = new CommandObserver();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(commands).Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "synthetic" };
            var model = new PrinterModel { Id = Guid.NewGuid(), Name = "synthetic", Manufacturer = manufacturer };
            db.PrinterModels.Add(model);
            await db.SaveChangesAsync();
            return new Fixture(connection, db, commands, model);
        }

        internal Printer Printer(Guid id) => new()
        {
            Id = id,
            Name = "synthetic",
            ServerUrl = $"http://synthetic-{id:N}.invalid",
            ManufacturerId = _model.ManufacturerId,
            ModelId = _model.Id,
        };

        internal async Task<Guid> AddPrinterAsync()
        {
            Guid id = Guid.NewGuid();
            Db.Printers.Add(Printer(id));
            await Db.SaveChangesAsync();
            return id;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    internal sealed class CommandObserver : DbCommandInterceptor, IMaterializationInterceptor
    {
        internal List<string> Sql { get; } = [];
        internal List<int> ParameterCounts { get; } = [];
        internal int JobEntities { get; private set; }
        internal int? FailOnCommand { get; set; }
        internal bool Cancel { get; set; }

        internal void Clear()
        {
            Sql.Clear();
            ParameterCounts.Clear();
            JobEntities = 0;
        }

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is PrintJob)
            {
                JobEntities++;
            }

            return entity;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            ParameterCounts.Add(command.Parameters.Count);
            if (Sql.Count == FailOnCommand)
            {
                if (Cancel)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new InvalidOperationException("synthetic second-chunk failure");
            }

            return ValueTask.FromResult(result);
        }

    }

    private sealed class SuppressConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult.Suppress());
    }
}
