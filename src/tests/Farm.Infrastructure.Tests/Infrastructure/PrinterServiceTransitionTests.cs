using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Farm.Infrastructure.Tests.Infrastructure;

public sealed class PrinterServiceTransitionTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task SaveChangesAsync_WhenDisableAndAssignmentInterleave_OnlyWinnerPersists(
        bool disableFirst, bool existingBarrier)
    {
        await using DatabaseFixture database = await DatabaseFixture.CreateAsync(existingBarrier);
        await using AppDbContext winner = database.CreateContext();
        await using AppDbContext loser = database.CreateContext(new BeforeSave(async () =>
        {
            _ = await winner.SaveChangesAsync();
        }));
        AppDbContext disabling = disableFirst ? winner : loser;
        AppDbContext assigning = disableFirst ? loser : winner;
        Printer printer = await disabling.Printers.SingleAsync();
        printer.IsEnabled = false;
        PrintJob job = new()
        {
            Name = "Concurrent assignment",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
        };
        assigning.PrintJobs.Add(job);

        _ = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => loser.SaveChangesAsync());

        await using AppDbContext verify = database.CreateContext();
        Assert.Equal(!disableFirst, (await verify.Printers.SingleAsync()).IsEnabled);
        Assert.Equal(disableFirst ? 0 : 1, await verify.PrintJobs.CountAsync());
        if (!disableFirst)
        {
            PrintJob saved = await verify.PrintJobs.SingleAsync();
            Assert.Equal(printer.Id, saved.AssignedPrinterId);
            Assert.Equal(PrintJobStatus.Assigned, saved.Status);
        }
    }

    [Theory]
    [InlineData(PrintJobStatus.Assigned)]
    [InlineData(PrintJobStatus.Starting)]
    [InlineData(PrintJobStatus.Printing)]
    [InlineData(PrintJobStatus.Paused)]
    public async Task SaveChangesAsync_WhenDestinationWasDisabled_RejectsAssignment(PrintJobStatus status)
    {
        await using DatabaseFixture database = await DatabaseFixture.CreateAsync();
        await using AppDbContext assigning = database.CreateContext();
        Printer stalePrinter = await assigning.Printers.SingleAsync();
        await using (AppDbContext disabling = database.CreateContext())
        {
            (await disabling.Printers.SingleAsync()).IsEnabled = false;
            _ = await disabling.SaveChangesAsync();
        }

        assigning.PrintJobs.Add(new PrintJob
        {
            Name = "Stale assignment",
            AssignedPrinterId = stalePrinter.Id,
            Status = status,
        });
        _ = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => assigning.SaveChangesAsync());
        await using AppDbContext verify = database.CreateContext();
        Assert.False((await verify.Printers.SingleAsync()).IsEnabled);
        Assert.Empty(await verify.PrintJobs.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChangesAsync_WhenDisabledPrinterIsObservedPrinting_PreservesWorkForReview(bool fromHistory)
    {
        await using DatabaseFixture database = await DatabaseFixture.CreateAsync();
        await using AppDbContext context = database.CreateContext();
        Printer printer = await context.Printers.SingleAsync();
        printer.IsEnabled = false;
        _ = await context.SaveChangesAsync();
        PrintJob job = new()
        {
            Name = "Observed work",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Printing,
            ActualStartTime = DateTime.UtcNow,
            ExternalJobId = "observed-123",
            WasSeededFromHistory = fromHistory,
            IsExternalPrint = !fromHistory,
        };
        context.PrintJobs.Add(job);

        _ = await context.SaveChangesAsync();

        await using AppDbContext verify = database.CreateContext();
        PrintJob saved = await verify.PrintJobs.SingleAsync();
        Assert.True(saved.RequiresOperatorReview);
        Assert.Equal(PrintJobStatus.Printing, saved.Status);
        Assert.Equal(printer.Id, saved.AssignedPrinterId);
        Assert.False((await verify.Printers.SingleAsync()).IsEnabled);
    }

    [Fact]
    public async Task SaveChanges_WhenDisablingWithTrackedAssignment_RejectsWholeSave()
    {
        await using DatabaseFixture database = await DatabaseFixture.CreateAsync();
        using AppDbContext context = database.CreateContext();
        Printer printer = context.Printers.Single();
        printer.IsEnabled = false;
        context.PrintJobs.Add(new PrintJob
        {
            Name = "Same-save assignment",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
        });

        _ = Assert.Throws<DbUpdateConcurrencyException>(() => context.SaveChanges());

        await using AppDbContext verify = database.CreateContext();
        Assert.True((await verify.Printers.SingleAsync()).IsEnabled);
        Assert.Empty(await verify.PrintJobs.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChanges_WhenReviewedWorkRequeuesAndReassigns_DoesNotResurfaceOldReview(bool asyncSave)
    {
        await using DatabaseFixture database = await DatabaseFixture.CreateAsync();
        await using AppDbContext context = database.CreateContext();
        Printer printer = await context.Printers.SingleAsync();
        PrintJob job = new()
        {
            Name = "Reviewed multi-copy work",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Printing,
            Copies = 2,
            RequiresOperatorReview = true,
        };
        context.PrintJobs.Add(job);
        _ = await context.SaveChangesAsync();

        job.Status = PrintJobStatus.Queued;
        if (asyncSave)
        {
            _ = await context.SaveChangesAsync();
        }
        else
        {
            _ = context.SaveChanges();
        }

        Assert.False(job.RequiresOperatorReview);
        job.Status = PrintJobStatus.Assigned;
        _ = await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        Assert.False((await context.PrintJobs.SingleAsync()).RequiresOperatorReview);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenReviewedWorkMovesPrinters_ClearsOriginalReview()
    {
        await using DatabaseFixture database = await DatabaseFixture.CreateAsync();
        await using AppDbContext context = database.CreateContext();
        Printer original = await context.Printers.SingleAsync();
        Printer destination = new()
        {
            Name = "Synthetic destination",
            ServerUrl = "http://destination.invalid",
            IsEnabled = true,
            ManufacturerId = original.ManufacturerId,
            ModelId = original.ModelId,
        };
        context.Printers.Add(destination);
        PrintJob job = new()
        {
            Name = "Operator recovery",
            AssignedPrinterId = original.Id,
            Status = PrintJobStatus.Assigned,
            RequiresOperatorReview = true,
        };
        context.PrintJobs.Add(job);
        _ = await context.SaveChangesAsync();

        job.AssignedPrinterId = destination.Id;
        _ = await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        PrintJob saved = await context.PrintJobs.SingleAsync();
        Assert.Equal(destination.Id, saved.AssignedPrinterId);
        Assert.False(saved.RequiresOperatorReview);
    }

    private sealed class BeforeSave(Func<Task> action) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await action();
            return result;
        }
    }

    private sealed class DatabaseFixture(SqliteConnection connection) : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        public AppDbContext CreateContext(SaveChangesInterceptor? interceptor = null) =>
            new(interceptor is null ? _options
                : new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(interceptor).Options);

        public static async Task<DatabaseFixture> CreateAsync(bool existingBarrier = true)
        {
            SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=False");
            await connection.OpenAsync();
            DatabaseFixture fixture = new(connection);
            await using AppDbContext context = fixture.CreateContext();
            _ = await context.Database.EnsureCreatedAsync();
            Printer printer = new()
            {
                Name = "Synthetic service printer",
                ServerUrl = "http://synthetic.invalid",
                IsEnabled = true,
                Backend = (int)PrinterBackend.PrusaLink,
                ManufacturerId = Guid.NewGuid(),
                ModelId = Guid.NewGuid(),
            };
            context.Printers.Add(printer);
            if (existingBarrier)
            {
                context.PrinterDispatchStates.Add(new PrinterDispatchState { Printer = printer });
            }

            _ = await context.SaveChangesAsync();
            return fixture;
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
