using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Infrastructure.Tests.Infrastructure.Repositories.Queue;

/// <summary>
/// Direct repository-level coverage for <see cref="EfPrintJobStatisticsRepository.GetAggregateByPrinterModelAsync"/>
/// (issue #2329). PrintStatsSyncModelBatchAggregateTests (in Farm.Modules.Maintenance.Tests)
/// proves the query-count reduction and end-to-end value equivalence via a real Sqlite database
/// driven through the hosted service; this file complements it with focused, filter-by-filter
/// coverage of the aggregate query itself - <c>successfulOnly</c> both ways, a <c>fromDate</c>
/// boundary, cross-model isolation, null durations, and the zero-match fallback - so each filter
/// branch is independently exercised rather than only through the single "successful, non-null-
/// duration" combination the end-to-end test seeds.
/// </summary>
public class EfPrintJobStatisticsRepositoryAggregateTests
{
    [Fact]
    public async Task GetAggregateByPrinterModelAsync_SuccessfulOnlyTrue_ExcludesFailedJobs()
    {
        Guid modelId = Guid.NewGuid();
        string dbName = $"{nameof(GetAggregateByPrinterModelAsync_SuccessfulOnlyTrue_ExcludesFailedJobs)}_{Guid.NewGuid():N}";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using (AppDbContext seed = new(options))
        {
            seed.PrintJobStatistics.AddRange(
                NewJob(modelId, isSuccess: true, durationMs: 10 * 3600 * 1000L),
                NewJob(modelId, isSuccess: true, durationMs: 5 * 3600 * 1000L),
                NewJob(modelId, isSuccess: false, durationMs: 999 * 3600 * 1000L));
            await seed.SaveChangesAsync();
        }

        await using AppDbContext query = new(options);
        EfPrintJobStatisticsRepository repository = new(query);

        PrintJobStatisticsAggregate result = await repository.GetAggregateByPrinterModelAsync(modelId, successfulOnly: true);

        Assert.Equal(2, result.JobCount);
        Assert.Equal(15 * 3600 * 1000L, result.TotalDurationMs);
        Assert.Equal(15.0, result.TotalDurationHours, precision: 6);
    }

    [Fact]
    public async Task GetAggregateByPrinterModelAsync_SuccessfulOnlyFalse_IncludesFailedJobs()
    {
        Guid modelId = Guid.NewGuid();
        string dbName = $"{nameof(GetAggregateByPrinterModelAsync_SuccessfulOnlyFalse_IncludesFailedJobs)}_{Guid.NewGuid():N}";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using (AppDbContext seed = new(options))
        {
            seed.PrintJobStatistics.AddRange(
                NewJob(modelId, isSuccess: true, durationMs: 10 * 3600 * 1000L),
                NewJob(modelId, isSuccess: false, durationMs: 2 * 3600 * 1000L));
            await seed.SaveChangesAsync();
        }

        await using AppDbContext query = new(options);
        EfPrintJobStatisticsRepository repository = new(query);

        PrintJobStatisticsAggregate result = await repository.GetAggregateByPrinterModelAsync(modelId, successfulOnly: false);

        Assert.Equal(2, result.JobCount);
        Assert.Equal(12 * 3600 * 1000L, result.TotalDurationMs);
        Assert.Equal(12.0, result.TotalDurationHours, precision: 6);
    }

    [Fact]
    public async Task GetAggregateByPrinterModelAsync_FromDateBoundary_ExcludesJobsCompletedBeforeIt()
    {
        Guid modelId = Guid.NewGuid();
        DateTime cutoffUtc = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        string dbName = $"{nameof(GetAggregateByPrinterModelAsync_FromDateBoundary_ExcludesJobsCompletedBeforeIt)}_{Guid.NewGuid():N}";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using (AppDbContext seed = new(options))
        {
            seed.PrintJobStatistics.AddRange(
                NewJob(modelId, isSuccess: true, durationMs: 1 * 3600 * 1000L, completedAtUtc: cutoffUtc.AddSeconds(-1)),
                NewJob(modelId, isSuccess: true, durationMs: 2 * 3600 * 1000L, completedAtUtc: cutoffUtc),
                NewJob(modelId, isSuccess: true, durationMs: 3 * 3600 * 1000L, completedAtUtc: cutoffUtc.AddDays(1)));
            await seed.SaveChangesAsync();
        }

        await using AppDbContext query = new(options);
        EfPrintJobStatisticsRepository repository = new(query);

        PrintJobStatisticsAggregate result = await repository.GetAggregateByPrinterModelAsync(
            modelId,
            successfulOnly: true,
            fromDate: cutoffUtc);

        // fromDate is an inclusive lower bound (>=), matching GetByPrinterModelAsync.
        Assert.Equal(2, result.JobCount);
        Assert.Equal(5 * 3600 * 1000L, result.TotalDurationMs);
    }

