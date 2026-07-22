using System.Collections;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Repositories.Tasks;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Infrastructure.Services.ShiftPlan;
using Farm.Infrastructure.Services.ShiftPlan.Sources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TestDbHelpers = Farm.Web.Api.Tests.TestInfrastructure.TestHelpers;

namespace Farm.Web.Api.Tests.Services.ShiftPlan;

public sealed class PrintedPartReorderShiftPlanTaskSourceTests
{
    [Fact]
    public async Task ProduceAsync_BoundaryCandidatesAndDuplicateLabels_MapsStableTypedSpecs()
    {
        Guid firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Guid zeroId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        Guid negativeId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        Guid binId = Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA");
        var reorder = new ControlledReorderEvaluationService
        {
            Candidates =
            [
                Candidate(secondId, "DUPLICATE", "Shared name", 5, 5, 0),
                Candidate(firstId, "DUPLICATE", "Shared name", 4, 5, 1, binId, "BIN-A", "Rack A"),
                Candidate(zeroId, "ZERO", "Zero threshold", 0, 0, 0),
                Candidate(negativeId, "NEG", "Negative stock", -3, 0, 3),
            ],
        };
        var source = new PrintedPartReorderShiftPlanTaskSource(reorder, ControlledFeatureGate.Enabled());

        IReadOnlyList<ShiftPlanTaskSpec> specs = await source.ProduceAsync(CancellationToken.None);

        Assert.Equal(4, specs.Count);
        Assert.Equal(4, specs.Select(spec => spec.SourceId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(specs, spec =>
        {
            Assert.Equal(UserTaskType.PrintedPartRestock, spec.TaskType);
            Assert.Equal(UserTaskSourceKind.PrintedPartStock, spec.SourceKind);
            Assert.Equal(UserTaskAnchorKind.AnytimeToday, spec.AnchorKind);
            Assert.Equal(UserTaskPriority.Normal, spec.Priority);
            Assert.Equal(nameof(PartInventory), spec.EntityType);
            Assert.True(spec.SourceId.Length <= 128);
        });

        ShiftPlanTaskSpec first = Assert.Single(specs, spec => spec.EntityId == firstId);
        Assert.Equal($"partinventory:{firstId:N}", first.SourceId);
        PrintedPartRestockTaskMetadata metadata = DeserializeMetadata(first);
        Assert.Equal(firstId, metadata.PartInventoryId);
        Assert.Equal("DUPLICATE", metadata.Sku);
        Assert.Equal("Shared name", metadata.Name);
        Assert.Equal(4, metadata.OnHand);
        Assert.Equal(5, metadata.ReorderPoint);
        Assert.Equal(1, metadata.Deficit);
        Assert.Equal(binId, metadata.DefaultBinId);
        Assert.Equal("BIN-A", metadata.DefaultBinCode);
        Assert.Equal("Rack A", metadata.DefaultBinName);

        Assert.Equal(0, DeserializeMetadata(Assert.Single(specs, spec => spec.EntityId == zeroId)).Deficit);
        Assert.Equal(3, DeserializeMetadata(Assert.Single(specs, spec => spec.EntityId == negativeId)).Deficit);
    }

    [Fact]
    public async Task ProduceAsync_MetadataChanges_PreserveStableInventoryIdentity()
    {
        Guid inventoryId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var reorder = new ControlledReorderEvaluationService
        {
            Candidates = [Candidate(inventoryId, "SKU-OLD", "Old name", 1, 4, 3)],
        };
        var source = new PrintedPartReorderShiftPlanTaskSource(reorder, ControlledFeatureGate.Enabled());

        ShiftPlanTaskSpec original = Assert.Single(await source.ProduceAsync(CancellationToken.None));
        reorder.Candidates =
        [
            Candidate(
                inventoryId,
                "SKU-NEW",
                "New name",
                -2,
                7,
                9,
                Guid.Parse("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"),
                "BIN-B",
                "Rack B"),
        ];

        ShiftPlanTaskSpec refreshed = Assert.Single(await source.ProduceAsync(CancellationToken.None));

        Assert.Equal(original.SourceId, refreshed.SourceId);
        Assert.Equal(original.EntityId, refreshed.EntityId);
        Assert.NotEqual(original.Title, refreshed.Title);
        Assert.NotEqual(original.Description, refreshed.Description);
        Assert.NotEqual(original.MetadataJson, refreshed.MetadataJson);
        Assert.Equal("SKU-NEW", DeserializeMetadata(refreshed).Sku);
        Assert.Equal("BIN-B", DeserializeMetadata(refreshed).DefaultBinCode);
    }

    [Fact]
    public async Task ProduceAsync_TwoHundredCharName_TitleFitsAndMetadataNameIsUntruncated()
    {
        // "Restock " (8 chars) + 200-char name = 208, which overflows UserTask.Title [MaxLength(200)].
        // The fix must cap the title at 200 while preserving the full name in metadata.
        string fullName = new string('X', 200);
        Guid inventoryId = Guid.Parse("EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE");
        var reorder = new ControlledReorderEvaluationService
        {
            Candidates = [Candidate(inventoryId, "SKU-200", fullName, 0, 5, 5)],
        };
        var source = new PrintedPartReorderShiftPlanTaskSource(reorder, ControlledFeatureGate.Enabled());

        ShiftPlanTaskSpec spec = Assert.Single(await source.ProduceAsync(CancellationToken.None));

        Assert.True(spec.Title.Length <= 200, $"Title length {spec.Title.Length} exceeds the 200-char persistence maximum.");
        Assert.Equal(200, spec.Title.Length);
        PrintedPartRestockTaskMetadata metadata = DeserializeMetadata(spec);
        Assert.Equal(fullName, metadata.Name);
        Assert.Equal(200, metadata.Name.Length);
    }

    [Fact]
    public async Task ProduceAsync_SurrogatePairCrossesTitleBoundary_TitleRemainsValidAndMetadataNameIsUntruncated()
    {
        string fullName = new string('X', 191) + "😀";
        Guid inventoryId = Guid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");
        var reorder = new ControlledReorderEvaluationService
        {
            Candidates = [Candidate(inventoryId, "SKU-SUR", fullName, 0, 5, 5)],
        };
        var source = new PrintedPartReorderShiftPlanTaskSource(reorder, ControlledFeatureGate.Enabled());

        ShiftPlanTaskSpec spec = Assert.Single(await source.ProduceAsync(CancellationToken.None));

        Assert.Equal($"Restock {new string('X', 191)}", spec.Title);
        Assert.True(spec.Title.Length <= 200, $"Title length {spec.Title.Length} exceeds the 200-char persistence maximum.");
        Exception? encodingException = Record.Exception(
            () => _ = new UTF8Encoding(false, true).GetByteCount(spec.Title));
        Assert.Null(encodingException);

        PrintedPartRestockTaskMetadata metadata = DeserializeMetadata(spec);
        Assert.Equal(fullName, metadata.Name);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ProduceAsync_RequiredGateDisabled_DoesNotQuery(
        bool shiftPlanEnabled,
        bool inventoryEnabled)
    {
        var reorder = new ControlledReorderEvaluationService();
        var gate = new ControlledFeatureGate((feature, _) => feature switch
        {
            OperatorFeature.ShiftPlan => shiftPlanEnabled,
            OperatorFeature.PrintedPartsInventory => inventoryEnabled,
            _ => true,
        });
        var source = new PrintedPartReorderShiftPlanTaskSource(reorder, gate);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ProduceAsync(CancellationToken.None));

        Assert.Equal(0, reorder.QueryCount);
    }

    [Theory]
    [InlineData(OperatorFeature.ShiftPlan)]
    [InlineData(OperatorFeature.PrintedPartsInventory)]
    public async Task ProduceAsync_GateDisablesAfterQuery_ReportsIncomplete(
        OperatorFeature disabledFeature)
    {
        var reorder = new ControlledReorderEvaluationService
        {
            Candidates = [Candidate(Guid.NewGuid(), "SKU", "Name", 0, 1, 1)],
        };
        var gate = new ControlledFeatureGate(
            (feature, readNumber) => feature != disabledFeature || readNumber == 1);
        var source = new PrintedPartReorderShiftPlanTaskSource(reorder, gate);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ProduceAsync(CancellationToken.None));

        Assert.Equal(1, reorder.QueryCount);
    }

    [Fact]
    public async Task ProduceAsync_QueryFailure_Propagates()
    {
        var reorder = new ControlledReorderEvaluationService
        {
            Handler = _ => throw new InvalidOperationException("query failed"),
        };
        var source = new PrintedPartReorderShiftPlanTaskSource(reorder, ControlledFeatureGate.Enabled());

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ProduceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProduceAsync_CancelledQuery_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reorder = new ControlledReorderEvaluationService
        {
            Handler = ct => throw new OperationCanceledException(ct),
        };
        var source = new PrintedPartReorderShiftPlanTaskSource(reorder, ControlledFeatureGate.Enabled());

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.ProduceAsync(cancellation.Token));
    }

