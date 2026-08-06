using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Tags;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Tags;

/// <summary>
/// Tests for the fleet-scoped <see cref="ITagService.GetObjectsTagsAsync"/> read (PR 1146,
/// item 1) that replaces N per-object <c>GET /api/tags/object/{id}</c> round trips with one
/// grouped query. Runs against a real SQLite in-memory database so the underlying
/// <see cref="EfTagRepository.GetTagsByObjectsAsync"/> query (Include/skip-navigation for
/// Printer and GcodeFile, join-table grouping for Model3D) is exercised for real rather than
/// mocked away.
/// </summary>
public class TagServiceFleetReadsTests
{
    private static TagService CreateService(AppDbContext db, IModel3DQueryProvider? model3DQuery = null)
    {
        var repo = new EfTagRepository(db, model3DQuery);
        return new TagService(repo, NullLogger<TagService>.Instance);
    }

    /// <summary>
    /// Seeds a Manufacturer/PrinterModel pair so <see cref="NewPrinterAsync"/> can satisfy
    /// Printer's real FK constraints under the SQLite provider (unlike the EF InMemory
    /// provider, SQLite enforces them).
    /// </summary>
    private static async Task<(Guid ManufacturerId, Guid ModelId)> SeedCatalogAsync(AppDbContext db)
    {
        Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = $"Mfr-{Guid.NewGuid():N}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), Name = $"Model-{Guid.NewGuid():N}", ManufacturerId = manufacturer.Id };
        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();
        return (manufacturer.Id, model.Id);
    }

    private static async Task<Printer> NewPrinterAsync(AppDbContext db, string name)
    {
        (Guid manufacturerId, Guid modelId) = await SeedCatalogAsync(db);
        return new Printer
        {
            Id = Guid.NewGuid(),
            Name = name,
            ServerUrl = $"http://{name.ToLowerInvariant()}",
            BackendPort = 80,
            Backend = (int)PrinterBackend.PrusaLink,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
        };
    }

    private static async Task<Guid> SeedGcodeFolderAsync(AppDbContext db)
    {
        FolderNode folder = new() { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };
        db.Set<FolderNode>().Add(folder);
        await db.SaveChangesAsync();
        return folder.Id;
    }

    [Fact]
    public async Task GetObjectsTagsAsync_Printer_GroupsTagsPerPrinterInOneRead()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);

        TagDto red = await service.CreateTagAsync(new CreateTagDto { Name = "Red" }, CancellationToken.None);
        TagDto pla = await service.CreateTagAsync(new CreateTagDto { Name = "Pla" }, CancellationToken.None);
        Tag redEntity = await db.Set<Tag>().FirstAsync(t => t.Id == red.Id);
        Tag plaEntity = await db.Set<Tag>().FirstAsync(t => t.Id == pla.Id);

        Printer tagged = await NewPrinterAsync(db, "Tagged");
        tagged.Tags.Add(redEntity);
        tagged.Tags.Add(plaEntity);
        Printer untagged = await NewPrinterAsync(db, "Untagged");
        db.Printers.AddRange(tagged, untagged);
        await db.SaveChangesAsync();

        IReadOnlyList<ObjectTagsDto> result = await service.GetObjectsTagsAsync("Printer", CancellationToken.None);

        Assert.Equal(2, result.Count);
        ObjectTagsDto taggedEntry = Assert.Single(result, r => r.ObjectId == tagged.Id);
        Assert.Equal(2, taggedEntry.Tags.Count);
        Assert.Contains(taggedEntry.Tags, t => t.Name == "Red");
        Assert.Contains(taggedEntry.Tags, t => t.Name == "Pla");

        // Printers with no tags are still present with an empty list, not omitted -
        // callers must be able to tell "no tags" apart from "object not found".
        ObjectTagsDto untaggedEntry = Assert.Single(result, r => r.ObjectId == untagged.Id);
        Assert.Empty(untaggedEntry.Tags);
    }

    [Fact]
    public async Task GetObjectsTagsAsync_NoPrinters_ReturnsEmptyList()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);

        IReadOnlyList<ObjectTagsDto> result = await service.GetObjectsTagsAsync("Printer", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetObjectsTagsAsync_GcodeFile_GroupsTagsPerFileInOneRead()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);

        TagDto support = await service.CreateTagAsync(new CreateTagDto { Name = "Support" }, CancellationToken.None);
        Tag supportEntity = await db.Set<Tag>().FirstAsync(t => t.Id == support.Id);
        Guid folderId = await SeedGcodeFolderAsync(db);

        GcodeFile file = new() { Id = Guid.NewGuid(), FileName = "benchy.gcode", FolderId = folderId };
        file.Tags.Add(supportEntity);
        db.GcodeFiles.Add(file);
        await db.SaveChangesAsync();

        IReadOnlyList<ObjectTagsDto> result = await service.GetObjectsTagsAsync("GcodeFile", CancellationToken.None);

        ObjectTagsDto entry = Assert.Single(result);
        Assert.Equal(file.Id, entry.ObjectId);
        Assert.Equal("Support", Assert.Single(entry.Tags).Name);
    }

    [Fact]
    public async Task GetObjectsTagsAsync_Model3D_GroupsTagsPerModelViaJoinTableInOneRead()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        Guid model3DId = Guid.NewGuid();
        var model3DQuery = new Mock<IModel3DQueryProvider>();
        model3DQuery.Setup(q => q.GetAllIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([model3DId]);
        TagService service = CreateService(db, model3DQuery.Object);

        TagDto decorative = await service.CreateTagAsync(new CreateTagDto { Name = "Decorative" }, CancellationToken.None);
        db.Set<Model3DTagMapping>().Add(new Model3DTagMapping { Model3DId = model3DId, TagsId = decorative.Id });
        await db.SaveChangesAsync();

        IReadOnlyList<ObjectTagsDto> result = await service.GetObjectsTagsAsync("Model3D", CancellationToken.None);

        ObjectTagsDto entry = Assert.Single(result);
        Assert.Equal(model3DId, entry.ObjectId);
        Assert.Equal("Decorative", Assert.Single(entry.Tags).Name);
    }

    [Fact]
    public async Task GetObjectsTagsAsync_UnsupportedObjectType_ReturnsEmptyList()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);

        IReadOnlyList<ObjectTagsDto> result = await service.GetObjectsTagsAsync("PrintJob", CancellationToken.None);

        Assert.Empty(result);
    }
}