    [Fact]
    public async Task GetAggregateByPrinterModelAsync_OtherModelJobsAreExcluded()
    {
        Guid modelA = Guid.NewGuid();
        Guid modelB = Guid.NewGuid();
        string dbName = $"{nameof(GetAggregateByPrinterModelAsync_OtherModelJobsAreExcluded)}_{Guid.NewGuid():N}";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using (AppDbContext seed = new(options))
        {
            seed.PrintJobStatistics.AddRange(
                NewJob(modelA, isSuccess: true, durationMs: 4 * 3600 * 1000L),
                NewJob(modelB, isSuccess: true, durationMs: 999 * 3600 * 1000L));
            await seed.SaveChangesAsync();
        }

        await using AppDbContext query = new(options);
        EfPrintJobStatisticsRepository repository = new(query);

        PrintJobStatisticsAggregate result = await repository.GetAggregateByPrinterModelAsync(modelA);

        Assert.Equal(1, result.JobCount);
        Assert.Equal(4 * 3600 * 1000L, result.TotalDurationMs);
    }

    [Fact]
    public async Task GetAggregateByPrinterModelAsync_NullDuration_CountsJobButExcludesFromDurationSum()
    {
        Guid modelId = Guid.NewGuid();
        string dbName = $"{nameof(GetAggregateByPrinterModelAsync_NullDuration_CountsJobButExcludesFromDurationSum)}_{Guid.NewGuid():N}";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using (AppDbContext seed = new(options))
        {
            seed.PrintJobStatistics.AddRange(
                NewJob(modelId, isSuccess: true, durationMs: 7 * 3600 * 1000L),
                NewJob(modelId, isSuccess: true, durationMs: null));
            await seed.SaveChangesAsync();
        }

        await using AppDbContext query = new(options);
        EfPrintJobStatisticsRepository repository = new(query);

        PrintJobStatisticsAggregate result = await repository.GetAggregateByPrinterModelAsync(modelId);

        // JobCount reflects every matching row (mirrors the old `printerJobs.Count`, which counted
        // ALL matching rows before the separate `.Where(HasValue)` filter used only for the
        // duration sum); the null-duration row is excluded only from TotalDurationMs/Hours.
        Assert.Equal(2, result.JobCount);
        Assert.Equal(7 * 3600 * 1000L, result.TotalDurationMs);
        Assert.Equal(7.0, result.TotalDurationHours, precision: 6);
    }

    [Fact]
    public async Task GetAggregateByPrinterModelAsync_NoMatchingRows_ReturnsZeroValueAggregate()
    {
        Guid modelId = Guid.NewGuid();
        string dbName = $"{nameof(GetAggregateByPrinterModelAsync_NoMatchingRows_ReturnsZeroValueAggregate)}_{Guid.NewGuid():N}";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using AppDbContext query = new(options);
        EfPrintJobStatisticsRepository repository = new(query);

        PrintJobStatisticsAggregate result = await repository.GetAggregateByPrinterModelAsync(modelId);

        Assert.Equal(0, result.JobCount);
        Assert.Equal(0L, result.TotalDurationMs);
        Assert.Equal(0.0, result.TotalDurationHours);
    }

    private static PrintJobStatistics NewJob(
        Guid modelId,
        bool isSuccess,
        long? durationMs,
        DateTime? completedAtUtc = null)
    {
        return new PrintJobStatistics
        {
            Id = Guid.NewGuid(),
            PrintJobId = Guid.NewGuid(),
            PrinterModelId = modelId,
            IsSuccess = isSuccess,
            ActualDurationMs = durationMs,
            CompletedAtUtc = completedAtUtc ?? DateTime.UtcNow,
        };
    }
}