    [Fact]
    public async Task ProduceAsync_PartialEnumeration_PropagatesWithoutReturningSpecs()
    {
        var reorder = new ControlledReorderEvaluationService
        {
            Candidates = new PartiallyThrowingCandidates(
                Candidate(Guid.NewGuid(), "FIRST", "First", 0, 1, 1),
                Candidate(Guid.NewGuid(), "SECOND", "Second", 0, 1, 1)),
        };
        var source = new PrintedPartReorderShiftPlanTaskSource(reorder, ControlledFeatureGate.Enabled());

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ProduceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProduceAsync_DuplicateStableInventoryId_ReportsIncomplete()
    {
        Guid inventoryId = Guid.NewGuid();
        var reorder = new ControlledReorderEvaluationService
        {
            Candidates =
            [
                Candidate(inventoryId, "FIRST", "First", 0, 1, 1),
                Candidate(inventoryId, "SECOND", "Second", 0, 2, 2),
            ],
        };
        var source = new PrintedPartReorderShiftPlanTaskSource(reorder, ControlledFeatureGate.Enabled());

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ProduceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CompileAsync_RepeatedMetadataUpdateAndAuthoritativeClear_MutatesOneTaskAndResolvesOnce()
    {
        using SqliteConnection connection = TestDbHelpers.CreateOpenSqliteConnection();
        await InitializeDatabaseAsync(connection);

        Guid inventoryId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var reorder = new ControlledReorderEvaluationService
        {
            Candidates = [Candidate(inventoryId, "SKU-1", "Part one", 2, 5, 3)],
        };
        ControlledFeatureGate gate = ControlledFeatureGate.Enabled();
        ShiftPlanSuppressionState suppression = new();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));

        ShiftPlanCompileResult created = await CompileOnceAsync(connection, reorder, gate, suppression, clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        reorder.Candidates =
        [
            Candidate(
                inventoryId,
                "SKU-2",
                "Renamed part",
                -1,
                8,
                9,
                Guid.Parse("CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC"),
                "BIN-C",
                "Rack C"),
        ];
        ShiftPlanCompileResult updated = await CompileOnceAsync(connection, reorder, gate, suppression, clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        reorder.Candidates = [];
        ShiftPlanCompileResult cleared = await CompileOnceAsync(connection, reorder, gate, suppression, clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        ShiftPlanCompileResult repeatedClear = await CompileOnceAsync(connection, reorder, gate, suppression, clock);

        Assert.Equal((1, 0, 0), (created.Created, created.Updated, created.AutoCompleted));
        Assert.Equal((0, 1, 0), (updated.Created, updated.Updated, updated.AutoCompleted));
        Assert.Equal((0, 0, 1), (cleared.Created, cleared.Updated, cleared.AutoCompleted));
        Assert.Equal((0, 0, 0), (repeatedClear.Created, repeatedClear.Updated, repeatedClear.AutoCompleted));

        await using AppDbContext verify = TestDbHelpers.CreateContext(connection);
        UserTask task = Assert.Single(await verify.UserTasks.AsNoTracking().ToListAsync());
        Assert.Equal(UserTaskStatus.Completed, task.Status);
        Assert.Equal($"partinventory:{inventoryId:N}", task.SourceId);
        PrintedPartRestockTaskMetadata metadata = JsonSerializer.Deserialize<PrintedPartRestockTaskMetadata>(
            task.MetadataJson!,
            JsonSerializerOptions.Web)!;
        Assert.Equal("SKU-2", metadata.Sku);
        Assert.Equal(-1, metadata.OnHand);
        Assert.Equal(8, metadata.ReorderPoint);
        Assert.Equal("BIN-C", metadata.DefaultBinCode);
    }

    [Fact]
    public async Task CompileAsync_TwoCompilerInstancesSameCandidate_CreateExactlyOneOpenTask()
    {
        using SqliteConnection connection = TestDbHelpers.CreateOpenSqliteConnection();
        await InitializeDatabaseAsync(connection);

        Guid inventoryId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var reorder = new ControlledReorderEvaluationService
        {
            Candidates = [Candidate(inventoryId, "RACE", "Race part", 0, 1, 1)],
        };
        ControlledFeatureGate gate = ControlledFeatureGate.Enabled();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 13, 0, 0, TimeSpan.Zero));

        await using AppDbContext firstContext = TestDbHelpers.CreateContext(connection);
        await using AppDbContext secondContext = TestDbHelpers.CreateContext(connection);
        ShiftPlanCompiler firstCompiler = BuildCompiler(firstContext, reorder, gate, clock);
        ShiftPlanCompiler secondCompiler = BuildCompiler(secondContext, reorder, gate, clock);

        ShiftPlanCompileResult[] results = await Task.WhenAll(
            firstCompiler.CompileAsync(new ShiftPlanSuppressionState()),
            secondCompiler.CompileAsync(new ShiftPlanSuppressionState()));

        Assert.Equal(1, results.Sum(result => result.Created));
        await using AppDbContext verify = TestDbHelpers.CreateContext(connection);
        Assert.Equal(1, await verify.UserTasks.CountAsync(task =>
            task.SourceKind == UserTaskSourceKind.PrintedPartStock
            && task.SourceId == $"partinventory:{inventoryId:N}"
            && (task.Status == UserTaskStatus.Pending || task.Status == UserTaskStatus.InProgress)));
    }

    [Theory]
    [InlineData(UserTaskStatus.Skipped)]
    [InlineData(UserTaskStatus.Dismissed)]
    public async Task CompileAsync_SuppressedContinuousOccurrence_ClearThenRecross_CreatesFreshTask(
        UserTaskStatus suppressedStatus)
    {
        using SqliteConnection connection = TestDbHelpers.CreateOpenSqliteConnection();
        await InitializeDatabaseAsync(connection);

        Guid inventoryId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var reorder = new ControlledReorderEvaluationService
        {
            Candidates = [Candidate(inventoryId, "STICKY", "Sticky part", 0, 2, 2)],
        };
        ControlledFeatureGate gate = ControlledFeatureGate.Enabled();
        ShiftPlanSuppressionState suppression = new();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 14, 0, 0, TimeSpan.Zero));

