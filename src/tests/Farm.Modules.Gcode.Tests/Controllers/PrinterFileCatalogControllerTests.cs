using System.Security.Claims;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Modules.Gcode.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Farm.Modules.Gcode.Tests.Controllers;

public sealed class PrinterFileCatalogControllerTests
{
    [Fact]
    public async Task ListAsync_FiltersSourcesBeforeCountsPagingOriginsAndScanStatus()
    {
        await using var fixture = await Fixture.CreateAsync();
        IActionResult result = await fixture.Controller.ListAsync(null, null);
        string json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("public.gcode",
            Assert.Single(document.RootElement.GetProperty("items").EnumerateArray()).GetProperty("relativePath").GetString());
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Hidden.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Single(document.RootElement.GetProperty("printers").EnumerateArray());
        Assert.Single(document.RootElement.GetProperty("scans").EnumerateArray());
    }

    [Fact]
    public async Task ListAsync_OriginIdsRequireIndependentLocalAssetAuthority()
    {
        await using var fixture = await Fixture.CreateAsync();
        PrinterFileLocation source = await fixture.Db.PrinterFileLocations.SingleAsync(
            x => x.SourcePrinterId == fixture.Visible);
        FolderNode folder = new() { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };
        GcodeFile permitted = new() { Id = Guid.NewGuid(), Folder = folder, FileName = "permitted.gcode", FileHash = "permitted" };
        GcodeFile restricted = new() { Id = Guid.NewGuid(), Folder = folder, FileName = "restricted.gcode", FileHash = "restricted" };
        fixture.Db.GcodeFiles.AddRange(permitted, restricted);
        foreach (GcodeFile file in new[] { permitted, restricted })
        {
            fixture.Db.PrinterFileOrigins.Add(new()
            {
                Id = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                LocationId = source.Id,
                GcodeFileId = file.Id,
                SourcePrinterId = fixture.Visible,
                StorageRoot = source.StorageRoot,
                RelativePath = source.RelativePath,
            });
        }

        await fixture.Db.SaveChangesAsync();
        fixture.Access.Setup(x => x.CanAccessLocalAsync(fixture.Actor, permitted.Id,
            PrinterGroupAccessLevel.View, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        IActionResult result = await fixture.Controller.ListAsync(null, null);
        string json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains(permitted.Id.ToString(), json);
        Assert.DoesNotContain(restricted.Id.ToString(), json);
    }

    [Fact]
    public async Task ListAsync_FilterForInaccessiblePrinterCannotRevealCount()
    {
        await using var fixture = await Fixture.CreateAsync();
        IActionResult result = await fixture.Controller.ListAsync(fixture.Hidden, null);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(result).Value));
        Assert.Equal(0, document.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task RefreshAsync_ViewDoesNotImplyManage()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.IsType<NotFoundResult>(await fixture.Controller.RefreshAsync(fixture.Visible, CancellationToken.None));
        Assert.Equal(DateTime.MaxValue, (await fixture.Db.PrinterFileScanStates.SingleAsync(
            x => x.SourcePrinterId == fixture.Visible)).NextScanAtUtc);
    }

    [Fact]
    public async Task ListAsync_RejectsUnboundedPages()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.IsType<BadRequestObjectResult>(await fixture.Controller.ListAsync(null, null, 0));
        Assert.IsType<BadRequestObjectResult>(await fixture.Controller.ListAsync(null, null, pageSize: 201));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
            Access.Setup(x => x.CanReadAsync(Actor, Visible, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Controller = new PrinterFileCatalogController(db, Access.Object, Mock.Of<IQueueResourceAuthorizationService>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, Actor.ToString())], "test")),
                    },
                },
            };
        }

        public Guid Actor { get; } = Guid.NewGuid();

        public Guid Visible { get; } = Guid.NewGuid();

        public Guid Hidden { get; } = Guid.NewGuid();

        public AppDbContext Db { get; }

        public Mock<IPrinterFileAccessService> Access { get; } = new();

        public PrinterFileCatalogController Controller { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            var fixture = new Fixture(connection, db);
            await db.Database.EnsureCreatedAsync();
            var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "API test" };
            var model = new PrinterModel { Id = Guid.NewGuid(), Name = "API test", Manufacturer = manufacturer };
            foreach ((Guid id, string name) in new[] { (fixture.Visible, "public"), (fixture.Hidden, "secret") })
            {
                db.Printers.Add(new Printer
                {
                    Id = id,
                    Name = name,
                    ServerUrl = $"http://{name}.invalid",
                    IsEnabled = true,
                    Manufacturer = manufacturer,
                    Model = model,
                });
                db.PrinterFileLocations.Add(new PrinterFileLocation
                {
                    Id = Guid.NewGuid(),
                    SourcePrinterId = id,
                    RegisteredPrinterId = id,
                    StorageRoot = name,
                    RelativePath = $"{name}.gcode",
                    IdentityKey = PrinterInventoryPath.ComputeIdentity(name, $"{name}.gcode"),
                    DisplayName = $"{name}.gcode",
                });
                db.PrinterFileScanStates.Add(new PrinterFileScanState
                {
                    Id = id,
                    SourcePrinterId = id,
                    RegisteredPrinterId = id,
                    ReasonCode = name,
                    NextScanAtUtc = DateTime.MaxValue,
                });
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
