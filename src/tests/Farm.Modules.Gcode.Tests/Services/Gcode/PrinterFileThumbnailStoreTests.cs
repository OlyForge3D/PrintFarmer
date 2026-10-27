using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Modules.Gcode.Tests.Services.Gcode;

public sealed class PrinterFileThumbnailStoreTests
{
    private const int Maximum = PrinterFileEnrichmentLimits.MaximumImageBytes;

    private static readonly CancellationToken Ct = CancellationToken.None;

    private static byte[] Png => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public async Task RoundTrip_IndependentInstances_ReadImmutableBytesWithoutPublishingLocation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid artifactId = await fixture.WriteAsync();
        PrinterFilePreviewContent? read = await fixture.Store().ReadAsync(artifactId, Ct);

        Assert.Equal(Png, read!.Image);
        Assert.Equal("image/png", read.ContentType);
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        PrinterFileThumbnailArtifact artifact = await db.PrinterFileThumbnailArtifacts.SingleAsync();
        Assert.Equal(1, artifact.Width);
        Assert.Equal(1, artifact.Height);
        Assert.Equal(Png.Length, artifact.SizeBytes);
        Assert.Equal(64, artifact.ContentSha256.Length);
        Assert.Empty(await db.PrinterFileEnrichments.ToListAsync());
        Assert.Empty(await db.PrinterFileThumbnailReservations.ToListAsync());
        Assert.Empty(Directory.GetFiles(fixture.CacheDirectory, "*.pending"));
        Assert.Single(Directory.GetFiles(fixture.CacheDirectory, "*.image"));
    }

    [Fact]
    public async Task Reserve_ExactBoundaryIncludesCommittedBytesAndReservations_WithoutEviction()
    {
        await using Fixture fixture = await Fixture.CreateAsync(2L * Maximum + Png.Length);
        Guid artifactId = await fixture.WriteAsync();
        Guid first = (await fixture.Store().ReserveAsync(Ct)).ReservationId!.Value;
        Assert.NotNull((await fixture.Store().ReserveAsync(Ct)).ReservationId);
        Assert.Equal("cache_full", (await fixture.Store().ReserveAsync(Ct)).Reason);

        await fixture.Store().ReleaseAsync(first, Ct);

        Assert.NotNull((await fixture.Store().ReserveAsync(Ct)).ReservationId);
        Assert.Equal(Png, (await fixture.Store().ReadAsync(artifactId, Ct))!.Image);
        Assert.Equal(2L * Maximum + Png.Length, await fixture.ChargedBytesAsync());
    }

    [Fact]
    public async Task Reserve_ConcurrentReplicas_CannotOverbook()
    {
        await using Fixture fixture = await Fixture.CreateAsync(2L * Maximum);
        PrinterFileThumbnailStore[] replicas = Enumerable.Range(0, 8).Select(_ => fixture.Store()).ToArray();
#pragma warning disable IDISP013 // All replica tasks are joined below before the fixture's await-using scope ends.
        Task<PrinterFileThumbnailReservationResult>[] attempts = replicas
            .Select(store => Task.Run(() => store.ReserveAsync(Ct))).ToArray();
#pragma warning restore IDISP013

        PrinterFileThumbnailReservationResult[] results = await Task.WhenAll(attempts);

        Assert.Equal(2, results.Count(x => x.ReservationId is not null));
        Assert.Equal(6, results.Count(x => x.Reason == "cache_full"));
        Assert.Equal(2L * Maximum, await fixture.ChargedBytesAsync());
    }

    [Fact]
    public async Task FailedCommitAfterRename_RemainsChargedUntilExpiredPhysicalAttemptIsDeleted()
    {
        await using Fixture fixture = await Fixture.CreateAsync(Maximum);
        Guid reservation = (await fixture.Store().ReserveAsync(Ct)).ReservationId!.Value;
        PrinterFileThumbnailStore failing = fixture.Store(new RejectArtifactCommit());

        await Assert.ThrowsAsync<IOException>(() => failing.WriteAsync(reservation, Png, "image/png", Ct));

        Assert.True(File.Exists(fixture.ImagePath(reservation)));
        Assert.Equal(Maximum, await fixture.ChargedBytesAsync());
        Assert.Equal("cache_full", (await fixture.Store().ReserveAsync(Ct)).Reason);
        fixture.Clock.Advance(PrinterFileThumbnailStore.ReservationLifetime);
        await fixture.Store().CleanupAsync(Ct);
        Assert.False(File.Exists(fixture.ImagePath(reservation)));
        Assert.Equal(0, await fixture.ChargedBytesAsync());
        Assert.NotNull((await fixture.Store().ReserveAsync(Ct)).ReservationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store().WriteAsync(reservation, Png, null, Ct));
    }

    [Fact]
    public async Task Release_FilesystemFailureAndUnavailableMount_NeverReleaseLogicalQuota()
    {
        await using Fixture fixture = await Fixture.CreateAsync(Maximum);
        Guid reservation = (await fixture.Store().ReserveAsync(Ct)).ReservationId!.Value;
        // A directory at a generated file key reliably fails deletion on every supported OS.
        string blocked = fixture.PendingPath(reservation);
        Directory.CreateDirectory(blocked);
        await fixture.Store().ReleaseAsync(reservation, Ct);
        Assert.Equal(Maximum, await fixture.ChargedBytesAsync());
        Directory.Delete(blocked);

        Directory.Move(fixture.StorageRoot, fixture.StorageRoot + "-offline");
        await fixture.Store().ReleaseAsync(reservation, Ct);
        Assert.Equal(Maximum, await fixture.ChargedBytesAsync());
        Assert.Equal("storage_unavailable", (await fixture.Store().ReserveAsync(Ct)).Reason);
        Assert.False(Directory.Exists(fixture.StorageRoot));
        Directory.Move(fixture.StorageRoot + "-offline", fixture.StorageRoot);

        await fixture.Store().ReleaseAsync(reservation, Ct);
        Assert.Equal(0, await fixture.ChargedBytesAsync());
    }

    [Fact]
    public async Task Cleanup_ReferencedArtifactSurvives_ThenGraceStartsAtDetachment()
    {
        await using Fixture fixture = await Fixture.CreateAsync(Maximum);
        Guid artifact = await fixture.WriteAsync();
        Guid location = await fixture.LinkAsync(artifact);
        fixture.Clock.Advance(TimeSpan.FromDays(3));
        await fixture.Store().CleanupAsync(Ct);
        Assert.NotNull(await fixture.Store().ReadAsync(artifact, Ct));
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            PrinterFileLocation row = await db.PrinterFileLocations.SingleAsync(x => x.Id == location);
            row.Availability = PrinterFileAvailability.Deleted;
            await db.SaveChangesAsync();
        }

        await fixture.Store().CleanupAsync(Ct);
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            Assert.Null((await db.PrinterFileEnrichments.SingleAsync()).ArtifactId);
            Assert.Equal(fixture.Clock.GetUtcNow().UtcDateTime,
                (await db.PrinterFileThumbnailArtifacts.SingleAsync()).RetiredAtUtc);
        }

        fixture.Clock.Advance(PrinterFileThumbnailStore.RetirementGrace - TimeSpan.FromTicks(1));
        await fixture.Store().CleanupAsync(Ct);
        Assert.Equal(Png.Length, await fixture.ChargedBytesAsync());
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        await fixture.Store().CleanupAsync(Ct);
        Assert.Null(await fixture.Store().ReadAsync(artifact, Ct));
        Assert.Equal(0, await fixture.ChargedBytesAsync());
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("configuration-generation")]
    [InlineData("configuration-fingerprint")]
    [InlineData("printer-removed")]
    [InlineData("unregistered")]
    [InlineData("missing")]
    public async Task Cleanup_IndependentlyDetachesInvalidAssociations(string change)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid artifact = await fixture.WriteAsync();
        await fixture.LinkAsync(artifact);
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            PrinterFileLocation location = await db.PrinterFileLocations.SingleAsync();
            switch (change)
            {
                case "identity":
                    location.SizeBytes++;
                    break;
                case "configuration-generation":
                    (await db.PrinterFileScanStates.SingleAsync()).ConfigurationGeneration++;
                    break;
                case "configuration-fingerprint":
                    (await db.Printers.SingleAsync()).ServerUrl = "http://replacement.invalid";
                    break;
                case "printer-removed":
                    db.Printers.Remove(await db.Printers.SingleAsync());
                    break;
                case "unregistered":
                    location.RegisteredPrinterId = null;
                    break;
                case "missing":
                    location.Availability = PrinterFileAvailability.Missing;
                    break;
            }

            await db.SaveChangesAsync();
        }

        await fixture.Store().CleanupAsync(Ct);

        await using AppDbContext verify = fixture.Factory.CreateDbContext();
        Assert.Null((await verify.PrinterFileEnrichments.SingleAsync()).ArtifactId);
        Assert.NotNull(await fixture.Store().ReadAsync(artifact, Ct));
    }

    [Fact]
    public async Task Cleanup_RechecksNewReferenceBeforeDeletingRetiredArtifact()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid artifact = await fixture.WriteAsync();
        await fixture.Store().CleanupAsync(Ct);
        fixture.Clock.Advance(PrinterFileThumbnailStore.RetirementGrace);
        await fixture.LinkAsync(artifact);

        await Task.WhenAll(Task.Run(() => fixture.Store().CleanupAsync(Ct)),
            Task.Run(() => fixture.Store().CleanupAsync(Ct)));

        Assert.NotNull(await fixture.Store().ReadAsync(artifact, Ct));
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        Assert.Null((await db.PrinterFileThumbnailArtifacts.SingleAsync()).RetiredAtUtc);
    }

    [Fact]
    public async Task Cleanup_FailedArtifactDeletionRemainsCharged_AndSucceedsAfterRepair()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid artifact = await fixture.WriteAsync();
        await fixture.Store().CleanupAsync(Ct);
        fixture.Clock.Advance(PrinterFileThumbnailStore.RetirementGrace);
        File.Delete(fixture.ImagePath(artifact));
        Directory.CreateDirectory(fixture.ImagePath(artifact));

        await fixture.Store().CleanupAsync(Ct);
        Assert.Equal(Png.Length, await fixture.ChargedBytesAsync());

        Directory.Delete(fixture.ImagePath(artifact));
        await fixture.Store().CleanupAsync(Ct);
        Assert.Equal(0, await fixture.ChargedBytesAsync());
    }

    [Fact]
    public async Task Cleanup_DeletionCommitFailure_DurableTombstoneRetainsChargeAndPreventsPublication()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid artifact = await fixture.WriteAsync();
        await fixture.Store().CleanupAsync(Ct);
        fixture.Clock.Advance(PrinterFileThumbnailStore.RetirementGrace);

        await fixture.Store(new RejectReservationRemoval()).CleanupAsync(Ct);

        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            Assert.Empty(await db.PrinterFileThumbnailArtifacts.ToListAsync());
            Assert.Equal(Png.Length, (await db.PrinterFileThumbnailReservations.SingleAsync()).ReservedBytes);
        }

        Assert.False(File.Exists(fixture.ImagePath(artifact)));
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.LinkAsync(artifact));
        await fixture.Store().CleanupAsync(Ct);
        Assert.Equal(0, await fixture.ChargedBytesAsync());
    }

    [Fact]
    public async Task Cleanup_RacesPublication_OnlyUnreferencedKeysCanBeRemoved()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Guid artifact = await fixture.WriteAsync();
        await fixture.Store().CleanupAsync(Ct);
        fixture.Clock.Advance(PrinterFileThumbnailStore.RetirementGrace);
        bool published = false;
        await Task.WhenAll(
            Task.Run(() => fixture.Store().CleanupAsync(Ct)),
            Task.Run(async () =>
            {
                try
                {
                    await fixture.LinkAsync(artifact);
                    published = true;
                }
                catch (DbUpdateException)
                {
                    // The retired immutable key was removed first; its FK fences late publication.
                }
            }));

        await using AppDbContext db = fixture.Factory.CreateDbContext();
        Assert.Equal(published, await db.PrinterFileEnrichments.AnyAsync(x => x.ArtifactId == artifact));
        Assert.Equal(published, await fixture.Store().ReadAsync(artifact, Ct) is not null);
    }

    [Fact]
    public async Task Reserve_ReconcilesOrphanNamesBeforeAdmittingAnotherCapture()
    {
        await using Fixture fixture = await Fixture.CreateAsync(Maximum);
        Directory.CreateDirectory(fixture.CacheDirectory);
        Guid orphan = Guid.NewGuid();
        await File.WriteAllBytesAsync(fixture.ImagePath(orphan), Png, Ct);
        await File.WriteAllBytesAsync(fixture.PendingPath(orphan), Png, Ct);

        Assert.NotNull((await fixture.Store().ReserveAsync(Ct)).ReservationId);

        Assert.False(File.Exists(fixture.ImagePath(orphan)));
        Assert.False(File.Exists(fixture.PendingPath(orphan)));
        Assert.Equal(Maximum, await fixture.ChargedBytesAsync());
    }

    [Fact]
    public async Task Write_MalformedOversizedAndMismatchedImages_DoNotConsumeReservation()
    {
        await using Fixture fixture = await Fixture.CreateAsync(Maximum);
        Guid reservation = (await fixture.Store().ReserveAsync(Ct)).ReservationId!.Value;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Store().WriteAsync(reservation, "<svg/>"u8.ToArray(), "image/svg+xml", Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Store().WriteAsync(reservation, new byte[Maximum + 1], "image/png", Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Store().WriteAsync(reservation, Png, "image/jpeg", Ct));
        Assert.Equal(Maximum, await fixture.ChargedBytesAsync());
        Assert.Empty(Directory.GetFiles(fixture.CacheDirectory));
        await fixture.Store().ReleaseAsync(reservation, Ct);
        Assert.Equal(0, await fixture.ChargedBytesAsync());
    }

    [Fact]
    public async Task Read_MissingArtifactIsNull_UnavailableRootAndCorruptionAreIoErrors()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.Null(await fixture.Store().ReadAsync(Guid.NewGuid(), Ct));
        Guid artifact = await fixture.WriteAsync();
        Directory.Move(fixture.StorageRoot, fixture.StorageRoot + "-offline");
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Store().ReadAsync(artifact, Ct));
        Directory.Move(fixture.StorageRoot + "-offline", fixture.StorageRoot);
        await File.WriteAllBytesAsync(fixture.ImagePath(artifact), new byte[Maximum + 1], Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store().ReadAsync(artifact, Ct));
        Assert.Equal("storage_unavailable", (await fixture.Store().ReserveAsync(Ct)).Reason);
        File.Delete(fixture.ImagePath(artifact));
        Assert.Null(await fixture.Store().ReadAsync(artifact, Ct));
        Assert.Equal(Png.Length, await fixture.ChargedBytesAsync());
    }

    private sealed class RejectArtifactCommit : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PrinterFileThumbnailArtifact>().Any())
            {
                throw new IOException("Simulated crash after rename and before artifact commit.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class RejectReservationRemoval : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PrinterFileThumbnailReservation>()
                .Any(x => x.State == EntityState.Deleted))
            {
                throw new IOException("Simulated crash after physical deletion and before quota release.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Join(Path.GetTempPath(), $"thumbnail-store-{Guid.NewGuid():N}");

        public string StorageRoot => Path.Join(_directory, "storage");

        public string CacheDirectory => Path.Join(StorageRoot, PrinterFileThumbnailStore.DirectoryName);

        public Clock Clock { get; } = new();

        public Factory Factory => CreateFactory();

        public string ImagePath(Guid id) => Path.Join(CacheDirectory, id.ToString("N") + ".image");

        public string PendingPath(Guid id) => Path.Join(CacheDirectory, id.ToString("N") + ".pending");

        public static async Task<Fixture> CreateAsync(long maximum = 512L * 1024 * 1024)
        {
            Fixture fixture = new();
            Directory.CreateDirectory(fixture.StorageRoot);
            await using AppDbContext db = fixture.Factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            db.AppSettingsEntities.Add(new()
            {
                Key = PrinterFileIndexSettings.SectionName,
                SettingsJson = JsonSerializer.Serialize(new PrinterFileIndexSettings { ThumbnailCacheMaxBytes = maximum }),
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public PrinterFileThumbnailStore Store(IInterceptor? interceptor = null)
        {
            Mock<IStoragePathService> paths = new(MockBehavior.Strict);
            paths.Setup(x => x.GetGcodeStorageDirectory()).Returns(StorageRoot);
            return new(CreateFactory(interceptor), paths.Object, NullLogger<PrinterFileThumbnailStore>.Instance, Clock);
        }

        public async Task<Guid> WriteAsync()
        {
            PrinterFileThumbnailStore store = Store();
            Guid reservation = (await store.ReserveAsync(Ct)).ReservationId!.Value;
            return await store.WriteAsync(reservation, Png, "image/png", Ct);
        }

        public async Task<long> ChargedBytesAsync()
        {
            await using AppDbContext db = Factory.CreateDbContext();
            return (await db.PrinterFileThumbnailArtifacts.SumAsync(x => (long?)x.SizeBytes) ?? 0) +
                (await db.PrinterFileThumbnailReservations.SumAsync(x => (long?)x.ReservedBytes) ?? 0);
        }

        public async Task<Guid> LinkAsync(Guid artifact)
        {
            await using AppDbContext db = Factory.CreateDbContext();
            Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = "Thumbnail test" };
            PrinterModel model = new() { Id = Guid.NewGuid(), Name = "Thumbnail test", Manufacturer = manufacturer };
            Printer printer = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer",
                ServerUrl = "http://printer.invalid",
                Manufacturer = manufacturer,
                Model = model,
                IsEnabled = true,
            };
            PrinterFileLocation location = new()
            {
                Id = Guid.NewGuid(),
                SourcePrinterId = printer.Id,
                RegisteredPrinter = printer,
                RegisteredPrinterId = printer.Id,
                StorageRoot = "gcodes",
                RelativePath = "test.gcode",
                IdentityKey = PrinterInventoryPath.ComputeIdentity("gcodes", "test.gcode"),
                SizeBytes = 123,
            };
            db.PrinterFileLocations.Add(location);
            db.PrinterFileScanStates.Add(new()
            {
                Id = Guid.NewGuid(),
                SourcePrinterId = printer.Id,
                RegisteredPrinterId = printer.Id,
                ConfigurationFingerprint = PrinterFileIndexPolicy.ConfigurationFingerprint(printer),
            });
            db.PrinterFileEnrichments.Add(new()
            {
                Location = location,
                ArtifactId = artifact,
                SourceIdentity = PrinterFileEnrichmentIdentity.For(location),
                ThumbnailState = PrinterFileEnrichmentState.Ready,
            });
            await db.SaveChangesAsync();
            return location.Id;
        }

        private Factory CreateFactory(IInterceptor? interceptor = null)
        {
            DbContextOptionsBuilder<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Join(_directory, "test.db")};Pooling=False;Default Timeout=30;Foreign Keys=True");
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }

            return new(options.Options);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
