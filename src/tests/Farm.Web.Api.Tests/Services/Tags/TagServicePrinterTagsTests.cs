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
/// Regression tests for the printer-tag single-object read/write defect discovered while
/// building the PR 1146 fleet-read endpoint (see <see cref="TagServiceFleetReadsTests"/>).
/// <see cref="TagService.GetObjectTagsAsync"/>, <see cref="TagService.AssignTagAsync"/>, and
/// <see cref="TagService.RemoveTagAsync"/> previously ignored their <c>objectType</c> argument
/// and delegated to the <see cref="ITagRepository"/> object-agnostic overloads, which only
/// probe GcodeFile then Model3D. Since a Printer id matches neither probe, reads always
/// returned an empty list and writes silently no-op'd. Runs against a real SQLite in-memory
/// database so the underlying <see cref="EfTagRepository"/> skip-navigation query per object
/// type is exercised for real rather than mocked away.
/// </summary>
public class TagServicePrinterTagsTests
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
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ServerUrl = $"http://{name.ToLowerInvariant()}",
            BackendPort = 80,
            Backend = (int)PrinterBackend.PrusaLink,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();
        return printer;
    }

    private static async Task<Guid> SeedGcodeFolderAsync(AppDbContext db)
    {
        FolderNode folder = new() { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };
        db.Set<FolderNode>().Add(folder);
        await db.SaveChangesAsync();
        return folder.Id;
    }

    [Fact]
    public async Task GetObjectTagsAsync_Printer_ReturnsAssignedTags()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);

        TagDto red = await service.CreateTagAsync(new CreateTagDto { Name = "Red" }, CancellationToken.None);
        Tag redEntity = await db.Set<Tag>().FirstAsync(t => t.Id == red.Id);
        Printer printer = await NewPrinterAsync(db, "Voron");
        printer.Tags.Add(redEntity);
        await db.SaveChangesAsync();

        IReadOnlyList<TagDto> result = await service.GetObjectTagsAsync(printer.Id, "Printer", CancellationToken.None);

        Assert.Equal("Red", Assert.Single(result).Name);
    }

    [Fact]
    public async Task GetObjectTagsAsync_PrinterWithNoTags_ReturnsEmptyList()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);
        Printer printer = await NewPrinterAsync(db, "Bambu");

        IReadOnlyList<TagDto> result = await service.GetObjectTagsAsync(printer.Id, "Printer", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task AssignTagAsync_Printer_PersistsTagOnPrinter()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);
        TagDto pla = await service.CreateTagAsync(new CreateTagDto { Name = "Pla" }, CancellationToken.None);
        Printer printer = await NewPrinterAsync(db, "Ender");

        await service.AssignTagAsync(printer.Id, pla.Id, "Printer", CancellationToken.None);

        Printer? reloaded = await db.Printers.Include(p => p.Tags).FirstOrDefaultAsync(p => p.Id == printer.Id);
        Assert.NotNull(reloaded);
        Assert.Contains(reloaded.Tags, t => t.Id == pla.Id);
    }

    [Fact]
    public async Task AssignTagAsync_PrinterAlreadyTagged_IsIdempotent()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);
        TagDto pla = await service.CreateTagAsync(new CreateTagDto { Name = "Pla" }, CancellationToken.None);
        Printer printer = await NewPrinterAsync(db, "Ender");

        await service.AssignTagAsync(printer.Id, pla.Id, "Printer", CancellationToken.None);
        await service.AssignTagAsync(printer.Id, pla.Id, "Printer", CancellationToken.None);

        Printer? reloaded = await db.Printers.Include(p => p.Tags).FirstOrDefaultAsync(p => p.Id == printer.Id);
        Assert.NotNull(reloaded);
        Assert.Single(reloaded.Tags);
    }

    [Fact]
    public async Task RemoveTagAsync_Printer_RemovesTagFromPrinter()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);
        TagDto pla = await service.CreateTagAsync(new CreateTagDto { Name = "Pla" }, CancellationToken.None);
        Printer printer = await NewPrinterAsync(db, "Ender");
        printer.Tags.Add(await db.Set<Tag>().FirstAsync(t => t.Id == pla.Id));
        await db.SaveChangesAsync();

        await service.RemoveTagAsync(printer.Id, pla.Id, "Printer", CancellationToken.None);

        Printer? reloaded = await db.Printers.Include(p => p.Tags).FirstOrDefaultAsync(p => p.Id == printer.Id);
        Assert.NotNull(reloaded);
        Assert.Empty(reloaded.Tags);
    }

    [Fact]
    public async Task RemoveTagAsync_PrinterNotTagged_DoesNotThrow()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);
        TagDto pla = await service.CreateTagAsync(new CreateTagDto { Name = "Pla" }, CancellationToken.None);
        Printer printer = await NewPrinterAsync(db, "Ender");

        await service.RemoveTagAsync(printer.Id, pla.Id, "Printer", CancellationToken.None);

        Printer? reloaded = await db.Printers.Include(p => p.Tags).FirstOrDefaultAsync(p => p.Id == printer.Id);
        Assert.NotNull(reloaded);
        Assert.Empty(reloaded.Tags);
    }

    [Fact]
    public async Task AssignThenGetObjectTagsAsync_Printer_ReadReflectsWrite()
    {
        // Core symptom of the defect: a tag assigned via AssignTagAsync must be visible to a
        // subsequent GetObjectTagsAsync read for the same printer (the tagging modal's
        // save-then-refetch flow), and removing it must make the read empty again.
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);
        TagDto abs = await service.CreateTagAsync(new CreateTagDto { Name = "Abs" }, CancellationToken.None);
        Printer printer = await NewPrinterAsync(db, "Prusa");

        await service.AssignTagAsync(printer.Id, abs.Id, "Printer", CancellationToken.None);
        IReadOnlyList<TagDto> afterAssign = await service.GetObjectTagsAsync(printer.Id, "Printer", CancellationToken.None);
        Assert.Equal("Abs", Assert.Single(afterAssign).Name);

        await service.RemoveTagAsync(printer.Id, abs.Id, "Printer", CancellationToken.None);
        IReadOnlyList<TagDto> afterRemove = await service.GetObjectTagsAsync(printer.Id, "Printer", CancellationToken.None);
        Assert.Empty(afterRemove);
    }

    [Fact]
    public async Task AssignThenGetObjectTagsAsync_GcodeFile_StillWorks()
    {
        // Compatibility check: routing AssignTagAsync/RemoveTagAsync/GetObjectTagsAsync by
        // objectType must not change existing GcodeFile behavior.
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);
        TagDto support = await service.CreateTagAsync(new CreateTagDto { Name = "Support" }, CancellationToken.None);
        Guid folderId = await SeedGcodeFolderAsync(db);
        GcodeFile file = new() { Id = Guid.NewGuid(), FileName = "benchy.gcode", FolderId = folderId };
        db.GcodeFiles.Add(file);
        await db.SaveChangesAsync();

        await service.AssignTagAsync(file.Id, support.Id, "GcodeFile", CancellationToken.None);
        IReadOnlyList<TagDto> afterAssign = await service.GetObjectTagsAsync(file.Id, "GcodeFile", CancellationToken.None);
        Assert.Equal("Support", Assert.Single(afterAssign).Name);

        await service.RemoveTagAsync(file.Id, support.Id, "GcodeFile", CancellationToken.None);
        IReadOnlyList<TagDto> afterRemove = await service.GetObjectTagsAsync(file.Id, "GcodeFile", CancellationToken.None);
        Assert.Empty(afterRemove);
    }

    [Fact]
    public async Task AssignThenGetObjectTagsAsync_Model3D_StillWorks()
    {
        // Compatibility check: routing AssignTagAsync/RemoveTagAsync/GetObjectTagsAsync by
        // objectType must not change existing Model3D (join-table) behavior.
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        Guid model3DId = Guid.NewGuid();
        var model3DQuery = new Mock<IModel3DQueryProvider>();
        model3DQuery.Setup(q => q.ExistsAsync(model3DId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        TagService service = CreateService(db, model3DQuery.Object);
        TagDto decorative = await service.CreateTagAsync(new CreateTagDto { Name = "Decorative" }, CancellationToken.None);

        await service.AssignTagAsync(model3DId, decorative.Id, "Model3D", CancellationToken.None);
        IReadOnlyList<TagDto> afterAssign = await service.GetObjectTagsAsync(model3DId, "Model3D", CancellationToken.None);
        Assert.Equal("Decorative", Assert.Single(afterAssign).Name);

        await service.RemoveTagAsync(model3DId, decorative.Id, "Model3D", CancellationToken.None);
        IReadOnlyList<TagDto> afterRemove = await service.GetObjectTagsAsync(model3DId, "Model3D", CancellationToken.None);
        Assert.Empty(afterRemove);
    }

    [Fact]
    public async Task AssignTagAsync_PrinterAndGcodeFileTaggedIndependently_DoNotCrossContaminate()
    {
        // Guards against a regression where dispatch-by-objectType could fall back to
        // scanning the wrong table: tagging a printer must not affect an unrelated GcodeFile,
        // and vice versa, even when both are read back with their own objectType.
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);
        TagDto shared = await service.CreateTagAsync(new CreateTagDto { Name = "Shared" }, CancellationToken.None);
        Printer printer = await NewPrinterAsync(db, "Klipper");
        Guid folderId = await SeedGcodeFolderAsync(db);
        GcodeFile file = new() { Id = Guid.NewGuid(), FileName = "vase.gcode", FolderId = folderId };
        db.GcodeFiles.Add(file);
        await db.SaveChangesAsync();

        await service.AssignTagAsync(printer.Id, shared.Id, "Printer", CancellationToken.None);

        IReadOnlyList<TagDto> printerTags = await service.GetObjectTagsAsync(printer.Id, "Printer", CancellationToken.None);
        IReadOnlyList<TagDto> fileTags = await service.GetObjectTagsAsync(file.Id, "GcodeFile", CancellationToken.None);

        Assert.Single(printerTags);
        Assert.Empty(fileTags);
    }
}
