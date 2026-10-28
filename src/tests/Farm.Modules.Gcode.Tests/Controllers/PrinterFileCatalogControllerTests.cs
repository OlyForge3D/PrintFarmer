using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Settings;
using Farm.Modules.Gcode.Controllers;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Modules.Gcode.Tests.Controllers;

public sealed class PrinterFileCatalogControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlsAsync_ConcurrentScanWriteReturnsConflictRatherThanServerError(bool toggle)
    {
        ScanWriteConflictInterceptor interceptor = new();
        await using Fixture fixture = await Fixture.CreateAsync(interceptor);
        fixture.Resources.Setup(x => x.CanActorAccessPrinterAsync(fixture.Actor.ToString(), fixture.Visible,
            PrinterGroupAccessLevel.Manage, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        interceptor.Fail = true;
        IActionResult result = toggle
            ? await fixture.Controller.SetEnabledAsync(fixture.Visible, new(false), CancellationToken.None)
            : await fixture.Controller.RefreshAsync(fixture.Visible, ct: CancellationToken.None);
        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Contains("scan_state_changed", JsonSerializer.Serialize(conflict.Value));
    }

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

    [Theory]
    [InlineData(null, 2)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(16, 16)]
    public async Task ListAsync_ReportsConfiguredRefreshConcurrency(int? configured, int expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (configured is { } maxConcurrentIo)
        {
            fixture.Db.AppSettingsEntities.Add(new AppSettingsEntity
            {
                Key = PrinterFileIndexSettings.SectionName,
                SettingsJson = JsonSerializer.Serialize(new PrinterFileIndexSettings { MaxConcurrentIo = maxConcurrentIo }),
                UpdatedAt = DateTime.UtcNow,
            });
            await fixture.Db.SaveChangesAsync();
            fixture.Db.ChangeTracker.Clear();
        }

        using JsonDocument document = ReadOk(await fixture.Controller.ListAsync(null, null));

        Assert.Equal(expected, document.RootElement.GetProperty("maxConcurrentIo").GetInt32());
    }

    [Fact]
    public async Task ListAsync_PersistedDetails_ReturnsMetadataWithoutReadingPrinter()
    {
        await using var fixture = await Fixture.CreateAsync();
        PrinterFileLocation location = await fixture.Db.PrinterFileLocations.SingleAsync(
            x => x.SourcePrinterId == fixture.Visible);
        DateTime fetched = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        location.DisplayName = "Long printable part.bgcode";
        location.SizeBytes = 123;
        location.DetailsFetchedAtUtc = fetched;
        location.Metadata = new() { BrimWidthMm = 0, QuietLeftPresent = false, FilamentUsedPerToolG = [0, 2.5] };
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        using JsonDocument document = ReadOk(await fixture.Controller.ListAsync(null, null));
        JsonElement entry = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Long printable part.bgcode", entry.GetProperty("displayName").GetString());
        Assert.Equal("public.gcode", entry.GetProperty("relativePath").GetString());
        Assert.Equal(123, entry.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(fetched, entry.GetProperty("detailsFetchedAtUtc").GetDateTime());
        JsonElement metadata = entry.GetProperty("metadata");
        Assert.Equal(0, metadata.GetProperty("brimWidthMm").GetDouble());
        Assert.False(metadata.GetProperty("quietLeftPresent").GetBoolean());
        Assert.Equal(new double[] { 0, 2.5 },
            metadata.GetProperty("filamentUsedPerToolG").EnumerateArray().Select(value => value.GetDouble()));
        Assert.False(metadata.TryGetProperty("hasValues", out _));
    }

    [Fact]
    public async Task ListAsync_EnrichmentStatusIsShownOnlyForTheCurrentSourceGeneration()
    {
        await using var fixture = await Fixture.CreateAsync();
        PrinterFileLocation location = await fixture.Db.PrinterFileLocations.AsNoTracking().SingleAsync(
            x => x.SourcePrinterId == fixture.Visible);
        Guid artifactId = await fixture.SettleEnrichmentAsync(location, DateTime.UtcNow.AddHours(24));

        using (JsonDocument document = ReadOk(await fixture.Controller.ListAsync(null, null)))
        {
            JsonElement entry = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("Ready", entry.GetProperty("metadataState").GetString());
            Assert.Equal("Ready", entry.GetProperty("thumbnailState").GetString());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("metadataReason").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("thumbnailReason").ValueKind);
            Assert.Equal(artifactId, entry.GetProperty("thumbnailVersion").GetGuid());
            Assert.Equal(
                $"/api/printer-files/locations/{location.Id}/thumbnail?observationRevision=1&version={artifactId}",
                entry.GetProperty("thumbnailUrl").GetString());
            Assert.False(entry.GetProperty("thumbnailIsStale").GetBoolean());
        }

        await fixture.Db.PrinterFileLocations.Where(x => x.Id == location.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ConfigurationGeneration, 7));

        using (JsonDocument document = ReadOk(await fixture.Controller.ListAsync(null, null)))
        {
            JsonElement entry = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
            foreach (string name in new[]
                     {
                         "metadataState", "thumbnailState", "metadataReason", "thumbnailReason",
                         "thumbnailVersion", "thumbnailUrl",
                     })
            {
                Assert.Equal(JsonValueKind.Null, entry.GetProperty(name).ValueKind);
            }

            Assert.False(entry.GetProperty("thumbnailIsStale").GetBoolean());
        }
    }

    [Fact]
    public async Task ListAsync_DueReacquisitionServesPreviousThumbnailAsStale()
    {
        await using var fixture = await Fixture.CreateAsync();
        PrinterFileLocation location = await fixture.Db.PrinterFileLocations.AsNoTracking().SingleAsync(
            x => x.SourcePrinterId == fixture.Visible);
        Guid artifactId = await fixture.SettleEnrichmentAsync(location, DateTime.UtcNow.AddMinutes(-1));

        using JsonDocument document = ReadOk(await fixture.Controller.ListAsync(null, null));

        JsonElement entry = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(artifactId, entry.GetProperty("thumbnailVersion").GetGuid());
        Assert.True(entry.GetProperty("thumbnailIsStale").GetBoolean());
    }

    [Fact]
    public async Task RefreshAsync_WithoutForceMarksOnlyThatPrintersEnrichmentDue()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Resources.Setup(x => x.CanActorAccessPrinterAsync(fixture.Actor.ToString(), fixture.Visible,
            PrinterGroupAccessLevel.Manage, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        DateTime settled = DateTime.UtcNow.AddHours(24);
        foreach (PrinterFileLocation location in await fixture.Db.PrinterFileLocations.AsNoTracking().ToListAsync())
        {
            await fixture.SettleEnrichmentAsync(location, settled);
        }

        DateTime before = DateTime.UtcNow;
        Assert.IsType<AcceptedResult>(
            await fixture.Controller.RefreshAsync(fixture.Visible, ct: CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        var rows = await fixture.Db.PrinterFileEnrichments.AsNoTracking()
            .Select(x => new { x.Location.SourcePrinterId, x.NextAttemptAtUtc, x.ArtifactId })
            .ToListAsync();
        var visible = Assert.Single(rows, x => x.SourcePrinterId == fixture.Visible);
        Assert.InRange(visible.NextAttemptAtUtc, before, DateTime.UtcNow);
        Assert.NotNull(visible.ArtifactId);
        Assert.Equal(settled, Assert.Single(rows, x => x.SourcePrinterId == fixture.Hidden).NextAttemptAtUtc);
        Assert.InRange((await fixture.Db.PrinterFileScanStates.SingleAsync(
            x => x.SourcePrinterId == fixture.Visible)).NextScanAtUtc, before, DateTime.UtcNow);
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
        IReadOnlyCollection<Guid>? candidates = null;
        fixture.Access.Setup(x => x.FilterAccessibleLocalAsync(fixture.Actor, It.IsAny<IReadOnlyCollection<Guid>>(),
                PrinterGroupAccessLevel.View, It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyCollection<Guid>, PrinterGroupAccessLevel, CancellationToken>(
                (_, ids, _, _) => candidates = ids)
            .ReturnsAsync(new HashSet<Guid> { permitted.Id });
        IActionResult result = await fixture.Controller.ListAsync(null, null);
        string json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains(permitted.Id.ToString(), json);
        Assert.DoesNotContain(restricted.Id.ToString(), json);
        Assert.NotNull(candidates);
        Assert.Equal(new[] { permitted.Id, restricted.Id }.Order(), candidates!.Order());
        fixture.Access.Verify(x => x.CanAccessLocalAsync(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()), Times.Never);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_ViewDoesNotImplyManage(bool force)
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.IsType<NotFoundResult>(await fixture.Controller.RefreshAsync(fixture.Visible, force, CancellationToken.None));
        Assert.Equal(DateTime.MaxValue, (await fixture.Db.PrinterFileScanStates.SingleAsync(
            x => x.SourcePrinterId == fixture.Visible)).NextScanAtUtc);
    }

    [Fact]
    public async Task RefreshAsync_ForceDoesNotScanWhenPrinterIsNotConfirmedIdle()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Resources.Setup(x => x.CanActorAccessPrinterAsync(fixture.Actor.ToString(), fixture.Visible,
            PrinterGroupAccessLevel.Manage, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var leases = new Mock<IPrinterFileLeaseCoordinator>();
        var status = new Mock<IPrinterStatusCacheReader>();
        status.Setup(x => x.GetSnapshot(fixture.Visible)).Returns(new PrinterStatusCacheSnapshot(
            new PrinterStatusDto(fixture.Visible, true, "Printing") { RawActivityState = "printing" }, DateTime.UtcNow));
        var catalog = new PrinterFileCatalogService(
            Mock.Of<IDbContextFactory<AppDbContext>>(), Mock.Of<IPrintersRepository>(),
            Mock.Of<IBackendCapabilityFactory>(), leases.Object, status.Object,
            NullLogger<PrinterFileCatalogService>.Instance);
        PrinterFileCatalogController controller = fixture.CreateController(
            fixture.Access.Object, fixture.Resources.Object, catalog: catalog);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(
            await controller.RefreshAsync(fixture.Visible, true, CancellationToken.None));

        Assert.Contains("waiting_for_confirmed_idle", JsonSerializer.Serialize(conflict.Value));
        leases.Verify(x => x.TryAcquireForRefreshAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        leases.Verify(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

    [Fact]
    public async Task ListAsync_CapabilityFlagsComeFromOneBatchedAuthorityCall()
    {
        Mock<IBackendClientFactory> backends = CreateBackends();
        await using Fixture fixture = await Fixture.CreateAsync(backendClients: backends.Object);
        Guid full = await fixture.AddPrinterAsync("a-full");
        Guid prusa = await fixture.AddPrinterAsync("b-prusalink", PrinterBackend.PrusaLink);
        Guid copyOnly = await fixture.AddPrinterAsync("c-copy-only");
        Guid manageOnly = await fixture.AddPrinterAsync("d-manage-only");
        Guid readOnly = await fixture.AddPrinterAsync("e-read-only");
        Guid unreadable = await fixture.AddPrinterAsync("f-unreadable");
        Guid disabled = await fixture.AddPrinterAsync("g-disabled", enabled: false);
        Guid[]? candidates = null;
        fixture.Access.Setup(x => x.GetCatalogAuthorityAsync(fixture.Actor, It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyCollection<Guid>, CancellationToken>((_, ids, _) => candidates = [.. ids])
            .ReturnsAsync(new PrinterFileCatalogAuthority(
                new HashSet<Guid> { full, prusa, copyOnly, manageOnly, readOnly },
                new HashSet<Guid> { full, prusa, manageOnly },
                new HashSet<Guid> { full, prusa, copyOnly },
                new HashSet<Guid> { full, prusa }));

        using JsonDocument document = ReadOk(await fixture.Controller.ListAsync(null, null));

        Assert.NotNull(candidates);
        Assert.Equal(
            new[] { fixture.Visible, fixture.Hidden, full, prusa, copyOnly, manageOnly, readOnly, unreadable }.Order(),
            candidates!.Order());
        Assert.DoesNotContain(disabled, candidates!);
        var flags = document.RootElement.GetProperty("printers").EnumerateArray().Select(x => (
            Name: x.GetProperty("name").GetString(),
            Manage: x.GetProperty("canManage").GetBoolean(),
            Supports: x.GetProperty("supportsCopy").GetBoolean(),
            Copy: x.GetProperty("canCopy").GetBoolean(),
            Delete: x.GetProperty("canDelete").GetBoolean())).ToArray();
        Assert.Equal(
            [
                ("a-full", true, true, true, true),
                ("b-prusalink", true, false, false, true),
                ("c-copy-only", false, true, true, false),
                ("d-manage-only", true, true, false, false),
                ("e-read-only", false, true, false, false),
            ],
            flags);
        foreach (JsonElement printer in document.RootElement.GetProperty("printers").EnumerateArray())
        {
            bool supported = printer.GetProperty("name").GetString() != "b-prusalink";
            Assert.Equal(supported, printer.GetProperty("supportsPreview").GetBoolean());

            // Previews are local reads of persisted enrichment, so every readable printer may open them.
            Assert.True(printer.GetProperty("canPreview").GetBoolean());
            Assert.Equal(supported, printer.GetProperty("supportsDownload").GetBoolean());
            Assert.Equal(printer.GetProperty("canCopy").GetBoolean(), printer.GetProperty("canDownload").GetBoolean());
        }
        fixture.Access.Verify(x => x.GetCatalogAuthorityAsync(fixture.Actor, It.IsAny<IReadOnlyCollection<Guid>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Access.Verify(x => x.CanReadAsync(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.Access.Verify(x => x.CanAccessLocalAsync(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Resources.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("benchy", "Models/Benchy_PLA.gcode")]
    [InlineData("BENCHY", "Models/Benchy_PLA.gcode")]
    [InlineData("benchy_pla", "Models/Benchy_PLA.gcode")]
    [InlineData("MODELS/", "Models/Benchy_PLA.gcode|Models/axb.gcode")]
    [InlineData("werkstatt", "Werkstatt/Müller.gcode|Werkstatt/ÄBC.gcode")]
    [InlineData("Müller", "Werkstatt/Müller.gcode")]
    [InlineData("ÄBC", "Werkstatt/ÄBC.gcode")]
    [InlineData("a_b", "")]
    [InlineData("secret", "")]
    public async Task ListAsync_SearchIgnoresCaseWithinAuthorizedSources(string search, string expected)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        foreach (string path in new[]
                 {
                     "Models/Benchy_PLA.gcode", "Models/axb.gcode", "Werkstatt/Müller.gcode", "Werkstatt/ÄBC.gcode",
                 })
        {
            await fixture.AddLocationAsync(fixture.Visible, path);
        }

        using JsonDocument document = ReadOk(await fixture.Controller.ListAsync(null, search));

        string[] actual = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("relativePath").GetString()!).Order(StringComparer.Ordinal).ToArray();
        string[] wanted = expected.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(wanted, actual);
        Assert.Equal(wanted.Length, document.RootElement.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void WherePathContains_TranslatesLoweredMatchForEveryProvider(string provider)
    {
        DbContextOptionsBuilder<AppDbContext> options = new();
        _ = provider switch
        {
            "sqlite" => options.UseSqlite("Data Source=:memory:"),
            "postgres" => options.UseNpgsql("Host=localhost;Database=model_only;Username=model_only"),
            "sqlserver" => options.UseSqlServer("Server=(localdb)\\model_only;Database=model_only;Trusted_Connection=True"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        using AppDbContext db = new(options.Options);

        string sql = PrinterFileCatalogSearch.WherePathContains(db.PrinterFileLocations, "Benchy").ToQueryString();

        Assert.Matches(new Regex(@"lower\([^)]*RelativePath", RegexOptions.IgnoreCase), sql);
        Assert.Contains("Benchy", sql, StringComparison.Ordinal);
        Assert.Contains("benchy", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_AuthorizationQueryCountDoesNotGrowWithPrinterCount()
    {
        CatalogMeasurement one = await MeasureCatalogAsync(1);
        CatalogMeasurement twenty = await MeasureCatalogAsync(20);

        Assert.True(one.Catalog == twenty.Catalog,
            $"catalog SQL commands must not depend on printer count (1 printer: {one.Catalog}, 20: {twenty.Catalog})");
        Assert.True(one.Authority == twenty.Authority && twenty.Authority <= 13,
            $"batched authority must stay constant and bounded (1 printer: {one.Authority}, 20: {twenty.Authority})");
        Assert.True(twenty.Catalog <= 29,
            $"catalog listing issued {twenty.Catalog} commands ({twenty.Authority} on printer authority); measured baseline is 29");
        Assert.True(twenty.Legacy - one.Legacy >= 19,
            $"control: the per-printer loop should grow with printer count (1: {one.Legacy}, 20: {twenty.Legacy})");
    }

    private static async Task<CatalogMeasurement> MeasureCatalogAsync(int printerCount)
    {
        CommandCountingInterceptor counter = new();
        Mock<IBackendClientFactory> backends = CreateBackends();
        await using Fixture fixture = await Fixture.CreateAsync(counter, backends.Object);
        AppDbContext db = fixture.Db;
        Role role = new() { Id = Guid.NewGuid(), Name = "catalog-test-operator", DisplayName = "Catalog test", IsActive = true };
        db.Users.Add(new User
        {
            Id = fixture.Actor,
            Username = $"catalog-{fixture.Actor:N}",
            Email = $"catalog-{fixture.Actor:N}@example.invalid",
            PasswordHash = "not-a-hash",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.Roles.Add(role);
        foreach ((string resource, string action) in new[]
                 {
                     ("printer_files", "read"), ("printer_files", "copy"), ("printer_files", "delete"),
                     ("gcode_library", "read"), ("gcode_library", "create"),
                 })
        {
            Resource resourceRow = await db.Resources.FirstOrDefaultAsync(x => x.Name == resource) ??
                db.Resources.Local.FirstOrDefault(x => x.Name == resource) ??
                db.Resources.Add(new Resource
                {
                    Id = Guid.NewGuid(),
                    Name = resource,
                    DisplayName = resource,
                    ResourceType = "system",
                    IsActive = true,
                }).Entity;
            UserAction actionRow = await db.UserActions.FirstOrDefaultAsync(x => x.Name == action) ??
                db.UserActions.Local.FirstOrDefault(x => x.Name == action) ??
                db.UserActions.Add(new UserAction { Id = Guid.NewGuid(), Name = action, DisplayName = action }).Entity;
            db.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(),
                RoleId = role.Id,
                ResourceId = resourceRow.Id,
                ActionId = actionRow.Id,
                Granted = true,
            });
        }

        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = fixture.Actor,
            RoleId = role.Id,
            AssignedAt = DateTime.UtcNow,
            IsActive = true,
        });
        PrinterGroup group = new() { Id = Guid.NewGuid(), Name = "catalog-test-group" };
        db.PrinterGroups.Add(group);
        db.PrinterGroupAccesses.Add(new PrinterGroupAccess
        {
            Id = Guid.NewGuid(),
            PrinterGroupId = group.Id,
            RoleId = role.Id,
            AccessLevel = PrinterGroupAccessLevel.Manage,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        FolderNode folder = new() { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };
        db.Add(folder);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        List<Guid> grouped = [];
        for (int i = 0; i < printerCount; i++)
        {
            Guid printerId = await fixture.AddPrinterAsync($"grouped-{i:D2}", groupId: group.Id);
            PrinterFileLocation location = await fixture.AddLocationAsync(printerId, $"grouped/{i:D2}.gcode");
            GcodeFile file = new()
            {
                Id = Guid.NewGuid(),
                FolderId = folder.Id,
                FileName = $"grouped-{i:D2}.gcode",
                FileHash = $"hash-{i:D2}",
                PrinterGroupId = group.Id,
            };
            db.GcodeFiles.Add(file);
            db.PrinterFileOrigins.Add(new PrinterFileOrigin
            {
                Id = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                LocationId = location.Id,
                GcodeFileId = file.Id,
                SourcePrinterId = printerId,
                StorageRoot = location.StorageRoot,
                RelativePath = location.RelativePath,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            grouped.Add(printerId);
        }

        db.ChangeTracker.Clear();
        QueueResourceAuthorizationService resources = new(db);
        PrinterFileAccessService access = new(db, new EfUsersRepository(db), resources);
        PrinterFileCatalogController controller = fixture.CreateController(access, resources, backends.Object);

        counter.Reset();
        IActionResult result = await controller.ListAsync(null, null, pageSize: 200);
        int catalog = counter.CommandCount;
        using (JsonDocument document = ReadOk(result))
        {
            Assert.Equal(printerCount + 2, document.RootElement.GetProperty("total").GetInt32());
            JsonElement[] printers = [.. document.RootElement.GetProperty("printers").EnumerateArray()];
            Assert.Equal(printerCount + 2, printers.Length);
            foreach (JsonElement printer in printers.Where(x => grouped.Contains(x.GetProperty("id").GetGuid())))
            {
                Assert.True(printer.GetProperty("canManage").GetBoolean());
                Assert.True(printer.GetProperty("canCopy").GetBoolean());
                Assert.True(printer.GetProperty("canDelete").GetBoolean());
            }

            JsonElement[] groupedItems = [.. document.RootElement.GetProperty("items").EnumerateArray()
                .Where(x => grouped.Contains(x.GetProperty("printerId").GetGuid()))];
            Assert.Equal(printerCount, groupedItems.Length);
            Assert.All(groupedItems, item => Assert.Single(item.GetProperty("localCopies").EnumerateArray()));
        }

        Guid[] enabled = [.. grouped, fixture.Visible, fixture.Hidden];
        counter.Reset();
        PrinterFileCatalogAuthority authority = await access.GetCatalogAuthorityAsync(fixture.Actor, enabled);
        int authorityCount = counter.CommandCount;
        Assert.Equal(enabled.Order(), authority.Readable.Order());

        counter.Reset();
        foreach (Guid printerId in enabled)
        {
            Assert.True(await access.CanReadAsync(fixture.Actor, printerId));
        }

        return new CatalogMeasurement(catalog, authorityCount, counter.CommandCount);
    }

    private static Mock<IBackendClientFactory> CreateBackends()
    {
        Mock<IBackendClientFactory> backends = new();
        backends.Setup(x => x.IsBackendSupported(It.IsAny<PrinterBackend>())).Returns(true);
        backends.Setup(x => x.GetClient((int)PrinterBackend.Moonraker)).Returns(new CopyCapableClient());
        backends.Setup(x => x.GetClient((int)PrinterBackend.PrusaLink)).Returns(new PlainClient());
        return backends;
    }

    private static JsonDocument ReadOk(IActionResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private sealed record CatalogMeasurement(int Catalog, int Authority, int Legacy);

    private sealed class CopyCapableClient : IBackendClient, ISupportsPrinterFileCopy,
        ISupportsPrinterFileEnrichment, ISupportsFileInventory
    {
        public Task<PrinterFileEnrichmentContent> EnrichFileAsync(
            string baseUrl, PrinterFileInventoryEntry observation, PrinterCredential? credential,
            bool captureThumbnail, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PrinterFileInventoryResult> GetFileInventoryAsync(
            string baseUrl, PrinterCredential? credential = null, PrinterFileInventoryOptions? options = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PrinterFileCopyBytes> CopyFileToAsync(
            string baseUrl, string storageRoot, string relativePath, PrinterCredential? credential,
            Stream destination, long maximumBytes, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class PlainClient : IBackendClient;

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public void Reset() => CommandCount = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, AppDbContext db, IBackendClientFactory? backendClients)
        {
            _connection = connection;
            Db = db;
            Access.Setup(x => x.CanReadAsync(Actor, Visible, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Access.Setup(x => x.GetCatalogAuthorityAsync(Actor, It.IsAny<IReadOnlyCollection<Guid>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PrinterFileCatalogAuthority(
                    new HashSet<Guid> { Visible }, new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>()));
            Access.Setup(x => x.FilterAccessibleLocalAsync(Actor, It.IsAny<IReadOnlyCollection<Guid>>(),
                    It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HashSet<Guid>());
            Controller = CreateController(Access.Object, Resources.Object, backendClients);
        }

        public Guid Actor { get; } = Guid.NewGuid();

        public Guid Visible { get; } = Guid.NewGuid();

        public Guid Hidden { get; } = Guid.NewGuid();

        public AppDbContext Db { get; }

        public Mock<IPrinterFileAccessService> Access { get; } = new();

        public Mock<IQueueResourceAuthorizationService> Resources { get; } = new();

        public PrinterFileCatalogController Controller { get; }

        public PrinterFileCatalogController CreateController(
            IPrinterFileAccessService access,
            IQueueResourceAuthorizationService resources,
            IBackendClientFactory? backendClients = null,
            PrinterFileCatalogService? catalog = null) =>
            new(Db, access, resources, backendClients, catalog)
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

        public async Task<Guid> AddPrinterAsync(
            string name,
            PrinterBackend backend = PrinterBackend.Moonraker,
            bool enabled = true,
            Guid? groupId = null)
        {
            Printer template = await Db.Printers.AsNoTracking().SingleAsync(x => x.Id == Visible);
            Printer printer = new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                ServerUrl = $"http://{name}-{Guid.NewGuid():N}.invalid",
                Backend = (int)backend,
                IsEnabled = enabled,
                ManufacturerId = template.ManufacturerId,
                ModelId = template.ModelId,
                PrinterGroupId = groupId,
            };
            Db.Printers.Add(printer);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return printer.Id;
        }

        public async Task<Guid> SettleEnrichmentAsync(PrinterFileLocation location, DateTime nextAttemptAtUtc)
        {
            PrinterFileThumbnailArtifact artifact = new()
            {
                Id = Guid.NewGuid(),
                ContentSha256 = new string('A', 64),
                ContentType = "image/png",
                Width = 1,
                Height = 1,
                SizeBytes = 8,
                CreatedAtUtc = DateTime.UtcNow,
            };
            Db.PrinterFileThumbnailArtifacts.Add(artifact);
            Db.PrinterFileEnrichments.Add(new PrinterFileEnrichment
            {
                LocationId = location.Id,
                SourceIdentity = PrinterFileEnrichmentIdentity.For(location),
                ExtractorVersion = PrinterFileEnrichmentLimits.ExtractorVersion,
                MetadataState = PrinterFileEnrichmentState.Ready,
                ThumbnailState = PrinterFileEnrichmentState.Ready,
                MetadataSource = "native",
                AcquiredAtUtc = DateTime.UtcNow,
                NextAttemptAtUtc = nextAttemptAtUtc,
                ArtifactId = artifact.Id,
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return artifact.Id;
        }

        public async Task<PrinterFileLocation> AddLocationAsync(Guid printerId, string relativePath)
        {
            PrinterFileLocation location = new()
            {
                Id = Guid.NewGuid(),
                SourcePrinterId = printerId,
                RegisteredPrinterId = printerId,
                StorageRoot = "gcodes",
                RelativePath = relativePath,
                IdentityKey = PrinterInventoryPath.ComputeIdentity("gcodes", relativePath),
                DisplayName = Path.GetFileName(relativePath),
            };
            Db.PrinterFileLocations.Add(location);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return location;
        }

        public static async Task<Fixture> CreateAsync(
            IInterceptor? interceptor = null,
            IBackendClientFactory? backendClients = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection);
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }

            var db = new AppDbContext(options.Options);
            var fixture = new Fixture(connection, db, backendClients);
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

    private sealed class ScanWriteConflictInterceptor : SaveChangesInterceptor
    {
        public bool Fail { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new DbUpdateConcurrencyException("simulated scan lease revision conflict");
            }

            return ValueTask.FromResult(result);
        }
    }
}
