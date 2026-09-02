using System.Data.Common;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Tests.Builders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Farm.Infrastructure.Tests.Infrastructure.Repositories.Queue;

public class EfPrintJobManagementRepositoryTests
{
    [Fact]
    public async Task GetEnabledPrintersAsync_WhenServiceStateExists_LoadsServiceStateForWatermarkReads()
    {
        string dbName = $"GetEnabledPrintersAsync_{Guid.NewGuid():N}";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        Guid printerId = Guid.NewGuid();
        DateTime watermarkUtc = DateTime.UtcNow.AddMinutes(-42);

        await using (AppDbContext seedContext = new(options))
        {
            Printer printer = new()
            {
                Id = printerId,
                Name = "Enabled Printer",
                ServerUrl = "http://printer.local",
                BackendPort = 80,
                Backend = (int)PrinterBackend.PrusaLink,
                IsEnabled = true,
                ManufacturerId = Guid.NewGuid(),
                ModelId = Guid.NewGuid(),
            };

            PrinterServiceState serviceState = new()
            {
                PrinterId = printerId,
                LastHistorySeedUtc = watermarkUtc,
            };

            seedContext.Printers.Add(printer);
            seedContext.PrinterServiceStates.Add(serviceState);
            await seedContext.SaveChangesAsync();
        }

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        List<Printer> printers = await repository.GetEnabledPrintersAsync();

        Printer loaded = Assert.Single(printers);
        Assert.NotNull(loaded.ServiceState);
        Assert.Equal(watermarkUtc, loaded.ServiceState!.LastHistorySeedUtc);
    }

    [Fact]
    public async Task UpdatePrinterLastHistorySeedAsync_IncrementsServiceStateRevision()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        Guid printerId = Guid.NewGuid();
        DateTime watermarkUtc = DateTime.UtcNow.AddMinutes(-5);

