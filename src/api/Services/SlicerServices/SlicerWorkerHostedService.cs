using System.Diagnostics;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Infrastructure.Temp;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.SlicerServices.Process;
using Farm.Web.Api.Services.SlicerServices.Progress;
using Farm.Web.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Hosted service that processes distributed slicing jobs in the background.
/// Regularly polls for available jobs and processes them using configured slicer executables.
/// </summary>
public class SlicerWorkerHostedService : BackgroundService
{
    private readonly ILogger<SlicerWorkerHostedService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SlicerWorkerConfiguration _config;
    private readonly ITempPathProvider _tempProvider;
    private readonly IProcessRunner _processRunner;
    private readonly ISettingsService _settingsService;
    private readonly SlicerSettings? _slicerSettings;

    public SlicerWorkerHostedService(
        ILogger<SlicerWorkerHostedService> logger,
        IServiceScopeFactory scopeFactory,
        SlicerWorkerConfiguration config,
        ITempPathProvider tempProvider,
        IProcessRunner processRunner,
        ISettingsService settingsService)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _config = config;
        _tempProvider = tempProvider;
        _processRunner = processRunner;
        _settingsService = settingsService;
        // Obtain SlicerSettings from settings service
        _slicerSettings = _settingsService.Get<SlicerSettings>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SlicerWorkerHostedService starting with WorkerId: {WorkerId}", _slicerSettings?.WorkerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Use SlicerSettings for runtime values
                if (_slicerSettings is not null && !_slicerSettings.Enabled)
                {
                    _logger.LogInformation("Slicer worker is disabled via settings. Sleeping...");
                    await Task.Delay(10000, stoppingToken); // Sleep 10s if disabled
                    continue;
                }

                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                ISlicerJobQueue jobQueue = scope.ServiceProvider.GetRequiredService<ISlicerJobQueue>();

                int pollingIntervalMs = 5000; // Default 5 seconds polling interval (no per-engine override)

                DistributedSlicingJob? dequeuedJob = await jobQueue.DequeueAsync(_slicerSettings?.WorkerId ?? "unknown-worker", null, stoppingToken);

