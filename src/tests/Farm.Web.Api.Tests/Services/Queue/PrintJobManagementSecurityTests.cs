using Farm.Api.Services.PrintQueue;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.Services.Queue;

public sealed class PrintJobManagementSecurityTests
{
    private readonly Mock<IPrintJobManagementRepository> _repository = new();
    private readonly Mock<IPrintersService> _printers = new();
    private readonly Mock<IStoragePathService> _storagePaths = new();
    private readonly Mock<IHubContext<PrinterHub>> _hub = new();
    private readonly Mock<IStoredFileOperationsService> _fileOperations = new();
    private readonly Mock<IPrinterStatusCacheReader> _statusCache = new();
    private readonly RecordingLogger<PrintJobManagementService> _logger = new();

    [Fact]
    public async Task DispatchJobAsync_WithDisabledPrinter_RejectsBeforeStorageOrBackendAccess()
    {
        PrintJob job = CreateJob();
        job.AssignedPrinter!.IsEnabled = false;
        ConfigureJob(job);
        PrintJobManagementService service = CreateService();

        Func<Task> dispatch = () => service.DispatchJobAsync(job.Id.ToString(), "user-1");

        _ = await dispatch.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Cannot dispatch a job to a disabled printer.");
        _storagePaths.Verify(
            paths => paths.GetGcodeStorageDirectory(),
            Times.Never);
        _printers.Verify(
            printers => printers.UploadAndStartPrintAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<IProgress<UploadAndPrintStage>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _repository.Verify(
            repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchJobAsync_WithMissingArtifact_ReturnsAndLogsNoStoragePath()
    {
        const string privateRoot = @"C:\private-storage\tenant-secret";
        PrintJob job = CreateJob();
        job.GcodeFile!.FilePath = "hidden-folder";
        ConfigureJob(job);
        _storagePaths.Setup(paths => paths.GetGcodeStorageDirectory()).Returns(privateRoot);
        PrintJobManagementService service = CreateService();

        var result = await service.DispatchJobAsync(job.Id.ToString(), "user-1");

        _ = result.FailureReason.Should().Be("The G-code artifact is unavailable for dispatch.");
        _ = result.FailureReason.Should().NotContain(privateRoot);
        _ = _logger.Entries.Should().NotContain(entry =>
            entry.Contains(privateRoot, StringComparison.Ordinal)
            || entry.Contains("hidden-folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchJobAsync_WithBackendFailure_RedactsBackendDetailFromResultAndLogs()
    {
        const string backendSecret =
            "https://admin:super-secret@10.0.0.5/private?apiKey=top-secret";
        string storageRoot = Path.Combine(
            Path.GetTempPath(),
            $"printfarmer-dispatch-{Guid.NewGuid()}");
        Directory.CreateDirectory(storageRoot);
        string fileName = "artifact.gcode";
        await File.WriteAllTextAsync(Path.Combine(storageRoot, fileName), string.Empty);

        try
        {
            PrintJob job = CreateJob();
            job.GcodeFile!.FileName = fileName;
            ConfigureJob(job);
            _storagePaths.Setup(paths => paths.GetGcodeStorageDirectory()).Returns(storageRoot);
            _printers
                .Setup(printers => printers.UploadAndStartPrintAsync(
                    job.AssignedPrinterId!.Value,
                    job.GcodeFile.Name,
                    It.IsAny<Stream>(),
                    It.IsAny<IProgress<UploadAndPrintStage>?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(UploadAndPrintResult.Fail(
                    UploadAndPrintStage.StartingPrint,
                    backendSecret));
            PrintJobManagementService service = CreateService();

            var result = await service.DispatchJobAsync(job.Id.ToString(), "user-1");

            _ = result.FailureReason.Should().Be(
                "The printer could not start the dispatched job.");
            _ = result.FailureReason.Should().NotContain(backendSecret);
            _ = _logger.Entries.Should().NotContain(entry =>
                entry.Contains(backendSecret, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(storageRoot, recursive: true);
        }
    }

    private void ConfigureJob(PrintJob job)
    {
        _repository
            .Setup(repository => repository.GetByIdWithRelationsAsync(
                job.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        _repository
            .Setup(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private PrintJobManagementService CreateService() =>
        new(
            _repository.Object,
            _logger,
            _printers.Object,
            _storagePaths.Object,
            _hub.Object,
            _fileOperations.Object,
            _statusCache.Object);

    private static PrintJob CreateJob()
    {
        Guid printerId = Guid.NewGuid();
        return new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "Secure dispatch",
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
                FileName = "missing-artifact.gcode",
                FilePath = string.Empty,
            },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            NoOpScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = logLevel;
            _ = eventId;
            Entries.Add(formatter(state, exception));
            if (exception is not null)
            {
                Entries.Add(exception.ToString());
            }
        }

        private sealed class NoOpScope : IDisposable
        {
            public static NoOpScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
