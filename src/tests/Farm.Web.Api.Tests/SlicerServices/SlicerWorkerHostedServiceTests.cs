using Farm.Web.Api.Infrastructure.Temp;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Api.Services.SlicerServices.Process;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Tests.SlicerServices;

public class SlicerWorkerHostedServiceTests
{
    private class FakeExecutableManager : ISlicerExecutableManager
    {
        private readonly string _exePath;
        private readonly string _argsTemplate;
        public FakeExecutableManager(string exePath, string argsTemplate = "{input} -o {output}") { _exePath = exePath; _argsTemplate = argsTemplate; }
        public bool TryGetExecutable(SlicerEngineType engine, out string? executablePath, out string? argsTemplate)
        {
            executablePath = _exePath;
            argsTemplate = _argsTemplate;
            return true;
        }

        public Task<bool> ValidateSlicerInstallationAsync(SlicerEngineType engine, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private class FakeFileStorage : ISlicerFileStorage
    {
        public List<(string key, byte[] data)> Uploads { get; } = new List<(string, byte[])>();
        public Task<string> UploadFileAsync(string key, Stream fileStream, string contentType, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            fileStream.CopyTo(ms);
            Uploads.Add((key, ms.ToArray()));
            return Task.FromResult($"/mock/{key}");
        }
        public Task<string> UploadFileAsync(string key, byte[] fileData, string contentType, CancellationToken cancellationToken = default) { Uploads.Add((key, fileData)); return Task.FromResult($"/mock/{key}"); }
        public Task<Stream> DownloadFileAsync(string keyOrUrl, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream([]));
        public Task<byte[]> DownloadFileBytesAsync(string keyOrUrl, CancellationToken cancellationToken = default) => Task.FromResult(new byte[] { 0x20 });
        public Task<bool> FileExistsAsync(string keyOrUrl, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DeleteFileAsync(string keyOrUrl, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SlicerFileMetadata?> GetFileMetadataAsync(string keyOrUrl, CancellationToken cancellationToken = default) => Task.FromResult<SlicerFileMetadata?>(null);
        public Task<string> GenerateSignedUrlAsync(string keyOrUrl, TimeSpan expiration, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public void CleanupTempFiles(TimeSpan maxAge, CancellationToken cancellationToken = default) { }
    }

    private class FakeJobQueue : ISlicerJobQueue
    {
        public bool CompletedCalled { get; private set; }
        public bool FailedCalled { get; private set; }
        public bool RequeuedCalled { get; private set; }
        public DistributedSlicingJob? LastRequeuedJob { get; private set; }
        public double? LastRequeueJitterPercent { get; private set; }
        public Task EnqueueAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DistributedSlicingJob?> DequeueAsync(string workerId, SlicerEngineType? preferredEngine = null, CancellationToken cancellationToken = default) => Task.FromResult<DistributedSlicingJob?>(null);
        public Task CompleteJobAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default) { CompletedCalled = true; return Task.CompletedTask; }
        public Task FailJobAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default) { FailedCalled = true; return Task.CompletedTask; }
        public Task UpdateProgressAsync(Guid jobId, int progress, string? currentStep = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DistributedSlicingJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<DistributedSlicingJob?>(null);
        public Task CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SlicerQueueStats> GetQueueStatsAsync(SlicerEngineType? engine = null, CancellationToken cancellationToken = default) => Task.FromResult(new SlicerQueueStats());
        public Task<List<DistributedSlicingJob>> GetUserJobsAsync(Guid userId, int? limit = null, CancellationToken cancellationToken = default) => Task.FromResult(new List<DistributedSlicingJob>());
        public Task CleanupOldJobsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RequeueFailedJobsAsync(int maxRetryCount = 3, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RequeueJobAsync(DistributedSlicingJob job, TimeSpan? delay = null, double jitterPercent = 0.0, CancellationToken cancellationToken = default)
        {
            RequeuedCalled = true;
            // Simulate queue behavior: increment retry count and set scheduled time
            job.RetryCount++;
            job.LastRetryAt = DateTime.UtcNow;
            if (delay.HasValue && delay.Value > TimeSpan.Zero)
            {
                job.ScheduledAt = DateTime.UtcNow.Add(delay.Value);
            }
            LastRequeuedJob = job;
            LastRequeueJitterPercent = jitterPercent;
            return Task.CompletedTask;
        }
        public Task<DistributedSlicingJob?> FindExistingJobAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default) => Task.FromResult<DistributedSlicingJob?>(null);
        public Task<bool> JobExistsAsync(Guid correlationId, string checksum, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    [Fact(Timeout = 20000)]
    public async Task Worker_ProcessJob_CreatesAndUploadsGcode_AndCompletesJob()
    {
        // Arrange: temp exe and a TestProcessRunner that writes a .gcode file into the working directory
        var tempExe = Path.Combine(Path.GetTempPath(), "fake-exe" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tempExe, "#!/bin/sh\nexit 0");

        var testRunner = new Farm.Web.Api.Tests.TestUtilities.TestProcessRunner(psi =>
        {
            // Create a handle that will write a .gcode file into WorkingDirectory when waiting
            return new WorkerTestProcessHandle(psi.WorkingDirectory ?? Path.GetTempPath());
        });

        var services = new ServiceCollection();
        services.AddLogging();
        var cfg = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton<ITempPathProvider, DefaultTempPathProvider>();
        services.AddSingleton<ISlicerExecutableManager>(new FakeExecutableManager(tempExe));
        var fileStorage = new FakeFileStorage();
        services.AddSingleton<ISlicerFileStorage>(fileStorage);
        var jobQueue = new FakeJobQueue();
        services.AddSingleton<ISlicerJobQueue>(jobQueue);
        services.AddSingleton<ISlicerProgressNotifier, TestNotifier>();
        services.AddSingleton<ISlicerSettingsService, InMemorySlicerSettingsService>();
        services.AddSingleton<Farm.Web.Api.Services.SlicerServices.Process.IProcessRunner>(testRunner);

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var logger = sp.GetRequiredService<ILogger<SlicerWorkerHostedService>>();
        var settingsSvc = sp.GetRequiredService<ISlicerSettingsService>();
        services.AddSingleton<IPrintFarmerTelemetryService, NoopTelemetry>();
        var sp2 = services.BuildServiceProvider();
        var scopeFactory2 = sp2.GetRequiredService<IServiceScopeFactory>();
        var worker = new SlicerWorkerHostedService(scopeFactory2, sp2.GetRequiredService<ISlicerExecutableManager>(), sp2.GetRequiredService<ITempPathProvider>(), logger, cfg, settingsSvc, sp2.GetRequiredService<Farm.Web.Api.Services.SlicerServices.Process.IProcessRunner>(), sp2.GetRequiredService<IPrintFarmerTelemetryService>());

        // Create a job that refers to a model (download will return small bytes)
        var job = new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            EngineType = SlicerEngineType.PrusaSlicer,
            ModelFileUrl = new Uri("test://model"),
            ModelFileName = "model.stl"
        };

        // Invoke private ProcessJobAsync via reflection
        var task = (Task)typeof(SlicerWorkerHostedService).GetMethod("ProcessJobAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(worker, [job, CancellationToken.None])!;

        await task; // wait for completion

        // Assert: jobQueue.CompleteJobAsync was called
        jobQueue.CompletedCalled.Should().BeTrue();
        // Assert: gcode uploaded
        fileStorage.Uploads.Should().NotBeEmpty();
    }

    private class WorkerTestProcessHandle : IProcessHandle
    {
        private readonly System.IO.MemoryStream _ms;
        private readonly System.IO.StreamReader _sr;
        private bool _exited;
        private readonly string _workingDir;

        public WorkerTestProcessHandle(string workingDir)
        {
            _workingDir = workingDir;
            var bytes = System.Text.Encoding.UTF8.GetBytes("Progress: 100%\nExported gcode\n");
            _ms = new System.IO.MemoryStream(bytes);
            _sr = new System.IO.StreamReader(_ms);
        }

        public System.IO.StreamReader StandardOutput => _sr;
        public System.IO.StreamReader StandardError => new System.IO.StreamReader(new System.IO.MemoryStream());
        public bool HasExited => _exited;
        public int ExitCode { get; private set; } = 0;

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            // Simulate slicer producing a gcode file in working directory before exiting
            try
            {
                var path = Path.Combine(_workingDir, "output.gcode");
                File.WriteAllText(path, "; gcode mock");
            }
            catch { }
            _exited = true;
            return Task.FromResult(0);
        }

        public void Kill()
        {
            _exited = true;
            ExitCode = -1;
        }
    }

    private class TestNotifier : ISlicerProgressNotifier
    {
        public List<SlicingProgressUpdate> Updates { get; } = new List<SlicingProgressUpdate>();
        public Task NotifyProgressAsync(SlicingProgressUpdate update, CancellationToken cancellationToken = default)
        {
            lock (Updates)
            { Updates.Add(update); }
            return Task.CompletedTask;
        }
        public Task NotifyCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyFailureAsync(DistributedSlicingJob job, string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SubscribeToJobAsync(Guid jobId, string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnsubscribeFromJobAsync(Guid jobId, string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact(Timeout = 20000)]
    public async Task Worker_TransientFailure_RequeuesWithBackoff()
    {
        // Arrange: temp exe and a TestProcessRunner that returns a handle with non-zero exit code
        var tempExe = Path.Combine(Path.GetTempPath(), "fake-exe" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tempExe, "#!/bin/sh\nexit 1");

        var testRunner = new Farm.Web.Api.Tests.TestUtilities.TestProcessRunner(psi =>
        {
            // Return a handle that simulates a failure (exit code 1 and stderr)
            return new FailingProcessHandle();
        });

        var services = new ServiceCollection();
        services.AddLogging();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SlicerWorker:JitterPercent"] = "5.0" }).Build();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton<ITempPathProvider, DefaultTempPathProvider>();
        services.AddSingleton<ISlicerExecutableManager>(new FakeExecutableManager(tempExe));
        var fileStorage = new FakeFileStorage();
        services.AddSingleton<ISlicerFileStorage>(fileStorage);
        var jobQueue = new FakeJobQueue();
        services.AddSingleton<ISlicerJobQueue>(jobQueue);
        services.AddSingleton<ISlicerProgressNotifier, TestNotifier>();
        services.AddSingleton<ISlicerSettingsService, InMemorySlicerSettingsService>();
        services.AddSingleton<Farm.Web.Api.Services.SlicerServices.Process.IProcessRunner>(testRunner);

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var logger = sp.GetRequiredService<ILogger<SlicerWorkerHostedService>>();
        var settingsSvc = sp.GetRequiredService<ISlicerSettingsService>();
        services.AddSingleton<IPrintFarmerTelemetryService, NoopTelemetry>();
        var sp2 = services.BuildServiceProvider();
        var scopeFactory2 = sp2.GetRequiredService<IServiceScopeFactory>();
        var worker = new SlicerWorkerHostedService(scopeFactory2, sp2.GetRequiredService<ISlicerExecutableManager>(), sp2.GetRequiredService<ITempPathProvider>(), logger, cfg, settingsSvc, sp2.GetRequiredService<Farm.Web.Api.Services.SlicerServices.Process.IProcessRunner>(), sp2.GetRequiredService<IPrintFarmerTelemetryService>());

        // Create a job that refers to a model
        var job = new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            EngineType = SlicerEngineType.OrcaSlicer,
            ModelFileUrl = new Uri("test://model"),
            ModelFileName = "model.stl",
            RetryCount = 0
        };

        // Invoke private ProcessJobAsync via reflection
        var task = (Task)typeof(SlicerWorkerHostedService).GetMethod("ProcessJobAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(worker, [job, CancellationToken.None])!;

        await task; // wait for completion

        // Assert: the job was requeued instead of permanently failed
        jobQueue.RequeuedCalled.Should().BeTrue();
        jobQueue.LastRequeuedJob.Should().NotBeNull();
        jobQueue.LastRequeuedJob!.RetryCount.Should().BeGreaterThan(0);
        jobQueue.LastRequeuedJob!.ScheduledAt.Should().NotBeNull();
        var scheduledValue = jobQueue.LastRequeuedJob!.ScheduledAt ?? DateTime.MinValue;
        scheduledValue.Should().BeAfter(DateTime.UtcNow);

        // Assert: worker passed configured jitter percent into queue
        var jitterValue = jobQueue.LastRequeueJitterPercent ?? double.NaN;
        jitterValue.Should().BeApproximately(5.0, 0.0001);
    }

    private class FailingProcessHandle : IProcessHandle
    {
        private readonly System.IO.StreamReader _srOut;
        private readonly System.IO.StreamReader _srErr;
        public FailingProcessHandle()
        {
            _srOut = new System.IO.StreamReader(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("Progress: 50%\n")));
            _srErr = new System.IO.StreamReader(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("fatal error")));
        }
        public System.IO.StreamReader StandardOutput => _srOut;
        public System.IO.StreamReader StandardError => _srErr;
        public bool HasExited => true;
        public int ExitCode { get; private set; } = 1;
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ExitCode);
        }
        public void Kill() { }
    }
}

internal sealed class NoopTelemetry : IPrintFarmerTelemetryService
{
    public System.Diagnostics.Activity? StartActivity(string name, System.Diagnostics.ActivityKind kind = System.Diagnostics.ActivityKind.Internal) => null;
    public void RecordApiCall(string endpoint, string method, int statusCode, TimeSpan duration) { }
    public void RecordPrinterOperation(string operation, string printerId, bool success) { }
    public void RecordSlicerOperation(string operation, string engine, bool success, TimeSpan? duration = null) { }
    public void RecordFileOperation(string operation, string fileType, long? fileSize = null) { }
    public void RecordDatabaseOperation(string table, string operation, int recordCount) { }
}