                if (dequeuedJob != null)
                {
                    _logger.LogInformation("Dequeued slicing job {JobId} for processing", dequeuedJob.Id);
                    await ProcessJobAsync(dequeuedJob!, stoppingToken);
                }
                else
                {
                    await Task.Delay(pollingIntervalMs, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when service is stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SlicerWorkerHostedService execution loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("SlicerWorkerHostedService stopped");
    }

    private async Task ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        // Create a scope for all scoped services used while processing this job
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ISlicerJobQueue jobQueue = scope.ServiceProvider.GetRequiredService<ISlicerJobQueue>();
        ISlicerFileStorage fileStorage = scope.ServiceProvider.GetRequiredService<ISlicerFileStorage>();
        ISlicerProgressNotifier notifier = scope.ServiceProvider.GetRequiredService<ISlicerProgressNotifier>();

        DateTime started = DateTime.UtcNow;
        job.WorkerId = _config.WorkerId;

        try
        {
            _logger.LogInformation("Processing slicing job {JobId} (engine {EngineType})", job.Id, job.EngineType);
            await notifier.NotifyProgressAsync(new SlicingProgressUpdate
            {
                JobId = job.Id,
                Progress = 5,
                Status = SlicingJobStatus.Slicing,
                CurrentStep = "Queued to worker"
            }, cancellationToken);

            // Download model file
            string tempRoot = Path.GetFullPath(_tempProvider.GetTempRoot());
            string jobDir = Path.Combine(tempRoot, "slicer", job.Id.ToString());
            _ = Directory.CreateDirectory(jobDir);

            string inputPath = Path.Combine(jobDir, job.ModelFileName);
            using (FileStream inputStream = new(inputPath, FileMode.Create))
            {
                using HttpClient httpClient = new();
                HttpResponseMessage response = await httpClient.GetAsync(job.ModelFileUrl, cancellationToken);
                await response.Content.CopyToAsync(inputStream, cancellationToken);
            }

            await notifier.NotifyProgressAsync(new SlicingProgressUpdate
            {
                JobId = job.Id,
                Progress = 10,
                Status = SlicingJobStatus.Slicing,
                CurrentStep = "Model downloaded"
            }, cancellationToken);

            // For now, implement simple mock slicing until proper slicer integration is restored
            await notifier.NotifyProgressAsync(new SlicingProgressUpdate
            {
                JobId = job.Id,
                Progress = 50,
                Status = SlicingJobStatus.Slicing,
                CurrentStep = "Processing with unified settings system"
            }, cancellationToken);

            // Mock slicing process
            string outputGcode = Path.Combine(jobDir, Path.GetFileNameWithoutExtension(inputPath) + ".gcode");
            await File.WriteAllTextAsync(outputGcode,
                $"; Mock G-code for job {job.Id}\n" +
                $"; Generated at {DateTime.UtcNow:O}\n" +
                $"; Using unified settings system\n" +
                "G28 ; Home all axes\n" +
                "G1 Z10 F3000 ; Lift nozzle\n" +
                "; End of mock gcode",
                cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            using FileStream gcodeStream = File.OpenRead(outputGcode);
            string key = $"gcode/{job.Id}/{Path.GetFileName(outputGcode)}";
            string url = await fileStorage.UploadFileAsync(key, gcodeStream, "text/plain", cancellationToken);

            SlicingResult result = new()
            {
                Success = true,
                ResultFileUrl = new Uri(url, UriKind.RelativeOrAbsolute),
                OutputFileSizeBytes = new System.IO.FileInfo(outputGcode).Length,
                ProcessingTimeSeconds = (DateTime.UtcNow - started).TotalSeconds,
                EstimatedPrintTimeSeconds = 60 * 30, // mock 30m
                EstimatedFilamentUsageGrams = 10.0,
                LayerCount = 150
            };

            await jobQueue.CompleteJobAsync(job, result, cancellationToken: cancellationToken);
            await notifier.NotifyCompletionAsync(job, result, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Slicing job {JobId} failed: {ErrorMessage}", job.Id, ex.Message);

            try
            {
                // Decide whether to retry or fail permanently
                int maxRetries = _slicerSettings?.MaxRetryCount ?? 3;

                // Treat IO/timeout related exceptions as transient; also treat process exit (InvalidOperationException) as transient
                bool isTransient =
                    ex is System.IO.IOException
                    || ex is TimeoutException
                    || ex is System.Net.Http.HttpRequestException
                    || (ex is InvalidOperationException && !(ex.Message?.Contains("No gcode", StringComparison.OrdinalIgnoreCase) ?? false));

                if (isTransient && job.RetryCount < maxRetries)
                {
                    // Exponential backoff: base 10s
                    int delaySeconds = Math.Min(3600, (int)(Math.Pow(2, job.RetryCount) * 10));
                    TimeSpan delay = TimeSpan.FromSeconds(delaySeconds);

                    // Get jitter from SlicerSettings
                    double jitterToUse = _slicerSettings?.JitterPercent ?? 15.0;

                    await jobQueue.RequeueJobAsync(job, delay, jitterToUse, cancellationToken);

                    string message = $"Transient error occurred: {ex.Message}. Scheduled retry #{job.RetryCount} in {delaySeconds} seconds.";
                    await notifier.NotifyFailureAsync(job, message, cancellationToken);
                    _logger.LogInformation("Job {JobId} scheduled for retry #{RetryCount} in {DelaySeconds}s", job.Id, job.RetryCount, delaySeconds);
                }
                else
                {
                    string errMsg = ex.Message ?? ex.ToString();
                    await jobQueue.FailJobAsync(job.Id, errMsg, cancellationToken);
                    await notifier.NotifyFailureAsync(job, errMsg, cancellationToken);
                }
            }
            catch (Exception notifyEx)
            {
                _logger.LogError(notifyEx, "Failed to requeue/fail job {JobId} after exception: {ErrorMessage}", job.Id, notifyEx.Message);
            }
        }
        finally
        {
            try
            {
                // Cleanup job directory
                string tempRoot = Path.GetFullPath(_tempProvider.GetTempRoot());
                string jobDir = Path.Combine(tempRoot, "slicer", job.Id.ToString());
                if (Directory.Exists(jobDir))
                {
                    Directory.Delete(jobDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to cleanup job temp dir for {JobId}: {ErrorMessage}", job.Id, ex.Message);
            }
        }
    }
}
