using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Infrastructure.Temp;
using Farm.Web.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Farm.Web.Api.Services.SlicerServices.Progress;

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
    private readonly ILogger<SlicerWorkerHostedService> _logger;
    private readonly SlicerWorkerConfiguration _config;
    private readonly ISlicerSettingsService _settingsService;

    public SlicerWorkerHostedService(
        IServiceScopeFactory scopeFactory,
        ISlicerExecutableManager exeManager,
        ITempPathProvider tempProvider,
        ILogger<SlicerWorkerHostedService> logger,
        IConfiguration cfg,
        ISlicerSettingsService settingsService)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _exeManager = exeManager ?? throw new ArgumentNullException(nameof(exeManager));
        _tempProvider = tempProvider ?? throw new ArgumentNullException(nameof(tempProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = new SlicerWorkerConfiguration();
        cfg?.GetSection("SlicerWorker")?.Bind(_config);
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Slicer worker started (worker id {WorkerId})", _config.WorkerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var runtimeSettings = _settingsService.GetSettings();
                if (!runtimeSettings.Enabled)
                {
                    _logger.LogDebug("Slicer worker is disabled via runtime settings; sleeping");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                // Attempt to dequeue a job (non-blocking)
                using (var scope = _scopeFactory.CreateScope())
                {
                    var jobQueue = scope.ServiceProvider.GetRequiredService<ISlicerJobQueue>();
                    var job = await jobQueue.DequeueAsync(_config.WorkerId, null, stoppingToken);
                    if (job == null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                        continue;
                    }

                    _ = Task.Run(() => ProcessJobAsync(job, stoppingToken), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while dequeuing slicing job");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("Slicer worker stopping (worker id {WorkerId})", _config.WorkerId);
    }

    private async Task ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        // Create a scope for all scoped services used while processing this job
        using var scope = _scopeFactory.CreateScope();
        var jobQueue = scope.ServiceProvider.GetRequiredService<ISlicerJobQueue>();
        var fileStorage = scope.ServiceProvider.GetRequiredService<ISlicerFileStorage>();
        var notifier = scope.ServiceProvider.GetRequiredService<ISlicerProgressNotifier>();

        var started = DateTime.UtcNow;
        job.WorkerId = _config.WorkerId;
        try
        {
            _logger.LogInformation("Processing slicing job {JobId} (engine {Engine})", job.Id, job.EngineType);
            await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = job.Id, Progress = 5, Status = SlicingJobStatus.Slicing, CurrentStep = "Queued to worker" }, cancellationToken);

            // Download model to temp
            var tempRoot = Path.GetFullPath(_tempProvider.GetTempRoot());
            var jobDir = Path.Combine(tempRoot, "slicer", job.Id.ToString());
            Directory.CreateDirectory(jobDir);

            var fileBytes = await fileStorage.DownloadFileBytesAsync(job.ModelFileUrl.ToString(), cancellationToken);
            var inputFileName = string.IsNullOrWhiteSpace(job.ModelFileName) ? $"{job.Id}.stl" : job.ModelFileName;
            var inputPath = Path.Combine(jobDir, inputFileName);
            await File.WriteAllBytesAsync(inputPath, fileBytes, cancellationToken);

            string? engineConfigPath = null;

            // Decide execution path
            if (_exeManager.TryGetExecutable(job.EngineType, out var exe, out var argsTemplate) && !string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = job.Id, Progress = 10, Status = SlicingJobStatus.Slicing, CurrentStep = "Initializing slicer" }, cancellationToken);

                // Engine-specific preparation
                if (job.EngineType == SlicerEngineType.PrusaSlicer)
                {
                    // Generate PrusaSlicer configuration file from profile when provided
                    engineConfigPath = Path.Combine(jobDir, "config.ini");
                    try
                    {
                        var ini = SlicerArgTemplateBuilder.GeneratePrusaSlicerConfig(job.Profile);
                        await File.WriteAllTextAsync(engineConfigPath, ini, cancellationToken);
                        // Prefer explicit args template from admin settings if present
                        if (string.IsNullOrWhiteSpace(argsTemplate))
                        {
                            argsTemplate = $"--load \"{engineConfigPath}\" --output \"{Path.Combine(jobDir, Path.GetFileNameWithoutExtension(inputPath) + ".gcode")}\" \"{inputPath}\"";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate PrusaSlicer config for job {JobId}; continuing with default args", job.Id);
                    }
                }

                if (job.EngineType == SlicerEngineType.OrcaSlicer)
                {
                    // Generate OrcaSlicer configuration file from profile when provided
                    engineConfigPath = Path.Combine(jobDir, "orca_config.ini");
                    try
                    {
                        var ini = SlicerArgTemplateBuilder.GenerateOrcaSlicerConfig(job.Profile);
                        await File.WriteAllTextAsync(engineConfigPath, ini, cancellationToken);

                        if (string.IsNullOrWhiteSpace(argsTemplate))
                        {
                            argsTemplate = $"--config \"{engineConfigPath}\" --output \"{Path.Combine(jobDir, Path.GetFileNameWithoutExtension(inputPath) + ".gcode")}\" \"{inputPath}\"";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate OrcaSlicer config for job {JobId}; falling back to default args", job.Id);
                        if (string.IsNullOrWhiteSpace(argsTemplate))
                        {
                            argsTemplate = "--export-gcode -o {output} {input}";
                        }
                    }
                }

                var outputGcode = Path.Combine(jobDir, Path.GetFileNameWithoutExtension(inputPath) + ".gcode");
                var args = (argsTemplate ?? "{input} -o {output}")
                    .Replace("{input}", inputPath)
                    .Replace("{output}", outputGcode);
                if (!string.IsNullOrEmpty(engineConfigPath))
                {
                    args = args.Replace("{config}", engineConfigPath);
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = jobDir
                };

                using var proc = Process.Start(psi)!;
                if (proc == null)
                {
                    throw new InvalidOperationException("Failed to start slicer process");
                }

                // After starting the process, choose a tailored progress parser when available
                if (job.EngineType == SlicerEngineType.PrusaSlicer)
                {
                    var parser = new PrusaProgressParser();
                    _ = Task.Run(async () => await MonitorWithParserAsync(job.Id, proc, notifier, parser, cancellationToken), cancellationToken);
                }
                else if (job.EngineType == SlicerEngineType.OrcaSlicer)
                {
                    var parser = new OrcaProgressParser();
                    _ = Task.Run(async () => await MonitorWithParserAsync(job.Id, proc, notifier, parser, cancellationToken), cancellationToken);
                }
                else
                {
                    _ = Task.Run(async () =>
                    {
                        // simple stdout scanning for percent-like lines
                        try
                        {
                            using var sr = proc.StandardOutput;
                            while (!sr.EndOfStream)
                            {
                                var line = await sr.ReadLineAsync();
                                if (line == null)
                                {
                                    break;
                                }
                                if (line.Contains('%'))
                                {
                                    var digits = string.Concat(line.Where(char.IsDigit));
                                    if (!string.IsNullOrEmpty(digits) && int.TryParse(digits, out var p))
                                    {
                                        var clamped = Math.Max(0, Math.Min(100, p));
                                        await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = job.Id, Progress = 10 + (int)(clamped * 0.8), Status = SlicingJobStatus.Slicing, CurrentStep = line }, cancellationToken);
                                    }
                                }
                            }
                        }
                        catch { /* best-effort */ }
                    }, cancellationToken);
                }

                await proc.WaitForExitAsync(cancellationToken);

                if (proc.ExitCode != 0)
                {
                    var err = await proc.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException($"Slicer process failed (exit {proc.ExitCode}): {err}");
                }

                // Upload gcode
                if (!File.Exists(Path.Combine(jobDir, Path.GetFileName(outputGcode))))
                {
                    // Some slicers may write to different location; try to find any .gcode in jobDir
                    var found = Directory.GetFiles(jobDir, "*.gcode", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (found != null)
                    {
                        outputGcode = found;
                    }
                }

                if (!File.Exists(outputGcode))
                {
                    throw new InvalidOperationException("No gcode file produced by slicer");
                }

                var gcodeStream = File.OpenRead(outputGcode);
                var key = $"gcode/{job.Id}/{Path.GetFileName(outputGcode)}";
                var url = await fileStorage.UploadFileAsync(key, gcodeStream, "text/plain", cancellationToken);
                await gcodeStream.DisposeAsync();

                var result = new SlicingResult
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
                var outputGcode = Path.Combine(jobDir, Path.GetFileNameWithoutExtension(inputPath) + ".gcode");
                await File.WriteAllTextAsync(outputGcode, $"; Mock G-code for job {job.Id}\nG28\n; Generated at {DateTime.UtcNow:O}", cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

                var gcodeStream = File.OpenRead(outputGcode);
                var key = $"gcode/{job.Id}/{Path.GetFileName(outputGcode)}";
                var url = await fileStorage.UploadFileAsync(key, gcodeStream, "text/plain", cancellationToken);
                await gcodeStream.DisposeAsync();

                var result = new SlicingResult
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
            _logger.LogError(ex, "Slicing job {JobId} failed", job.Id);
            await jobQueue.FailJobAsync(job.Id, ex.Message, cancellationToken);
            await notifier.NotifyFailureAsync(job, ex.Message, cancellationToken);
        }
        finally
        {
            try
            {
                // Cleanup job directory
                var tempRoot = Path.GetFullPath(_tempProvider.GetTempRoot());
                var jobDir = Path.Combine(tempRoot, "slicer", job.Id.ToString());
                if (Directory.Exists(jobDir))
                {
                    Directory.Delete(jobDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to cleanup job temp dir for {JobId}", job.Id);
            }
        }
    }

    private async Task MonitorWithParserAsync(Guid jobId, Process process, ISlicerProgressNotifier notifier, Progress.IProgressParser parser, CancellationToken ct)
    {
        try
        {
            var start = DateTime.UtcNow;
            using var stdout = process.StandardOutput;
            while (!process.HasExited && !ct.IsCancellationRequested)
            {
                if (!stdout.EndOfStream)
                {
                    var line = await stdout.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line))
                    {
                        ProgressUpdate? parsed = null;
                        try
                        {
                            parsed = parser.Parse(line);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Progress parser threw while handling a line for job {JobId}", jobId);
                        }

                        if (parsed != null)
                        {
                            var pct = (int)Math.Max(0, Math.Min(100, Math.Round(parsed.Percentage)));
                            await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = jobId, Progress = pct, Status = SlicingJobStatus.Slicing, CurrentStep = parsed.Message }, ct);
                            // If parser reports completion, send a final heartbeat progress (actual completion will be handled when process exits)
                            if (parsed.State == SlicerProgressState.Completed)
                            {
                                await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = jobId, Progress = 100, Status = SlicingJobStatus.Slicing, CurrentStep = parsed.Message }, ct);
                            }
                            continue;
                        }
                    }
                }

                // Heartbeat fallback progress estimation based on elapsed time (generic fallback)
                var elapsed = DateTime.UtcNow - start;
                var estimated = Math.Min(95, 10 + (int)Math.Min(85, elapsed.TotalSeconds / 1.5));
                await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = jobId, Progress = estimated, Status = SlicingJobStatus.Slicing, CurrentStep = "Slicing in progress..." }, ct);

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error monitoring slicer process for job {JobId}", jobId);
        }
    }
}