        ShiftPlanCompileResult initial = await CompileOnceAsync(connection, reorder, gate, suppression, clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        await using (AppDbContext userAction = TestDbHelpers.CreateContext(connection))
        {
            UserTask task = await userAction.UserTasks.SingleAsync();
            task.Status = suppressedStatus;
            task.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            _ = await userAction.SaveChangesAsync();
        }

        ShiftPlanCompileResult continuous = await CompileOnceAsync(connection, reorder, gate, suppression, clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        reorder.Candidates = [];
        ShiftPlanCompileResult clear = await CompileOnceAsync(connection, reorder, gate, suppression, clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        reorder.Candidates = [Candidate(inventoryId, "STICKY", "Sticky part", 0, 2, 2)];
        ShiftPlanCompileResult recross = await CompileOnceAsync(connection, reorder, gate, suppression, clock);

        Assert.Equal(1, initial.Created);
        Assert.Equal((0, 0, 0), (continuous.Created, continuous.Updated, continuous.AutoCompleted));
        Assert.Equal((0, 0, 0), (clear.Created, clear.Updated, clear.AutoCompleted));
        Assert.Equal(1, recross.Created);

        await using AppDbContext verify = TestDbHelpers.CreateContext(connection);
        List<UserTask> rows = await verify.UserTasks.AsNoTracking()
            .Where(task => task.SourceKind == UserTaskSourceKind.PrintedPartStock)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, task => task.Status == suppressedStatus);
        Assert.Single(rows, task => task.Status == UserTaskStatus.Pending);
    }

    [Fact]
    public async Task CompileAsync_FailedPartialOrGateRace_PreservesOpenTaskWithoutWrites()
    {
        using SqliteConnection connection = TestDbHelpers.CreateOpenSqliteConnection();
        await InitializeDatabaseAsync(connection);

        Guid inventoryId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var reorder = new ControlledReorderEvaluationService
        {
            Candidates = [Candidate(inventoryId, "PRESERVE", "Preserve part", 0, 2, 2)],
        };
        ShiftPlanSuppressionState suppression = new();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero));
        _ = await CompileOnceAsync(
            connection,
            reorder,
            ControlledFeatureGate.Enabled(),
            suppression,
            clock);

