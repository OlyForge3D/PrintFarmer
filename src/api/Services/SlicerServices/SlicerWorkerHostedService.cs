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
                        var ini = GeneratePrusaSlicerConfig(job.Profile);
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

                // After starting the process, choose a tailored progress monitor when available
                if (job.EngineType == SlicerEngineType.PrusaSlicer)
                {
                    _ = Task.Run(async () => await MonitorPrusaProgressAsync(job.Id, proc, notifier, cancellationToken), cancellationToken);
                }
                else if (job.EngineType == SlicerEngineType.OrcaSlicer)
                {
                    _ = Task.Run(async () => await MonitorOrcaProgressAsync(job.Id, proc, notifier, cancellationToken), cancellationToken);
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

    private async Task MonitorPrusaProgressAsync(Guid jobId, Process process, ISlicerProgressNotifier notifier, CancellationToken ct)
    {
        try
        {
            // Use phase-based progress estimates tied to common PrusaSlicer stages
            var phases = new (int Start, int End, string Message)[]
            {
                (Start: 0, End: 20, Message: "Initializing slicer"),
                (Start: 20, End: 45, Message: "Loading model"),
                (Start: 45, End: 70, Message: "Generating toolpaths"),
                (Start: 70, End: 90, Message: "Calculating time & writes"),
                (Start: 90, End: 100, Message: "Finalizing G-code")
            };
            var phaseIdx = 0;
            var start = DateTime.UtcNow;

            using var stdout = process.StandardOutput;
            while (!process.HasExited && !ct.IsCancellationRequested)
            {
                // Read any available line without blocking indefinitely
                if (!stdout.EndOfStream)
                {
                    var line = await stdout.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line))
                    {
                        // Move forward in phases when recognized keywords appear
                        var lower = line.ToLowerInvariant();
                        if (lower.Contains("loading") || lower.Contains("load"))
                        {
                            phaseIdx = Math.Max(phaseIdx, 1);
                        }

                        if (lower.Contains("analyzing") || lower.Contains("toolpath") || lower.Contains("toolpaths"))
                        {
                            phaseIdx = Math.Max(phaseIdx, 2);
                        }

                        if (lower.Contains("writing") || lower.Contains("writing g-code") || lower.Contains("exporting"))
                        {
                            phaseIdx = Math.Max(phaseIdx, 3);
                        }

                        if (lower.Contains("done") || lower.Contains("finished"))
                        {
                            phaseIdx = Math.Max(phaseIdx, 4);
                        }

                        var phase = phases[Math.Min(phaseIdx, phases.Length - 1)];
                        var progress = phase.Start + (phase.End - phase.Start) / 2;
                        await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = jobId, Progress = progress, Status = SlicingJobStatus.Slicing, CurrentStep = line }, ct);
                    }
                }

                // Periodic heartbeat progress based on elapsed time as fallback
                var elapsed = DateTime.UtcNow - start;
                var estimated = Math.Min(95, 20 + (int)Math.Min(70, elapsed.TotalSeconds / 2));
                await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = jobId, Progress = estimated, Status = SlicingJobStatus.Slicing, CurrentStep = "Slicing in progress..." }, ct);

                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error monitoring PrusaSlicer process for job {JobId}", jobId);
        }
    }

    private async Task MonitorOrcaProgressAsync(Guid jobId, Process process, ISlicerProgressNotifier notifier, CancellationToken ct)
    {
        try
        {
            // Phase-based progress estimates tuned for OrcaSlicer's common stdout markers
            var phases = new (int Start, int End, string Message)[]
            {
                (Start: 0, End: 20, Message: "Initializing OrcaSlicer"),
                (Start: 20, End: 50, Message: "Preparing geometry"),
                (Start: 50, End: 80, Message: "Generating toolpaths"),
                (Start: 80, End: 95, Message: "Exporting G-code"),
                (Start: 95, End: 100, Message: "Finalizing")
            };

            var phaseIdx = 0;
            var start = DateTime.UtcNow;

            using var stdout = process.StandardOutput;
            while (!process.HasExited && !ct.IsCancellationRequested)
            {
                if (!stdout.EndOfStream)
                {
                    var line = await stdout.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line))
                    {
                        var lower = line.ToLowerInvariant();
                        if (lower.Contains("mesh") || lower.Contains("geometry") || lower.Contains("load"))
                        {
                            phaseIdx = Math.Max(phaseIdx, 1);
                        }

                        if (lower.Contains("slic") || lower.Contains("toolpath") || lower.Contains("path"))
                        {
                            phaseIdx = Math.Max(phaseIdx, 2);
                        }

                        if (lower.Contains("export") || lower.Contains("writing") || lower.Contains("gcode"))
                        {
                            phaseIdx = Math.Max(phaseIdx, 3);
                        }

                        // If the line contains a numeric percent like '42%' try to parse and use it directly
                        if (lower.Contains('%'))
                        {
                            var digits = string.Concat(line.Where(char.IsDigit));
                            if (!string.IsNullOrEmpty(digits) && int.TryParse(digits, out var p))
                            {
                                var clamped = Math.Max(0, Math.Min(100, p));
                                await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = jobId, Progress = clamped, Status = SlicingJobStatus.Slicing, CurrentStep = line }, ct);
                                continue;
                            }
                        }

                        var phase = phases[Math.Min(phaseIdx, phases.Length - 1)];
                        var progress = phase.Start + (phase.End - phase.Start) / 2;
                        await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = jobId, Progress = progress, Status = SlicingJobStatus.Slicing, CurrentStep = line }, ct);
                    }
                }

                // Heartbeat fallback progress based on elapsed time
                var elapsed = DateTime.UtcNow - start;
                var estimated = Math.Min(95, 10 + (int)Math.Min(85, elapsed.TotalSeconds / 1.5));
                await notifier.NotifyProgressAsync(new SlicingProgressUpdate { JobId = jobId, Progress = estimated, Status = SlicingJobStatus.Slicing, CurrentStep = "OrcaSlicer processing..." }, ct);

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error monitoring OrcaSlicer process for job {JobId}", jobId);
        }
    }

    private static string GeneratePrusaSlicerConfig(Farm.Web.Shared.SlicerProfileDto? profile)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Generated by PrintFarmer");
        sb.AppendLine($"# Generated at {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("[print]");
        sb.AppendLine($"layer_height = {profile?.LayerHeight ?? 0.2}");
        sb.AppendLine($"fill_density = {profile?.InfillPercentage ?? 20}");
        sb.AppendLine($"perimeter_speed = {profile?.PrintSpeed ?? 50}");
        sb.AppendLine($"nozzle_temperature = {profile?.NozzleTemperature ?? 210}");
        sb.AppendLine($"bed_temperature = {profile?.BedTemperature ?? 60}");
        sb.AppendLine($"support_material = {(profile?.Supports ?? false ? "1" : "0")}");
        sb.AppendLine();
        sb.AppendLine("[filament]");
        sb.AppendLine($"filament_type = {profile?.Material ?? "PLA"}");
        return sb.ToString();
    }
}
