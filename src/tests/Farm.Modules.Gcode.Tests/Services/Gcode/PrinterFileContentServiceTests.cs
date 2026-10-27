using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Modules.Gcode.Controllers;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Modules.Gcode.Tests.Services.Gcode;

public sealed class PrinterFileContentServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData("ready", true)]
    [InlineData("printing", true)]
    [InlineData(null, false)]
    public async Task PreviewAndThumbnail_ServePersistedEnrichmentWithZeroPrinterIo(string? activity, bool online)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Activity = activity;
        fixture.Online = online;
        fixture.PrinterUnreachable = !online;
        fixture.Location.Metadata = new PrinterPrintMetadata { EstimatedPrintTimeSeconds = 123, LayerHeightMm = 0.2 };
        fixture.Location.DetailsFetchedAtUtc = DateTime.UtcNow;
        await fixture.Db.SaveChangesAsync();
        Guid artifact = await fixture.SeedArtifactAsync();

        PrinterFilePreviewDto preview = await fixture.PreviewAsync();
        PrinterFilePreviewContent image = await fixture.ThumbnailAsync(artifact);
        PrinterFilePreviewContent unpinned = await fixture.ThumbnailAsync(null);

        Assert.True(preview.HasImage);
        Assert.Equal(artifact, preview.ThumbnailVersion);
        Assert.Equal(
            $"/api/printer-files/locations/{fixture.Location.Id}/thumbnail?observationRevision=1&version={artifact}",
            preview.ThumbnailUrl);
        Assert.Equal(PrinterFilePreviewState.Available, preview.State);
        Assert.Equal(PrinterFileEnrichmentState.Ready, preview.MetadataState);
        Assert.Equal(PrinterFileEnrichmentState.Ready, preview.ThumbnailState);
        Assert.Equal(PrinterFileEnrichmentLimits.ExtractorVersion, preview.ExtractorVersion);
        Assert.False(preview.IsStale);
        Assert.Null(preview.ReasonCode);
        Assert.Equal(123d, preview.PrintTimeSeconds);
        Assert.Equal(0.2, preview.LayerHeightMm);
        Assert.Equal(Fixture.Png, image.Image);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(Fixture.Png, unpinned.Image);
        fixture.AssertNoPrinterIo();
    }

    [Fact]
    public async Task ThumbnailAsync_ReplicaAfterRestartServesSharedArtifactWhilePrinterUnreachable()
    {
        Guid artifact;
        Guid locationId;
        await using Fixture fixture = await Fixture.CreateAsync();
        artifact = await fixture.SeedArtifactAsync();
        locationId = fixture.Location.Id;
        fixture.PrinterUnreachable = true;
        fixture.Online = false;

        PrinterFileContentService replica = fixture.CreateReplica();
        PrinterFilePreviewDto preview = await replica.PreviewAsync(fixture.Actor, locationId, 1, Ct);
        PrinterFilePreviewContent image = await replica.ThumbnailAsync(fixture.Actor, locationId, 1, artifact, Ct);

        Assert.Equal(artifact, preview.ThumbnailVersion);
        Assert.Equal(Fixture.Png, image.Image);
        fixture.AssertNoPrinterIo();
    }

    [Fact]
    public async Task ThumbnailAsync_VersionMismatchIsUnavailableWithoutServingOtherBytes()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SeedArtifactAsync();

        PrinterFileCommandException error = await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.ThumbnailAsync(Guid.NewGuid()));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("thumbnail_version_changed", error.Reason);
        fixture.AssertNoPrinterIo();
    }

    [Fact]
    public async Task ThumbnailAsync_MissingLocalFileSchedulesBackgroundRepairWithoutPrinterFallback()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid artifact = await fixture.SeedArtifactAsync();
        File.Delete(fixture.ImagePath(artifact));

        PrinterFileCommandException error = await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.ThumbnailAsync(artifact));
        PrinterFilePreviewDto preview = await fixture.PreviewAsync();

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("thumbnail_unavailable", error.Reason);
        PrinterFileEnrichment row = await fixture.EnrichmentAsync();
        Assert.Null(row.ArtifactId);
        Assert.Equal(PrinterFileEnrichmentState.Pending, row.ThumbnailState);
        Assert.Equal("thumbnail_missing", row.ThumbnailReason);
        Assert.True(row.NextAttemptAtUtc <= DateTime.UtcNow);
        Assert.False(preview.HasImage);
        Assert.Null(preview.ThumbnailUrl);
        Assert.Equal(PrinterFileEnrichmentState.Pending, preview.ThumbnailState);
        Assert.Equal("thumbnail_missing", preview.ReasonCode);
        fixture.AssertNoPrinterIo();
    }

    [Fact]
    public async Task ThumbnailAsync_NoArtifactIsUnavailable()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        PrinterFileCommandException error = await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.ThumbnailAsync(null));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("thumbnail_unavailable", error.Reason);
        fixture.AssertNoPrinterIo();
    }

    [Fact]
    public async Task PreviewAsync_WithoutEnrichmentReportsPendingAndDurableSize()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        PrinterFilePreviewDto preview = await fixture.PreviewAsync();

        Assert.False(preview.HasImage);
        Assert.Null(preview.ThumbnailUrl);
        Assert.Null(preview.ThumbnailVersion);
        Assert.Equal(PrinterFilePreviewState.Available, preview.State);
        Assert.Equal(4, preview.SizeBytes);
        Assert.Equal(PrinterFileEnrichmentState.Pending, preview.MetadataState);
        Assert.Equal(PrinterFileEnrichmentState.Pending, preview.ThumbnailState);
        Assert.Equal("image_unavailable", preview.ReasonCode);
        Assert.Null(preview.ExtractorVersion);
        Assert.Null(preview.NextAttemptAtUtc);
        Assert.False(preview.IsStale);
        fixture.AssertNoPrinterIo();
    }

    [Fact]
    public async Task PreviewAsync_UnsupportedBackendReportsSettledUnsupported()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Backends.Setup(x => x.IsBackendSupported(It.IsAny<PrinterBackend>())).Returns(false);

        PrinterFilePreviewDto preview = await fixture.PreviewAsync();

        Assert.Equal(PrinterFilePreviewState.Unsupported, preview.State);
        Assert.Equal("preview_unsupported", preview.ReasonCode);
        Assert.Equal(PrinterFileEnrichmentState.Unsupported, preview.MetadataState);
        Assert.Equal(PrinterFileEnrichmentState.Unsupported, preview.ThumbnailState);
        Assert.Equal("enrichment_unsupported", preview.MetadataReason);
        fixture.AssertNoPrinterIo();
    }

    [Fact]
    public async Task PreviewAsync_FailedSameSourceRefreshServesPreviousImageAsStale()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid artifact = await fixture.SeedArtifactAsync(row =>
        {
            row.ThumbnailState = PrinterFileEnrichmentState.Failed;
            row.ThumbnailReason = "transfer_failed";
            row.ConsecutiveFailures = 1;
            row.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(1);
        });

        PrinterFilePreviewDto preview = await fixture.PreviewAsync();
        PrinterFilePreviewContent image = await fixture.ThumbnailAsync(artifact);

        Assert.True(preview.HasImage);
        Assert.True(preview.IsStale);
        Assert.Equal(PrinterFileEnrichmentState.Failed, preview.ThumbnailState);
        Assert.Equal("transfer_failed", preview.ThumbnailReason);
        Assert.Equal(Fixture.Png, image.Image);
        fixture.AssertNoPrinterIo();
    }

    [Fact]
    public async Task ContentAsync_RevokedAuthorityCannotReadPersistedPreviewThumbnailOrDownload()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid artifact = await fixture.SeedArtifactAsync();
        fixture.CanRead = false;

        Assert.Equal(404, (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.PreviewAsync())).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.ThumbnailAsync(artifact))).StatusCode);
        using MemoryStream target = new();
        Assert.Equal(404, (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.DownloadAsync(target))).StatusCode);
        Assert.Equal(0, target.Length);
        fixture.AssertNoPrinterIo();
    }

    [Fact]
    public async Task DownloadAsync_ReadOnlyActorCannotDownload()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.CanCopy = false;
        using MemoryStream target = new();
        Assert.Equal(404, (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.DownloadAsync(target))).StatusCode);
        Assert.Equal(0, fixture.Copies);
        Assert.Equal(0, fixture.Inventories);
    }

    [Fact]
    public async Task ContentAsync_StaleRevisionRejectsAndChangedSourceHidesPreviousEnrichment()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid artifact = await fixture.SeedArtifactAsync();
        fixture.Location.ObservationRevision++;
        fixture.Location.ModifiedValue = "2";
        fixture.Location.SizeBytes++;
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(409, (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.Service.PreviewAsync(fixture.Actor, fixture.Location.Id, 1, Ct))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.Service.ThumbnailAsync(fixture.Actor, fixture.Location.Id, 1, artifact, Ct))).StatusCode);
        using MemoryStream target = new();
        Assert.Equal(409, (await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            fixture.Service.DownloadAsync(fixture.Actor, fixture.Location.Id, 1, _ => target, Ct))).StatusCode);
        Assert.Equal("thumbnail_unavailable", (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.ThumbnailAsync(artifact))).Reason);
        PrinterFilePreviewDto preview = await fixture.PreviewAsync();
        Assert.False(preview.HasImage);
        Assert.Null(preview.ThumbnailVersion);
        Assert.Equal(PrinterFileEnrichmentState.Pending, preview.MetadataState);
        Assert.Equal(artifact, (await fixture.EnrichmentAsync()).ArtifactId);
        fixture.AssertNoPrinterIo();
    }

    [Fact]
    public async Task ContentAsync_ChangedConfigurationRejectsPersistedContent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid artifact = await fixture.SeedArtifactAsync();
        fixture.Printer.ServerUrl = "http://changed.invalid";
        await fixture.Db.SaveChangesAsync();

        Assert.Equal("source_observation_changed", (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.PreviewAsync())).Reason);
        Assert.Equal("source_observation_changed", (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.ThumbnailAsync(artifact))).Reason);
        fixture.AssertNoPrinterIo();
    }

    [Fact]
    public async Task DownloadAsync_StreamsWithoutPersistentCopyAndReleasesLease()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using MemoryStream target = new();
        await fixture.DownloadAsync(target);
        Assert.Equal("G28\n"u8.ToArray(), target.ToArray());
        Assert.Equal(1, fixture.Copies);
        Assert.Equal(2, fixture.Inventories);
        Assert.Empty(await fixture.Db.GcodeFiles.ToListAsync());
        Assert.Empty(await fixture.Db.PrintJobs.ToListAsync());
        Assert.Empty(await fixture.Db.PrinterFileOperations.ToListAsync());
        Assert.Empty(await fixture.Db.PrinterFileThumbnailArtifacts.ToListAsync());
        Assert.Equal(0, fixture.Enrichments);
        fixture.Lease.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DownloadAsync_ChangedRemoteObservationRejectsBeforeResponse()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.RemoteModified = "changed";
        using MemoryStream target = new();
        Assert.Equal("source_changed", (await Assert.ThrowsAsync<PrinterFileCopyException>(
            () => fixture.DownloadAsync(target))).Reason);
        Assert.Equal(0, target.Length);
        Assert.Equal(0, fixture.Copies);
        fixture.Lease.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DownloadAsync_ActivityChangesBeforeChunkRefusesWrite()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.BeforeCopy = () => fixture.Activity = "printing";
        using MemoryStream target = new();
        await Assert.ThrowsAsync<PrinterFileCommandException>(() => fixture.DownloadAsync(target));
        Assert.Equal(0, target.Length);
        Assert.True(target.CanWrite);
        fixture.Lease.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DownloadAsync_CancelledLeaseRefusesBytesAndReleasesOwnership()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using CancellationTokenSource cancellation = new();
        fixture.Lease.SetupGet(x => x.Token).Returns(cancellation.Token);
        fixture.BeforeCopy = cancellation.Cancel;
        using MemoryStream target = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.DownloadAsync(target));
        Assert.Equal(0, target.Length);
        fixture.Lease.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Theory]
    [InlineData(200_000_001)]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task DownloadAsync_InvalidDeclaredSizeRejectsBeforeIo(long size)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Location.SizeBytes = size;
        await fixture.Db.SaveChangesAsync();
        using MemoryStream target = new();
        Assert.Equal(413, (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.DownloadAsync(target))).StatusCode);
        Assert.Equal(0, fixture.Inventories);
        Assert.Equal(0, target.Length);
    }

    [Fact]
    public async Task ControllerAsync_ThumbnailReturnsPrivateImageWithStrongContentEtag()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid artifact = await fixture.SeedArtifactAsync();
        DefaultHttpContext context = fixture.HttpContext();
        PrinterFileContentController controller = new(fixture.Service)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };

        FileContentResult result = Assert.IsType<FileContentResult>(
            await controller.ThumbnailAsync(fixture.Location.Id, 1, artifact, Ct));

        Assert.Equal(Fixture.Png, result.FileContents);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal($"\"{Convert.ToHexStringLower(SHA256.HashData(Fixture.Png))}\"", result.EntityTag!.Tag.Value);
        Assert.False(result.EntityTag.IsWeak);
        Assert.Equal("private, no-store", context.Response.Headers.CacheControl);
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions);
        fixture.AssertNoPrinterIo();
    }

    [Fact]
    public async Task ControllerAsync_ThumbnailVersionMismatchReturnsNotFoundProblem()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SeedArtifactAsync();
        PrinterFileContentController controller = new(fixture.Service)
        {
            ControllerContext = new ControllerContext { HttpContext = fixture.HttpContext() },
        };

        ObjectResult result = Assert.IsType<ObjectResult>(
            await controller.ThumbnailAsync(fixture.Location.Id, 1, Guid.NewGuid(), Ct));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("thumbnail_version_changed", Assert.IsType<ProblemDetails>(result.Value).Extensions["code"]);
    }

    [Fact]
    public async Task ControllerAsync_RequiresSessionAndCopyPolicyAndReturnsSafeAttachment()
    {
        Assert.Contains(typeof(PrinterFileContentController).GetCustomAttributes(typeof(AuthorizeAttribute), true),
            value => ((AuthorizeAttribute)value).Policy == "RequireInteractiveSession");
        Assert.Contains(typeof(PrinterFileContentController).GetMethod(nameof(PrinterFileContentController.DownloadAsync))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), true), value => ((AuthorizeAttribute)value).Policy == "ModelWrite");
        await using Fixture fixture = await Fixture.CreateAsync();
        DefaultHttpContext context = fixture.HttpContext();
        using MemoryStream target = new();
        context.Response.Body = target;
        PrinterFileContentController controller = new(fixture.Service)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
        Assert.IsType<EmptyResult>(await controller.DownloadAsync(fixture.Location.Id, 1, Ct));
        Assert.Contains("attachment", context.Response.Headers.ContentDisposition.ToString());
        Assert.Contains("part.gcode", context.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("private, no-store", context.Response.Headers.CacheControl);
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions);
        Assert.Null(context.Response.ContentLength);
        Assert.Equal("name.gcode", PrinterFileContentService.AttachmentName("folder/name\r\n.gcode"));
    }

    [Fact]
    public async Task ControllerAsync_FailureAfterResponseStartedAbortsRatherThanSendingJson()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DefaultHttpContext context = fixture.HttpContext();
        Mock<IHttpResponseFeature> response = new();
        response.SetupGet(x => x.Headers).Returns(new HeaderDictionary());
        response.SetupGet(x => x.HasStarted).Returns(true);
        Mock<IHttpRequestLifetimeFeature> lifetime = new();
        context.Features.Set(response.Object);
        context.Features.Set(lifetime.Object);
        PrinterFileContentController controller = new(fixture.Service)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
        Assert.IsType<EmptyResult>(await controller.DownloadAsync(fixture.Location.Id, 999, Ct));
        lifetime.Verify(x => x.Abort(), Times.Once);
        Assert.Equal(0, fixture.Copies);
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Join(Path.GetTempPath(), $"printer-content-{Guid.NewGuid():N}");
        private readonly List<AppDbContext> _contexts = [];
        private readonly Mock<IPrintersRepository> _printers = new();
        private readonly Mock<IPrinterFileAccessService> _access = new();

        public static byte[] Png { get; } = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        public Guid Actor { get; } = Guid.NewGuid();

        public AppDbContext Db { get; private set; } = null!;

        public Printer Printer { get; private set; } = null!;

        public PrinterFileLocation Location { get; private set; } = null!;

        public Mock<IPrinterFileLeaseCoordinator> Leases { get; } = new();

        public Mock<IPrinterFileLease> Lease { get; } = new();

        public Mock<IPrinterStatusCacheReader> Status { get; } = new();

        public Mock<IBackendClientFactory> Backends { get; } = new();

        public PrinterFileContentService Service { get; private set; } = null!;

        public bool CanRead { get; set; } = true;

        public bool CanCopy { get; set; } = true;

        public string? Activity { get; set; } = "ready";

        public bool Online { get; set; } = true;

        public bool PrinterUnreachable { get; set; }

        public string RemoteModified { get; set; } = "1";

        public Action? BeforeCopy { get; set; }

        public int Enrichments { get; private set; }

        public int LegacyPreviews { get; private set; }

        public int Copies { get; private set; }

        public int Inventories { get; private set; }

        private string StorageRoot => Path.Join(_directory, "storage");

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            Directory.CreateDirectory(fixture.StorageRoot);
            await using (AppDbContext setup = fixture.CreateFactory().CreateDbContext())
            {
                await setup.Database.EnsureCreatedAsync();
                setup.AppSettingsEntities.Add(new()
                {
                    Key = PrinterFileIndexSettings.SectionName,
                    SettingsJson = JsonSerializer.Serialize(new PrinterFileIndexSettings
                    {
                        ThumbnailCacheMaxBytes = 64L * 1024 * 1024,
                    }),
                });
                await setup.SaveChangesAsync();
            }

            fixture.Db = fixture.CreateContext();
            Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = "Content test" };
            fixture.Printer = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer",
                ServerUrl = "http://printer.invalid",
                Backend = (int)PrinterBackend.Moonraker,
                IsEnabled = true,
                Manufacturer = manufacturer,
                Model = new PrinterModel { Id = Guid.NewGuid(), Name = "Content test", Manufacturer = manufacturer },
            };
            fixture.Location = new()
            {
                Id = Guid.NewGuid(),
                SourcePrinterId = fixture.Printer.Id,
                RegisteredPrinterId = fixture.Printer.Id,
                StorageRoot = "gcodes",
                RelativePath = "folder/part.gcode",
                DisplayName = "part.gcode",
                SizeBytes = 4,
                ModifiedValue = "1",
                ObservationRevision = 1,
                ConfigurationGeneration = 1,
                Availability = PrinterFileAvailability.Available,
                IdentityKey = PrinterInventoryPath.ComputeIdentity("gcodes", "folder/part.gcode"),
            };
            fixture.Db.AddRange(fixture.Printer, fixture.Location, new PrinterFileScanState
            {
                Id = fixture.Printer.Id,
                SourcePrinterId = fixture.Printer.Id,
                RegisteredPrinterId = fixture.Printer.Id,
                ConfigurationFingerprint = PrinterFileIndexPolicy.ConfigurationFingerprint(fixture.Printer),
            });
            await fixture.Db.SaveChangesAsync();
            fixture.Configure();
            fixture.Service = fixture.CreateService(fixture.Db);
            return fixture;
        }

        public Task<PrinterFilePreviewDto> PreviewAsync() =>
            Service.PreviewAsync(Actor, Location.Id, Location.ObservationRevision, Ct);

        public Task<PrinterFilePreviewContent> ThumbnailAsync(Guid? version) =>
            Service.ThumbnailAsync(Actor, Location.Id, Location.ObservationRevision, version, Ct);

        public Task DownloadAsync(Stream response) =>
            Service.DownloadAsync(Actor, Location.Id, Location.ObservationRevision, _ => response, Ct);

        public string ImagePath(Guid artifact) =>
            Path.Join(StorageRoot, PrinterFileThumbnailStore.DirectoryName, artifact.ToString("N") + ".image");

        /// <summary>A fresh process: new context, store and service over the same database and directory.</summary>
        public PrinterFileContentService CreateReplica() => CreateService(CreateContext());

        public async Task<Guid> SeedArtifactAsync(Action<PrinterFileEnrichment>? configure = null)
        {
            PrinterFileThumbnailStore store = CreateStore();
            PrinterFileThumbnailReservationResult reserved = await store.ReserveAsync(Ct);
            Guid artifact = await store.WriteAsync(reserved.ReservationId!.Value, Png, "image/png", Ct);
            DateTime now = DateTime.UtcNow;
            PrinterFileEnrichment row = new()
            {
                LocationId = Location.Id,
                SourceIdentity = PrinterFileEnrichmentIdentity.For(Location),
                ExtractorVersion = PrinterFileEnrichmentLimits.ExtractorVersion,
                MetadataState = PrinterFileEnrichmentState.Ready,
                ThumbnailState = PrinterFileEnrichmentState.Ready,
                AcquiredAtUtc = now,
                LastAttemptAtUtc = now,
                NextAttemptAtUtc = now + PrinterFileEnrichmentLimits.RevalidationInterval,
                ArtifactId = artifact,
            };
            configure?.Invoke(row);
            await using AppDbContext db = CreateFactory().CreateDbContext();
            db.PrinterFileEnrichments.Add(row);
            await db.SaveChangesAsync();
            return artifact;
        }

        public async Task<PrinterFileEnrichment> EnrichmentAsync()
        {
            await using AppDbContext db = CreateFactory().CreateDbContext();
            return await db.PrinterFileEnrichments.AsNoTracking().SingleAsync(x => x.LocationId == Location.Id);
        }

        public DefaultHttpContext HttpContext()
        {
            DefaultHttpContext context = new();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Actor.ToString())], "test"));
            return context;
        }

        /// <summary>Local reads must never contact the printer, take a lease or consult live activity.</summary>
        public void AssertNoPrinterIo()
        {
            Assert.Equal(0, Enrichments);
            Assert.Equal(0, LegacyPreviews);
            Assert.Equal(0, Inventories);
            Assert.Equal(0, Copies);
            Leases.VerifyNoOtherCalls();
            Status.Verify(x => x.GetSnapshot(It.IsAny<Guid>()), Times.Never);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (AppDbContext context in _contexts)
            {
                await context.DisposeAsync();
            }

            Directory.Delete(_directory, recursive: true);
        }

        private void Configure()
        {
            _access.Setup(x => x.CanReadAsync(Actor, Printer.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => CanRead);
            _access.Setup(x => x.CanActAsync(Actor, Printer.Id, PrinterFileAction.Copy, null,
                It.IsAny<CancellationToken>())).ReturnsAsync(() => CanRead && CanCopy);
            Lease.SetupGet(x => x.ConfigurationGeneration).Returns(1);
            Lease.Setup(x => x.IsCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Leases.Setup(x => x.TryAcquireAsync(Printer.Id, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Lease.Object);
            Status.Setup(x => x.GetSnapshot(Printer.Id)).Returns(() => new PrinterStatusCacheSnapshot(
                new PrinterStatusDto(Printer.Id, Online, "Idle") { RawActivityState = Activity },
                DateTime.UtcNow));
            Mock<IBackendClient> backend = new();
            backend.As<ISupportsPrinterFileEnrichment>().Setup(x => x.EnrichFileAsync(
                    It.IsAny<string>(), It.IsAny<PrinterFileInventoryEntry>(), It.IsAny<PrinterCredential?>(),
                    It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback(() => Enrichments++)
                .ThrowsAsync(new HttpRequestException("printer contacted"));
            backend.As<ISupportsPrinterFilePreview>().Setup(x => x.GetFilePreviewAsync(
                    It.IsAny<string>(), It.IsAny<PrinterFileInventoryEntry>(), It.IsAny<PrinterCredential?>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => LegacyPreviews++)
                .ThrowsAsync(new HttpRequestException("printer contacted"));
            backend.As<ISupportsFileInventory>().Setup(x => x.GetFileInventoryAsync(
                    It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<PrinterFileInventoryOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Inventories++;
                    return PrinterUnreachable
                        ? Task.FromException<PrinterFileInventoryResult>(new HttpRequestException("unreachable"))
                        : Task.FromResult(new PrinterFileInventoryResult(
                            PrinterFileInventoryOutcome.Complete, PrinterFileInventoryReason.None,
                            [new("gcodes", Location.RelativePath, 4, ModifiedValue: RemoteModified)],
                            [new("gcodes", string.Empty, true, PrinterFileInventoryEvidence.NativeRecursiveListing)]));
                });
            backend.As<ISupportsPrinterFileCopy>().Setup(x => x.CopyFileToAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PrinterCredential?>(),
                    It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns(async (string _, string _, string _, PrinterCredential? _, Stream target, long limit, CancellationToken ct) =>
                {
                    Copies++;
                    BeforeCopy?.Invoke();
                    using MemoryStream source = new("G28\n"u8.ToArray());
                    return await PrinterFileCopyTransport.CopyBoundedAsync(source, target, limit, 4, ct);
                });
            Backends.Setup(x => x.IsBackendSupported(It.IsAny<PrinterBackend>())).Returns(true);
            Backends.Setup(x => x.GetClient(It.IsAny<int>())).Returns(backend.Object);
            _printers.Setup(x => x.FindByIdWithIncludesAsync(Printer.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Printer);
        }

        private PrinterFileContentService CreateService(AppDbContext db) =>
            new(db, _access.Object, Leases.Object, Status.Object, Backends.Object, _printers.Object,
                CreateStore(), Options.Create(new BackendTimeoutSettings()));

        private PrinterFileThumbnailStore CreateStore()
        {
            Mock<IStoragePathService> paths = new();
            paths.Setup(x => x.GetGcodeStorageDirectory()).Returns(StorageRoot);
            return new(CreateFactory(), paths.Object, NullLogger<PrinterFileThumbnailStore>.Instance);
        }

        private AppDbContext CreateContext()
        {
            AppDbContext context = CreateFactory().CreateDbContext();
            _contexts.Add(context);
            return context;
        }

        private Factory CreateFactory() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Join(_directory, "test.db")};Pooling=False;Default Timeout=30;Foreign Keys=True")
            .Options);
    }
}
