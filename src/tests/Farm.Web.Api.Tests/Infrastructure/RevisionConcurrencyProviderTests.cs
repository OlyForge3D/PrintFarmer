using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Farm.Web.Api.Tests.Infrastructure;

/// <summary>
/// Verifies the portable application-managed revision convention across database providers.
/// </summary>
[Collection(ProviderDatabaseTestCollection.Name)]
public sealed class RevisionConcurrencyProviderTests
{
    [Fact]
    public async Task SaveChangesAsync_WhenAddedEntitySuppliesRevision_InitializesAtOne()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using AppDbContext context = new(options);
        OutboxSequenceState state = new()
        {
            Id = 1,
            NextSequence = 1,
            Revision = 999,
        };
        context.OutboxSequenceStates.Add(state);

        _ = await context.SaveChangesAsync();

        Assert.Equal(1, state.Revision);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenTrackedOriginalRevisionIsInvalid_Throws()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using AppDbContext context = new(options);
        OutboxSequenceState state = new()
        {
            Id = 1,
            NextSequence = 1,
        };
        context.OutboxSequenceStates.Add(state);
        _ = await context.SaveChangesAsync();

        state.NextSequence++;
        context.Entry(state).Property(entity => entity.Revision).OriginalValue = 0;

        DbUpdateConcurrencyException exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => context.SaveChangesAsync());
        _ = Assert.Single(exception.Entries);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenTrackedOriginalRevisionCannotAdvance_ThrowsConcurrency()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using AppDbContext context = new(options);
        OutboxSequenceState state = new()
        {
            Id = 1,
            NextSequence = 1,
        };
        context.OutboxSequenceStates.Add(state);
        _ = await context.SaveChangesAsync();

        state.NextSequence++;
        context.Entry(state).Property(entity => entity.Revision).OriginalValue = long.MaxValue;

        _ = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => context.SaveChangesAsync());
    }

    private const string PostgresConnectionVariable = "PFARM_TEST_POSTGRES_CONN";
    private const string SqlServerConnectionVariable = "PFARM_TEST_SQLSERVER_CONN";

    private static readonly Type[] ExpectedAppRevisionedTypes =
    [
        typeof(AppSettingsEntity),
        typeof(DispatchSettings),
        typeof(GcodeFile),
        typeof(GcodeHarvestOperation),
        typeof(GcodeHarvestQueueItem),
        typeof(JobExecution),
        typeof(OutboxSequenceState),
        typeof(PrintJob),
        typeof(PrintProject),
        typeof(PrintProjectFile),
        typeof(PrintProjectTemplate),
        typeof(Printer),
        typeof(PrinterDispatchState),
        typeof(PrinterServiceState),
        typeof(QueueDispatchAttempt),
        typeof(QueueDispatchOutbox),
        typeof(Spool),
        typeof(UserSettings),
    ];

    [Fact]
    public void AppModel_WhenBuilt_ContainsOnlyPortableRevisionConcurrencyTokens()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new AppDbContext(options);

        AssertRevisionModel(context.Model, ExpectedAppRevisionedTypes);
    }

    [Fact]
    public void SlicerModel_WhenBuilt_ContainsOnlyPortableRevisionConcurrencyTokens()
    {
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new SlicerDbContext(options);

        AssertRevisionModel(context.Model, [typeof(Model3D)]);
    }

    [Fact]
    public void RevisionETag_WhenLegacyTokenIsDecoded_TreatsItAsStale()
    {
        byte[] legacyGuidToken = Guid.NewGuid().ToByteArray();
        byte[] legacySqlServerToken = [0, 0, 0, 0, 0, 0, 0, 1];

        Assert.Equal(0, RevisionETag.Decode(legacyGuidToken));
        Assert.Equal(0, RevisionETag.Decode(legacySqlServerToken));
        Assert.Equal(7, RevisionETag.Decode(RevisionETag.EncodeBytes(7)));
        Assert.Equal(sizeof(byte) + sizeof(long), RevisionETag.EncodeBytes(7).Length);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task SaveChangesAsync_WhenTwoSqliteContextsModifySameEntity_SecondWriteThrows()
    {
        string databaseName = $"revision_{Guid.NewGuid():N}";
        string connectionString =
            $"Data Source=file:{databaseName}?mode=memory&cache=shared;Foreign Keys=False";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(
                connectionString,
                provider => provider.MigrationsAssembly("Farm.Migrations.Sqlite"))
            .Options;

        await AssertSecondWriteThrowsAsync(options, deleteDatabase: false);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task AllocateAsync_WhenUsingDirectSql_AdvancesRevision()
    {
        string databaseName = $"revision_allocator_{Guid.NewGuid():N}";
        string connectionString =
            $"Data Source=file:{databaseName}?mode=memory&cache=shared;Foreign Keys=False";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(
                connectionString,
                provider => provider.MigrationsAssembly("Farm.Migrations.Sqlite"))
            .Options;
        await using var context = new AppDbContext(options);
        await context.Database.MigrateAsync();
        long originalRevision = await context.OutboxSequenceStates
            .Select(state => state.Revision)
            .SingleAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        _ = await new DbOutboxSequenceAllocator().AllocateAsync(context);
        await transaction.CommitAsync();
        context.ChangeTracker.Clear();

        long updatedRevision = await context.OutboxSequenceStates
            .Select(state => state.Revision)
            .SingleAsync();
        Assert.Equal(originalRevision + 1, updatedRevision);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SaveChangesAsync_WhenTwoPostgreSqlContextsModifySameEntity_SecondWriteThrows()
    {
        string? connectionString = Environment.GetEnvironmentVariable(PostgresConnectionVariable);
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            $"PostgreSQL provider verification did not run: set {PostgresConnectionVariable}.");
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                connectionString,
                provider => provider.MigrationsAssembly("Farm.Migrations.PostgreSQL"))
            .Options;

        await AssertSecondWriteThrowsAsync(options, deleteDatabase: true);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SaveChangesAsync_WhenTwoSqlServerContextsModifySameEntity_SecondWriteThrows()
    {
        string? connectionString = Environment.GetEnvironmentVariable(SqlServerConnectionVariable);
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            $"SQL Server provider verification did not run: set {SqlServerConnectionVariable}.");
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(
                connectionString,
                provider => provider.MigrationsAssembly("Farm.Migrations.SqlServer"))
            .Options;

        await AssertSecondWriteThrowsAsync(options, deleteDatabase: true);
    }

    private static void AssertRevisionModel(IModel model, IReadOnlyCollection<Type> expectedTypes)
    {
        Type[] actualTypes = model.GetEntityTypes()
            .Where(entityType => typeof(IRevisionedEntity).IsAssignableFrom(entityType.ClrType))
            .Select(entityType => entityType.ClrType)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        Type[] orderedExpected = expectedTypes
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(orderedExpected, actualTypes);
        Assert.DoesNotContain(
            model.GetEntityTypes().SelectMany(entityType => entityType.GetProperties()),
            property => property.Name == "RowVersion");
        foreach (IEntityType entityType in model.GetEntityTypes()
                     .Where(entityType => expectedTypes.Contains(entityType.ClrType)))
        {
            IProperty revision = Assert.IsAssignableFrom<IProperty>(
                entityType.FindProperty(nameof(IRevisionedEntity.Revision)));
            Assert.True(revision.IsConcurrencyToken);
            Assert.Equal(ValueGenerated.Never, revision.ValueGenerated);
        }
    }

    private static async Task AssertSecondWriteThrowsAsync(
        DbContextOptions<AppDbContext> options,
        bool deleteDatabase)
    {
        await using (var setup = new AppDbContext(options))
        {
            if (deleteDatabase)
            {
                _ = await setup.Database.EnsureDeletedAsync();
            }

            await setup.Database.MigrateAsync();
        }

        await using var first = new AppDbContext(options);
        await using var second = new AppDbContext(options);
        OutboxSequenceState firstCopy = await first.OutboxSequenceStates.SingleAsync();
        OutboxSequenceState secondCopy = await second.OutboxSequenceStates.SingleAsync();
        long originalRevision = firstCopy.Revision;
        Assert.Equal(originalRevision, secondCopy.Revision);

        firstCopy.NextSequence++;
        secondCopy.NextSequence += 2;
        _ = await first.SaveChangesAsync();

        Assert.Equal(originalRevision + 1, firstCopy.Revision);
        _ = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => second.SaveChangesAsync());
    }
}
