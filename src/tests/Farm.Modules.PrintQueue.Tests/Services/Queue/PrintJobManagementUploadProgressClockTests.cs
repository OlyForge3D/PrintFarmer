using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Modules.PrintQueue.DTOs.SignalR;
using Farm.Modules.PrintQueue.Services.PrintQueue;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Modules.PrintQueue.Tests.Services.Queue;

/// <summary>
/// Pins the manual-dispatch upload-progress throttle to the injected monotonic clock (#2972):
/// progress snapshots are emitted every 500ms of <see cref="TimeProvider"/> elapsed time, not
/// a <see cref="System.Diagnostics.Stopwatch"/>.
/// </summary>
public sealed class PrintJobManagementUploadProgressClockTests : IDisposable
{
    private static readonly byte[] ArtifactBytes = "G28\nG1 X10 Y10\n"u8.ToArray();

    private readonly string _storageRoot = Path.Join(
        Path.GetTempPath(),
        $"printfarmer-upload-progress-{Guid.NewGuid():N}");

    private readonly List<DispatchUploadProgressDto> _progress = [];

    public PrintJobManagementUploadProgressClockTests()
    {
        Directory.CreateDirectory(_storageRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DispatchJobAsync_UploadProgressThrottle_UsesInjectedMonotonicClock()
    {
        const string fileName = "artifact.gcode";
        await File.WriteAllBytesAsync(Path.Join(_storageRoot, fileName), ArtifactBytes);
        var clock = new SteppingTimeProvider();
        PrintJob job = CreateJob(fileName);

        var printers = new Mock<IPrintersService>();
        printers
            .Setup(service => service.UploadAndStartPrintAsync(
                job.AssignedPrinterId!.Value,
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<IProgress<UploadAndPrintStage>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                Guid _,
                string _,
                Stream stream,
                IProgress<UploadAndPrintStage>? _,
                CancellationToken ct) =>
            {
                // One byte per read, 100ms of fake monotonic time per read.
                byte[] buffer = new byte[1];
                while (true)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(100));
                    if (await stream.ReadAsync(buffer.AsMemory(0, 1), ct) == 0)
                    {
                        return UploadAndPrintResult.Ok();
                    }
                }
            });

        PrintJobManagementService service = CreateService(job, printers.Object, clock);

        _ = await service.DispatchJobAsync(job.Id.ToString(), "user-1");

        // Forced 0% snapshot, a throttled report each time 500ms elapses (bytes 5, 10, 15 at
        // 500ms, 1000ms, 1500ms), then the forced 100% snapshot. A wall-clock throttle would
        // collapse this to the two forced snapshots because the fake upload takes microseconds.
        _progress.Select(dto => dto.BytesSent).Should().Equal(0, 5, 10, 15, ArtifactBytes.Length);
        _progress.Select(dto => dto.IsCompleted).Should().Equal(false, false, false, false, true);
    }

    private PrintJobManagementService CreateService(
        PrintJob job,
        IPrintersService printers,
        TimeProvider clock)
    {
        var repository = new Mock<IPrintJobManagementRepository>();
        repository
            .Setup(value => value.GetByIdWithRelationsAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        repository
            .Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var claimService = new Mock<IDispatchClaimService>();
        claimService
            .Setup(value => value.AcquireClaimAsync(It.IsAny<DispatchClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchClaimResult.Ok(new QueueDispatchAttempt
            {
                Id = Guid.NewGuid(),
                PrintJobId = job.Id,
                PrinterId = job.AssignedPrinterId!.Value,
                BackendFileName = "dispatch.gcode",
                ClaimedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            }));
        claimService
            .Setup(value => value.RecordBackendCallStartedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        claimService
            .Setup(value => value.RecordBackendAcceptedAsync(
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var storagePaths = new Mock<IStoragePathService>();
        storagePaths.Setup(paths => paths.GetGcodeStorageDirectory()).Returns(_storageRoot);

        return new PrintJobManagementService(
            repository.Object,
            NullLogger<PrintJobManagementService>.Instance,
            printers,
            storagePaths.Object,
            CreateCapturingHub(),
            Mock.Of<IStoredFileOperationsService>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            dispatchClaimService: claimService.Object,
            timeProvider: clock);
    }

    private IHubContext<PrinterHub> CreateCapturingHub()
    {
        var client = new Mock<IClientProxy>();
        client
            .Setup(proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Callback((string method, object?[] args, CancellationToken _) =>
            {
                if (method == "dispatchuploadprogress" && args is [DispatchUploadProgressDto dto])
                {
                    _progress.Add(dto);
                }
            })
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(value => value.Group(It.IsAny<string>())).Returns(client.Object);
        clients.SetupGet(value => value.All).Returns(client.Object);
        var hub = new Mock<IHubContext<PrinterHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        return hub.Object;
    }

    private static PrintJob CreateJob(string fileName)
    {
        Guid printerId = Guid.NewGuid();
        return new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "Upload progress clock",
            Status = PrintJobStatus.Assigned,
            AssignedPrinterId = printerId,
            AssignedPrinter = new Printer
            {
                Id = printerId,
                Name = "Printer",
                IsEnabled = true,
            },
            GcodeFileId = Guid.NewGuid(),
            GcodeFile = new GcodeFile
            {
                Id = Guid.NewGuid(),
                Name = "display-name.gcode",
                FileName = fileName,
                FilePath = string.Empty,
            },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
    }

    /// <summary>Monotonic-only fake clock; wall time is left on the system clock.</summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
