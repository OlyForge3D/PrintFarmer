using System.Diagnostics;
using Farm.Web.Api.Infrastructure.Temp;
using Farm.Web.Api.Services.SlicerServices.Process;
using Farm.Web.Api.Services.SlicerServices.Progress;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Background service that consumes slicing jobs from the configured job queue and executes
/// either a configured CLI slicer or a fallback simulated slicer.
/// </summary>
public class SlicerWorkerHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISlicerExecutableManager _exeManager;
    private readonly ITempPathProvider _tempProvider;
    private readonly IUnifiedLoggingService _logger;
    private readonly SlicerWorkerConfiguration _config;
    private readonly ISlicerSettingsService _settingsService;
    private readonly Farm.Web.Api.Services.SlicerServices.Process.IProcessRunner _processRunner;
    private readonly IPrintFarmerTelemetryService _telemetry;

    public SlicerWorkerHostedService(
        IServiceScopeFactory scopeFactory,
        ISlicerExecutableManager exeManager,
        ITempPathProvider tempProvider,
    IUnifiedLoggingService logger,
        IConfiguration cfg,
        ISlicerSettingsService settingsService,
        Farm.Web.Api.Services.SlicerServices.Process.IProcessRunner processRunner,
        IPrintFarmerTelemetryService telemetry)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _exeManager = exeManager ?? throw new ArgumentNullException(nameof(exeManager));
        _tempProvider = tempProvider ?? throw new ArgumentNullException(nameof(tempProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = new SlicerWorkerConfiguration();
        cfg?.GetSection("SlicerWorker")?.Bind(_config);
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        category: "IDisposableAnalyzers",
        checkId: "IDISP013:Await in using",
        Justification = "IServiceScope implements only synchronous dispose; awaited operations occur before dispose. Scope disposal is fast and non-blocking, and converting to async scope adds no safety benefit here.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using Activity? activity = _telemetry.StartActivity("SlicerWorkerHostedService.ExecuteAsync");
        _logger.LogInformation($"Slicer worker started (worker id {_config.WorkerId})");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using Activity? loopActivity = _telemetry.StartActivity("SlicerWorkerHostedService.WorkLoop");

                _logger.LogDebug($"Getting runtime settings from SlicerSettingsService");
                SlicerSettingsDto runtimeSettings = _settingsService.GetSettings();
                _logger.LogDebug($"Got runtime settings: Enabled={runtimeSettings.Enabled}");

                if (!runtimeSettings.Enabled)
                {
                    _logger.LogDebug($"Slicer worker is disabled via runtime settings; sleeping");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                _logger.LogDebug($"Attempting to dequeue slicer job");
                // Attempt to dequeue a job (non-blocking)
                DistributedSlicingJob? dequeuedJob = null;
                // Create scope (AsyncServiceScope implements IAsyncDisposable but synchronous disposal acceptable here)
                using (var scope = _scopeFactory.CreateScope())
                {
                    ISlicerJobQueue jobQueue = scope.ServiceProvider.GetRequiredService<ISlicerJobQueue>();
                    dequeuedJob = await jobQueue.DequeueAsync(_config.WorkerId, null, stoppingToken);
                }

                if (dequeuedJob == null)
                {
                    _logger.LogDebug($"No jobs available, sleeping for 2 seconds");
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                _logger.LogDebug($"Dequeued job {dequeuedJob.JobId}, starting background processing");
                // Start background processing after scope is disposed (ProcessJobAsync creates its own scope)
                _ = Task.Run(() => ProcessJobAsync(dequeuedJob!, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error while dequeuing slicing job: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation($"Slicer worker stopping (worker id {_config.WorkerId})");
    }

    private async Task ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        // Create a scope for all scoped services used while processing this job
        using IServiceScope scope = _scopeFactory.CreateScope();
        ISlicerJobQueue jobQueue = scope.ServiceProvider.GetRequiredService<ISlicerJobQueue>();
        ISlicerFileStorage fileStorage = scope.ServiceProvider.GetRequiredService<ISlicerFileStorage>();
        ISlicerProgressNotifier notifier = scope.ServiceProvider.GetRequiredService<ISlicerProgressNotifier>();

        DateTime started = DateTime.UtcNow;
        job.WorkerId = _config.WorkerId;
        try
        {
            _logger.LogInformation($"Processing slicing job {job.Id} (engine {job.EngineType})");
            await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = job.Id, Progress = 5, Status = SlicingJobStatus.Slicing, CurrentStep = "Queued to worker" }, cancellationToken);

            // Download model to temp
            string tempRoot = Path.GetFullPath(_tempProvider.GetTempRoot());
            string jobDir = Path.Combine(tempRoot, "slicer", job.Id.ToString());
            Directory.CreateDirectory(jobDir);

            byte[] fileBytes = await fileStorage.DownloadFileBytesAsync(job.ModelFileUrl.ToString(), cancellationToken);
            string inputFileName = string.IsNullOrWhiteSpace(job.ModelFileName) ? $"{job.Id}.stl" : job.ModelFileName;
            string inputPath = Path.Combine(jobDir, inputFileName);
            await File.WriteAllBytesAsync(inputPath, fileBytes, cancellationToken);

            string? engineConfigPath = null;

            // Decide execution path
            if (_exeManager.TryGetExecutable(job.EngineType, out string? exe, out string? argsTemplate) && !string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = job.Id, Progress = 10, Status = SlicingJobStatus.Slicing, CurrentStep = "Initializing slicer" }, cancellationToken);

                // Engine-specific preparation
                if (job.EngineType == SlicerEngineType.PrusaSlicer)
                {
                    // Generate PrusaSlicer configuration file from profile when provided
                    engineConfigPath = Path.Combine(jobDir, "config.ini");
                    try
                    {
                        string ini = SlicerArgTemplateBuilder.GeneratePrusaSlicerConfig(job.Profile);
                        await File.WriteAllTextAsync(engineConfigPath, ini, cancellationToken);
                        // Prefer explicit args template from admin settings if present
                        if (string.IsNullOrWhiteSpace(argsTemplate))
                        {
                            argsTemplate = $"--load \"{engineConfigPath}\" --output \"{Path.Combine(jobDir, Path.GetFileNameWithoutExtension(inputPath) + ".gcode")}\" \"{inputPath}\"";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to generate PrusaSlicer config for job {job.Id}; continuing with default args: {ex.Message}");
                    }
                }

                if (job.EngineType == SlicerEngineType.OrcaSlicer)
                {
                    // Generate OrcaSlicer configuration file from profile when provided
                    engineConfigPath = Path.Combine(jobDir, "orca_config.ini");
                    try
                    {
                        string ini = SlicerArgTemplateBuilder.GenerateOrcaSlicerConfig(job.Profile);
                        await File.WriteAllTextAsync(engineConfigPath, ini, cancellationToken);

                        if (string.IsNullOrWhiteSpace(argsTemplate))
                        {
                            argsTemplate = $"--config \"{engineConfigPath}\" --output \"{Path.Combine(jobDir, Path.GetFileNameWithoutExtension(inputPath) + ".gcode")}\" \"{inputPath}\"";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to generate OrcaSlicer config for job {job.Id}; falling back to default args: {ex.Message}");
                        if (string.IsNullOrWhiteSpace(argsTemplate))
                        {
                            argsTemplate = "--export-gcode -o {output} {input}";
                        }
                    }
                }

                string outputGcode = Path.Combine(jobDir, Path.GetFileNameWithoutExtension(inputPath) + ".gcode");
                string args = (argsTemplate ?? "{input} -o {output}")
                    .Replace("{input}", inputPath)
                    .Replace("{output}", outputGcode);
                if (!string.IsNullOrEmpty(engineConfigPath))
                {
                    args = args.Replace("{config}", engineConfigPath);
                }

                ProcessStartInfo psi = new()
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = jobDir
                };

                IProcessHandle procHandle = _processRunner.Start(psi);

                // Track whether parser-driven completion already finalized the job to avoid double-completion
                int parserCompletedFlag = 0; // 0 = not completed, 1 = completed

                // After starting the process, choose a tailored progress parser when available
                if (job.EngineType == SlicerEngineType.PrusaSlicer)
                {
                    PrusaProgressParser parser = new();
                    _ = Task.Run(async () =>
                    {
                        await SlicerProgressMonitor.MonitorAsync(job.Id, procHandle, notifier, parser, _logger, cancellationToken,
                            // onParserCompleted
                            async (jid, ct) =>
                            {
                                try
                                {
                                    // Ensure only a single completion path wins
                                    if (System.Threading.Interlocked.Exchange(ref parserCompletedFlag, 1) == 0)
                                    {
                                        // Try to find produced gcode
                                        string? found = Directory.GetFiles(jobDir, "*.gcode", SearchOption.TopDirectoryOnly).FirstOrDefault();
                                        if (found != null)
                                        {
                                            await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = job.Id, Progress = 95, Status = SlicingJobStatus.Slicing, CurrentStep = "Parser detected completion, uploading gcode" }, ct);
                                            using FileStream gcodeStream = File.OpenRead(found);
                                            string key = $"gcode/{job.Id}/{Path.GetFileName(found)}";
                                            string url = await fileStorage.UploadFileAsync(key, gcodeStream, "text/plain", ct);
                                            SlicingResult result = new()
                                            {
                                                Success = true,
                                                ResultFileUrl = new Uri(url, UriKind.RelativeOrAbsolute),
                                                OutputFileSizeBytes = new System.IO.FileInfo(found).Length,
                                                ProcessingTimeSeconds = (DateTime.UtcNow - started).TotalSeconds,
                                                EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
                                                EstimatedFilamentUsageGrams = job.EstimatedFilamentUsageGrams,
                                                LayerCount = job.LayerCount ?? 0
                                            };
                                            await jobQueue.CompleteJobAsync(job, result, cancellationToken: ct);
                                            await notifier.NotifyCompletionAsync(job, result, ct);
                                        }
                                        // Ask process to terminate now that we're done
                                        try
                                        {
                                            procHandle.Kill();
                                        }
                                        catch { }
                                    }
                                }
                                catch { /* best-effort */ }
                            },
                            // onParserFailure
                            async (jid, msg, ct) =>
                            {
                                try
                                {
                                    await jobQueue.FailJobAsync(job.Id, msg, cancellationToken);
                                    await notifier.NotifyFailureAsync(job, msg, cancellationToken);
                                }
                                catch { /* best-effort */ }
                            });
                    }, cancellationToken);
                }
                else if (job.EngineType == SlicerEngineType.OrcaSlicer)
                {
                    OrcaProgressParser parser = new();
                    _ = Task.Run(async () =>
                    {
                        await SlicerProgressMonitor.MonitorAsync(job.Id, procHandle, notifier, parser, _logger, cancellationToken,
                            async (jid, ct) =>
                            {
                                try
                                {
                                    if (System.Threading.Interlocked.Exchange(ref parserCompletedFlag, 1) == 0)
                                    {
                                        string? found = Directory.GetFiles(jobDir, "*.gcode", SearchOption.TopDirectoryOnly).FirstOrDefault();
                                        if (found != null)
                                        {
                                            await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = job.Id, Progress = 95, Status = SlicingJobStatus.Slicing, CurrentStep = "Parser detected completion, uploading gcode" }, ct);
                                            using FileStream gcodeStream = File.OpenRead(found);
                                            string key = $"gcode/{job.Id}/{Path.GetFileName(found)}";
                                            string url = await fileStorage.UploadFileAsync(key, gcodeStream, "text/plain", ct);
                                            SlicingResult result = new()
                                            {
                                                Success = true,
                                                ResultFileUrl = new Uri(url, UriKind.RelativeOrAbsolute),
                                                OutputFileSizeBytes = new System.IO.FileInfo(found).Length,
                                                ProcessingTimeSeconds = (DateTime.UtcNow - started).TotalSeconds,
                                                EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
                                                EstimatedFilamentUsageGrams = job.EstimatedFilamentUsageGrams,
                                                LayerCount = job.LayerCount ?? 0
                                            };
                                            await jobQueue.CompleteJobAsync(job, result, cancellationToken: ct);
                                            await notifier.NotifyCompletionAsync(job, result, ct);
                                        }
                                        try
                                        {
                                            procHandle.Kill();
                                        }
                                        catch { }
                                    }
                                }
                                catch { /* best-effort */ }
                            },
                            async (jid, msg, ct) =>
                            {
                                try
                                {
                                    await jobQueue.FailJobAsync(job.Id, msg, cancellationToken);
                                    await notifier.NotifyFailureAsync(job, msg, cancellationToken);
                                }
                                catch { /* best-effort */ }
                            });
                    }, cancellationToken);
                }
                else
                {
                    _ = Task.Run(async () =>
                    {
                        // simple stdout scanning for percent-like lines
                        try
                        {
                            StreamReader sr = procHandle.StandardOutput; // do not dispose injected stream
                            while (!sr.EndOfStream)
                            {
                                string? line = await sr.ReadLineAsync();
                                if (line == null)
                                {
                                    break;
                                }
                                if (line.Contains('%'))
                                {
                                    string digits = string.Concat(line.Where(char.IsDigit));
                                    if (!string.IsNullOrEmpty(digits) && int.TryParse(digits, out int p))
                                    {
                                        int clamped = Math.Max(0, Math.Min(100, p));
                                        await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = job.Id, Progress = 10 + (int)(clamped * 0.8), Status = SlicingJobStatus.Slicing, CurrentStep = line }, cancellationToken);
                                    }
                                }
                            }
                        }
                        catch { /* best-effort */ }
                    }, cancellationToken);
                }

                await procHandle.WaitForExitAsync(cancellationToken);

                // If parser already completed and finalized the job, skip additional exit code checks and completion logic
                if (System.Threading.Volatile.Read(ref parserCompletedFlag) != 1 && procHandle.ExitCode != 0)
                {
                    string err = await procHandle.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException($"Slicer process failed (exit {procHandle.ExitCode}): {err}");
                }

                // Upload gcode
                if (!File.Exists(Path.Combine(jobDir, Path.GetFileName(outputGcode))))
                {
                    // Some slicers may write to different location; try to find any .gcode in jobDir
                    string? found = Directory.GetFiles(jobDir, "*.gcode", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (found != null)
                    {
                        outputGcode = found;
                    }
                }

                if (!File.Exists(outputGcode))
                {
                    throw new InvalidOperationException("No gcode file produced by slicer");
                }

                FileStream gcodeStream = File.OpenRead(outputGcode);
                string key = $"gcode/{job.Id}/{Path.GetFileName(outputGcode)}";
                string url = await fileStorage.UploadFileAsync(key, gcodeStream, "text/plain", cancellationToken);
                await gcodeStream.DisposeAsync();

                SlicingResult result = new()
                {
                    Success = true,
                    ResultFileUrl = new Uri(url, UriKind.RelativeOrAbsolute),
                    OutputFileSizeBytes = new System.IO.FileInfo(outputGcode).Length,
                    ProcessingTimeSeconds = (DateTime.UtcNow - started).TotalSeconds,
                    EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
                    EstimatedFilamentUsageGrams = job.EstimatedFilamentUsageGrams,
                    LayerCount = job.LayerCount ?? 0
                };

                await jobQueue.CompleteJobAsync(job, result, cancellationToken: cancellationToken);
                await notifier.NotifyCompletionAsync(job, result, cancellationToken);
            }
            else
            {
                // No executable available - fallback to lightweight mock slicing (for dev/test)
                await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = job.Id, Progress = 20, Status = SlicingJobStatus.Slicing, CurrentStep = "Mock slicing (no executable configured)" }, cancellationToken);
                string outputGcode = Path.Combine(jobDir, Path.GetFileNameWithoutExtension(inputPath) + ".gcode");
                await File.WriteAllTextAsync(outputGcode, $"; Mock G-code for job {job.Id}\nG28\n; Generated at {DateTime.UtcNow:O}", cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

                FileStream gcodeStream = File.OpenRead(outputGcode);
                string key = $"gcode/{job.Id}/{Path.GetFileName(outputGcode)}";
                string url = await fileStorage.UploadFileAsync(key, gcodeStream, "text/plain", cancellationToken);
                await gcodeStream.DisposeAsync();

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
        }
        catch (Exception ex)
        {
            _logger.LogError($"Slicing job {job.Id} failed: {ex.Message}");

            try
            {
                // Decide whether to retry or fail permanently
                int maxRetries = _config.MaxRetryCount;
                // Treat IO/timeout related exceptions as transient; also treat process exit (InvalidOperationException) as transient
                bool isTransient =
                    ex is System.IO.IOException
                    || ex is TimeoutException
                    || ex is System.Net.Http.HttpRequestException
                    || (ex is InvalidOperationException && !(ex.Message?.Contains("No gcode") ?? false));

                if (isTransient && job.RetryCount < maxRetries)
                {
                    // Exponential backoff: base 10s
                    int delaySeconds = Math.Min(3600, (int)(Math.Pow(2, job.RetryCount) * 10));
                    TimeSpan delay = TimeSpan.FromSeconds(delaySeconds);

                    // Prefer runtime-configured jitter (admin-tunable) but fall back to static worker config
                    SlicerSettingsDto runtimeSettings = _settingsService.GetSettings();
                    double jitterToUse = runtimeSettings.JitterPercent > 0 ? runtimeSettings.JitterPercent : _config.JitterPercent;

                    await jobQueue.RequeueJobAsync(job, delay, jitterToUse, cancellationToken);

                    string message = $"Transient error occurred: {ex.Message}. Scheduled retry #{job.RetryCount} in {delaySeconds} seconds.";
                    await notifier.NotifyFailureAsync(job, message, cancellationToken);
                    _logger.LogInformation($"Job {job.Id} scheduled for retry #{job.RetryCount} in {delaySeconds}s");
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
                _logger.LogError($"Failed to requeue/fail job {job.Id} after exception: {notifyEx.Message}");
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
                _logger.LogDebug($"Failed to cleanup job temp dir for {job.Id}: {ex.Message}");
            }
        }
    }
}