        reorder.Candidates = new PartiallyThrowingCandidates(
            Candidate(Guid.NewGuid(), "PARTIAL-1", "Partial one", 0, 1, 1),
            Candidate(Guid.NewGuid(), "PARTIAL-2", "Partial two", 0, 1, 1));
        ShiftPlanCompileResult partial = await CompileOnceAsync(
            connection,
            reorder,
            ControlledFeatureGate.Enabled(),
            suppression,
            clock);

        reorder.Candidates = [Candidate(inventoryId, "CHANGED", "Changed", 1, 3, 2)];
        var racingGate = new ControlledFeatureGate(
            (feature, readNumber) =>
                feature != OperatorFeature.PrintedPartsInventory || readNumber == 1);
        ShiftPlanCompileResult gateRace = await CompileOnceAsync(
            connection,
            reorder,
            racingGate,
            suppression,
            clock);

        reorder.Handler = _ => throw new InvalidOperationException("query failed");
        ShiftPlanCompileResult failed = await CompileOnceAsync(
            connection,
            reorder,
            ControlledFeatureGate.Enabled(),
            suppression,
            clock);

        Assert.Equal((0, 0, 0, 1), (partial.Created, partial.Updated, partial.AutoCompleted, partial.SourceFailures));
        Assert.Equal((0, 0, 0, 1), (gateRace.Created, gateRace.Updated, gateRace.AutoCompleted, gateRace.SourceFailures));
        Assert.Equal((0, 0, 0, 1), (failed.Created, failed.Updated, failed.AutoCompleted, failed.SourceFailures));

