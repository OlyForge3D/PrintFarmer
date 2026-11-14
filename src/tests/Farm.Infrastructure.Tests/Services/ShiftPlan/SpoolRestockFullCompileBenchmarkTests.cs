using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.ShiftPlan;
using Farm.Infrastructure.Services.ShiftPlan.Sources;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Infrastructure.Tests.Services.ShiftPlan;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class SpoolRestockFullCompileBenchmarkCollection
{
    public const string CollectionName = "SpoolRestockFullCompileBenchmark";
}

[Collection(SpoolRestockFullCompileBenchmarkCollection.CollectionName)]
public sealed class SpoolRestockFullCompileBenchmarkTests(ITestOutputHelper output)
{
    private const int WarmupRuns = 10;
    private const int MeasurementRuns = 20;
    private const int InventorySize = 1100;

    [Fact]
    [Trait("Category", "Performance")]
    public async Task CompileAsync_MaterializesMixedRestockTasks_ReportsFullPipelineMetrics()
    {
        foreach (int sourceCount in new[] { 1, 6 })
        {
            foreach (int spoolCount in new[] { 10, 50, 100 })
            {
                BenchmarkResult result = await RunScenarioAsync(spoolCount, sourceCount);
                output.WriteLine(
                    string.Join(
                        ",",
                        "sqlite_full_compile",
                        $"spools={spoolCount}",
                        $"sources={sourceCount}",
                        $"mixedStates=true",
                        $"readyTasks={result.ReadyTaskCount}",
                        $"sqlStatements={result.SqlStatementCount}",
                        $"usageAggregateQueries={result.UsageAggregateQueryCount}",
                        $"centralPages={result.CentralPageCount}",
                        $"centralBodyBytes={result.CentralBodyBytes}",
                        $"nativeCalls={result.NativeCallCount}",
                        $"nativeBodyBytes={result.NativeBodyBytes}",
                        $"materializedTaskP50Ms={result.MaterializedTaskP50Milliseconds:F2}",
                        $"materializedTaskP95Ms={result.MaterializedTaskP95Milliseconds:F2}",
                        $"taskFreshnessP50Ms={result.TaskFreshnessP50Milliseconds:F2}",
                        $"taskFreshnessP95Ms={result.TaskFreshnessP95Milliseconds:F2}",
                        $"allocatedP50Bytes={result.AllocatedP50Bytes}",
                        $"allocatedP95Bytes={result.AllocatedP95Bytes}"));
            }
        }
    }

