using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Tags;
using Farm.Testing.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Tags;

/// <summary>
/// Regression coverage for issue #2362: <c>TagService.GetPopularTagsAsync</c> (and
/// <c>SearchTagsAsync</c> / <c>GetAnalyticsAsync</c>, which share the same
/// <see cref="ITagRepository"/> primitive) used to call
/// <c>ITagRepository.GetTagUsageCountAsync</c> / <c>GetTagLastUsedAtAsync</c> once PER TAG —
/// roughly 600 sequential SQL round trips for <c>GET /api/tags/popular</c> once the tag
/// catalog grew large. The fix batches usage counts (and, for analytics, last-used
/// timestamps) into a small, FIXED number of GROUP BY queries via
/// <see cref="ITagRepository.GetTagUsageCountsAsync"/> and
/// <see cref="ITagRepository.GetTagLastUsedAtBatchAsync"/>, independent of how many tags
/// exist.
///
/// Runs against a real SQLite in-memory database (not the EF Core InMemory provider, which
/// never issues real <see cref="System.Data.Common.DbCommand"/>s) with a
/// <see cref="DbCommandInterceptor"/> counting the actual SQL commands executed, so the
/// query-count assertions are backed by genuine translated SQL rather than an in-memory LINQ
/// shortcut that could hide N+1 behavior.
/// </summary>
public class TagServiceUsageAggregationTests
{
    /// <summary>Counts every <see cref="System.Data.Common.DbCommand"/> executed against the connection it is attached to.</summary>
    private sealed class CommandCountInterceptor : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            CommandCount++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            CommandCount++;
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            CommandCount++;
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static TagService CreateService(AppDbContext db, IModel3DQueryProvider? model3DQuery = null)
    {
        var repo = new EfTagRepository(db, model3DQuery);
        return new TagService(repo, NullLogger<TagService>.Instance);
    }

