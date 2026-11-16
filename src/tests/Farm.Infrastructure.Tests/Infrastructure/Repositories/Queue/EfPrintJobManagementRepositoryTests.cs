using System.Data.Common;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Tests.Builders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace Farm.Infrastructure.Tests.Infrastructure.Repositories.Queue;

public class EfPrintJobManagementRepositoryTests
{
    [Theory]
    [InlineData("name", false)]
    [InlineData("name_desc", true)]
    [InlineData("printer", false)]
    [InlineData("printer_desc", true)]
    [InlineData("model", false)]
    [InlineData("model_desc", true)]
    [InlineData("material", false)]
    [InlineData("material_desc", true)]
    [InlineData("filament", false)]
    [InlineData("filament_desc", true)]
    [InlineData("FILAMENT_DESC", true)]
    [InlineData("cost", false)]
    [InlineData("cost_desc", true)]
    [InlineData("duration_asc", false)]
    [InlineData("duration", true)]
    [InlineData("oldest", false)]
    [InlineData("newest", true)]
    [InlineData("completedAt", true)]
    public async Task GetHistoryAsync_SqliteSortModes_OrderFullResultsBeforePaging(string mode, bool descending)
    {
        await using SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using AppDbContext db = new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var jobs = new List<PrintJob>();
        foreach (int index in new[] { 3, 1, 2, 0 })
        {
            var model = new PrinterModel { Id = Guid.NewGuid(), Name = $"Model {index}" };
            var printer = new Printer { Id = Guid.NewGuid(), Name = $"Printer {index}", ServerUrl = $"http://history-{index}.invalid", Model = model, ModelId = model.Id };
            var file = new GcodeFile { Id = Guid.NewGuid(), Name = $"File {index}", FileHash = $"history-sort-{index}", RequiredMaterial = $"Material {index}", EstimatedFilamentWeightG = index + 1 };
            PrintJob job = new PrintJobBuilder().WithId(Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"))
                .WithName($"Reverse name {3 - index}").WithGcodeFile(file).WithAssignedPrinter(printer)
                .WithStatus(PrintJobStatus.Completed).WithActualEndTime(start.AddDays(index))
                .WithActualPrintTime(TimeSpan.FromMinutes(index + 1)).Build();
            job.MaterialCostUsd = index + 1;
            // Totals must prefer toolheads, then actual usage, then the file estimate.
            if (index == 1)
            {
                job.ActualFilamentUsage = 200;
                job.MaterialCostUsd = 200;
                job.ToolheadUsages.Add(new PrintJobToolheadUsage
                {
                    Id = Guid.NewGuid(),
                    PrintJobId = job.Id,
                    ToolheadIndex = 0,
                    FilamentUsageGrams = 1,
                    MaterialCostUsd = 1
                });
                job.ToolheadUsages.Add(new PrintJobToolheadUsage
                {
                    Id = Guid.NewGuid(),
                    PrintJobId = job.Id,
                    ToolheadIndex = 1,
                    FilamentUsageGrams = 1,
                    MaterialCostUsd = 1
                });
            }
            if (index == 2)
            {
                job.ActualFilamentUsage = 3;
            }

            jobs.Add(job);
        }
        db.PrintJobs.AddRange(jobs);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repository = new EfPrintJobManagementRepository(db);
        Guid[] expected = jobs.OrderBy(job => job.Id).Select(job => job.Id).ToArray();
        if (descending)
        {
            Array.Reverse(expected);
        }

        var first = await repository.GetHistoryAsync(limit: 2, sortBy: mode, dateStart: start, dateEnd: start.AddDays(3));
        var second = await repository.GetHistoryAsync(limit: 2, offset: 2, sortBy: mode, dateStart: start, dateEnd: start.AddDays(3));

        Assert.Equal(expected, first.jobs.Concat(second.jobs).Select(job => job.Id));
        Assert.Equal(4, first.totalCount);
        Assert.Equal(4, first.completedCount);
        Assert.Equal(600, first.totalPrintTimeSeconds);
        Assert.Equal(2, first.jobs.Concat(second.jobs).Single(job => job.Id == jobs.Single(value => value.ToolheadUsages.Count == 2).Id).ToolheadUsages.Count);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("duration_asc", new[] { 1, 2, 3, 4, 5 }, new[] { 0d, 0d, 600d, 600d, 900d })]
    [InlineData("duration", new[] { 5, 3, 4, 1, 2 }, new[] { 900d, 600d, 600d, 0d, 0d })]
    public async Task GetHistoryAsync_SqliteDuration_SortsMissingAsDisplayedZeroAcrossPages(string mode, int[] expectedIds, double[] expectedSeconds)
    {
        await using SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        var interceptor = new PrintJobQueryInterceptor();
        await using AppDbContext db = new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).AddInterceptors(interceptor).Options);
        await db.Database.EnsureCreatedAsync();
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var jobs = new List<PrintJob>();
        foreach (int index in new[] { 5, 2, 4, 1, 3 })
        {
            PrintJob job = new PrintJobBuilder()
                .WithId(Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}"))
                .WithGcodeFile(new GcodeFile { Id = Guid.NewGuid(), Name = $"Duration {index}", FileHash = $"duration-sort-{index}" })
                .WithStatus(PrintJobStatus.Completed).WithActualEndTime(now).Build();
            job.ActualPrintTime = index == 2 ? null : TimeSpan.FromMinutes(index == 1 ? 0 : index == 5 ? 15 : 10);
            jobs.Add(job);
        }
        db.PrintJobs.AddRange(jobs);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repository = new EfPrintJobManagementRepository(db);
        interceptor.Reset();

