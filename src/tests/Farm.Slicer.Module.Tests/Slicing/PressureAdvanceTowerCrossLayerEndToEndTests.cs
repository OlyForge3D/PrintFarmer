extern alias OrcaWorker;

using System.Security.Claims;
using System.Text.Json;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Contracts.Libraries;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrcaWorkerCore = OrcaWorker::Farm.Slicer.Worker.Core;
using OrcaWorkerSvc = OrcaWorker::Farm.OrcaSlicer.Worker.Services;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Issue #2136: drives a pressure advance tower calibration request through the real production
/// chain -- <see cref="SliceJobController.SubmitAsync"/>, a real EF Core-backed
/// <see cref="SliceJob"/> persisted through <see cref="EfSliceJobRepository"/>, the real
/// <see cref="DbSlicerJobQueue.DequeueAsync"/> claim/mapping (not a hand-built
/// <c>DistributedSlicingJob</c>), and finally the worker's own
/// <see cref="OrcaWorkerSvc.OrcaSlicingPipelineService.PrepareCalibrationModel"/> and
/// <see cref="OrcaWorkerSvc.OrcaSlicingPipelineService.ApplyPressureAdvanceTowerGcodeAsync"/>
/// entrypoints, via the existing <c>extern alias OrcaWorker</c> bridge (see
/// <c>ProfileFamilyEndToEndHttpTests</c>).
/// </summary>
/// <remarks>
/// <see cref="SliceJobControllerCalibrationTests"/> and <c>CalibrationTests</c> (in
/// <c>Farm.OrcaSlicer.Worker.Tests</c>) each cover the submission boundary and the worker
/// pipeline stages in isolation, against a mocked repository and a hand-constructed
/// <c>DistributedSlicingJob</c> respectively. Neither proves that a client-submitted
/// <c>calibration.params</c> map actually survives the real queue/claim mapping
/// (<see cref="DbSlicerJobQueue"/>'s private <c>ToDistributedJob</c>) unchanged before the
/// worker ever sees it. This test closes that gap by asserting on the exact numeric advance
/// values embedded in the injected <c>layer_change_gcode</c>, tracing them back to the
/// <c>start_advance</c>/<c>advance_step</c>/<c>band_count</c> values submitted over HTTP -- no
/// OrcaSlicer CLI invocation is required or performed.
/// </remarks>
public sealed class PressureAdvanceTowerCrossLayerEndToEndTests : IAsyncDisposable
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"printfarmer-pa-tower-crosslayer-{Guid.NewGuid():N}.db");

    private readonly string _tempDir =
        Path.Join(Path.GetTempPath(), $"printfarmer-pa-tower-crosslayer-work-{Guid.NewGuid():N}");

    [Fact]
    public async Task SubmitThenClaimThenWorkerPipeline_PressureAdvanceTowerRequest_InjectedGcodeReflectsSubmittedParams()
    {
        Directory.CreateDirectory(_tempDir);
        string connectionString = $"Data Source={_databasePath}";
        await using SlicerDbContext context = CreateContext(connectionString);
        _ = await context.Database.EnsureCreatedAsync();

        // --- Stage 1: submit through the real SliceJobController, backed by a real EF Core
        // repository (not a mock) so the resulting SliceJob is genuinely persisted. ---
        SliceJobController controller = CreateController(context, out Guid userId);
        var request = new SubmitSliceJobRequest
        {
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Priority = 1,
            Calibration = new CalibrationRequest
            {
                Method = "pressure_advance_tower",
                Params = new Dictionary<string, double>
                {
                    ["start_advance"] = 0.1,
                    ["advance_step"] = 0.05,
                    ["band_height_mm"] = 5,
                    ["band_count"] = 3,
                },
            },
        };

        IActionResult submitResult = await controller.SubmitAsync(request, CancellationToken.None);

        _ = submitResult.Should().BeOfType<CreatedResult>("a valid pressure advance tower request must be accepted and queued");
        SliceJob persisted = await context.SliceJobs.AsNoTracking().SingleAsync();
        _ = persisted.CalibrationMethod.Should().Be("pressure_advance_tower");
        _ = persisted.Status.Should().Be(SliceJobStatus.Queued);

        // --- Stage 2: claim through the real production queue mapping -- DbSlicerJobQueue's own
        // DequeueAsync/ToDistributedJob, not a hand-constructed DistributedSlicingJob. This is the
        // exact call a worker makes to receive a job. ---
        var queue = new DbSlicerJobQueue(new EfSliceJobRepository(context));
        DistributedSlicingJob? claimed = await queue.DequeueAsync(Guid.NewGuid().ToString());

        _ = claimed.Should().NotBeNull("the queued job must be claimable by a worker through the real mapping");
        _ = claimed!.CalibrationMethod.Should().Be("pressure_advance_tower");
        _ = claimed.CalibrationParamsJson.Should().NotBeNullOrEmpty();

        // The mapped job's params JSON must be the same values submitted over HTTP, having
        // survived controller serialization, EF Core persistence, and the queue's claim mapping
        // unchanged.
        Dictionary<string, double> mappedParams =
            JsonSerializer.Deserialize<Dictionary<string, double>>(claimed.CalibrationParamsJson!)!;
        _ = mappedParams["start_advance"].Should().Be(0.1);
        _ = mappedParams["advance_step"].Should().Be(0.05);
        _ = mappedParams["band_count"].Should().Be(3);

        // --- Stage 3: hand the real, mapped DistributedSlicingJob to the worker's own pipeline
        // entrypoints, exactly as OrcaSlicingPipelineService.RunAsync would. ---
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources");
        string towerPath = Path.Combine(calibResourcesRoot, "pressure_advance", "tower_with_seam.drc");
        Directory.CreateDirectory(Path.GetDirectoryName(towerPath)!);
        await File.WriteAllTextAsync(towerPath, "fake-pa-tower-resource");

        OrcaWorkerSvc.OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        string preparedModelPath = pipeline.PrepareCalibrationModel(claimed, workDir);
        _ = File.Exists(preparedModelPath).Should().BeTrue();

        string processJsonPath = Path.Combine(_tempDir, $"process-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(processJsonPath, """{"name": "Test Process"}""");
        string machineJsonPath = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(machineJsonPath, """{"name": "Test Machine", "gcode_flavor": "klipper"}""");

        await OrcaWorkerSvc.OrcaSlicingPipelineService.ApplyPressureAdvanceTowerGcodeAsync(
            claimed, processJsonPath, machineJsonPath, CancellationToken.None);

        string updatedProcessContent = await File.ReadAllTextAsync(processJsonPath);
        using JsonDocument doc = JsonDocument.Parse(updatedProcessContent);
        string layerChangeGcode = doc.RootElement.GetProperty("layer_change_gcode").GetString()!;

        // start_advance=0.1, advance_step=0.05, band_count=3 must compound to exactly these three
        // per-band advance values (bottom, middle, top) -- proving the value submitted over HTTP
        // is the same value that reaches the injected gcode, through every intermediate stage.
        _ = layerChangeGcode.Should().Contain("SET_PRESSURE_ADVANCE ADVANCE=0.1");
        _ = layerChangeGcode.Should().Contain("SET_PRESSURE_ADVANCE ADVANCE=0.15");
        _ = layerChangeGcode.Should().Contain("SET_PRESSURE_ADVANCE ADVANCE=0.2");
    }

    private static SliceJobController CreateController(SlicerDbContext context, out Guid userId)
    {
        userId = Guid.NewGuid();
        var repository = new EfSliceJobRepository(context);
        var events = new Mock<ISliceJobEventService>();

        Mock<IRateLimitService> rateLimit = new();
        _ = rateLimit
            .Setup(instance => instance.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerRateLimitResult(true));

        return new SliceJobController(
            repository,
            events.Object,
            NullLogger<SliceJobController>.Instance,
            new Mock<IArtifactsService>().Object,
            rateLimit.Object,
            new SliceJobMetrics(),
            new Mock<IWorkerAuthService>().Object,
            new Mock<IWorkerRepository>().Object,
            new Mock<ISlicerRegistry>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                            "Test")),
                },
            },
        };
    }

    private static OrcaWorkerSvc.OrcaSlicingPipelineService CreatePipeline(string calibResourcesRoot)
    {
        IConfiguration configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Worker:WorkingDirectory"] = Path.Combine(Path.GetTempPath(), $"orca-worker-{Guid.NewGuid():N}"),
                    ["SlicerApi:BaseUrl"] = "http://localhost",
                    ["Worker:CalibrationResourcesPath"] = calibResourcesRoot,
                })
                .Build();
        return new OrcaWorkerSvc.OrcaSlicingPipelineService(
            new HttpClient(),
            new NullProgressReporter(),
            NullLogger<OrcaWorkerSvc.OrcaSlicingPipelineService>.Instance,
            configuration,
            new OrcaWorkerCore.WorkerStateService());
    }

    private static SlicerDbContext CreateContext(string connectionString)
    {
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new SlicerDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        await Task.CompletedTask;
    }

    private sealed class NullProgressReporter : OrcaWorkerCore.IProgressReporter
    {
        public Task ReportProgressAsync(
            Guid jobId,
            Guid claimToken,
            int progress,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReportCompletionAsync(
            DistributedSlicingJob job,
            SlicingResult result,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReportFailureAsync(
            Guid jobId,
            Guid claimToken,
            string errorMessage,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
