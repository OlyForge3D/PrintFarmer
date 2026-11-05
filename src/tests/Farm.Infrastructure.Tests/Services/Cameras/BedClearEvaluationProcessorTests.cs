using System.Net;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Cameras;

public sealed class BedClearEvaluationProcessorTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly AppDbContext _db;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bed-clear-tests-{Guid.NewGuid():N}");
    private readonly Mock<IPrinterStatusSnapshotReader> _status = new();
    private readonly SnapshotHandler _handler = new();
    private readonly DateTime _now = DateTime.UtcNow;
    private readonly Printer _printer = new() { Id = Guid.NewGuid(), Name = "Test printer" };
    private readonly Camera _camera = new()
    {
        Id = Guid.NewGuid(),
        Name = "Bed camera",
        CameraType = CameraType.Bed,
        SnapshotUrl = "http://192.168.1.25/snapshot.jpg",
        HealthStatus = CameraHealthStatus.Healthy,
    };

    public BedClearEvaluationProcessorTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        var bedType = new BedType { Id = Guid.NewGuid(), Name = "Test plate" };
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test manufacturer" };
        var model = new PrinterModel { Id = Guid.NewGuid(), Name = "Test model", ManufacturerId = manufacturer.Id };
        _printer.ManufacturerId = manufacturer.Id;
        _printer.ModelId = model.Id;
        _printer.BedTypeId = bedType.Id;
        _camera.LastHealthCheck = _now;
        _db.AddRange(manufacturer, model, bedType, _printer, _camera);
        _db.Add(new CameraPrinter { PrinterId = _printer.Id, CameraId = _camera.Id, IsPrimary = true });
        _db.SaveChanges();
        _status.Setup(reader => reader.GetStatusSnapshot(_printer.Id)).Returns(Snapshot());
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ProcessPending_WhenReferencesMatch_RecordsClearWithoutChangingDispatch()
    {
        await AddReferencesAsync();
        BedClearEvaluation sample = await AddSampleAsync();

        await CreateProcessor().ProcessPendingAsync(CancellationToken.None);
        await _db.Entry(sample).ReloadAsync();

        sample.Verdict.Should().Be(BedClearDetectorVerdict.Clear);
        sample.Score.Should().Be(1);
        sample.State.Should().Be(BedClearEvaluationState.Complete);
        sample.IsReferenceEligible.Should().BeTrue();
        sample.IsActiveReference.Should().BeFalse();
        sample.CameraId.Should().Be(_camera.Id);
        sample.ToolheadX.Should().Be(10);
        sample.CapturedAtUtc.Should().NotBeNull();
        File.Exists(Path.Combine(_root, sample.ImageFilePath!)).Should().BeTrue();
        (await _db.PrintJobs.CountAsync()).Should().Be(0);
        (await _db.PrinterDispatchStates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CleanupOrphanedImages_AfterPrinterDeletion_RemovesOnlyManagedImagesOfDeletedPrinters()
    {
        BedClearEvaluation sample = await AddSampleAsync();
        BedClearEvaluationProcessor processor = CreateProcessor();
        await processor.ProcessPendingAsync(CancellationToken.None);
        await _db.Entry(sample).ReloadAsync();
        string capturedImage = Path.Combine(_root, sample.ImageFilePath!);
        File.Exists(capturedImage).Should().BeTrue();

        var livePrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Retained printer",
            ServerUrl = "http://retained-printer.invalid",
            ManufacturerId = _printer.ManufacturerId,
            ModelId = _printer.ModelId,
        };
        _db.Printers.Add(livePrinter);
        await _db.SaveChangesAsync();
        string liveDirectory = Path.Combine(_root, "bed-clear", livePrinter.Id.ToString("N"));
        Directory.CreateDirectory(liveDirectory);
        string liveImage = Path.Combine(liveDirectory, $"{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(liveImage, [1]);
        string unmanagedFile = Path.Combine(Path.GetDirectoryName(capturedImage)!, "keep.txt");
        await File.WriteAllTextAsync(unmanagedFile, "Unmanaged file");
        string unrelatedDirectory = Path.Combine(_root, "bed-clear", "not-a-printer");
        Directory.CreateDirectory(unrelatedDirectory);
        string unrelatedImage = Path.Combine(unrelatedDirectory, $"{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(unrelatedImage, [1]);

        _db.Printers.Remove(_printer);
        await _db.SaveChangesAsync();
        (await _db.BedClearEvaluations.CountAsync()).Should().Be(0);

        await processor.CleanupOrphanedImagesAsync(CancellationToken.None);
        await processor.CleanupOrphanedImagesAsync(CancellationToken.None);

        File.Exists(capturedImage).Should().BeFalse();
        File.Exists(liveImage).Should().BeTrue();
        File.Exists(unmanagedFile).Should().BeTrue();
        File.Exists(unrelatedImage).Should().BeTrue();
    }

    [Theory]
    [InlineData("old_ack", "capture_window_missed")]
    [InlineData("stale_status", "printer_status_stale")]
    [InlineData("printing", "printer_moved_to_dispatch")]
    [InlineData("missing_position", "toolhead_position_unavailable")]
    [InlineData("stale_camera", "camera_health_stale")]
    [InlineData("different_plate", "bed_type_changed")]
    public async Task ProcessPending_WhenCaptureIsUnsafe_ReturnsUnknownWithoutRequestingImage(
        string condition,
        string reason)
    {
        BedClearEvaluation sample = await AddSampleAsync();
        if (condition == "old_ack")
        {
            sample.AcknowledgedAtUtc = _now.AddMinutes(-1);
        }
        else if (condition == "stale_status")
        {
            _status.Setup(reader => reader.GetStatusSnapshot(_printer.Id))
                .Returns(Snapshot() with { ObservedAtUtc = _now.AddMinutes(-1) });
        }
        else if (condition == "printing")
        {
            PrinterStatusSnapshot current = Snapshot();
            _status.Setup(reader => reader.GetStatusSnapshot(_printer.Id))
                .Returns(current with { Status = current.Status with { State = "Printing" } });
        }
        else if (condition == "missing_position")
        {
            PrinterStatusSnapshot current = Snapshot();
            _status.Setup(reader => reader.GetStatusSnapshot(_printer.Id))
                .Returns(current with { Status = current.Status with { X = null } });
        }
        else if (condition == "stale_camera")
        {
            _camera.LastHealthCheck = _now.AddHours(-1);
        }
        else
        {
            sample.BedTypeId = null;
        }

        await _db.SaveChangesAsync();
        await CreateProcessor().ProcessPendingAsync(CancellationToken.None);
        await _db.Entry(sample).ReloadAsync();

        sample.Verdict.Should().Be(BedClearDetectorVerdict.Unknown);
        sample.Score.Should().BeNull();
        sample.ReasonCode.Should().Be(reason);
        sample.IsReferenceEligible.Should().BeFalse();
        _handler.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("camera_configuration")]
    [InlineData("bed_type")]
    [InlineData("unreviewed")]
    public async Task ProcessPending_WhenReferencesDoNotMatchScope_ReturnsUnknown(string mismatch)
    {
        await AddReferencesAsync(mismatch);
        BedClearEvaluation sample = await AddSampleAsync();

        await CreateProcessor().ProcessPendingAsync(CancellationToken.None);
        await _db.Entry(sample).ReloadAsync();

        sample.Verdict.Should().Be(BedClearDetectorVerdict.Unknown);
        sample.ReasonCode.Should().Be("insufficient_references");
        sample.IsReferenceEligible.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessPending_WhenPositionChangesDuringCapture_ReturnsUnknownAndCannotBecomeReference()
    {
        await AddReferencesAsync();
        BedClearEvaluation sample = await AddSampleAsync();
        PrinterStatusSnapshot current = Snapshot();
        _status.SetupSequence(reader => reader.GetStatusSnapshot(_printer.Id))
            .Returns(current)
            .Returns(current with { Status = current.Status with { X = 100 } });

        await CreateProcessor().ProcessPendingAsync(CancellationToken.None);
        await _db.Entry(sample).ReloadAsync();

        sample.Verdict.Should().Be(BedClearDetectorVerdict.Unknown);
        sample.ReasonCode.Should().Be("printer_moved_during_capture");
        sample.IsReferenceEligible.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessPending_WhenImageDiffers_RecordsOccupiedWithoutDispatch()
    {
        await AddReferencesAsync();
        BedClearEvaluation sample = await AddSampleAsync();
        _handler.ImageBytes = new byte[] { 2 };

        await CreateProcessor().ProcessPendingAsync(CancellationToken.None);
        await _db.Entry(sample).ReloadAsync();

        sample.Verdict.Should().Be(BedClearDetectorVerdict.Occupied);
        sample.Score.Should().Be(0);
        sample.IsActiveReference.Should().BeFalse();
        (await _db.PrintJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ProcessPending_WhenFrameAdvertisesOldTimestamp_ReturnsUnknown()
    {
        BedClearEvaluation sample = await AddSampleAsync();
        _handler.LastModified = new DateTimeOffset(_now.AddMinutes(-1), TimeSpan.Zero);

        await CreateProcessor().ProcessPendingAsync(CancellationToken.None);
        await _db.Entry(sample).ReloadAsync();

        sample.Verdict.Should().Be(BedClearDetectorVerdict.Unknown);
        sample.ReasonCode.Should().Be("camera_frame_stale");
        sample.IsReferenceEligible.Should().BeFalse();
        sample.ImageFilePath.Should().BeNull();
    }

    [Fact]
    public async Task ProcessPending_WhenProcessingLeaseExpires_CompletesMissedCaptureAsUnknown()
    {
        BedClearEvaluation sample = await AddSampleAsync();
        sample.State = BedClearEvaluationState.Processing;
        sample.AcknowledgedAtUtc = _now.AddMinutes(-1);
        sample.ProcessingStartedAtUtc = _now.AddMinutes(-1);
        await _db.SaveChangesAsync();

        await CreateProcessor().ProcessPendingAsync(CancellationToken.None);
        await _db.Entry(sample).ReloadAsync();

        sample.State.Should().Be(BedClearEvaluationState.Complete);
        sample.Verdict.Should().Be(BedClearDetectorVerdict.Unknown);
        sample.ReasonCode.Should().Be("capture_window_missed");
        _handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ProcessPending_WhenCameraFails_CompletesUnknownInsteadOfRetryingAsSuccess()
    {
        BedClearEvaluation sample = await AddSampleAsync();
        _handler.StatusCode = HttpStatusCode.ServiceUnavailable;

        await CreateProcessor().ProcessPendingAsync(CancellationToken.None);
        await _db.Entry(sample).ReloadAsync();

        sample.State.Should().Be(BedClearEvaluationState.Complete);
        sample.Verdict.Should().Be(BedClearDetectorVerdict.Unknown);
        sample.ReasonCode.Should().Be("camera_capture_failed");
        sample.ImageFilePath.Should().BeNull();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _handler.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private PrinterStatusSnapshot Snapshot() => new(
        new PrinterStatusDto(_printer.Id, true, "Idle", X: 10, Y: 20, Z: 30),
        _now,
        _now,
        "test");

    private BedClearEvaluationProcessor CreateProcessor()
    {
        var storage = new Mock<IStoragePathService>();
        storage.Setup(service => service.GetSnapshotStorageDirectory()).Returns(_root);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(service => service.CreateClient("BedClearSnapshot"))
            .Returns(() => new HttpClient(_handler, disposeHandler: false));
        return new BedClearEvaluationProcessor(
            _db,
            factory.Object,
            storage.Object,
            _status.Object,
            new TestHashService(),
            NullLogger<BedClearEvaluationProcessor>.Instance,
            new FixedTimeProvider(_now));
    }

    private async Task<BedClearEvaluation> AddSampleAsync()
    {
        var sample = new BedClearEvaluation
        {
            Id = Guid.NewGuid(),
            PrinterId = _printer.Id,
            BedTypeId = _printer.BedTypeId,
            SourceAcknowledgementId = Guid.NewGuid(),
            AcknowledgedBySubject = "capturing-operator",
            AcknowledgedAtUtc = _now,
            CreatedAtUtc = _now,
        };
        _db.Add(sample);
        await _db.SaveChangesAsync();
        return sample;
    }

    private async Task AddReferencesAsync(string? mismatch = null)
    {
        for (int i = 0; i < BedClearImageClassifier.MinimumReferences; i++)
        {
            BedClearEvaluation reference = await AddSampleAsync();
            reference.CameraId = _camera.Id;
            reference.CameraConfigurationAtUtc = mismatch == "camera_configuration"
                ? _camera.CreatedAt.AddDays(-1)
                : _camera.CreatedAt;
            reference.BedTypeId = mismatch == "bed_type" ? null : _printer.BedTypeId;
            reference.ToolheadX = mismatch == "position" ? 11 : 10;
            reference.ToolheadY = 20;
            reference.ToolheadZ = 30;
            reference.State = BedClearEvaluationState.Complete;
            reference.GroundTruth = BedClearGroundTruth.Clear;
            reference.GroundTruthBySubject = "reviewing-operator";
            reference.GroundTruthAtUtc = _now;
            reference.IsActiveReference = mismatch != "unreviewed";
            reference.IsReferenceEligible = true;
            reference.ImageFilePath = $"{reference.Id:N}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(_root, reference.ImageFilePath), new byte[] { 1 });
        }

        await _db.SaveChangesAsync();
    }

    private sealed class TestHashService : IBedClearImageHashService
    {
        public bool TryCreateHash(ReadOnlyMemory<byte> imageBytes, out ulong hash, out string? reasonCode)
        {
            hash = imageBytes.Span[0] == 1 ? 0 : ulong.MaxValue;
            reasonCode = null;
            return true;
        }
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private sealed class SnapshotHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        public byte[] ImageBytes { get; set; } = new byte[] { 1 };

        public DateTimeOffset? LastModified { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(StatusCode)
            {
                Content = new ByteArrayContent(ImageBytes),
            };
            response.Content.Headers.LastModified = LastModified;
            return Task.FromResult(response);
        }
    }
}