        var first = await repository.GetHistoryAsync(limit: 2, sortBy: mode);
        var second = await repository.GetHistoryAsync(limit: 2, offset: 2, sortBy: mode);
        var third = await repository.GetHistoryAsync(limit: 2, offset: 4, sortBy: mode);
        PrintJob[] ordered = first.jobs.Concat(second.jobs).Concat(third.jobs).ToArray();

        Assert.Equal(expectedIds.Select(id => Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}")), ordered.Select(job => job.Id));
        Assert.Equal(expectedSeconds, ordered.Select(job => job.ActualPrintTime?.TotalSeconds ?? 0));
        Assert.Equal(5, first.totalCount);
        string[] pageQueries = interceptor.PrintJobCommands
            .Where(sql => sql.Contains("LIMIT", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, pageQueries.Length);
        foreach (string sql in pageQueries)
        {
            // Explicit SQL coalescing, not SQLite's default NULL placement, determines the order.
            Assert.Contains("ORDER BY COALESCE(", sql, StringComparison.Ordinal);
            Assert.Contains(mode == "duration" ? ", 0) DESC," : ", 0),", sql, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("model")]
    [InlineData("model_desc")]
    [InlineData("material")]
    [InlineData("material_desc")]
    [InlineData("filament")]
    [InlineData("filament_desc")]
    [InlineData("cost")]
    [InlineData("cost_desc")]
    public async Task GetHistoryAsync_MissingValuesAndTies_AreStableAcrossPages(string mode)
    {
        await using AppDbContext db = new(CreateInMemoryOptions(Guid.NewGuid().ToString()));
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        PrintJob Job(int id, bool missing)
        {
            PrintJob job = new PrintJobBuilder().WithId(Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"))
                .WithStatus(PrintJobStatus.Completed).WithActualEndTime(now).WithQueuedAt(now).Build();
            job.AssignedPrinterId = null;
            if (!missing)
            {
                job.AssignedPrinter = new Printer { Id = Guid.NewGuid(), Name = "Same", Model = new PrinterModel { Id = Guid.NewGuid(), Name = "Same" } };
                job.RequiredMaterialType = "PLA";
                job.ActualFilamentUsage = 10;
                job.MaterialCostUsd = 1;
            }
            return job;
        }
        db.PrintJobs.AddRange(Job(3, false), Job(1, true), Job(2, false));
        await db.SaveChangesAsync();
        var repository = new EfPrintJobManagementRepository(db);
        var first = await repository.GetHistoryAsync(limit: 1, sortBy: mode);
        var second = await repository.GetHistoryAsync(limit: 1, offset: 1, sortBy: mode);
        var third = await repository.GetHistoryAsync(limit: 1, offset: 2, sortBy: mode);
        Assert.Equal(new[] { 2, 3, 1 }.Select(id => Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}")),
            first.jobs.Concat(second.jobs).Concat(third.jobs).Select(job => job.Id));
    }

    [Fact]
    public async Task GetAverageWaitTimeMinutesAsync_FakeClock_PreservesCutoffAndFilters()
    {
        DateTimeOffset now = new(2031, 4, 5, 6, 7, 8, TimeSpan.Zero);
        var clock = new Mock<TimeProvider>();
        clock.Setup(value => value.GetUtcNow()).Returns(() => now);
        await using AppDbContext db = new(CreateInMemoryOptions(nameof(GetAverageWaitTimeMinutesAsync_FakeClock_PreservesCutoffAndFilters)));
        var printer = new Printer { Id = Guid.NewGuid(), Name = "clock printer", ModelId = Guid.NewGuid() };
        DateTime cutoff = now.UtcDateTime.AddDays(-30);
        PrintJob Job(DateTime queuedAt, double? wait, PrintJobStatus status = PrintJobStatus.Completed)
        {
            PrintJob job = new PrintJobBuilder()
                .WithAssignedPrinter(printer)
                .WithStatus(status)
                .WithQueuedAt(queuedAt)
                .Build();
            job.ActualStartTime = wait.HasValue ? queuedAt.AddMinutes(wait.Value) : null;
            return job;
        }

        db.PrintJobs.AddRange(
            Job(cutoff, 20),
            Job(cutoff.AddTicks(-1), 1000),
            Job(cutoff.AddDays(1), -10),
            Job(cutoff.AddDays(1), null),
            Job(cutoff.AddDays(1), 1000, PrintJobStatus.Printing));
        PrintJob unassigned = Job(cutoff.AddDays(1), 40);
        unassigned.AssignedPrinter = null;
        unassigned.AssignedPrinterId = null;
        db.PrintJobs.Add(unassigned);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        EfPrintJobManagementRepository repository = new(db, clock.Object);

        Assert.Equal(10, await repository.GetAverageWaitTimeMinutesAsync(printer.ModelId));
        Assert.Equal(20, await repository.GetAverageWaitTimeMinutesAsync());
        Assert.Equal(0, await repository.GetAverageWaitTimeMinutesAsync(Guid.NewGuid()));
        now = now.AddDays(32);
        Assert.Equal(0, await repository.GetAverageWaitTimeMinutesAsync());
    }

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
            seedContext.Printers.Add(new PrinterBuilder().WithId(printerId).Build());
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
            seedContext.Printers.Add(new PrinterBuilder().WithId(printerId).Build());
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
            seedContext.Printers.AddRange(
                new PrinterBuilder().WithId(printerA).Build(),
                new PrinterBuilder().WithId(printerB).Build());
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
