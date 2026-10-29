using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Repositories.Tasks;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Catalog.Caching;
using Farm.Infrastructure.Services.Tasks;
using Farm.Slicer.Module.Api.HostedServices;
using Farm.Slicer.Module.Api.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Slicer.Module.Tests.HostedServices;

public sealed class ProfileTaskScanBatchTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(201)]
    public async Task BatchReads_RequestedIdsOnly_UseBoundedDistinctProjections(int count)
    {
        await using Fixture fixture = await Fixture.CreateAsync(count, count);
        Guid[] ids = fixture.Printers.Select(p => p.ModelId).ToArray();
        foreach (Guid id in ids)
        {
            fixture.Slicer.MachineModelProfiles.Add(new() { Id = Guid.NewGuid(), PrinterModelId = id, Name = id.ToString() });
            fixture.Slicer.MachineProfiles.Add(new() { Id = Guid.NewGuid(), PrinterModelId = id, Name = id.ToString() });
        }

        fixture.Slicer.MachineProfiles.Add(new() { Id = Guid.NewGuid(), PrinterModelId = Guid.NewGuid(), Name = "unrelated" });
        fixture.Slicer.MachineProfiles.Add(new() { Id = Guid.NewGuid(), Name = "unlinked" });
        await fixture.Slicer.SaveChangesAsync();
        fixture.Slicer.ChangeTracker.Clear();
        fixture.App.ChangeTracker.Clear();
        fixture.Reset();
        Guid[] duplicated = ids.Concat(ids).ToArray();

        Assert.True((await fixture.ModelProfiles.GetPrinterModelIdsWithProfilesAsync(duplicated)).SetEquals(ids));
        Assert.True((await fixture.MachineProfiles.GetPrinterModelIdsWithProfilesAsync(duplicated)).SetEquals(ids));
        IReadOnlyList<ProfileTaskModelDto> names = await fixture.Catalog.GetProfileTaskModelsAsync(duplicated);

        Assert.True(names.Select(n => n.Id).ToHashSet().SetEquals(ids));
        Assert.All(names, name => Assert.Equal("Acme", name.ManufacturerName));
        Assert.Equal(2 * Chunks(count), fixture.SlicerCommands.Total);
        Assert.Equal(Chunks(count), fixture.AppCommands.Total);
        Assert.Empty(fixture.App.ChangeTracker.Entries());
        Assert.Empty(fixture.Slicer.ChangeTracker.Entries());
        Assert.All(fixture.AppCommands.Sql, sql =>
        {
            Assert.Contains("LEFT JOIN", sql);
            Assert.DoesNotContain("Toolheads", sql);
            Assert.DoesNotContain("Filament", sql);
        });
        Assert.True(fixture.AppCommands.MaxParameters <= 100);
        Assert.True(fixture.SlicerCommands.MaxParameters <= 100);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(100, 10)]
    [InlineData(1000, 100)]
    [InlineData(1000, 1)]
    [InlineData(10000, 1000)]
    public async Task Scan_CardinalityMatrix_ReportsSqlMutationsAndEvents(int printers, int models)
    {
        // Single-sample timings, not a statistical benchmark. Setup and verification
        // are outside the measured region; both scans use real, separate SQLite stores.
        foreach (string workload in new[] { "covered", "missing", "unchanged" })
        {
            foreach (bool legacy in new[] { true, false })
            {
                await using Fixture fixture = await Fixture.CreateAsync(printers, models);
                if (workload == "covered")
                {
                    int i = 0;
                    foreach (Guid id in fixture.Printers.Select(p => p.ModelId).Distinct())
                    {
                        if (i++ % 2 == 0)
                        {
                            fixture.Slicer.MachineModelProfiles.Add(new() { Id = Guid.NewGuid(), PrinterModelId = id, Name = id.ToString() });
                        }
                        else
                        {
                            fixture.Slicer.MachineProfiles.Add(new() { Id = Guid.NewGuid(), PrinterModelId = id, Name = id.ToString() });
                        }
                    }

                    await fixture.Slicer.SaveChangesAsync();
                }
                else if (workload == "unchanged")
                {
                    await fixture.ScanAsync();
                }

                fixture.App.ChangeTracker.Clear();
                fixture.Slicer.ChangeTracker.Clear();
                fixture.Reset();
                long allocatedBefore = GC.GetTotalAllocatedBytes();
                Stopwatch timer = Stopwatch.StartNew();
                if (legacy)
                {
                    await fixture.LegacyScanAsync();
                }
                else
                {
                    await fixture.ScanAsync();
                }

                timer.Stop();
                long allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
                int expectedMutations = workload == "missing" ? (legacy ? printers : models) : 0;
                int expectedAppSql = workload switch
                {
                    "covered" => 1,
                    "missing" => legacy ? 1 + (5 * models) + (6 * printers) : 1 + Chunks(models) + (6 * models),
                    _ => legacy ? 1 + (5 * models) + printers : 1 + Chunks(models) + models
                };
                Assert.Equal(expectedAppSql, fixture.AppCommands.Total);
                Assert.Equal(expectedMutations, fixture.AppCommands.TaskWrites);
                Assert.Equal(expectedMutations, fixture.Events.Count);
                Assert.Equal(expectedMutations, fixture.CountEvents);
                Assert.Equal(legacy ? 2 * models : 2 * Chunks(models), fixture.SlicerCommands.Total);
                Assert.Equal(workload == "covered" ? 0 : (legacy ? 2 * models : models == 0 ? 0 : 1), fixture.CatalogCalls);
                output.WriteLine(
                    $"{(legacy ? "legacy" : "batch")},{workload},P={printers},M={models}," +
                    $"appSql={fixture.AppCommands.Total},slicerSql={fixture.SlicerCommands.Total}," +
                    $"catalogCalls={fixture.CatalogCalls},taskWrites={fixture.AppCommands.TaskWrites}," +
                    $"taskEvents={fixture.Events.Count},countEvents={fixture.CountEvents}," +
                    $"maxParams={Math.Max(fixture.AppCommands.MaxParameters, fixture.SlicerCommands.MaxParameters)}," +
                    $"ms={timer.Elapsed.TotalMilliseconds:F2},allocatedBytes={allocated}");
                List<UserTask> tasks = await fixture.App.UserTasks.AsNoTracking().ToListAsync();
                Assert.Equal(workload == "covered" ? 0 : models, tasks.Count);
                foreach (UserTask task in tasks)
                {
                    Assert.True(JsonSerializer.Deserialize<Guid[]>(task.RelatedEntityIdsJson!)!.ToHashSet()
                        .SetEquals(fixture.Printers.Where(p => p.ModelId == task.EntityId).Select(p => p.Id)));
                    Assert.Equal($"Import slicer profiles for Acme Model-{Array.IndexOf(fixture.ModelIds, task.EntityId)}", task.Title);
                }
            }
        }
    }

    [Fact]
    public async Task Scan_MiddleModelWriteFails_NextScanConvergesWithoutDuplicateEvents()
    {
        await using Fixture fixture = await Fixture.CreateAsync(6, 3);
        fixture.AppCommands.FailTaskWriteNumber = 2;

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.ScanAsync());
        Assert.Single(fixture.Events);
        Assert.Equal(1, await fixture.App.UserTasks.CountAsync());

        // A new scan gets a new scope/context in production, not the failed
        // context's tracked Added entity.
        fixture.App.ChangeTracker.Clear();
        fixture.AppCommands.FailTaskWriteNumber = null;
        await fixture.ScanAsync();
        Assert.Equal(3, fixture.Events.Count);
        Assert.Equal(3, fixture.CountEvents);
        Assert.Equal(3, await fixture.App.UserTasks.CountAsync());
        await fixture.ScanAsync();
        Assert.Equal(3, fixture.Events.Count);
    }

    [Fact]
    public async Task BatchUpsert_ExistingUnionAndRepeatedScan_PreservesIdentityAndDoesNotMutateNoOp()
    {
        await using Fixture fixture = await Fixture.CreateAsync(3, 1);
        Guid historical = Guid.NewGuid();
        UserTaskDto created = await fixture.Tasks.CreateOrUpdateProfileImportTaskAsync(
            new CreateProfileImportTaskDto(fixture.ModelIds[0], "Original", "Original", historical));
        fixture.App.ChangeTracker.Clear();
        await fixture.ScanAsync();
        UserTask before = await fixture.App.UserTasks.AsNoTracking().SingleAsync();
        Assert.Equal(created.Id, before.Id);
        Assert.Equal(created.Title, before.Title);
        Assert.Equal(created.MetadataJson, before.MetadataJson);
        Assert.Equal("4 printers waiting for slicer profiles", before.Description);
        Assert.True(JsonSerializer.Deserialize<Guid[]>(before.RelatedEntityIdsJson!)!.ToHashSet()
            .SetEquals(fixture.Printers.Select(p => p.Id).Append(historical)));
        fixture.Reset();

        await fixture.ScanAsync();

        Assert.Equal(0, fixture.AppCommands.TaskWrites);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.CountEvents);
        UserTask after = await fixture.App.UserTasks.AsNoTracking().SingleAsync();
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.LastMutationSequence, after.LastMutationSequence);
    }

    [Theory]
    [InlineData(UserTaskStatus.Completed)]
    [InlineData(UserTaskStatus.Skipped)]
    [InlineData(UserTaskStatus.Dismissed)]
    public async Task BatchUpsert_TerminalTransitionBetweenReadAndWrite_LeavesTerminalRowAndRecoversCompleteGroup(UserTaskStatus terminalStatus)
    {
        await using Fixture fixture = await Fixture.CreateAsync(3, 1);
        Guid historical = Guid.NewGuid();
        Guid modelId = fixture.ModelIds[0];
        UserTaskDto original = await fixture.Tasks.CreateOrUpdateProfileImportTaskAsync(
            new CreateProfileImportTaskDto(modelId, "Original", "Original", historical));
        fixture.App.ChangeTracker.Clear();
        UserTask before = await fixture.App.UserTasks.AsNoTracking().SingleAsync();
        EfUserTaskRepository real = new(fixture.App);
        Mock<IUserTaskRepository> repository = new();
        bool transitioned = false;
        repository.Setup(r => r.GetByEntityAsync(UserTaskType.ProfileImport, "PrinterModel", modelId, It.IsAny<CancellationToken>()))
            .Returns(async (UserTaskType type, string entityType, Guid entityId, CancellationToken ct) =>
            {
                UserTask? snapshot = await real.GetByEntityAsync(type, entityType, entityId, ct);
                if (!transitioned)
                {
                    transitioned = true;
                    await using AppDbContext userContext = new(new DbContextOptionsBuilder<AppDbContext>()
                        .UseSqlite(fixture.App.Database.GetDbConnection()).Options);
                    await userContext.UserTasks.Where(t => t.Id == original.Id)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.Status, terminalStatus), ct);
                }

                return snapshot;
            });
        repository.Setup(r => r.TryUpdateFieldsIfOpenAsync(
                It.IsAny<UserTask>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(async (UserTask task, IReadOnlyCollection<string> properties, DateTime? expected, CancellationToken ct) =>
                await real.TryUpdateFieldsIfOpenAsync(task, properties, expected, ct));
        repository.Setup(r => r.AddAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()))
            .Returns(async (UserTask task, CancellationToken ct) => await real.AddAsync(task, ct));
        UserTaskService service = new(repository.Object, NullLogger<UserTaskService>.Instance);
        Guid[] incoming = fixture.Printers.Select(p => p.Id).ToArray();

        UserTaskDto recovered = await service.CreateOrUpdateProfileImportTaskAsync(
            new CreateProfileImportTaskBatchDto(modelId, "Model", "Acme", incoming));

        Assert.NotEqual(original.Id, recovered.Id);
        UserTask terminal = await fixture.App.UserTasks.AsNoTracking().SingleAsync(t => t.Id == original.Id);
        Assert.Equal(terminalStatus, terminal.Status);
        Assert.Equal(before.RelatedEntityIdsJson, terminal.RelatedEntityIdsJson);
        Assert.Equal(before.Description, terminal.Description);
        Assert.Equal(before.LastMutationSequence, terminal.LastMutationSequence);
        UserTask open = await fixture.App.UserTasks.AsNoTracking().SingleAsync(t => t.Id == recovered.Id);
        Assert.True(JsonSerializer.Deserialize<Guid[]>(open.RelatedEntityIdsJson!)!.ToHashSet().SetEquals(incoming));
        Assert.Equal(UserTaskStatus.Pending, open.Status);
        Assert.Equal("3 printers waiting for slicer profiles", open.Description);
    }

    private static int Chunks(int count) => (count + 99) / 100;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _appConnection = new("Data Source=:memory:");
        private readonly SqliteConnection _slicerConnection = new("Data Source=:memory:");
        private ServiceProvider _provider = null!;
        public AppDbContext App { get; private set; } = null!;
        public SlicerDbContext Slicer { get; private set; } = null!;
        public CommandCounter AppCommands { get; } = new();
        public CommandCounter SlicerCommands { get; } = new();
        public EfMachineModelProfileRepository ModelProfiles { get; private set; } = null!;
        public EfMachineProfileRepository MachineProfiles { get; private set; } = null!;
        public CatalogService Catalog { get; private set; } = null!;
        public UserTaskService Tasks { get; private set; } = null!;
        public Printer[] Printers { get; private set; } = [];
        public Guid[] ModelIds { get; private set; } = [];
        public List<UserTaskDto> Events { get; } = [];
        public int CountEvents { get; private set; }
        public int CatalogCalls { get; private set; }

        public static async Task<Fixture> CreateAsync(int printerCount, int modelCount)
        {
            Fixture f = new();
            await f._appConnection.OpenAsync();
            await f._slicerConnection.OpenAsync();
            f.App = new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(f._appConnection).AddInterceptors(f.AppCommands).Options);
            f.Slicer = new(new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(f._slicerConnection).AddInterceptors(f.SlicerCommands).Options);
            await f.App.Database.EnsureCreatedAsync();
            await f.Slicer.Database.EnsureCreatedAsync();
            Guid manufacturer = Guid.NewGuid();
            f.App.Manufacturers.Add(new() { Id = manufacturer, Name = "Acme" });
            f.ModelIds = Enumerable.Range(0, modelCount).Select(_ => Guid.NewGuid()).ToArray();
            for (int i = 0; i < modelCount; i++)
            {
                f.App.PrinterModels.Add(new() { Id = f.ModelIds[i], ManufacturerId = manufacturer, Name = $"Model-{i}" });
            }

            f.App.PrinterModels.Add(new() { Id = Guid.NewGuid(), ManufacturerId = manufacturer, Name = "Unrelated" });
            await f.App.SaveChangesAsync();
            f.App.ChangeTracker.Clear();
            f.Printers = Enumerable.Range(0, printerCount)
                .Select(i => new Printer
                {
                    Id = Guid.NewGuid(),
                    ManufacturerId = manufacturer,
                    ModelId = f.ModelIds[i % modelCount],
                    Name = $"Printer-{i}",
                    ServerUrl = $"http://printer-{i}.example"
                }).ToArray();
            foreach (Printer[] batch in f.Printers.Chunk(100))
            {
                f.App.Printers.AddRange(batch);
                await f.App.SaveChangesAsync();
                f.App.ChangeTracker.Clear();
            }
            f.ModelProfiles = new(f.Slicer);
            f.MachineProfiles = new(f.Slicer);
            f.Catalog = new(new EfCatalogRepository(f.App), Mock.Of<INormalizationEventLogger>(),
                Mock.Of<ICatalogCacheProvider>(), NullLogger<CatalogService>.Instance);
            Mock<ITaskBroadcaster> broadcaster = new();
            broadcaster.Setup(b => b.BroadcastTaskCreatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()))
                .Callback<UserTaskDto, CancellationToken>((dto, _) => f.Events.Add(dto)).Returns(Task.CompletedTask);
            broadcaster.Setup(b => b.BroadcastTaskUpdatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()))
                .Callback<UserTaskDto, CancellationToken>((dto, _) => f.Events.Add(dto)).Returns(Task.CompletedTask);
            broadcaster.Setup(b => b.BroadcastPendingTaskCountAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback(() => f.CountEvents++).Returns(Task.CompletedTask);
            f.Tasks = new(new EfUserTaskRepository(f.App), NullLogger<UserTaskService>.Instance, broadcaster.Object);
            Mock<ISlicersService> workers = new();
            workers.Setup(w => w.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new() { Id = Guid.NewGuid() }]);
            Mock<ICatalogService> catalog = new();
            catalog.Setup(c => c.GetProfileTaskModelsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
                .Returns((IEnumerable<Guid> ids, CancellationToken ct) =>
                {
                    f.CatalogCalls++;
                    return f.Catalog.GetProfileTaskModelsAsync(ids, ct);
                });
            f._provider = new ServiceCollection()
                .AddSingleton(workers.Object)
                .AddSingleton<IPrinterProfileCheckRepository>(new EfPrinterProfileCheckRepository(f.App))
                .AddSingleton<IMachineModelProfileRepository>(f.ModelProfiles)
                .AddSingleton<IMachineProfileRepository>(f.MachineProfiles)
                .AddSingleton<IUserTaskService>(f.Tasks)
                .AddSingleton(catalog.Object).BuildServiceProvider();
            return f;
        }

        public Task ScanAsync() => new ProfileTaskCheckService(
            _provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ProfileTaskCheckService>.Instance,
            new ConfigurationBuilder().Build()).CheckPrintersForMissingProfilesAsync(CancellationToken.None);

        public async Task LegacyScanAsync()
        {
            // Mirrors the pre-#3577 scan at 0c898df: both presence reads, pending
            // check, graph model + manufacturer reads, then singular printer upserts.
            foreach (var group in (await new EfPrinterProfileCheckRepository(App).GetAllAsync(CancellationToken.None)).GroupBy(p => p.ModelId))
            {
                MachineModelProfile? profile = await ModelProfiles.GetByPrinterModelIdAsync(group.Key);
                bool machineProfile = await MachineProfiles.HasAnyForPrinterModelAsync(group.Key);
                if (profile is not null || machineProfile)
                {
                    continue;
                }

                await Tasks.HasPendingProfileImportTaskAsync(group.Key);
                CatalogCalls++;
                PrinterModelDto model = (await Catalog.GetModelByIdAsync(group.Key, CancellationToken.None))!;
                CatalogCalls++;
                ManufacturerDto manufacturer = (await Catalog.GetManufacturerByIdAsync(model.ManufacturerId, CancellationToken.None))!;
                foreach (Printer printer in group)
                {
                    await Tasks.CreateOrUpdateProfileImportTaskAsync(new CreateProfileImportTaskDto(group.Key, model.Name, manufacturer.Name, printer.Id));
                }
            }
        }

        public void Reset()
        {
            AppCommands.Reset();
            SlicerCommands.Reset();
            Events.Clear();
            CountEvents = 0;
            CatalogCalls = 0;
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await App.DisposeAsync();
            await Slicer.DisposeAsync();
            await _appConnection.DisposeAsync();
            await _slicerConnection.DisposeAsync();
        }
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public int Total => Sql.Count;
        public int TaskWrites { get; private set; }
        public int MaxParameters { get; private set; }
        public int? FailTaskWriteNumber { get; set; }

        public void Reset()
        {
            Sql.Clear();
            TaskWrites = 0;
            MaxParameters = 0;
        }

        private void Record(DbCommand command)
        {
            Sql.Add(command.CommandText);
            MaxParameters = Math.Max(MaxParameters, command.Parameters.Count);
            if (command.CommandText.StartsWith("INSERT INTO \"UserTasks\"", StringComparison.Ordinal) ||
                command.CommandText.StartsWith("UPDATE \"UserTasks\"", StringComparison.Ordinal))
            {
                TaskWrites++;
                if (TaskWrites == FailTaskWriteNumber)
                {
                    throw new InvalidOperationException("Injected task write failure");
                }
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
    }
}
