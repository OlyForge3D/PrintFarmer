using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Security;
using Farm.Modules.Maintenance.Services.Maintenance;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Modules.Maintenance.Tests.Services.Maintenance;

/// <summary>
/// Regression coverage for issue #2329: <see cref="PrintStatsSyncHostedService"/> used to call
/// <see cref="IPrintJobStatisticsRepository.GetByPrinterModelAsync"/> — which materializes a
/// printer model's ENTIRE all-time successful-job history as a row list — once per printer in the
/// rotation batch, even when several printers in the same cycle share a <see cref="Printer.ModelId"/>
/// and would issue identical queries. The fix groups the batch by <c>ModelId</c> and issues one
/// grouped SQL aggregate (<see cref="IPrintJobStatisticsRepository.GetAggregateByPrinterModelAsync"/>)
/// per distinct model per cycle instead.
///
/// This test drives the REAL <see cref="PrintStatsSyncHostedService.SyncPrinterStatisticsAsync(PrintStatsSyncSettings,CancellationToken)"/>
/// batch entry point, through real EF Core repositories against a genuine (in-memory) Sqlite
/// database — not the EF Core InMemory provider, which never produces real <see cref="DbCommand"/>s
/// and therefore cannot prove SQL-level aggregation actually happened — so both acceptance
/// criteria are checked against real, translated SQL:
///   1. Query count: with N printers sharing M distinct models in one rotation batch, the number
///      of SQL commands touching the <c>PrintJobStatistics</c> table must equal M, not N.
///   2. Correctness: the resulting <see cref="PrinterStatistics.TotalJobsCompleted"/> /
///      <see cref="PrinterStatistics.TotalPrintHours"/> for a representative printer on each model
///      must exactly match an oracle computed independently, in memory, over the same seeded rows
///      — i.e. the new grouped-query path is numerically identical to the removed
///      full-materialization-then-aggregate-in-memory path.
/// </summary>
public sealed class PrintStatsSyncModelBatchAggregateTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly PrintJobStatisticsTableCommandInterceptor _interceptor;

    public PrintStatsSyncModelBatchAggregateTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        // PrintJobStatistics.PrintJobId carries a FK to PrintJob. Seeding 10,000+ statistics rows
        // with matching PrintJob rows would add unnecessary seeding cost for a test whose only
        // goal is exercising the job-statistics aggregate query; disable FK enforcement instead so
        // synthetic PrintJobId values are accepted without a matching PrintJob row.
        using (SqliteCommand disableForeignKeys = _connection.CreateCommand())
        {
            disableForeignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
            disableForeignKeys.ExecuteNonQuery();
        }

        _interceptor = new PrintJobStatisticsTableCommandInterceptor();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SyncPrinterStatisticsAsync_PrintersSharingModel_IssuesOneAggregateQueryPerDistinctModel()
    {
        // Two distinct printer models, each shared by multiple printers, backed by 10,000+ total
        // PrintJobStatistics rows (issue #2329 acceptance criterion 4). FK enforcement is off by
        // default on this Sqlite connection, so seeded PrintJobId values don't need a matching
        // PrintJob row.
        Guid modelA = Guid.NewGuid();
        Guid modelB = Guid.NewGuid();
        const int modelAJobCount = 6000;
        const int modelBJobCount = 4001;
        const long modelADurationMsPerJob = 25 * 60 * 1000L; // 25 minutes
        const long modelBDurationMsPerJob = 40 * 60 * 1000L; // 40 minutes

        // 3 printers share modelA, 2 share modelB - 5 printers total, all fit in one rotation
        // batch, so the fix's query-count reduction (2 distinct models, not 5 printers) is
        // unambiguous.
        List<Printer> modelAPrinters =
        [
            NewPrusaLinkPrinter(modelA),
            NewPrusaLinkPrinter(modelA),
            NewPrusaLinkPrinter(modelA),
        ];
        List<Printer> modelBPrinters =
        [
            NewPrusaLinkPrinter(modelB),
            NewPrusaLinkPrinter(modelB),
        ];
        List<Printer> allPrinters = [.. modelAPrinters, .. modelBPrinters];

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_interceptor)
            .Options;

        await using (AppDbContext seed = new(options))
        {
            await seed.Database.EnsureCreatedAsync();

            seed.Printers.AddRange(allPrinters);
            SeedJobStatistics(seed, modelA, modelAJobCount, modelADurationMsPerJob);
            SeedJobStatistics(seed, modelB, modelBJobCount, modelBDurationMsPerJob);

            await seed.SaveChangesAsync();
        }

        // Oracle: exactly what the OLD in-memory computation would have produced -
        // Count() over ALL matching (successful) rows, Sum() over ActualDurationMs excluding
        // nulls - computed independently of the production code under test.
        int oracleModelAJobs = modelAJobCount;
        double oracleModelAHours = modelAJobCount * modelADurationMsPerJob / 1000.0 / 3600.0;
        int oracleModelBJobs = modelBJobCount;
        double oracleModelBHours = modelBJobCount * modelBDurationMsPerJob / 1000.0 / 3600.0;

        ServiceCollection services = new();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_interceptor));
        services.AddScoped<IPrintersRepository>(sp =>
            new EfPrintersRepository(sp.GetRequiredService<AppDbContext>(), NoOpSensitiveDataProtector.Instance));
        services.AddScoped<IPrinterStatisticsRepository, EfPrinterStatisticsRepository>();
        services.AddScoped<IToolheadStatisticsRepository, EfToolheadStatisticsRepository>();
        services.AddScoped<IPrintJobStatisticsRepository, EfPrintJobStatisticsRepository>();
        Mock<IOperatorFeatureGate> featureGate = new();
        featureGate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        featureGate.Setup(g => g.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        services.AddSingleton<IOperatorFeatureGate>(featureGate.Object);
        using ServiceProvider provider = services.BuildServiceProvider();

        PrintStatsSyncHostedService service = new(
            provider,
            Mock.Of<ILogger<PrintStatsSyncHostedService>>(),
            Mock.Of<IOptionsMonitor<PrintStatsSyncSettings>>(),
            Mock.Of<IBackgroundServiceMonitor>());

        PrintStatsSyncSettings settings = new()
        {
            IncludePrintFarmerJobs = true,
            MaxPrintersPerIteration = allPrinters.Count,
            ApiTimeoutSeconds = 30,
        };

        _interceptor.Reset();

        await service.SyncPrinterStatisticsAsync(settings, CancellationToken.None);

        // Criterion 1: query count scales with distinct models (2), not printers (5).
        _interceptor.PrintJobStatisticsCommandCount.Should().Be(
            2,
            "printers sharing a ModelId within the same rotation batch must reuse one grouped " +
            "aggregate query per distinct model, not one per printer (issue #2329)");

        // Criterion 2/4: resulting aggregate values are IDENTICAL to the old in-memory computation.
        await using AppDbContext verify = new(options);
        foreach (Printer printer in modelAPrinters)
        {
            PrinterStatistics stats = await verify.PrinterStatisticsSet
                .AsNoTracking()
                .SingleAsync(s => s.PrinterId == printer.Id);
            stats.TotalJobsCompleted.Should().Be(oracleModelAJobs);
            stats.TotalPrintHours.Should().BeApproximately(oracleModelAHours, 0.0001);
        }

        foreach (Printer printer in modelBPrinters)
        {
            PrinterStatistics stats = await verify.PrinterStatisticsSet
                .AsNoTracking()
                .SingleAsync(s => s.PrinterId == printer.Id);
            stats.TotalJobsCompleted.Should().Be(oracleModelBJobs);
            stats.TotalPrintHours.Should().BeApproximately(oracleModelBHours, 0.0001);
        }
    }

    [Fact]
    public async Task GetAggregateByPrinterModelAsync_MixedDurations_MatchesOldPerRowSummationWithinFloatingPointEpsilon()
    {
        // The two facts above use a single, uniform duration-per-job value for every seeded row,
        // so a summation-order difference between the new grouped SQL SUM and the old in-memory
        // OrderByDescending(CompletedAtUtc).Sum(...) could never actually surface numerically.
        // This test seeds adversarial, non-uniform millisecond durations specifically to make any
        // such divergence visible, then proves it is negligible (well under any threshold that
        // could matter for maintenance wear tracking) rather than merely assuming it away.
        Guid modelId = Guid.NewGuid();
        long[] durationsMs =
        [
            12_345L, 987_654L, 3_333_333L, 7L, 999_999_999L,
            42L, 6_000_001L, 1L, 55_555L, 123_456_789L,
        ];

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_interceptor)
            .Options;

        await using (AppDbContext seed = new(options))
        {
            await seed.Database.EnsureCreatedAsync();
            DateTime baseTimeUtc = DateTime.UtcNow;
            for (int i = 0; i < durationsMs.Length; i++)
            {
                seed.PrintJobStatistics.Add(new PrintJobStatistics
                {
                    Id = Guid.NewGuid(),
                    PrintJobId = Guid.NewGuid(),
                    PrinterModelId = modelId,
                    IsSuccess = true,
                    ActualDurationMs = durationsMs[i],
                    CompletedAtUtc = baseTimeUtc.AddSeconds(-i),
                });
            }

            await seed.SaveChangesAsync();
        }

        await using AppDbContext query = new(options);
        EfPrintJobStatisticsRepository repository = new(query);

        // Oracle: replicate the EXACT removed in-memory computation. GetByPrinterModelAsync still
        // orders by CompletedAtUtc descending - the same order the removed hosted-service code
        // consumed - and this sums per-row-divided hours in that list order, exactly as
        // `printerJobs.Sum(j => j.ActualDurationMs!.Value / 1000.0 / 3600.0)` did before the fix.
        List<PrintJobStatistics> rows = await repository.GetByPrinterModelAsync(modelId, successfulOnly: true);
        long oracleTotalMs = rows.Sum(j => j.ActualDurationMs!.Value);
        double oracleHours = rows.Sum(j => j.ActualDurationMs!.Value / 1000.0 / 3600.0);

        PrintJobStatisticsAggregate result = await repository.GetAggregateByPrinterModelAsync(modelId, successfulOnly: true);

        Assert.Equal(durationsMs.Length, result.JobCount);
        Assert.Equal(oracleTotalMs, result.TotalDurationMs);

        // SQLite's internal row-iteration order for a SUM aggregate is engine-defined and not
        // provably identical to the old list's CompletedAtUtc-descending order, so bit-for-bit
        // identity is not guaranteed by any grouped-SQL rewrite. What this asserts instead is the
        // defined, tested tolerance for issue #2329's "identical aggregate values" criterion: a
        // relative divergence far too small to affect maintenance wear tracking. 1e-9 hours is
        // ~3.6 microseconds - many orders of magnitude below any print-duration measurement this
        // system records - even for this adversarially non-uniform dataset.
        result.TotalDurationHours.Should().BeApproximately(oracleHours, 1e-9);
    }

    private static Printer NewPrusaLinkPrinter(Guid modelId)
    {
        Guid id = Guid.NewGuid();
        return new Printer
        {
            Id = id,
            Name = $"Printer {id}",
            Backend = (int)PrinterBackend.PrusaLink,
            ModelId = modelId,
            ServerUrl = $"http://printer-{id}.local",
        };
    }

    private static void SeedJobStatistics(AppDbContext context, Guid modelId, int count, long durationMs)
    {
        for (int i = 0; i < count; i++)
        {
            context.PrintJobStatistics.Add(new PrintJobStatistics
            {
                Id = Guid.NewGuid(),
                PrintJobId = Guid.NewGuid(),
                PrinterModelId = modelId,
                IsSuccess = true,
                ActualDurationMs = durationMs,
                CompletedAtUtc = DateTime.UtcNow,
            });
        }
    }

    /// <summary>
    /// Null-op protector - this test only exercises the job-statistics aggregation path, not
    /// encryption/decryption of printer credentials.
    /// </summary>
    private sealed class NoOpSensitiveDataProtector : ISensitiveDataProtector
    {
        public static NoOpSensitiveDataProtector Instance { get; } = new();
        public string? Protect(string? plainText) => plainText;
        public string? Unprotect(string? protectedText) => protectedText;
    }

    /// <summary>
    /// Counts only SQL commands whose text references the <c>PrintJobStatistics</c> table, so the
    /// assertion isolates the job-statistics aggregate query from the rotation batch's other
    /// (unrelated) per-printer commands - printer fetch, stats upsert, toolhead lookups, cursor
    /// advance, etc. - which would otherwise inflate a raw total-command-count assertion.
    /// </summary>
    private sealed class PrintJobStatisticsTableCommandInterceptor : DbCommandInterceptor
    {
        public int PrintJobStatisticsCommandCount { get; private set; }

        public void Reset()
        {
            PrintJobStatisticsCommandCount = 0;
        }

        private void CountIfPrintJobStatistics(DbCommand command)
        {
            if (command.CommandText.Contains("PrintJobStatistics", StringComparison.OrdinalIgnoreCase))
            {
                PrintJobStatisticsCommandCount++;
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CountIfPrintJobStatistics(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CountIfPrintJobStatistics(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            CountIfPrintJobStatistics(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            CountIfPrintJobStatistics(command);
            return ValueTask.FromResult(result);
        }
    }
}