    private static async Task<BenchmarkResult> RunScenarioAsync(int spoolCount, int sourceCount)
    {
        Workload workload = CreateWorkload(spoolCount, sourceCount);
        InventoryProbe inventoryProbe = new();
        SqlCommandCounter commandCounter = new();
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(commandCounter)
            .Options;
        await using (AppDbContext seedContext = new(options))
        {
            await seedContext.Database.EnsureCreatedAsync();
            await SeedWorkloadAsync(seedContext, workload);
        }

        Mock<ISettingsService> settingsService = new();
        settingsService.Setup(service => service.Get<ShiftPlanSettings>())
            .Returns(new ShiftPlanSettings
            {
                SpoolReorderThresholdGrams = 250,
                SpoolBurnRateLookbackDays = 30,
                SpoolBurnRateMinimumSamples = 1,
                SpoolRestockLeadMinutes = 60,
            });
        settingsService.Setup(service => service.Get<SpoolCoverageSettings>())
            .Returns(new SpoolCoverageSettings());

        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(service => service.GetConfig())
            .Returns(new SpoolmanConfigDto("http://central.local"));
        spoolman.Setup(service => service.ListSpoolsAsync(
                It.IsAny<SpoolmanSpoolQueryParams>(),
                It.IsAny<CancellationToken>()))
            .Returns((SpoolmanSpoolQueryParams query, CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                int offset = query.Offset ?? 0;
                int limit = query.Limit ?? 500;
                SpoolmanSpoolDto[] page = workload.CentralInventory
                    .Skip(offset)
                    .Take(limit)
                    .ToArray();
                inventoryProbe.RecordCentralPage(page);
                return Task.FromResult(SpoolmanReadResult.Ok<SpoolmanSpoolDto>(
                    page,
                    workload.CentralInventory.Count));
            });

        Mock<IBackendClient> nativeClient = new();
        nativeClient.As<ISupportsSpoolman>()
            .Setup(client => client.GetSpoolmanSpoolsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((string serverUrl, CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                string identity = CanonicalSpoolIdentity.NormalizeSourceIdentity(serverUrl);
                string body = workload.NativeInventories[identity];
                inventoryProbe.RecordNativeBody(body);
                return Task.FromResult<string?>(body);
            });
        Mock<IBackendClientFactory> backendFactory = new();
        backendFactory.Setup(factory => factory.GetClient((int)PrinterBackend.Moonraker))
            .Returns(nativeClient.Object);

        Mock<IOperatorFeatureGate> featureGate = new();
        featureGate.Setup(gate => gate.IsEnabledStrictAsync(
                OperatorFeature.ShiftPlan,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        ServiceCollection services = new();
        _ = services.AddLogging();
        _ = services.AddDbContext<AppDbContext>(dbOptions =>
            dbOptions.UseSqlite(connection).AddInterceptors(commandCounter));
        _ = services.AddSingleton(settingsService.Object);
        _ = services.AddSingleton(spoolman.Object);
        _ = services.AddSingleton(backendFactory.Object);
        _ = services.AddSingleton(featureGate.Object);
        _ = services.AddSingleton<TimeProvider>(TimeProvider.System);
        _ = services.AddScoped<IMutationWatermarkReader, DbMutationWatermarkReader>();
        _ = services.AddScoped<IFilamentCoverageSpoolResolver, FilamentCoverageSpoolResolver>();
        _ = services.AddScoped<ISpoolBurnRateProjectionService, SpoolBurnRateProjectionService>();
        _ = services.AddScoped<IShiftPlanTaskSource, SpoolRestockShiftPlanTaskSource>();
        _ = services.AddScoped<IUserTaskRepository, EfUserTaskRepository>();
        _ = services.AddScoped<IShiftPlanCompiler, ShiftPlanCompiler>();
        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        List<double> materializationTimes = [];
        List<double> taskFreshnessTimes = [];
        List<long> allocatedBytes = [];
        int? sqlStatementCount = null;
        int? usageAggregateQueryCount = null;
        InventorySnapshot? inventorySnapshot = null;

        for (int run = 0; run < WarmupRuns + MeasurementRuns; run++)
        {
            await using (AsyncServiceScope resetScope = provider.CreateAsyncScope())
            {
                AppDbContext resetDb = resetScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await resetDb.UserTasks.ExecuteDeleteAsync();
            }

            commandCounter.Reset();
            inventoryProbe.Reset();

            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch stopwatch = Stopwatch.StartNew();
            await using AsyncServiceScope compileScope = provider.CreateAsyncScope();
            IShiftPlanCompiler compiler = compileScope.ServiceProvider.GetRequiredService<IShiftPlanCompiler>();
            ShiftPlanCompileResult compileResult = await compiler.CompileAsync(
                ct: CancellationToken.None);
            int currentSqlStatementCount = commandCounter.Commands.Length;
            int currentAggregateQueryCount = commandCounter.Commands.Count(command =>
                command.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase));
            AppDbContext db = compileScope.ServiceProvider.GetRequiredService<AppDbContext>();
            List<UserTask> tasks = await db.UserTasks.AsNoTracking().ToListAsync();
            DateTime materializedAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
            stopwatch.Stop();
            long currentAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            InventorySnapshot currentInventory = inventoryProbe.Snapshot();

            Assert.Equal(workload.ReadyTaskCount, compileResult.Created);
            Assert.Equal(0, compileResult.Updated);
            Assert.Equal(0, compileResult.AutoCompleted);
            Assert.Equal(0, compileResult.SourceFailures);
            Assert.Equal(workload.ReadyTaskCount, tasks.Count);
            Assert.All(tasks, task =>
            {
                Assert.Equal(UserTaskStatus.Pending, task.Status);
                Assert.Equal(UserTaskSourceKind.SpoolReorder, task.SourceKind);
                Assert.StartsWith("spoolrestock:v1:", task.SourceId, StringComparison.Ordinal);
                Assert.NotNull(task.MetadataJson);
            });
            Assert.Equal(sourceCount, currentAggregateQueryCount);
            Assert.Equal(6, currentInventory.CentralPageCalls);
            Assert.Equal(sourceCount == 1 ? 0 : 10, currentInventory.NativeCalls);

            DateTime oldestEvaluationUtc = tasks
                .Select(GetEvaluatedAtUtc)
                .Min();
            double taskFreshnessMilliseconds =
                (materializedAtUtc - oldestEvaluationUtc).TotalMilliseconds;
            Assert.True(taskFreshnessMilliseconds >= 0);

            if (run >= WarmupRuns)
            {
                materializationTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
                taskFreshnessTimes.Add(taskFreshnessMilliseconds);
                allocatedBytes.Add(currentAllocatedBytes);

                sqlStatementCount ??= currentSqlStatementCount;
                usageAggregateQueryCount ??= currentAggregateQueryCount;
                inventorySnapshot ??= currentInventory;
                Assert.Equal(sqlStatementCount, currentSqlStatementCount);
                Assert.Equal(usageAggregateQueryCount, currentAggregateQueryCount);
                Assert.Equal(inventorySnapshot, currentInventory);
            }
        }

        Assert.Equal(MeasurementRuns, materializationTimes.Count);
        Assert.Equal(MeasurementRuns, taskFreshnessTimes.Count);
        Assert.Equal(MeasurementRuns, allocatedBytes.Count);
        Assert.Contains(workload.SourceCount, new[] { 1, 6 });

        return new BenchmarkResult(
            workload.ReadyTaskCount,
            sqlStatementCount!.Value,
            usageAggregateQueryCount!.Value,
            inventorySnapshot!.CentralPageCalls,
            inventorySnapshot.CentralBodyBytes,
            inventorySnapshot.NativeCalls,
            inventorySnapshot.NativeBodyBytes,
            Percentile(materializationTimes, 0.50),
            Percentile(materializationTimes, 0.95),
            Percentile(taskFreshnessTimes, 0.50),
            Percentile(taskFreshnessTimes, 0.95),
            Percentile(allocatedBytes, 0.50),
            Percentile(allocatedBytes, 0.95));
    }

    private static Workload CreateWorkload(int spoolCount, int sourceCount)
    {
        List<Occurrence> occurrences = [];
        int[] sourceOccurrences = new int[sourceCount];
        for (int index = 0; index < spoolCount; index++)
        {
            int sourceIndex = index % sourceCount;
            int ordinal = sourceOccurrences[sourceIndex]++;
            SpoolSourceKind sourceKind = sourceIndex == 0
                ? SpoolSourceKind.Central
                : SpoolSourceKind.MoonrakerNative;
            string sourceIdentity = sourceIndex == 0
                ? "http://central.local"
                : $"http://moon-{sourceIndex}.local";
            OccurrenceState state = ordinal == 0 || index % 3 == 0
                ? OccurrenceState.Ready
                : (index % 3) switch
                {
                    1 => OccurrenceState.InsufficientData,
                    _ => OccurrenceState.Unavailable,
                };
            occurrences.Add(new Occurrence(
                sourceIndex,
                ordinal,
                new CanonicalSpoolIdentity(sourceKind, sourceIdentity, 1001 + index),
                state));
        }

        HashSet<int> centralMissingIds = occurrences
            .Where(item =>
                item.Identity.SourceKind == SpoolSourceKind.Central
                && item.State == OccurrenceState.Unavailable)
            .Select(item => item.Identity.SpoolId)
            .ToHashSet();
        List<SpoolmanSpoolDto> centralInventory = BuildInventory(centralMissingIds);
        Dictionary<string, string> nativeInventories = occurrences
            .Where(item => item.Identity.SourceKind == SpoolSourceKind.MoonrakerNative)
            .Select(item => item.Identity.SourceIdentity)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                identity => identity,
                identity =>
                {
                    HashSet<int> missingIds = occurrences
                        .Where(item =>
                            item.Identity.SourceKind == SpoolSourceKind.MoonrakerNative
                            && string.Equals(item.Identity.SourceIdentity, identity, StringComparison.Ordinal)
                            && item.State == OccurrenceState.Unavailable)
                        .Select(item => item.Identity.SpoolId)
                        .ToHashSet();
                    return JsonSerializer.Serialize(BuildInventory(missingIds)
                        .Select(spool => new
                        {
                            id = spool.Id,
                            remaining_weight = spool.RemainingWeightG,
                            material = spool.Material,
                        }));
                },
                StringComparer.Ordinal);

        Workload workload = new(
            occurrences,
            centralInventory,
            nativeInventories,
            occurrences.Count(item => item.State == OccurrenceState.Ready),
            sourceCount);
        Assert.Contains(occurrences, item => item.State == OccurrenceState.Ready);
        Assert.Contains(occurrences, item => item.State == OccurrenceState.InsufficientData);
        Assert.Contains(occurrences, item => item.State == OccurrenceState.Unavailable);
        return workload;
    }

    private static List<SpoolmanSpoolDto> BuildInventory(HashSet<int> missingSpoolIds) =>
        Enumerable.Range(1, InventorySize)
            .Where(spoolId => !missingSpoolIds.Contains(spoolId))
            .Select(spoolId => new SpoolmanSpoolDto(
                spoolId,
                $"Spool {spoolId}",
                "PLA",
                500,
                null,
                true))
            .ToList();

    private static async Task SeedWorkloadAsync(AppDbContext db, Workload workload)
    {
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        db.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "Benchmark manufacturer",
        });
        db.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            ManufacturerId = manufacturerId,
            Name = "Benchmark model",
        });
        foreach (IGrouping<int, Occurrence> sourceGroup in workload.Occurrences
            .GroupBy(occurrence => occurrence.SourceIndex))
        {
            PrinterBackend backend = sourceGroup.Key == 0
                ? PrinterBackend.PrusaLink
                : PrinterBackend.Moonraker;
            string serverUrl = sourceGroup.Key == 0
                ? "http://printer-central.local"
                : $"http://moon-{sourceGroup.Key}.local";
            Printer printer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"Benchmark printer {sourceGroup.Key}",
                ServerUrl = serverUrl,
                BackendPort = 7125,
                Backend = (int)backend,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            };

            foreach (Occurrence occurrence in sourceGroup)
            {
                printer.Toolheads.Add(new Toolhead
                {
                    Id = Guid.NewGuid(),
                    PrinterId = printer.Id,
                    Name = $"Toolhead {occurrence.LocalOrdinal}",
                    Index = occurrence.LocalOrdinal,
                    IsPrimary = occurrence.LocalOrdinal == 0,
                    ToolheadType = ToolheadType.Physical,
                    CurrentSpoolId = occurrence.Identity.SpoolId,
                });

                if (occurrence.State == OccurrenceState.Ready)
                {
                    PrintJob job = CompletedJob();
                    PrintJobToolheadUsage usage = new()
                    {
                        Id = Guid.NewGuid(),
                        PrintJobId = job.Id,
                        PrintJob = job,
                        ToolheadIndex = occurrence.LocalOrdinal,
                        SpoolmanSpoolId = occurrence.Identity.SpoolId,
                    };
                    _ = usage.RecordAuthoritativeUsage(30, occurrence.Identity);
                    db.AddRange(job, usage);
                }
            }

            db.Printers.Add(printer);
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static PrintJob CompletedJob()
    {
        DateTime completedAtUtc = DateTime.UtcNow.AddDays(-1);
        return new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "spool-restock-benchmark.gcode",
            Status = PrintJobStatus.Completed,
            ActualEndTime = completedAtUtc,
            CreatedAt = completedAtUtc.AddHours(-1),
            UpdatedAt = completedAtUtc,
            QueuedAt = completedAtUtc.AddHours(-1),
        };
    }

    private static DateTime GetEvaluatedAtUtc(UserTask task)
    {
        using JsonDocument metadata = JsonDocument.Parse(task.MetadataJson!);
        return metadata.RootElement
            .GetProperty("evaluatedAtUtc")
            .GetDateTime();
    }

    private static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        int index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static long Percentile(IReadOnlyCollection<long> values, double percentile)
    {
        long[] sorted = values.Order().ToArray();
        int index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private enum OccurrenceState
    {
        Ready,
        InsufficientData,
        Unavailable,
    }

    private sealed record Occurrence(
        int SourceIndex,
        int LocalOrdinal,
        CanonicalSpoolIdentity Identity,
        OccurrenceState State);

    private sealed record Workload(
        IReadOnlyList<Occurrence> Occurrences,
        IReadOnlyList<SpoolmanSpoolDto> CentralInventory,
        IReadOnlyDictionary<string, string> NativeInventories,
        int ReadyTaskCount,
        int SourceCount);

    private sealed record BenchmarkResult(
        int ReadyTaskCount,
        int SqlStatementCount,
        int UsageAggregateQueryCount,
        int CentralPageCount,
        long CentralBodyBytes,
        int NativeCallCount,
        long NativeBodyBytes,
        double MaterializedTaskP50Milliseconds,
        double MaterializedTaskP95Milliseconds,
        double TaskFreshnessP50Milliseconds,
        double TaskFreshnessP95Milliseconds,
        long AllocatedP50Bytes,
        long AllocatedP95Bytes);

    private sealed record InventorySnapshot(
        int CentralPageCalls,
        long CentralBodyBytes,
        int NativeCalls,
        long NativeBodyBytes);

    private sealed class InventoryProbe
    {
        private int _centralPageCalls;
        private long _centralBodyBytes;
        private int _nativeCalls;
        private long _nativeBodyBytes;

        public void RecordCentralPage(IReadOnlyList<SpoolmanSpoolDto> page)
        {
            _ = Interlocked.Increment(ref _centralPageCalls);
            _ = Interlocked.Add(
                ref _centralBodyBytes,
                JsonSerializer.SerializeToUtf8Bytes(page).Length);
        }

        public void RecordNativeBody(string body)
        {
            _ = Interlocked.Increment(ref _nativeCalls);
            _ = Interlocked.Add(ref _nativeBodyBytes, Encoding.UTF8.GetByteCount(body));
        }

        public InventorySnapshot Snapshot() => new(
            Volatile.Read(ref _centralPageCalls),
            Interlocked.Read(ref _centralBodyBytes),
            Volatile.Read(ref _nativeCalls),
            Interlocked.Read(ref _nativeBodyBytes));

        public void Reset()
        {
            _ = Interlocked.Exchange(ref _centralPageCalls, 0);
            _ = Interlocked.Exchange(ref _centralBodyBytes, 0);
            _ = Interlocked.Exchange(ref _nativeCalls, 0);
            _ = Interlocked.Exchange(ref _nativeBodyBytes, 0);
        }
    }

    private sealed class SqlCommandCounter : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public string[] Commands => _commands.ToArray();

        public void Reset()
        {
            while (_commands.TryDequeue(out _))
            {
            }
        }

        private void Record(DbCommand command) => _commands.Enqueue(command.CommandText);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class DbMutationWatermarkReader(AppDbContext db) : IMutationWatermarkReader
    {
        public Task<long> GetCurrentAsync(CancellationToken ct = default) =>
            db.MutationCounters
                .AsNoTracking()
                .Where(counter => counter.Id == MutationCounter.GlobalId)
                .Select(counter => counter.Value)
                .SingleAsync(ct);
    }
}
