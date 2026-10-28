using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Modules.Gcode.Tests.Services.Gcode;

public sealed class PrinterFileEnrichmentServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task RunSliceAsync_PublishesRichMetadataAndDurableThumbnailWithoutAdvancingObservationRevision()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid id = (await fixture.AddLocationsAsync(1))[0];

        Assert.Equal(1, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        Assert.Equal(("file-00.gcode", true), Assert.Single(fixture.Client.Calls));
        PrinterFileLocation location = await fixture.LocationAsync(id);
        PrinterFileEnrichment row = await fixture.EnrichmentAsync(id);
        Assert.Equal(1, location.ObservationRevision);
        Assert.NotNull(location.DetailsFetchedAtUtc);
        Assert.Equal(3600d, location.Metadata!.EstimatedPrintTimeSeconds);
        Assert.Equal(0.2d, location.Metadata.LayerHeightMm);
        Assert.Equal(PrinterFileEnrichmentIdentity.For(location), row.SourceIdentity);
        Assert.Equal(PrinterFileEnrichmentLimits.ExtractorVersion, row.ExtractorVersion);
        Assert.Equal(PrinterFileEnrichmentState.Ready, row.MetadataState);
        Assert.Equal(PrinterFileEnrichmentState.Ready, row.ThumbnailState);
        Assert.Equal("native", row.MetadataSource);
        Assert.Equal(0, row.ConsecutiveFailures);
        Assert.True(row.NextAttemptAtUtc > DateTime.UtcNow.AddHours(23));
        PrinterFilePreviewContent? image = await fixture.Store.ReadAsync(row.ArtifactId!.Value, Ct);
        Assert.Equal(Fixture.Png, image!.Image);
        Assert.False(await fixture.Service.HasDueWorkAsync(fixture.Lease.Object, Ct));
    }

    [Fact]
    public async Task RunSliceAsync_AdapterDeadlinePreservesNativeMetadataAndThumbnail()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid id = (await fixture.AddLocationsAsync(1))[0];
        List<string> reads = [];
        fixture.Client.Handler = async (observation, capture, token) =>
        {
            // Adapter setup starts its budget after the service's outer safety deadline.
            await Task.Delay(TimeSpan.FromMilliseconds(50), token);
            PrinterFileEnrichmentSources sources = new(
                budget =>
                {
                    budget.BeginRequest();
                    reads.Add("native");
                    return Task.FromResult<PrinterFileNativeEnrichment?>(new(
                        Fixture.ReadyContent(false).Metadata,
                        ReadThumbnailAsync: imageBudget =>
                        {
                            imageBudget.BeginRequest();
                            reads.Add("thumbnail");
                            return Task.FromResult<byte[]?>(Fixture.Png);
                        }));
                },
                async (_, _, _, budget) =>
                {
                    budget.BeginRequest();
                    reads.Add("fallback");
                    await Task.Delay(Timeout.InfiniteTimeSpan, budget.Token);
                    throw new InvalidOperationException("The deadline must cancel the stalled read.");
                });
            return await PrinterFileEnrichmentAcquisition.AcquireAsync(observation, sources, capture, token);
        };

        Assert.Equal(1, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        PrinterFileLocation location = await fixture.LocationAsync(id);
        PrinterFileEnrichment row = await fixture.EnrichmentAsync(id);
        Assert.Equal(PrinterFileEnrichmentState.Partial, row.MetadataState);
        Assert.Equal("deadline", row.MetadataReason);
        Assert.Equal("native", row.MetadataSource);
        Assert.Equal(3600d, location.Metadata!.EstimatedPrintTimeSeconds);
        Assert.Equal(0.2d, location.Metadata.LayerHeightMm);
        Assert.Equal(1, location.ObservationRevision);
        Assert.Equal(PrinterFileEnrichmentState.Ready, row.ThumbnailState);
        Assert.Null(row.ThumbnailReason);
        PrinterFilePreviewContent? image = await fixture.Store.ReadAsync(row.ArtifactId!.Value, Ct);
        Assert.Equal(Fixture.Png, image!.Image);
        Assert.Equal(["native", "thumbnail", "fallback"], reads);
        Assert.Empty(await fixture.ReservationsAsync());
    }

    [Fact]
    public async Task RunSliceAsync_LeaseCancellationDiscardsAcquisitionAndReleasesReservation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid id = (await fixture.AddLocationsAsync(1))[0];
        using CancellationTokenSource cancellation = new();
        fixture.Lease.SetupGet(x => x.Token).Returns(cancellation.Token);
        fixture.Client.Handler = async (_, capture, token) =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return Fixture.ReadyContent(capture);
        };

        Assert.Equal(0, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        Assert.Single(fixture.Client.Calls);
        Assert.Empty(await fixture.EnrichmentsAsync());
        Assert.Null((await fixture.LocationAsync(id)).Metadata);
        Assert.Empty(await fixture.ReservationsAsync());
    }

    [Fact]
    public async Task RunSliceAsync_ResumesFairlyAcrossSlicesSoFailingFilesCannotStarveOthers()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        List<Guid> ids = await fixture.AddLocationsAsync(10);
        int calls = 0;
        fixture.Client.Handler = async (_, capture, _) =>
        {
            await Task.Yield();
            return ++calls <= PrinterFileEnrichmentLimits.MaximumFilesPerSlice
                ? throw new HttpRequestException("offline")
                : Fixture.ReadyContent(capture);
        };

        Assert.Equal(8, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));
        List<string> first = fixture.Client.Calls.Select(x => x.Path).ToList();
        Assert.Equal(2, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));
        List<string> second = fixture.Client.Calls.Skip(first.Count).Select(x => x.Path).ToList();
        Assert.Equal(0, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        Assert.Equal(8, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(10, first.Concat(second).Distinct().Count());
        List<PrinterFileEnrichment> rows = await fixture.EnrichmentsAsync();
        Assert.Equal(10, rows.Count);
        Assert.Equal(ids.Order(), rows.Select(x => x.LocationId).Order());
        List<PrinterFileEnrichment> failed = rows.Where(x => x.MetadataState == PrinterFileEnrichmentState.Failed).ToList();
        Assert.Equal(8, failed.Count);
        Assert.All(failed, row =>
        {
            Assert.Equal("transfer_failed", row.MetadataReason);
            Assert.Equal(PrinterFileEnrichmentState.Failed, row.ThumbnailState);
            Assert.Null(row.ArtifactId);
            Assert.Equal(1, row.ConsecutiveFailures);
            TimeSpan delay = row.NextAttemptAtUtc - row.LastAttemptAtUtc!.Value;
            Assert.InRange(delay, TimeSpan.FromSeconds(47.9), TimeSpan.FromMinutes(1));
        });
        Assert.Equal(2, rows.Count(x => x.ThumbnailState == PrinterFileEnrichmentState.Ready && x.ArtifactId is not null));
        Assert.Empty(await fixture.ReservationsAsync());
    }

    [Fact]
    public async Task RunSliceAsync_StopsWhenReportedResponseBytesExhaustTheSliceBudget()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddLocationsAsync(4);
        fixture.Client.Handler = (_, capture, _) => Task.FromResult(
            Fixture.ReadyContent(capture) with { ResponseBytes = 10L * 1024 * 1024 });

        Assert.Equal(2, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        Assert.Equal(2, fixture.Client.Calls.Count);
        Assert.True(await fixture.Service.HasDueWorkAsync(fixture.Lease.Object, Ct));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("configuration")]
    [InlineData("mutation")]
    [InlineData("missing")]
    public async Task RunSliceAsync_DiscardsAcquisitionWhenSourceAuthorityChangesBeforePublication(string change)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid id = (await fixture.AddLocationsAsync(1))[0];
        fixture.Client.Handler = async (_, capture, _) =>
        {
            await using AppDbContext db = fixture.CreateContext();
            PrinterFileLocation location = await db.PrinterFileLocations.SingleAsync(x => x.Id == id);
            switch (change)
            {
                case "source":
                    location.ModifiedValue = "2";
                    location.SizeBytes++;
                    location.ObservationRevision++;
                    break;
                case "configuration":
                    location.ConfigurationGeneration++;
                    break;
                case "mutation":
                    location.MutationGeneration++;
                    break;
                default:
                    location.Availability = PrinterFileAvailability.Missing;
                    break;
            }

            await db.SaveChangesAsync();
            return Fixture.ReadyContent(capture);
        };

        Assert.Equal(0, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        Assert.Single(fixture.Client.Calls);
        Assert.Empty(await fixture.EnrichmentsAsync());
        PrinterFileLocation location = await fixture.LocationAsync(id);
        Assert.Null(location.Metadata);
        Assert.Null(location.DetailsFetchedAtUtc);
    }

    [Fact]
    public async Task RunSliceAsync_CacheFullStillPublishesMetadataAndDefersThumbnail()
    {
        await using Fixture fixture = await Fixture.CreateAsync(PrinterFileEnrichmentLimits.MaximumImageBytes);
        Guid id = (await fixture.AddLocationsAsync(1))[0];
        PrinterFileThumbnailReservationResult filler = await fixture.Store.ReserveAsync(Ct);
        await fixture.Store.WriteAsync(filler.ReservationId!.Value, Fixture.Png, "image/png", Ct);

        Assert.Equal(1, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        Assert.Equal(("file-00.gcode", false), Assert.Single(fixture.Client.Calls));
        PrinterFileEnrichment row = await fixture.EnrichmentAsync(id);
        Assert.Equal(PrinterFileEnrichmentState.Ready, row.MetadataState);
        Assert.Equal(PrinterFileEnrichmentState.Deferred, row.ThumbnailState);
        Assert.Equal("cache_full", row.ThumbnailReason);
        Assert.Null(row.ArtifactId);
        Assert.Equal(1, row.ConsecutiveFailures);
        PrinterFileLocation location = await fixture.LocationAsync(id);
        Assert.Equal(3600d, location.Metadata!.EstimatedPrintTimeSeconds);
        Assert.Equal(1, location.ObservationRevision);
    }

    [Fact]
    public async Task RunSliceAsync_CacheFullRetryDoesNotRepeatSettledMetadataRead()
    {
        await using Fixture fixture = await Fixture.CreateAsync(PrinterFileEnrichmentLimits.MaximumImageBytes);
        Guid id = (await fixture.AddLocationsAsync(1))[0];
        PrinterFileThumbnailReservationResult filler = await fixture.Store.ReserveAsync(Ct);
        await fixture.Store.WriteAsync(filler.ReservationId!.Value, Fixture.Png, "image/png", Ct);
        await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct);
        await fixture.MakeDueAsync(id);

        Assert.Equal(1, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        Assert.Single(fixture.Client.Calls);
        PrinterFileEnrichment row = await fixture.EnrichmentAsync(id);
        Assert.Equal(PrinterFileEnrichmentState.Ready, row.MetadataState);
        Assert.Equal("cache_full", row.ThumbnailReason);
        Assert.Equal(2, row.ConsecutiveFailures);
    }

    [Fact]
    public async Task RunSliceAsync_InvalidImageKeepsMetadata()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid id = (await fixture.AddLocationsAsync(1))[0];
        fixture.Client.Handler = (_, capture, _) => Task.FromResult(
            Fixture.ReadyContent(capture) with { Image = "not an image"u8.ToArray() });

        Assert.Equal(1, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        PrinterFileEnrichment row = await fixture.EnrichmentAsync(id);
        Assert.Equal(PrinterFileEnrichmentState.Ready, row.MetadataState);
        Assert.Equal(PrinterFileEnrichmentState.Failed, row.ThumbnailState);
        Assert.Equal("invalid_image", row.ThumbnailReason);
        Assert.Null(row.ArtifactId);
        Assert.Equal(3600d, (await fixture.LocationAsync(id)).Metadata!.EstimatedPrintTimeSeconds);
        Assert.Empty(await fixture.ReservationsAsync());
    }

    [Fact]
    public async Task RunSliceAsync_FailedSameSourceRefreshKeepsPreviousArtifact()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid id = (await fixture.AddLocationsAsync(1))[0];
        await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct);
        Guid previous = (await fixture.EnrichmentAsync(id)).ArtifactId!.Value;
        await fixture.MakeDueAsync(id);
        fixture.Client.Handler = async (_, _, _) =>
        {
            await Task.Yield();
            throw new HttpRequestException("offline");
        };

        Assert.Equal(1, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        PrinterFileEnrichment row = await fixture.EnrichmentAsync(id);
        Assert.Equal(previous, row.ArtifactId);
        Assert.Equal(PrinterFileEnrichmentState.Failed, row.ThumbnailState);
        Assert.Equal(PrinterFileEnrichmentState.Failed, row.MetadataState);
        Assert.Equal(3600d, (await fixture.LocationAsync(id)).Metadata!.EstimatedPrintTimeSeconds);
        Assert.NotNull(await fixture.Store.ReadAsync(previous, Ct));
    }

    [Fact]
    public async Task RunSliceAsync_RevalidatedIdenticalImageReusesArtifact()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid id = (await fixture.AddLocationsAsync(1))[0];
        await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct);
        Guid previous = (await fixture.EnrichmentAsync(id)).ArtifactId!.Value;
        await fixture.MakeDueAsync(id);

        Assert.Equal(1, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        PrinterFileEnrichment row = await fixture.EnrichmentAsync(id);
        Assert.Equal(previous, row.ArtifactId);
        Assert.Equal(PrinterFileEnrichmentState.Ready, row.ThumbnailState);
        Assert.Equal(2, fixture.Client.Calls.Count);
        await using AppDbContext db = fixture.CreateContext();
        Assert.Equal(1, await db.PrinterFileThumbnailArtifacts.CountAsync());
        Assert.Empty(await fixture.ReservationsAsync());
    }

    [Fact]
    public async Task RunSliceAsync_MetadataOnlyRetryDoesNotDownloadCurrentThumbnail()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid id = (await fixture.AddLocationsAsync(1))[0];
        await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct);
        Guid previous = (await fixture.EnrichmentAsync(id)).ArtifactId!.Value;
        await fixture.MakeDueAsync(id, PrinterFileEnrichmentState.Failed);

        Assert.Equal(1, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        Assert.False(fixture.Client.Calls[^1].Capture);
        PrinterFileEnrichment row = await fixture.EnrichmentAsync(id);
        Assert.Equal(previous, row.ArtifactId);
        Assert.Equal(PrinterFileEnrichmentState.Ready, row.MetadataState);
        Assert.Equal(PrinterFileEnrichmentState.Ready, row.ThumbnailState);
    }

    [Fact]
    public async Task RunSliceAsync_UnsupportedBackendSettlesRowsWithoutRemoteCalls()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddLocationsAsync(3);
        fixture.SetSupported(false);

        Assert.Equal(3, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        Assert.Empty(fixture.Client.Calls);
        Assert.All(await fixture.EnrichmentsAsync(), row =>
        {
            Assert.Equal(PrinterFileEnrichmentState.Unsupported, row.MetadataState);
            Assert.Equal(PrinterFileEnrichmentState.Unsupported, row.ThumbnailState);
            Assert.Equal("enrichment_unsupported", row.ThumbnailReason);
        });
        Assert.False(await fixture.Service.HasDueWorkAsync(fixture.Lease.Object, Ct));
    }

    [Theory]
    [InlineData("printing", true)]
    [InlineData("ready", false)]
    public async Task RunSliceAsync_RequiresConfirmedIdlePrinter(string activity, bool online)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddLocationsAsync(2);
        fixture.Activity = activity;
        fixture.Online = online;

        Assert.Equal(0, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        Assert.Empty(fixture.Client.Calls);
        Assert.Empty(await fixture.EnrichmentsAsync());
        Assert.True(await fixture.Service.HasDueWorkAsync(fixture.Lease.Object, Ct));
    }

    [Fact]
    public async Task RunSliceAsync_LostFenceDiscardsPublication()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid id = (await fixture.AddLocationsAsync(1))[0];
        fixture.Lease.Setup(x => x.FencePublicationAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Assert.Equal(0, await fixture.Service.RunSliceAsync(fixture.Lease.Object, Ct));

        Assert.Empty(await fixture.EnrichmentsAsync());
        Assert.Null((await fixture.LocationAsync(id)).Metadata);
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FakeEnrichmentClient : ISupportsPrinterFileEnrichment
    {
        public List<(string Path, bool Capture)> Calls { get; } = [];

        public Func<PrinterFileInventoryEntry, bool, CancellationToken, Task<PrinterFileEnrichmentContent>> Handler { get; set; } =
            (_, capture, _) => Task.FromResult(Fixture.ReadyContent(capture));

        public Task<PrinterFileEnrichmentContent> EnrichFileAsync(
            string baseUrl, PrinterFileInventoryEntry observation, PrinterCredential? credential,
            bool captureThumbnail, CancellationToken ct)
        {
            Calls.Add((observation.RelativePath, captureThumbnail));
            return Handler(observation, captureThumbnail, ct);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Join(Path.GetTempPath(), $"printer-enrichment-{Guid.NewGuid():N}");
        private readonly Mock<IBackendCapabilityFactory> _capabilities = new();
        private readonly Mock<IPrinterStatusCacheReader> _status = new();
        private readonly Mock<IPrintersRepository> _printers = new();
        private Printer _printer = null!;

        public static byte[] Png { get; } = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        public FakeEnrichmentClient Client { get; } = new();

        public Mock<IPrinterFileLease> Lease { get; } = new();

        public PrinterFileThumbnailStore Store { get; private set; } = null!;

        public PrinterFileEnrichmentService Service { get; private set; } = null!;

        public string Activity { get; set; } = "ready";

        public bool Online { get; set; } = true;

        public static PrinterFileEnrichmentContent ReadyContent(bool capture) => new(
            new PrinterPrintMetadata { EstimatedPrintTimeSeconds = 3600, LayerHeightMm = 0.2 },
            PrinterFileEnrichmentState.Ready,
            capture ? PrinterFileEnrichmentState.Ready : PrinterFileEnrichmentState.Pending,
            capture ? Png : null,
            capture ? "image/png" : null,
            MetadataSource: "native");

        public static async Task<Fixture> CreateAsync(long cacheMaxBytes = 64L * 1024 * 1024)
        {
            Fixture fixture = new();
            Directory.CreateDirectory(Path.Join(fixture._directory, "storage"));
            await using (AppDbContext db = fixture.CreateContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.AppSettingsEntities.Add(new()
                {
                    Key = PrinterFileIndexSettings.SectionName,
                    SettingsJson = JsonSerializer.Serialize(new PrinterFileIndexSettings
                    {
                        ThumbnailCacheMaxBytes = cacheMaxBytes,
                    }),
                });
                Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = "Enrichment test" };
                fixture._printer = new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Printer",
                    ServerUrl = "http://printer.invalid",
                    Backend = (int)PrinterBackend.Moonraker,
                    IsEnabled = true,
                    Manufacturer = manufacturer,
                    Model = new PrinterModel { Id = Guid.NewGuid(), Name = "Enrichment test", Manufacturer = manufacturer },
                };
                db.Printers.Add(fixture._printer);
                db.PrinterFileScanStates.Add(new PrinterFileScanState
                {
                    Id = fixture._printer.Id,
                    SourcePrinterId = fixture._printer.Id,
                    RegisteredPrinterId = fixture._printer.Id,
                    ConfigurationFingerprint = PrinterFileIndexPolicy.ConfigurationFingerprint(fixture._printer),
                });
                await db.SaveChangesAsync();
            }

            fixture.Configure();
            return fixture;
        }

        public AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Join(_directory, "test.db")};Pooling=False;Default Timeout=30;Foreign Keys=True")
            .Options);

        public void SetSupported(bool supported)
        {
            ISupportsPrinterFileEnrichment? client = supported ? Client : null;
            _capabilities.Setup(x => x.TryGetFileEnrichmentClientTyped(It.IsAny<PrinterBackend>(), out client))
                .Returns(supported);
        }

        public async Task<List<Guid>> AddLocationsAsync(int count)
        {
            await using AppDbContext db = CreateContext();
            List<PrinterFileLocation> locations = Enumerable.Range(0, count).Select(index =>
            {
                string path = $"file-{index:D2}.gcode";
                return new PrinterFileLocation
                {
                    Id = Guid.NewGuid(),
                    SourcePrinterId = _printer.Id,
                    RegisteredPrinterId = _printer.Id,
                    StorageRoot = "gcodes",
                    RelativePath = path,
                    DisplayName = path,
                    SizeBytes = 100 + index,
                    ModifiedValue = "1",
                    ObservationRevision = 1,
                    ConfigurationGeneration = 1,
                    Availability = PrinterFileAvailability.Available,
                    IdentityKey = PrinterInventoryPath.ComputeIdentity("gcodes", path),
                };
            }).ToList();
            db.PrinterFileLocations.AddRange(locations);
            await db.SaveChangesAsync();
            return locations.Select(x => x.Id).ToList();
        }

        /// <summary>Simulates elapsed revalidation (or a pending metadata retry) for the same source.</summary>
        public async Task MakeDueAsync(Guid id, PrinterFileEnrichmentState? metadataState = null)
        {
            await using AppDbContext db = CreateContext();
            PrinterFileEnrichment row = await db.PrinterFileEnrichments.SingleAsync(x => x.LocationId == id);
            row.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(-1);
            if (metadataState is { } state)
            {
                row.MetadataState = state;
                row.MetadataReason = "transfer_failed";
            }

            await db.SaveChangesAsync();
        }

        public async Task<PrinterFileLocation> LocationAsync(Guid id)
        {
            await using AppDbContext db = CreateContext();
            return await db.PrinterFileLocations.AsNoTracking().SingleAsync(x => x.Id == id);
        }

        public async Task<PrinterFileEnrichment> EnrichmentAsync(Guid id)
        {
            await using AppDbContext db = CreateContext();
            return await db.PrinterFileEnrichments.AsNoTracking().SingleAsync(x => x.LocationId == id);
        }

        public async Task<List<PrinterFileEnrichment>> EnrichmentsAsync()
        {
            await using AppDbContext db = CreateContext();
            return await db.PrinterFileEnrichments.AsNoTracking().ToListAsync();
        }

        public async Task<List<PrinterFileThumbnailReservation>> ReservationsAsync()
        {
            await using AppDbContext db = CreateContext();
            return await db.PrinterFileThumbnailReservations.AsNoTracking().ToListAsync();
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }

        private void Configure()
        {
            Factory factory = new(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Join(_directory, "test.db")};Pooling=False;Default Timeout=30;Foreign Keys=True")
                .Options);
            Mock<IStoragePathService> paths = new();
            paths.Setup(x => x.GetGcodeStorageDirectory()).Returns(Path.Join(_directory, "storage"));
            Store = new(factory, paths.Object, NullLogger<PrinterFileThumbnailStore>.Instance);
            Lease.SetupGet(x => x.PrinterId).Returns(_printer.Id);
            Lease.SetupGet(x => x.ConfigurationGeneration).Returns(1);
            Lease.SetupGet(x => x.MutationGeneration).Returns(0);
            Lease.SetupGet(x => x.Token).Returns(CancellationToken.None);
            Lease.Setup(x => x.IsCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Lease.Setup(x => x.FencePublicationAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            _status.Setup(x => x.GetSnapshot(_printer.Id)).Returns(() => new PrinterStatusCacheSnapshot(
                new PrinterStatusDto(_printer.Id, Online, "Idle") { RawActivityState = Activity },
                DateTime.UtcNow));
            _printers.Setup(x => x.FindByIdWithIncludesAsync(_printer.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(_printer);
            SetSupported(true);
            Service = new(factory, Store, _capabilities.Object, _printers.Object, _status.Object,
                NullLogger<PrinterFileEnrichmentService>.Instance);
        }
    }
}
