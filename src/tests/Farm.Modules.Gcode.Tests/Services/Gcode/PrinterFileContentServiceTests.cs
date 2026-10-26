using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Farm.Modules.Gcode.Controllers;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Modules.Gcode.Tests.Services.Gcode;

public sealed class PrinterFileContentServiceTests
{
    [Fact]
    public async Task PreviewAsync_CachesCaptureAndThumbnailDoesNotFetch()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        PrinterFileCommandException missing = await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.Service.ThumbnailAsync(fixture.Actor, fixture.Location.Id, 1, CancellationToken.None));
        Assert.Equal(404, missing.StatusCode);
        Assert.Equal(0, fixture.Fetches);
        PrinterFilePreviewDto first = await fixture.PreviewAsync();
        Assert.Equal("Available", first.State);
        Assert.True(first.HasImage);
        Assert.Equal(123, first.PrintTimeSeconds);
        Assert.Equal(0.2, first.LayerHeightMm);
        Assert.NotNull(first.CapturedAtUtc);
        fixture.Activity = "printing"; // Cached bytes need authority, not another printer request.
        Assert.Equal(first, await fixture.PreviewAsync());
        PrinterFilePreviewContent image = await fixture.Service.ThumbnailAsync(
            fixture.Actor, fixture.Location.Id, 1, CancellationToken.None);
        Assert.Equal(Fixture.Png, image.Image);
        Assert.Equal(1, fixture.Fetches);
        fixture.Lease.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mystery")]
    [InlineData("printing")]
    [InlineData("paused")]
    public async Task PreviewAsync_UnknownOrBusyActivityDefersWithoutNetwork(string? activity)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Activity = activity;
        PrinterFilePreviewDto result = await fixture.PreviewAsync();
        Assert.Equal("Deferred", result.State);
        Assert.Null(result.CapturedAtUtc);
        Assert.Equal(0, fixture.Fetches);
        fixture.Leases.Verify(x => x.TryAcquireAsync(It.IsAny<Guid>(), true, It.IsAny<CancellationToken>()), Times.Never);
        using MemoryStream target = new();
        Assert.Equal("waiting_for_confirmed_idle", (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.DownloadAsync(target))).Reason);
        Assert.Equal(0, target.Length);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, -5)]
    [InlineData(true, 5)]
    public async Task PreviewAsync_OfflineStaleOrFutureStatusDefers(bool online, int ageMinutes)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Online = online;
        fixture.StatusAgeMinutes = ageMinutes;
        Assert.Equal("Deferred", (await fixture.PreviewAsync()).State);
        Assert.Equal(0, fixture.Fetches);
    }

    [Fact]
    public async Task PreviewAsync_ContendedLeaseDefersWithoutNetwork()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Leases.Setup(x => x.TryAcquireAsync(fixture.Printer.Id, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPrinterFileLease?)null);
        Assert.Equal("Deferred", (await fixture.PreviewAsync()).State);
        using MemoryStream target = new();
        Assert.Equal("source_busy", (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.DownloadAsync(target))).Reason);
        Assert.Equal(0, fixture.Fetches);
    }

    [Fact]
    public async Task ContentAsync_RevokedAuthorityCannotReadWarmCacheOrDownload()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.PreviewAsync();
        fixture.CanRead = false;
        Assert.Equal(404, (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.PreviewAsync())).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            fixture.Service.ThumbnailAsync(fixture.Actor, fixture.Location.Id, 1, CancellationToken.None))).StatusCode);
        using MemoryStream target = new();
        Assert.Equal(404, (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.DownloadAsync(target))).StatusCode);
        Assert.Equal(1, fixture.Fetches);
        Assert.Equal(0, target.Length);
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
    public async Task ContentAsync_StaleRevisionRejectsAndNewObservationInvalidatesCache()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.PreviewAsync();
        fixture.Location.ObservationRevision++;
        fixture.Location.ModifiedValue = "2";
        fixture.Location.SizeBytes++;
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(409, (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.Service.PreviewAsync(fixture.Actor, fixture.Location.Id, 1, CancellationToken.None))).StatusCode);
        using MemoryStream target = new();
        Assert.Equal(409, (await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            fixture.Service.DownloadAsync(fixture.Actor, fixture.Location.Id, 1, _ => target, CancellationToken.None))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            fixture.Service.ThumbnailAsync(fixture.Actor, fixture.Location.Id, 2, CancellationToken.None))).StatusCode);
        await fixture.PreviewAsync();
        Assert.Equal(2, fixture.Fetches);
    }

    [Fact]
    public async Task PreviewAsync_ChangedConfigurationRejectsWarmCache()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.PreviewAsync();
        fixture.Printer.ServerUrl = "http://changed.invalid";
        await fixture.Db.SaveChangesAsync();
        Assert.Equal("source_observation_changed", (await Assert.ThrowsAsync<PrinterFileCommandException>(
            () => fixture.PreviewAsync())).Reason);
        Assert.Equal(1, fixture.Fetches);
    }

    [Fact]
    public async Task PreviewAsync_UnsupportedBackendDoesNotInventCapture()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Backends.Setup(x => x.GetClient(It.IsAny<int>())).Returns(Mock.Of<IBackendClient>());
        PrinterFilePreviewDto result = await fixture.PreviewAsync();
        Assert.Equal("Unsupported", result.State);
        Assert.False(result.HasImage);
        Assert.Null(result.CapturedAtUtc);
        Assert.Null(result.PrintTimeSeconds);
        Assert.Null(result.LayerHeightMm);
        Assert.Equal(0, fixture.Fetches);
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
        Assert.False(Directory.Exists(fixture.Root));
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
    public async Task ControllerAsync_RequiresSessionAndCopyPolicyAndReturnsSafeAttachment()
    {
        Assert.Contains(typeof(PrinterFileContentController).GetCustomAttributes(typeof(AuthorizeAttribute), true),
            value => ((AuthorizeAttribute)value).Policy == "RequireInteractiveSession");
        Assert.Contains(typeof(PrinterFileContentController).GetMethod(nameof(PrinterFileContentController.DownloadAsync))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), true), value => ((AuthorizeAttribute)value).Policy == "ModelWrite");
        await using Fixture fixture = await Fixture.CreateAsync();
        DefaultHttpContext context = new();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, fixture.Actor.ToString())], "test"));
        using MemoryStream target = new();
        context.Response.Body = target;
        PrinterFileContentController controller = new(fixture.Service)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
        Assert.IsType<EmptyResult>(await controller.DownloadAsync(fixture.Location.Id, 1, CancellationToken.None));
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
        DefaultHttpContext context = new();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, fixture.Actor.ToString())], "test"));
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
        Assert.IsType<EmptyResult>(await controller.DownloadAsync(fixture.Location.Id, 999, CancellationToken.None));
        lifetime.Verify(x => x.Abort(), Times.Once);
        Assert.Equal(0, fixture.Copies);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public static byte[] Png { get; } = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        public Guid Actor { get; } = Guid.NewGuid();
        public string Root { get; } = Path.Join(Path.GetTempPath(), $"printer-preview-{Guid.NewGuid():N}");
        public AppDbContext Db { get; } = new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Printer Printer { get; } = new()
        {
            Id = Guid.NewGuid(),
            Name = "Printer",
            ServerUrl = "http://printer.invalid",
            Backend = (int)PrinterBackend.Moonraker,
            IsEnabled = true,
        };
        public PrinterFileLocation Location { get; private set; } = null!;
        public Mock<IPrinterFileLeaseCoordinator> Leases { get; } = new();
        public Mock<IPrinterFileLease> Lease { get; } = new();
        public Mock<IBackendClientFactory> Backends { get; } = new();
        public PrinterFileContentService Service { get; private set; } = null!;
        public bool CanRead { get; set; } = true;
        public bool CanCopy { get; set; } = true;
        public string? Activity { get; set; } = "ready";
        public bool Online { get; set; } = true;
        public int StatusAgeMinutes { get; set; }
        public string RemoteModified { get; set; } = "1";
        public Action? BeforeCopy { get; set; }
        public int Fetches { get; private set; }
        public int Copies { get; private set; }
        public int Inventories { get; private set; }

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
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
            Mock<IPrinterFileAccessService> access = new();
            access.Setup(x => x.CanReadAsync(fixture.Actor, fixture.Printer.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => fixture.CanRead);
            access.Setup(x => x.CanActAsync(fixture.Actor, fixture.Printer.Id, PrinterFileAction.Copy, null,
                It.IsAny<CancellationToken>())).ReturnsAsync(() => fixture.CanRead && fixture.CanCopy);
            fixture.Lease.SetupGet(x => x.ConfigurationGeneration).Returns(1);
            fixture.Lease.Setup(x => x.IsCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            fixture.Leases.Setup(x => x.TryAcquireAsync(fixture.Printer.Id, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(fixture.Lease.Object);
            Mock<IPrinterStatusCacheReader> status = new();
            status.Setup(x => x.GetSnapshot(fixture.Printer.Id)).Returns(() => new PrinterStatusCacheSnapshot(
                new PrinterStatusDto(fixture.Printer.Id, fixture.Online, "Idle") { RawActivityState = fixture.Activity },
                DateTime.UtcNow.AddMinutes(fixture.StatusAgeMinutes)));
            Mock<IBackendClient> backend = new();
            backend.As<ISupportsPrinterFilePreview>().Setup(x => x.GetFilePreviewAsync(
                    It.IsAny<string>(), It.IsAny<PrinterFileInventoryEntry>(), It.IsAny<PrinterCredential?>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => fixture.Fetches++)
                .ReturnsAsync(new PrinterFilePreviewContent(123, 0.2, Png, "image/png"));
            backend.As<ISupportsFileInventory>().Setup(x => x.GetFileInventoryAsync(
                    It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<PrinterFileInventoryOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => fixture.Inventories++)
                .ReturnsAsync(() => new PrinterFileInventoryResult(
                    PrinterFileInventoryOutcome.Complete, PrinterFileInventoryReason.None,
                    [new("gcodes", fixture.Location.RelativePath, 4, ModifiedValue: fixture.RemoteModified)],
                    [new("gcodes", "", true, PrinterFileInventoryEvidence.NativeRecursiveListing)]));
            backend.As<ISupportsPrinterFileCopy>().Setup(x => x.CopyFileToAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PrinterCredential?>(),
                    It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns(async (string _, string _, string _, PrinterCredential? _, Stream target, long limit, CancellationToken ct) =>
                {
                    fixture.Copies++;
                    fixture.BeforeCopy?.Invoke();
                    using MemoryStream source = new("G28\n"u8.ToArray());
                    return await PrinterFileCopyTransport.CopyBoundedAsync(source, target, limit, 4, ct);
                });
            fixture.Backends.Setup(x => x.IsBackendSupported(It.IsAny<PrinterBackend>())).Returns(true);
            fixture.Backends.Setup(x => x.GetClient(It.IsAny<int>())).Returns(backend.Object);
            Mock<IPrintersRepository> printers = new();
            printers.Setup(x => x.FindByIdWithIncludesAsync(fixture.Printer.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(fixture.Printer);
            fixture.Service = new(fixture.Db, access.Object, fixture.Leases.Object, status.Object,
                fixture.Backends.Object, printers.Object, new PrinterFilePreviewCache(fixture.Root),
                Options.Create(new BackendTimeoutSettings()));
            return fixture;
        }

        public Task<PrinterFilePreviewDto> PreviewAsync() =>
            Service.PreviewAsync(Actor, Location.Id, Location.ObservationRevision, CancellationToken.None);

        public Task DownloadAsync(Stream response) =>
            Service.DownloadAsync(Actor, Location.Id, Location.ObservationRevision, _ => response, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