    private static Mock<IModel3DQueryProvider> CreateModel3DQueryMock(Guid model3DId)
    {
        var mock = new Mock<IModel3DQueryProvider>();
        mock.Setup(q => q.GetUpdatedAtByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<Guid, DateTime>)new Dictionary<Guid, DateTime> { [model3DId] = DateTime.UtcNow });
        return mock;
    }

    /// <summary>
    /// Seeds <paramref name="tagCount"/> tags and tags a FIXED small subset (not scaling with
    /// <paramref name="tagCount"/>) across GcodeFile, Printer, and Model3D, so there is always
    /// some real usage data to aggregate no matter how many total tags exist.
    /// </summary>
    private static async Task SeedTagsWithFixedUsageAsync(AppDbContext db, int tagCount, Guid model3DId)
    {
        List<Tag> tags = new(tagCount);
        for (int i = 0; i < tagCount; i++)
        {
            DateTime now = DateTime.UtcNow;
            tags.Add(new Tag { Id = Guid.NewGuid(), Name = $"Tag{i:D4}", Color = "#FF0000", CreatedAt = now, UpdatedAt = now });
        }

        db.Set<Tag>().AddRange(tags);

        Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = $"Mfr-{Guid.NewGuid():N}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), Name = $"Model-{Guid.NewGuid():N}", ManufacturerId = manufacturer.Id };
        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);

        FolderNode folder = new() { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };
        db.Set<FolderNode>().Add(folder);

        await db.SaveChangesAsync();

        // Exactly 3 of the tags get real usage, regardless of tagCount, so the seeded
        // catalog exercises real GROUP BY joins without the amount of usage data scaling
        // with the tag count (only the tag count is the independent variable here).
        GcodeFile gcodeFile = new() { Id = Guid.NewGuid(), FileName = "benchy.gcode", FolderId = folder.Id, UpdatedAt = DateTime.UtcNow };
        gcodeFile.Tags.Add(tags[0]);
        db.GcodeFiles.Add(gcodeFile);

        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Printer",
            ServerUrl = "http://printer",
            BackendPort = 80,
            Backend = (int)PrinterBackend.PrusaLink,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };
        printer.Tags.Add(tags[1]);
        db.Printers.Add(printer);

        db.Set<Model3DTagMapping>().Add(new Model3DTagMapping { Model3DId = model3DId, TagsId = tags[2].Id });

        await db.SaveChangesAsync();
    }

    private static async Task<int> MeasureCommandCountAsync(int tagCount, Func<TagService, Task> action)
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        Guid model3DId = Guid.NewGuid();
        await using (AppDbContext seed = new(options))
        {
            await seed.Database.EnsureCreatedAsync();
            await SeedTagsWithFixedUsageAsync(seed, tagCount, model3DId);
        }

        CommandCountInterceptor interceptor = new();
        DbContextOptions<AppDbContext> countedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using AppDbContext queryDb = new(countedOptions);
        Mock<IModel3DQueryProvider> model3DQuery = CreateModel3DQueryMock(model3DId);
        TagService service = CreateService(queryDb, model3DQuery.Object);

        await action(service);

        return interceptor.CommandCount;
    }

    [Fact]
    public async Task GetPopularTagsAsync_CommandCountIsIndependentOfTagCount()
    {
        int commandsWith10Tags = await MeasureCommandCountAsync(10, service => service.GetPopularTagsAsync(50, CancellationToken.None));
        int commandsWith300Tags = await MeasureCommandCountAsync(300, service => service.GetPopularTagsAsync(50, CancellationToken.None));

        Assert.True(commandsWith10Tags > 0, "expected GetPopularTagsAsync to issue at least one SQL command");
        Assert.Equal(commandsWith10Tags, commandsWith300Tags);
    }

    [Fact]
    public async Task GetAnalyticsAsync_CommandCountIsIndependentOfTagCount()
    {
        int commandsWith10Tags = await MeasureCommandCountAsync(10, service => service.GetAnalyticsAsync(CancellationToken.None));
        int commandsWith300Tags = await MeasureCommandCountAsync(300, service => service.GetAnalyticsAsync(CancellationToken.None));

        Assert.True(commandsWith10Tags > 0, "expected GetAnalyticsAsync to issue at least one SQL command");
        Assert.Equal(commandsWith10Tags, commandsWith300Tags);
    }

    [Fact]
    public async Task SearchTagsAsync_CommandCountIsIndependentOfTagCount()
    {
        int commandsWith10Tags = await MeasureCommandCountAsync(10, service => service.SearchTagsAsync("Tag", CancellationToken.None));
        int commandsWith300Tags = await MeasureCommandCountAsync(300, service => service.SearchTagsAsync("Tag", CancellationToken.None));

        Assert.True(commandsWith10Tags > 0, "expected SearchTagsAsync to issue at least one SQL command");
        Assert.Equal(commandsWith10Tags, commandsWith300Tags);
    }

    /// <summary>
    /// Correctness fixture (issue #2362 acceptance criterion #5): tags with zero usage, usage
    /// in exactly one source, and usage in all three sources must all be reported correctly by
    /// the batched <see cref="ITagRepository.GetTagUsageCountsAsync"/> primitive — zero-usage
    /// tags must show count 0 (not be dropped by an INNER JOIN), and a tag used in all three
    /// sources must sum them without double-counting.
    /// </summary>
    [Fact]
    public async Task GetTagUsageCountsAsync_CoversZeroUsageSingleSourceAndAllSources_WithoutDroppingOrDoubleCounting()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        Guid model3DId = Guid.NewGuid();

        DateTime now = DateTime.UtcNow;
        Tag unused = new() { Id = Guid.NewGuid(), Name = "Unused", CreatedAt = now, UpdatedAt = now };
        Tag gcodeOnly = new() { Id = Guid.NewGuid(), Name = "GcodeOnly", CreatedAt = now, UpdatedAt = now };
        Tag allThree = new() { Id = Guid.NewGuid(), Name = "AllThree", CreatedAt = now, UpdatedAt = now };
        db.Set<Tag>().AddRange(unused, gcodeOnly, allThree);

        Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = $"Mfr-{Guid.NewGuid():N}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), Name = $"Model-{Guid.NewGuid():N}", ManufacturerId = manufacturer.Id };
        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        FolderNode folder = new() { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };
        db.Set<FolderNode>().Add(folder);
        await db.SaveChangesAsync();

        // gcodeOnly is used in exactly one source (GcodeFile).
        GcodeFile gcodeFile = new() { Id = Guid.NewGuid(), FileName = "benchy.gcode", FolderId = folder.Id, UpdatedAt = now };
        gcodeFile.Tags.Add(gcodeOnly);
        gcodeFile.Tags.Add(allThree);
        db.GcodeFiles.Add(gcodeFile);

        // allThree is additionally used on a Printer and a Model3D.
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Printer",
            ServerUrl = "http://printer",
            BackendPort = 80,
            Backend = (int)PrinterBackend.PrusaLink,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };
        printer.Tags.Add(allThree);
        db.Printers.Add(printer);

        db.Set<Model3DTagMapping>().Add(new Model3DTagMapping { Model3DId = model3DId, TagsId = allThree.Id });

        await db.SaveChangesAsync();

        EfTagRepository repo = new(db, CreateModel3DQueryMock(model3DId).Object);
        IReadOnlyDictionary<Guid, int> counts = await repo.GetTagUsageCountsAsync([unused.Id, gcodeOnly.Id, allThree.Id], CancellationToken.None);

        Assert.True(counts.ContainsKey(unused.Id), "a zero-usage tag must be present in the result, not dropped");
        Assert.Equal(0, counts[unused.Id]);
        Assert.Equal(1, counts[gcodeOnly.Id]);
        Assert.Equal(3, counts[allThree.Id]); // GcodeFile + Printer + Model3D, no double-counting.
    }

    /// <summary>
    /// Mirrors <see cref="GetTagUsageCountsAsync_CoversZeroUsageSingleSourceAndAllSources_WithoutDroppingOrDoubleCounting"/>
    /// for <see cref="ITagRepository.GetTagLastUsedAtBatchAsync"/>: a tag used in both
    /// GcodeFile and Model3D sources must report the MAX of the two timestamps, and an unused
    /// tag must simply be absent (never a spurious 0/min-value entry).
    /// </summary>
    [Fact]
    public async Task GetTagLastUsedAtBatchAsync_PicksMaxAcrossSources_AndOmitsUnusedTags()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        Guid model3DId = Guid.NewGuid();

        DateTime createdAt = DateTime.UtcNow.AddDays(-30);
        Tag unused = new() { Id = Guid.NewGuid(), Name = "Unused", CreatedAt = createdAt, UpdatedAt = createdAt };
        Tag usedInBoth = new() { Id = Guid.NewGuid(), Name = "UsedInBoth", CreatedAt = createdAt, UpdatedAt = createdAt };
        db.Set<Tag>().AddRange(unused, usedInBoth);

        FolderNode folder = new() { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };
        db.Set<FolderNode>().Add(folder);
        await db.SaveChangesAsync();

        DateTime gcodeUpdatedAt = DateTime.UtcNow.AddDays(-2);
        DateTime model3DUpdatedAt = DateTime.UtcNow; // Newer than the GcodeFile timestamp - should win.

        GcodeFile gcodeFile = new() { Id = Guid.NewGuid(), FileName = "benchy.gcode", FolderId = folder.Id, UpdatedAt = gcodeUpdatedAt };
        gcodeFile.Tags.Add(usedInBoth);
        db.GcodeFiles.Add(gcodeFile);

        db.Set<Model3DTagMapping>().Add(new Model3DTagMapping { Model3DId = model3DId, TagsId = usedInBoth.Id });
        await db.SaveChangesAsync();

        var model3DQuery = new Mock<IModel3DQueryProvider>();
        model3DQuery.Setup(q => q.GetUpdatedAtByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<Guid, DateTime>)new Dictionary<Guid, DateTime> { [model3DId] = model3DUpdatedAt });

        EfTagRepository repo = new(db, model3DQuery.Object);
        IReadOnlyDictionary<Guid, DateTime> lastUsed = await repo.GetTagLastUsedAtBatchAsync([unused.Id, usedInBoth.Id], CancellationToken.None);

        Assert.False(lastUsed.ContainsKey(unused.Id), "an unused tag must be omitted, not reported with a spurious timestamp");
        Assert.True(lastUsed.ContainsKey(usedInBoth.Id));
        Assert.Equal(model3DUpdatedAt, lastUsed[usedInBoth.Id], TimeSpan.FromSeconds(1));
    }
}
