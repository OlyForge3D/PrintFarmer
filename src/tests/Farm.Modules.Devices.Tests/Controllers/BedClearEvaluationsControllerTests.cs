using System.Security.Claims;
using System.Text.Json;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Modules.Devices.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Farm.Modules.Devices.Tests.Controllers;

public sealed class BedClearEvaluationsControllerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly AppDbContext _db;
    private readonly Mock<IQueueResourceAuthorizationService> _authorization = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bed-clear-api-tests-{Guid.NewGuid():N}");
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _printerId = Guid.NewGuid();
    private readonly BedClearEvaluationsController _controller;

    public BedClearEvaluationsControllerTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test manufacturer" };
        var model = new PrinterModel { Id = Guid.NewGuid(), Name = "Test model", ManufacturerId = manufacturer.Id };
        _db.AddRange(manufacturer, model, new Printer
        {
            Id = _printerId,
            Name = "Evidence printer",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        });
        _db.SaveChanges();
        _authorization.Setup(service => service.CanAccessPrinterAsync(
                It.IsAny<ClaimsPrincipal>(), _printerId, It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var storage = new Mock<IStoragePathService>();
        storage.Setup(service => service.GetSnapshotStorageDirectory()).Returns(_root);
        _controller = new BedClearEvaluationsController(_db, storage.Object, _authorization.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, _actor.ToString()) }, "test")),
                },
            },
        };
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Capture_WhenRequested_DoesNotAcknowledgeReadinessOrDispatch()
    {
        IActionResult result = await _controller.CaptureAsync(_printerId, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        BedClearEvaluation sample = await _db.BedClearEvaluations.SingleAsync();
        sample.SourceAcknowledgementId.Should().BeNull();
        sample.AcknowledgedBySubject.Should().Be(_actor.ToString());
        sample.State.Should().Be(BedClearEvaluationState.Pending);
        sample.Verdict.Should().Be(BedClearDetectorVerdict.Unknown);
        (await _db.PrinterDispatchStates.CountAsync()).Should().Be(0);
        (await _db.DispatchLogs.CountAsync()).Should().Be(0);
        (await _db.BedClearCommandRecords.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Label_WhenActorCapturedSample_RejectsSelfReview()
    {
        BedClearEvaluation sample = await AddSampleAsync();
        sample.AcknowledgedBySubject = _actor.ToString();
        await _db.SaveChangesAsync();

        IActionResult result = await _controller.LabelAsync(
            sample.Id, new(BedClearGroundTruth.Clear), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        sample.GroundTruth.Should().BeNull();
        sample.IsActiveReference.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, BedClearGroundTruth.Clear, true)]
    [InlineData(false, BedClearGroundTruth.Clear, false)]
    [InlineData(true, BedClearGroundTruth.Occupied, false)]
    public async Task Label_WhenIndependent_ActivatesOnlyUsableClearReferences(
        bool eligible,
        BedClearGroundTruth label,
        bool active)
    {
        BedClearEvaluation sample = await AddSampleAsync();
        var camera = new Camera { Id = Guid.NewGuid(), Name = "Test camera" };
        var bedType = new BedType { Id = Guid.NewGuid(), Name = "Test plate" };
        _db.AddRange(camera, bedType);
        sample.CameraId = camera.Id;
        sample.BedTypeId = bedType.Id;
        sample.IsReferenceEligible = eligible;
        await _db.SaveChangesAsync();

        IActionResult result = await _controller.LabelAsync(sample.Id, new(label), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        sample.GroundTruth.Should().Be(label);
        sample.GroundTruthBySubject.Should().Be(_actor.ToString());
        sample.GroundTruthAtUtc.Should().NotBeNull();
        sample.IsActiveReference.Should().Be(active);
    }

    [Fact]
    public async Task GetMetrics_WithoutOccupiedLabels_LeavesFalseClearRateUnavailable()
    {
        BedClearEvaluation sample = await AddSampleAsync();
        sample.GroundTruth = BedClearGroundTruth.Clear;
        sample.GroundTruthBySubject = _actor.ToString();
        sample.Verdict = BedClearDetectorVerdict.Clear;
        await _db.SaveChangesAsync();

        JsonElement metrics = Metrics(await _controller.GetMetricsAsync(_printerId, CancellationToken.None));

        metrics.GetProperty("independentlyLabeledCount").GetInt32().Should().Be(1);
        metrics.GetProperty("agreementRate").GetDouble().Should().Be(1);
        metrics.GetProperty("falseClearRate").ValueKind.Should().Be(JsonValueKind.Null);
        metrics.GetProperty("independentlyLabeledOccupiedCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetMetrics_WithOccupiedLabels_CountsFalseClearsAndExcludesSelfLabels()
    {
        foreach (BedClearDetectorVerdict verdict in new[]
                 {
                     BedClearDetectorVerdict.Clear,
                     BedClearDetectorVerdict.Occupied,
                     BedClearDetectorVerdict.Unknown,
                 })
        {
            BedClearEvaluation sample = await AddSampleAsync();
            sample.GroundTruth = BedClearGroundTruth.Occupied;
            sample.GroundTruthBySubject = _actor.ToString();
            sample.Verdict = verdict;
        }

        BedClearEvaluation selfLabeled = await AddSampleAsync();
        selfLabeled.GroundTruth = BedClearGroundTruth.Occupied;
        selfLabeled.GroundTruthBySubject = selfLabeled.AcknowledgedBySubject;
        selfLabeled.Verdict = BedClearDetectorVerdict.Clear;
        await _db.SaveChangesAsync();

        JsonElement metrics = Metrics(await _controller.GetMetricsAsync(_printerId, CancellationToken.None));

        metrics.GetProperty("sampleCount").GetInt32().Should().Be(4);
        metrics.GetProperty("independentlyLabeledOccupiedCount").GetInt32().Should().Be(3);
        metrics.GetProperty("falseClearCount").GetInt32().Should().Be(1);
        metrics.GetProperty("falseClearRate").GetDouble().Should().BeApproximately(1d / 3, 0.0001);
        metrics.GetProperty("agreementRate").GetDouble().Should().BeApproximately(1d / 3, 0.0001);
    }

    [Fact]
    public async Task Delete_WhenReferenceExists_RemovesImageAndRecord()
    {
        BedClearEvaluation sample = await AddSampleAsync();
        sample.IsActiveReference = true;
        await _db.SaveChangesAsync();
        string path = Path.Combine(_root, sample.ImageFilePath!);
        await File.WriteAllBytesAsync(path, new byte[] { 1 });

        IActionResult result = await _controller.DeleteAsync(sample.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await _db.BedClearEvaluations.AnyAsync(evaluation => evaluation.Id == sample.Id)).Should().BeFalse();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_WhenSampleIsProcessing_RejectsDeletion()
    {
        BedClearEvaluation sample = await AddSampleAsync();
        sample.State = BedClearEvaluationState.Processing;
        await _db.SaveChangesAsync();

        IActionResult result = await _controller.DeleteAsync(sample.Id, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        (await _db.BedClearEvaluations.AnyAsync(evaluation => evaluation.Id == sample.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteBedType_WhenEvidenceExists_PreservesHistoricalPlateIdentity()
    {
        BedClearEvaluation sample = await AddSampleAsync();
        var bedType = new BedType { Id = Guid.NewGuid(), Name = "Removed plate type" };
        _db.Add(bedType);
        sample.BedTypeId = bedType.Id;
        Printer printer = await _db.Printers.SingleAsync();
        printer.BedTypeId = bedType.Id;
        await _db.SaveChangesAsync();

        await _db.BedTypes.Where(candidate => candidate.Id == bedType.Id).ExecuteDeleteAsync();
        await _db.Entry(sample).ReloadAsync();
        await _db.Entry(printer).ReloadAsync();

        sample.BedTypeId.Should().Be(bedType.Id);
        printer.BedTypeId.Should().BeNull();
    }

    [Fact]
    public async Task GetImage_WhenStoredPathEscapesSnapshotRoot_RejectsReadAndDeletion()
    {
        BedClearEvaluation sample = await AddSampleAsync();
        sample.ImageFilePath = Path.Combine("..", "outside.jpg");
        await _db.SaveChangesAsync();

        (await _controller.GetImageAsync(sample.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await _controller.DeleteAsync(sample.Id, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await _db.BedClearEvaluations.AnyAsync(candidate => candidate.Id == sample.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task GetImage_WhenPrinterIsInaccessible_HidesEvidence()
    {
        BedClearEvaluation sample = await AddSampleAsync();
        _authorization.Setup(service => service.CanAccessPrinterAsync(
                It.IsAny<ClaimsPrincipal>(), _printerId, It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        (await _controller.GetImageAsync(sample.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await _controller.GetMetricsAsync(_printerId, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await _controller.GetEvaluationsAsync(_printerId, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await _controller.CaptureAsync(_printerId, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await _controller.LabelAsync(sample.Id, new(BedClearGroundTruth.Clear), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
        (await _controller.DeleteAsync(sample.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static JsonElement Metrics(IActionResult result) =>
        JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Which.Value);

    private async Task<BedClearEvaluation> AddSampleAsync()
    {
        var sample = new BedClearEvaluation
        {
            Id = Guid.NewGuid(),
            PrinterId = _printerId,
            SourceAcknowledgementId = Guid.NewGuid(),
            AcknowledgedBySubject = Guid.NewGuid().ToString(),
            AcknowledgedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            State = BedClearEvaluationState.Complete,
            ImageFilePath = $"{Guid.NewGuid():N}.jpg",
        };
        _db.Add(sample);
        await _db.SaveChangesAsync();
        return sample;
    }
}