        await using AppDbContext verify = TestDbHelpers.CreateContext(connection);
        UserTask task = Assert.Single(await verify.UserTasks.AsNoTracking().ToListAsync());
        Assert.Equal(UserTaskStatus.Pending, task.Status);
        Assert.Equal("Restock Preserve part", task.Title);
    }

    [Fact]
    public async Task CompileAsync_CancelledQuery_PreservesOpenTaskAndPropagatesCancellation()
    {
        using SqliteConnection connection = TestDbHelpers.CreateOpenSqliteConnection();
        await InitializeDatabaseAsync(connection);

        Guid inventoryId = Guid.Parse("AAAAAAAA-1111-1111-1111-111111111111");
        var reorder = new ControlledReorderEvaluationService
        {
            Candidates = [Candidate(inventoryId, "CANCEL", "Cancel part", 0, 1, 1)],
        };
        ShiftPlanSuppressionState suppression = new();
        ControlledFeatureGate gate = ControlledFeatureGate.Enabled();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 16, 0, 0, TimeSpan.Zero));
        _ = await CompileOnceAsync(connection, reorder, gate, suppression, clock);

        reorder.Handler = ct => throw new OperationCanceledException(ct);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CompileOnceAsync(connection, reorder, gate, suppression, clock));

        await using AppDbContext verify = TestDbHelpers.CreateContext(connection);
        UserTask task = Assert.Single(await verify.UserTasks.AsNoTracking().ToListAsync());
        Assert.Equal(UserTaskStatus.Pending, task.Status);
    }

    private static ShiftPlanCompiler BuildCompiler(
        AppDbContext context,
        IReorderEvaluationService reorder,
        IOperatorFeatureGate gate,
        TimeProvider clock)
    {
        var source = new PrintedPartReorderShiftPlanTaskSource(reorder, gate);
        return new ShiftPlanCompiler(
            [source],
            new EfUserTaskRepository(context),
            NullLogger<ShiftPlanCompiler>.Instance,
            clock);
    }

    private static async Task<ShiftPlanCompileResult> CompileOnceAsync(
        SqliteConnection connection,
        IReorderEvaluationService reorder,
        IOperatorFeatureGate gate,
        ShiftPlanSuppressionState suppression,
        TimeProvider clock)
    {
        await using AppDbContext context = TestDbHelpers.CreateContext(connection);
        ShiftPlanCompiler compiler = BuildCompiler(context, reorder, gate, clock);
        return await compiler.CompileAsync(suppression, CancellationToken.None);
    }

    private static async Task InitializeDatabaseAsync(SqliteConnection connection)
    {
        await using AppDbContext context = TestDbHelpers.CreateContext(connection);
        _ = await context.Database.EnsureCreatedAsync();
    }

    private static ReorderCandidateResponse Candidate(
        Guid id,
        string sku,
        string name,
        int onHand,
        int reorderPoint,
        int deficit,
        Guid? defaultBinId = null,
        string? defaultBinCode = null,
        string? defaultBinName = null) =>
        new(
            id,
            sku,
            name,
            onHand,
            reorderPoint,
            deficit,
            defaultBinId,
            defaultBinCode,
            defaultBinName);

    private static PrintedPartRestockTaskMetadata DeserializeMetadata(ShiftPlanTaskSpec spec) =>
        JsonSerializer.Deserialize<PrintedPartRestockTaskMetadata>(
            spec.MetadataJson!,
            JsonSerializerOptions.Web)!;

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class ControlledReorderEvaluationService : IReorderEvaluationService
    {
        public IReadOnlyList<ReorderCandidateResponse> Candidates { get; set; } = [];

        public Func<CancellationToken, Task<IReadOnlyList<ReorderCandidateResponse>>>? Handler { get; set; }

        public int QueryCount { get; private set; }

        public Task<IReadOnlyList<ReorderCandidateResponse>> GetReorderCandidatesAsync(
            CancellationToken ct = default)
        {
            QueryCount++;
            return Handler?.Invoke(ct)
                ?? Task.FromResult(Candidates);
        }
    }

    private sealed class ControlledFeatureGate(
        Func<OperatorFeature, int, bool> resolver) : IOperatorFeatureGate
    {
        private readonly Dictionary<OperatorFeature, int> _reads = [];

        public IReadOnlyList<(OperatorFeature Feature, string FlagName)> AllFeatures =>
            throw new NotSupportedException();

        public static ControlledFeatureGate Enabled() => new((_, _) => true);

        public OperatorFeatureFlagsDto GetEffectiveFlags() => throw new NotSupportedException();

        public bool IsEnabled(OperatorFeature feature) => throw new NotSupportedException();

        public Task<bool> IsEnabledAsync(
            OperatorFeature feature,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> IsEnabledStrictAsync(
            OperatorFeature feature,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reads.TryGetValue(feature, out int current);
            int readNumber = current + 1;
            _reads[feature] = readNumber;
            return Task.FromResult(resolver(feature, readNumber));
        }

        public bool IsHardDisabledByEnvironment(OperatorFeature feature) =>
            throw new NotSupportedException();

        public string GetFlagName(OperatorFeature feature) => throw new NotSupportedException();
    }

    private sealed class PartiallyThrowingCandidates(
        ReorderCandidateResponse first,
        ReorderCandidateResponse second) : IReadOnlyList<ReorderCandidateResponse>
    {
        public int Count => 2;

        public ReorderCandidateResponse this[int index] => index switch
        {
            0 => first,
            1 => second,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        public IEnumerator<ReorderCandidateResponse> GetEnumerator()
        {
            yield return first;
            throw new InvalidOperationException("partial enumeration failed");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