        await using (AppDbContext seedContext = new(options))
        {
            await seedContext.Database.EnsureCreatedAsync();
            Guid manufacturerId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            seedContext.Manufacturers.Add(new Manufacturer
            {
                Id = manufacturerId,
                Name = "Revision Manufacturer",
            });
            seedContext.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                ManufacturerId = manufacturerId,
                Name = "Revision Model",
            });
            seedContext.Printers.Add(new Printer
            {
                Id = printerId,
                Name = "Revision Printer",
                ServerUrl = "http://printer.local",
                BackendPort = 80,
                Backend = (int)PrinterBackend.PrusaLink,
                IsEnabled = true,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            });
            seedContext.PrinterServiceStates.Add(new PrinterServiceState
            {
                PrinterId = printerId,
            });
            await seedContext.SaveChangesAsync();
        }

        await using (AppDbContext updateContext = new(options))
        {
            EfPrintJobManagementRepository repository = new(updateContext);
            await repository.UpdatePrinterLastHistorySeedAsync(printerId, watermarkUtc);
        }

        await using AppDbContext verifyContext = new(options);
        PrinterServiceState state = await verifyContext.PrinterServiceStates.SingleAsync();
        Assert.Equal(watermarkUtc, state.LastHistorySeedUtc);
        Assert.Equal(2, state.Revision);
    }

    private static DbContextOptions<AppDbContext> CreateInMemoryOptions(string testName) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"{testName}_{Guid.NewGuid():N}")
            .Options;

    [Fact]
    public async Task GetExternalJobIdsForPrinterAsync_Scoped_ReturnsOnlyCandidatesPresentCaseInsensitively()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        Guid printerId = Guid.NewGuid();
        Guid otherPrinterId = Guid.NewGuid();

        await using (AppDbContext seedContext = new(options))
        {
            await seedContext.Database.EnsureCreatedAsync();

            PrintJob matchingDifferentCase = new PrintJobBuilder().WithAssignedPrinterId(null).Build();
            matchingDifferentCase.GcodeFileId = null;
            matchingDifferentCase.GcodeFile = null;
            matchingDifferentCase.SourcePrinterId = printerId;
            matchingDifferentCase.ExternalJobId = "Ext-Job-ABC";

            PrintJob notInCandidateList = new PrintJobBuilder().WithAssignedPrinterId(null).Build();
            notInCandidateList.GcodeFileId = null;
            notInCandidateList.GcodeFile = null;
            notInCandidateList.SourcePrinterId = printerId;
            notInCandidateList.ExternalJobId = "ext-job-xyz";

            PrintJob differentPrinter = new PrintJobBuilder().WithAssignedPrinterId(null).Build();
            differentPrinter.GcodeFileId = null;
            differentPrinter.GcodeFile = null;
            differentPrinter.SourcePrinterId = otherPrinterId;
            differentPrinter.ExternalJobId = "ext-job-abc";

            seedContext.PrintJobs.AddRange(matchingDifferentCase, notInCandidateList, differentPrinter);
            await seedContext.SaveChangesAsync();
        }

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        // Candidate uses lowercase; the seeded matching row uses mixed case - the scoped query must
        // still find it via case-insensitive comparison, matching the original StringComparer.OrdinalIgnoreCase
        // in-memory HashSet semantics.
        HashSet<string> result = await repository.GetExternalJobIdsForPrinterAsync(
            printerId, ["ext-job-abc"]);

        string found = Assert.Single(result);
        Assert.Equal("Ext-Job-ABC", found);
    }

    [Fact]
    public async Task GetExternalJobIdsForPrinterAsync_Scoped_WithNoCandidates_ReturnsEmptyWithoutQuerying()
    {
        DbContextOptions<AppDbContext> options =
            CreateInMemoryOptions(nameof(GetExternalJobIdsForPrinterAsync_Scoped_WithNoCandidates_ReturnsEmptyWithoutQuerying));
        Guid printerId = Guid.NewGuid();

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        HashSet<string> result = await repository.GetExternalJobIdsForPrinterAsync(printerId, []);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActualStartTimesForPrinterAsync_Scoped_MatchesTruncatedCandidateAndExcludesOutOfRange()
    {
        DbContextOptions<AppDbContext> options =
            CreateInMemoryOptions(nameof(GetActualStartTimesForPrinterAsync_Scoped_MatchesTruncatedCandidateAndExcludesOutOfRange));
        Guid printerId = Guid.NewGuid();

        DateTime candidateWholeSecond = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime subSecondMatchingRow = candidateWholeSecond.AddMilliseconds(250);
        DateTime outOfRangeRow = candidateWholeSecond.AddHours(2);

        await using (AppDbContext seedContext = new(options))
        {
            PrintJob withinCandidateRange = new PrintJobBuilder().WithActualStartTime(subSecondMatchingRow).Build();
            withinCandidateRange.SourcePrinterId = printerId;

            PrintJob outsideCandidateRange = new PrintJobBuilder().WithActualStartTime(outOfRangeRow).Build();
            outsideCandidateRange.SourcePrinterId = printerId;

            seedContext.PrintJobs.AddRange(withinCandidateRange, outsideCandidateRange);
            await seedContext.SaveChangesAsync();
        }

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        HashSet<DateTime> result = await repository.GetActualStartTimesForPrinterAsync(
            printerId, [candidateWholeSecond]);

        DateTime matched = Assert.Single(result);
        Assert.Equal(candidateWholeSecond, matched);
    }

    [Fact]
    public async Task GetActualStartTimesForPrinterAsync_Scoped_WithNoCandidates_ReturnsEmptyWithoutQuerying()
    {
        DbContextOptions<AppDbContext> options =
            CreateInMemoryOptions(nameof(GetActualStartTimesForPrinterAsync_Scoped_WithNoCandidates_ReturnsEmptyWithoutQuerying));
        Guid printerId = Guid.NewGuid();

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        HashSet<DateTime> result = await repository.GetActualStartTimesForPrinterAsync(printerId, []);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(ReadOnlyPrintJobQuery.JobDetails)]
    [InlineData(ReadOnlyPrintJobQuery.FilteredQueue)]
    [InlineData(ReadOnlyPrintJobQuery.PrinterQueue)]
    [InlineData(ReadOnlyPrintJobQuery.History)]
    [InlineData(ReadOnlyPrintJobQuery.Timeline)]
    [InlineData(ReadOnlyPrintJobQuery.StateHistory)]
    [InlineData(ReadOnlyPrintJobQuery.CompletedAnalytics)]
    public async Task ReadOnlyQuery_WhenRowsMaterialize_LeavesChangeTrackerEmpty(
        ReadOnlyPrintJobQuery query)
    {
        DbContextOptions<AppDbContext> options =
            CreateInMemoryOptions(nameof(ReadOnlyQuery_WhenRowsMaterialize_LeavesChangeTrackerEmpty));
        Guid printerId = Guid.NewGuid();
        PrintJob queued = new PrintJobBuilder()
            .WithAssignedPrinterId(printerId)
            .Build();
        PrintJob completed = new PrintJobBuilder()
            .WithAssignedPrinterId(printerId)
            .AsCompleted()
            .Build();

        await using (AppDbContext seedContext = new(options))
        {
            seedContext.PrintJobs.AddRange(queued, completed);
            await seedContext.SaveChangesAsync();
        }

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        int resultCount = query switch
        {
            ReadOnlyPrintJobQuery.JobDetails =>
                (await repository.GetByIdWithGcodeFileAsync(queued.Id)) is null ? 0 : 1,
            ReadOnlyPrintJobQuery.FilteredQueue =>
                (await repository.GetFilteredJobsAsync()).Count,
            ReadOnlyPrintJobQuery.PrinterQueue =>
                (await repository.GetJobsByPrinterAsync(printerId)).Count,
            ReadOnlyPrintJobQuery.History =>
                (await repository.GetHistoryAsync()).jobs.Count,
            ReadOnlyPrintJobQuery.Timeline =>
                (await repository.GetTimelineJobsAsync()).Count,
            ReadOnlyPrintJobQuery.StateHistory =>
                (await repository.GetJobWithStateHistoryAsync(queued.Id)) is null ? 0 : 1,
            ReadOnlyPrintJobQuery.CompletedAnalytics =>
                (await repository.GetCompletedJobsForAnalyticsAsync()).Count,
            _ => throw new ArgumentOutOfRangeException(nameof(query), query, null),
        };

        Assert.True(resultCount > 0);
        Assert.Empty(queryContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetByIdWithRelationsAsync_WhenRowMaterializes_TracksEntityForMutation()
    {
        DbContextOptions<AppDbContext> options =
            CreateInMemoryOptions(nameof(GetByIdWithRelationsAsync_WhenRowMaterializes_TracksEntityForMutation));
        PrintJob job = new PrintJobBuilder().Build();

        await using (AppDbContext seedContext = new(options))
        {
            seedContext.PrintJobs.Add(job);
            await seedContext.SaveChangesAsync();
        }

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        PrintJob? loaded = await repository.GetByIdWithRelationsAsync(job.Id);

        Assert.NotNull(loaded);
        Assert.Contains(
            queryContext.ChangeTracker.Entries<PrintJob>(),
            entry => entry.Entity.Id == job.Id);
    }

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_NoActiveJobs_ReturnsEmptyList()
    {
        DbContextOptions<AppDbContext> options = CreateInMemoryOptions(nameof(GetPrinterQueueSummariesAsync_NoActiveJobs_ReturnsEmptyList));

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        List<PrinterQueueSummary> summaries = await repository.GetPrinterQueueSummariesAsync();

        Assert.Empty(summaries);
    }

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_PrintingPosition_ReflectsPriorityOrderNotArrivalOrder()
    {
        DbContextOptions<AppDbContext> options = CreateInMemoryOptions(nameof(GetPrinterQueueSummariesAsync_PrintingPosition_ReflectsPriorityOrderNotArrivalOrder));
        Guid printerId = Guid.NewGuid();
        DateTime baseTime = DateTime.UtcNow;

        // The printing job arrived first (earliest QueuedAt) but has LOWER priority than a
        // later-arriving queued job. QueueOrdering ranks by priority first, so the printing
        // job's position must reflect priority rank, not arrival order.
        PrintJob highPriorityQueued = new PrintJobBuilder()
            .WithAssignedPrinterId(printerId)
            .WithStatus(PrintJobStatus.Queued)
            .WithPriority(2)
            .WithQueuedAt(baseTime)
            .Build();
        PrintJob printing = new PrintJobBuilder()
            .WithAssignedPrinterId(printerId)
            .WithStatus(PrintJobStatus.Printing)
            .WithPriority(1)
            .WithQueuedAt(baseTime.AddMinutes(-10))
            .Build();
        PrintJob lowPriorityQueued = new PrintJobBuilder()
            .WithAssignedPrinterId(printerId)
            .WithStatus(PrintJobStatus.Queued)
            .WithPriority(0)
            .WithQueuedAt(baseTime.AddMinutes(5))
            .Build();

        await using (AppDbContext seedContext = new(options))
        {
            seedContext.PrintJobs.AddRange(highPriorityQueued, printing, lowPriorityQueued);
            await seedContext.SaveChangesAsync();
        }

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        List<PrinterQueueSummary> summaries = await repository.GetPrinterQueueSummariesAsync();

        PrinterQueueSummary summary = Assert.Single(summaries);
        Assert.Equal(printerId, summary.PrinterId);
        Assert.Equal(2, summary.QueuedCount);
        Assert.Equal(1, summary.PrintingCount);

        // Priority order is [highPriorityQueued(P2), printing(P1), lowPriorityQueued(P0)],
        // so the printing job is rank 2, not rank 1.
        Assert.Equal(2, summary.PrintingPosition);
    }

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_PrinterWithNoPrintingJob_PrintingPositionIsNull()
    {
        DbContextOptions<AppDbContext> options = CreateInMemoryOptions(nameof(GetPrinterQueueSummariesAsync_PrinterWithNoPrintingJob_PrintingPositionIsNull));
        Guid printerId = Guid.NewGuid();

        PrintJob first = new PrintJobBuilder().WithAssignedPrinterId(printerId).WithStatus(PrintJobStatus.Queued).WithPriority(1).Build();
        PrintJob second = new PrintJobBuilder().WithAssignedPrinterId(printerId).WithStatus(PrintJobStatus.Queued).WithPriority(0).Build();

        await using (AppDbContext seedContext = new(options))
        {
            seedContext.PrintJobs.AddRange(first, second);
            await seedContext.SaveChangesAsync();
        }

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        List<PrinterQueueSummary> summaries = await repository.GetPrinterQueueSummariesAsync();

        PrinterQueueSummary summary = Assert.Single(summaries);
        Assert.Equal(2, summary.QueuedCount);
        Assert.Equal(0, summary.PrintingCount);
        Assert.Null(summary.PrintingPosition);
    }

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_ExcludesTerminalAndPausedStatuses()
    {
        DbContextOptions<AppDbContext> options = CreateInMemoryOptions(nameof(GetPrinterQueueSummariesAsync_ExcludesTerminalAndPausedStatuses));
        Guid printerId = Guid.NewGuid();

        PrintJob activeQueued = new PrintJobBuilder().WithAssignedPrinterId(printerId).WithStatus(PrintJobStatus.Queued).Build();
        PrintJob assigned = new PrintJobBuilder().WithAssignedPrinterId(printerId).WithStatus(PrintJobStatus.Assigned).Build();
        PrintJob starting = new PrintJobBuilder().WithAssignedPrinterId(printerId).WithStatus(PrintJobStatus.Starting).Build();
        PrintJob paused = new PrintJobBuilder().WithAssignedPrinterId(printerId).WithStatus(PrintJobStatus.Paused).Build();
        PrintJob completed = new PrintJobBuilder().WithAssignedPrinterId(printerId).AsCompleted().Build();
        PrintJob failed = new PrintJobBuilder().WithAssignedPrinterId(printerId).AsFailed().Build();
        PrintJob cancelled = new PrintJobBuilder().WithAssignedPrinterId(printerId).AsCancelled().Build();

        await using (AppDbContext seedContext = new(options))
        {
            seedContext.PrintJobs.AddRange(activeQueued, assigned, starting, paused, completed, failed, cancelled);
            await seedContext.SaveChangesAsync();
        }

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        List<PrinterQueueSummary> summaries = await repository.GetPrinterQueueSummariesAsync();

        // Same active-job scope as GetJobsByPrinterAsync: only Queued/Printing count.
        // Assigned/Starting/Paused/Completed/Failed/Cancelled must not inflate the counts.
        PrinterQueueSummary summary = Assert.Single(summaries);
        Assert.Equal(1, summary.QueuedCount);
        Assert.Equal(0, summary.PrintingCount);
    }

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_PrinterWithOnlyNonActiveJobs_IsOmittedFromResult()
    {
        DbContextOptions<AppDbContext> options = CreateInMemoryOptions(nameof(GetPrinterQueueSummariesAsync_PrinterWithOnlyNonActiveJobs_IsOmittedFromResult));
        Guid printerId = Guid.NewGuid();
        PrintJob completed = new PrintJobBuilder().WithAssignedPrinterId(printerId).AsCompleted().Build();

        await using (AppDbContext seedContext = new(options))
        {
            seedContext.PrintJobs.Add(completed);
            await seedContext.SaveChangesAsync();
        }

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        List<PrinterQueueSummary> summaries = await repository.GetPrinterQueueSummariesAsync();

        // An idle printer with no active queue is absent, not zero-filled - callers must
        // treat a missing entry as "no active queue".
        Assert.Empty(summaries);
    }

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_IgnoresUnassignedJobs()
    {
        DbContextOptions<AppDbContext> options = CreateInMemoryOptions(nameof(GetPrinterQueueSummariesAsync_IgnoresUnassignedJobs));
        PrintJob unassigned = new PrintJobBuilder().WithAssignedPrinterId(null).WithStatus(PrintJobStatus.Queued).Build();

        await using (AppDbContext seedContext = new(options))
        {
            seedContext.PrintJobs.Add(unassigned);
            await seedContext.SaveChangesAsync();
        }

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        List<PrinterQueueSummary> summaries = await repository.GetPrinterQueueSummariesAsync();

        Assert.Empty(summaries);
    }

    [Fact]
    public async Task GetPrinterQueueSummariesAsync_MultiplePrinters_EachGetsAnIndependentSummary()
    {
        DbContextOptions<AppDbContext> options = CreateInMemoryOptions(nameof(GetPrinterQueueSummariesAsync_MultiplePrinters_EachGetsAnIndependentSummary));
        Guid printerA = Guid.NewGuid();
        Guid printerB = Guid.NewGuid();

        PrintJob printerAPrinting = new PrintJobBuilder().WithAssignedPrinterId(printerA).WithStatus(PrintJobStatus.Printing).WithPriority(1).Build();
        PrintJob printerAQueued = new PrintJobBuilder().WithAssignedPrinterId(printerA).WithStatus(PrintJobStatus.Queued).WithPriority(0).Build();
        PrintJob printerBQueued1 = new PrintJobBuilder().WithAssignedPrinterId(printerB).WithStatus(PrintJobStatus.Queued).WithPriority(1).Build();
        PrintJob printerBQueued2 = new PrintJobBuilder().WithAssignedPrinterId(printerB).WithStatus(PrintJobStatus.Queued).WithPriority(0).Build();

        await using (AppDbContext seedContext = new(options))
        {
            seedContext.PrintJobs.AddRange(printerAPrinting, printerAQueued, printerBQueued1, printerBQueued2);
            await seedContext.SaveChangesAsync();
        }

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        List<PrinterQueueSummary> summaries = await repository.GetPrinterQueueSummariesAsync();

        Assert.Equal(2, summaries.Count);
        PrinterQueueSummary summaryA = summaries.Single(s => s.PrinterId == printerA);
        Assert.Equal(1, summaryA.QueuedCount);
        Assert.Equal(1, summaryA.PrintingCount);
        Assert.Equal(1, summaryA.PrintingPosition);

        PrinterQueueSummary summaryB = summaries.Single(s => s.PrinterId == printerB);
        Assert.Equal(2, summaryB.QueuedCount);
        Assert.Equal(0, summaryB.PrintingCount);
        Assert.Null(summaryB.PrintingPosition);
    }

    [Fact]
    public async Task GetQueueStatsAsync_MixedStatuses_ReturnsCountsUsingOneAggregateQuery()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new PrintJobQueryInterceptor();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using (AppDbContext seedContext = new(options))
        {
            await seedContext.Database.EnsureCreatedAsync();
            seedContext.PrintJobs.AddRange(
                CreateJob(PrintJobStatus.Queued),
                CreateJob(PrintJobStatus.Queued),
                CreateJob(PrintJobStatus.Assigned),
                CreateJob(PrintJobStatus.Printing),
                CreateJob(PrintJobStatus.Paused),
                CreateJob(PrintJobStatus.Completed),
                CreateJob(PrintJobStatus.Failed),
                CreateJob(PrintJobStatus.Starting),
                CreateJob(PrintJobStatus.Cancelled));
            await seedContext.SaveChangesAsync();
        }

        interceptor.Reset();
        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        (int queued, int printing, int paused, int completed, int failed) = await repository.GetQueueStatsAsync();

        Assert.Equal(3, queued);
        Assert.Equal(1, printing);
        Assert.Equal(1, paused);
        Assert.Equal(1, completed);
        Assert.Equal(1, failed);
        string sql = Assert.Single(interceptor.PrintJobCommands);
        Assert.Contains("COUNT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Name\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetQueueStatsAsync_NoJobs_ReturnsZeroCounts()
    {
        DbContextOptions<AppDbContext> options = CreateInMemoryOptions(nameof(GetQueueStatsAsync_NoJobs_ReturnsZeroCounts));
        await using AppDbContext context = new(options);
        EfPrintJobManagementRepository repository = new(context);

        (int queued, int printing, int paused, int completed, int failed) = await repository.GetQueueStatsAsync();

        Assert.Equal(0, queued);
        Assert.Equal(0, printing);
        Assert.Equal(0, paused);
        Assert.Equal(0, completed);
        Assert.Equal(0, failed);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void BuildQueueStatsQuery_SupportedProvider_TranslatesToAggregateSql(string provider)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        _ = provider switch
        {
            "sqlite" => optionsBuilder.UseSqlite("Data Source=:memory:"),
            "postgres" => optionsBuilder.UseNpgsql("Host=localhost;Database=printfarmer"),
            "sqlserver" => optionsBuilder.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=printfarmer"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported provider test case."),
        };

        using var context = new AppDbContext(optionsBuilder.Options);
        string sql = EfPrintJobManagementRepository.BuildQueueStatsQuery(context.PrintJobs).ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static PrintJob CreateJob(PrintJobStatus status)
    {
        PrintJob job = new PrintJobBuilder()
            .WithAssignedPrinterId(null)
            .WithStatus(status)
            .Build();
        job.GcodeFileId = null;
        job.GcodeFile = null;
        return job;
    }

    private sealed class PrintJobQueryInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _printJobCommands = [];

        public IReadOnlyList<string> PrintJobCommands => _printJobCommands;

        public void Reset() => _printJobCommands.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("PrintJobs", StringComparison.Ordinal))
            {
                _printJobCommands.Add(command.CommandText);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    public enum ReadOnlyPrintJobQuery
    {
        JobDetails,
        FilteredQueue,
        PrinterQueue,
        History,
        Timeline,
        StateHistory,
        CompletedAnalytics,
    }
}
