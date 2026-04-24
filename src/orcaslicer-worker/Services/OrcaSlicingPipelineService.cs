using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using Microsoft.Extensions.Logging; // shared interfaces

namespace Farm.OrcaSlicer.Worker.Services;

public partial class OrcaSlicingPipelineService : ISlicingPipelineService
{
    private readonly HttpClient _httpClient;
    private readonly IProgressReporter _progressReporter;
    private readonly ILogger<OrcaSlicingPipelineService> _logger;
    private readonly string _workingDirectory;
    private readonly string _storageEndpoint;
    private readonly string _orcaSlicerBinaryPath;

    public OrcaSlicingPipelineService(HttpClient httpClient, IProgressReporter progressReporter, ILogger<OrcaSlicingPipelineService> logger, IConfiguration configuration)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _progressReporter = progressReporter ?? throw new ArgumentNullException(nameof(progressReporter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(configuration);
        _workingDirectory = configuration["Worker:WorkingDirectory"] ?? "/tmp/orca-work";
        _storageEndpoint = configuration["SlicerApi:BaseUrl"]
                          ?? configuration["Worker:StorageEndpoint"]
                          ?? "http://api:5245";
        _orcaSlicerBinaryPath = configuration["Worker:OrcaSlicerPath"] ?? "/opt/orcaslicer/bin/orca-slicer";
        if (!Directory.Exists(_workingDirectory))
        {
            _ = Directory.CreateDirectory(_workingDirectory);
        }
    }

    public async Task<SlicingResult> ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        string jobWorkDir = Path.Combine(_workingDirectory, job.Id.ToString());
        _ = Directory.CreateDirectory(jobWorkDir);
        try
        {
            _logger.LogInformation("Starting slicing pipeline for job {JobId}", job.Id);

            // Download model file(s)
            List<string> modelFilePaths;
            if (job.ModelFileUrls is { Count: > 0 })
            {
                await _progressReporter.ReportProgressAsync(job.Id, 5, $"Downloading {job.ModelFileUrls.Count} model files", cancellationToken);
                modelFilePaths = await FetchMultipleModelsAsync(job.ModelFileUrls, jobWorkDir, cancellationToken);
                job.InputFileSizeBytes = modelFilePaths.Sum(p => new FileInfo(p).Length);
                _logger.LogInformation("Downloaded {Count} model files for job {JobId}", modelFilePaths.Count, job.Id);
            }
            else
            {
                await _progressReporter.ReportProgressAsync(job.Id, 10, "Downloading STL file", cancellationToken);
                string singlePath = await FetchStlFileAsync(job, jobWorkDir, cancellationToken);
                modelFilePaths = [singlePath];
            }

            await _progressReporter.ReportProgressAsync(job.Id, 20, "Preparing slicer configuration", cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, 30, "Running OrcaSlicer", cancellationToken);
            string gcodeFilePath = await RunOrcaSlicerAsync(modelFilePaths, jobWorkDir, job, cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, 80, "Analyzing G-code", cancellationToken);
            GcodeMetadata metadata = await ExtractGcodeMetadataAsync(gcodeFilePath, cancellationToken);

            // Rename gcode to descriptive filename: {model}_{printer}_{material}_{time}.gcode
            gcodeFilePath = RenameGcodeFile(gcodeFilePath, job, metadata);

            await _progressReporter.ReportProgressAsync(job.Id, 90, "Uploading G-code", cancellationToken);
            string gcodeUrl = await UploadGcodeAsync(gcodeFilePath, job, cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, 100, "Slicing completed", cancellationToken);
            SlicingResult result = new SlicingResult
            {
                ResultFileUrl = new Uri(gcodeUrl, UriKind.RelativeOrAbsolute),
                EstimatedPrintTimeSeconds = metadata.PrintTimeSeconds,
                EstimatedFilamentUsageGrams = metadata.FilamentUsageGrams,
                OutputFileSizeBytes = new FileInfo(gcodeFilePath).Length,
                LayerCount = metadata.LayerCount,
                Success = true
            };
            result.Metadata["SlicerVersion"] = "OrcaSlicer 1.8.0";
            result.Metadata["ProcessedAt"] = DateTime.UtcNow.ToString("O");
            result.Metadata["WorkerId"] = job.WorkerId ?? "unknown";
            if (modelFilePaths.Count > 1)
            {
                result.Metadata["ModelCount"] = modelFilePaths.Count.ToString(CultureInfo.InvariantCulture);
            }

            return result;
        }
        finally
        {
            // Keep temp files on failure for debugging; only clean up on success
            if (Directory.Exists(jobWorkDir))
            {
                string outputDir = Path.Combine(jobWorkDir, "output");
                bool succeeded = Directory.Exists(outputDir) && Directory.GetFiles(outputDir, "*.gcode").Length > 0;
                if (succeeded)
                {
                    try
                    {
                        Directory.Delete(jobWorkDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed cleanup {JobWorkDir}", jobWorkDir);
                    }
                }
                else
                {
                    _logger.LogWarning("Keeping temp dir for debugging: {JobWorkDir}", jobWorkDir);
                }
            }
        }
    }

    private async Task<string> FetchStlFileAsync(DistributedSlicingJob job, string workDir, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await _httpClient.GetAsync(job.ModelFileUrl, cancellationToken);
        _ = response.EnsureSuccessStatusCode();
        string stlFilePath = Path.Combine(workDir, job.ModelFileName);
        await using FileStream fileStream = File.Create(stlFilePath);
        await response.Content.CopyToAsync(fileStream, cancellationToken);
        job.InputFileSizeBytes = new FileInfo(stlFilePath).Length;
        return stlFilePath;
    }

    private async Task<List<string>> FetchMultipleModelsAsync(List<string> modelUrls, string workDir, CancellationToken cancellationToken)
    {
        List<string> downloadedPaths = new(modelUrls.Count);
        for (int i = 0; i < modelUrls.Count; i++)
        {
            string url = modelUrls[i];
            Uri uri = new(url, UriKind.RelativeOrAbsolute);
            string fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = $"model_{i}{(url.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase) ? ".3mf" : ".stl")}";
            }

            // Ensure unique filenames when multiple models share the same name
            string destPath = Path.Combine(workDir, fileName);
            if (File.Exists(destPath))
            {
                string baseName = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);
                destPath = Path.Combine(workDir, $"{baseName}_{i}{ext}");
            }

            HttpResponseMessage response = await _httpClient.GetAsync(uri, cancellationToken);
            _ = response.EnsureSuccessStatusCode();
            await using FileStream fileStream = File.Create(destPath);
            await response.Content.CopyToAsync(fileStream, cancellationToken);
            downloadedPaths.Add(destPath);
            _logger.LogInformation("Downloaded model {Index}/{Total}: {Path}", i + 1, modelUrls.Count, destPath);
        }

        return downloadedPaths;
    }

    private static async Task<Dictionary<string, string>> GenerateProfileJsonFilesAsync(SlicerProfileDto? profile, string workDir, CancellationToken cancellationToken)
    {
        if (profile == null)
        {
            throw new ArgumentNullException(nameof(profile), "Profile is required for slicing");
        }

        string machineJsonPath = Path.Combine(workDir, "machine.json");
        string processJsonPath = Path.Combine(workDir, "process.json");

        // Write the profiles directly as JSON - they should already contain complete settings from the database
        // OrcaSlicer expects flat key-value JSON (native settings), not our DTO wrapper.
        // The Settings dictionary stores raw JSON text per key (from GetRawText()),
        // so we reconstruct proper JSON by writing the raw values directly.
        string machineJson = SettingsDictToNativeJson(profile.MachineProfile?.Settings);
        string processJson = SettingsDictToNativeJson(profile.ProcessProfile?.Settings);

        await File.WriteAllTextAsync(machineJsonPath, machineJson, cancellationToken);
        await File.WriteAllTextAsync(processJsonPath, processJson, cancellationToken);

        var result = new Dictionary<string, string>
        {
            { "machine", machineJsonPath },
            { "process", processJsonPath }
        };

        // Multi-extruder: write one filament JSON per extruder, semicolon-separated for --load-filaments
        if (profile.ExtruderFilamentProfiles is { Count: > 1 })
        {
            var filamentPaths = new List<string>();
            for (int i = 0; i < profile.ExtruderFilamentProfiles.Count; i++)
            {
                string path = Path.Combine(workDir, $"filament_{i}.json");
                string json = SettingsDictToNativeJson(profile.ExtruderFilamentProfiles[i].Settings);
                await File.WriteAllTextAsync(path, json, cancellationToken);
                filamentPaths.Add(path);
            }

            result["filament"] = string.Join(";", filamentPaths);
        }
        else
        {
            string filamentJsonPath = Path.Combine(workDir, "filament.json");
            string filamentJson = SettingsDictToNativeJson(profile.FilamentProfile?.Settings);
            await File.WriteAllTextAsync(filamentJsonPath, filamentJson, cancellationToken);
            result["filament"] = filamentJsonPath;
        }

        return result;
    }

    private async Task<string> RunOrcaSlicerAsync(List<string> modelPaths, string workDir, DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        string gcodeOutputDir = Path.Combine(workDir, "output");
        _ = Directory.CreateDirectory(gcodeOutputDir);

        string gcodeFilePath = Path.Combine(gcodeOutputDir, Path.GetFileNameWithoutExtension(job.ModelFileName) + ".gcode");
        if (!File.Exists(_orcaSlicerBinaryPath))
        {
            throw new InvalidOperationException($"OrcaSlicer binary not found at {_orcaSlicerBinaryPath}");
        }

        // Generate the three JSON profile files
        Dictionary<string, string> profilePaths = await GenerateProfileJsonFilesAsync(job.Profile, workDir, cancellationToken);

        string machineJson = profilePaths["machine"];
        string processJson = profilePaths["process"];
        string filamentJson = profilePaths["filament"];

        // Build command line: --slice 0 --arrange 1 --ensure-on-bed --load-settings ...
        // --arrange 1: auto-center model on build plate (CLI loads STL at origin)
        // --ensure-on-bed: lift objects partially below Z=0
        TransformResult transform = BuildTransformFlags(job.ModelTransformJson);
        string arrangeFlag = transform.HasCustomPosition ? "--arrange 0" : "--arrange 1";

        // Create a named pipe for real-time progress from OrcaSlicer
        string pipePath = Path.Combine(workDir, "progress.pipe");
        bool pipeCreated = TryCreateNamedPipe(pipePath);
        string pipeFlag = pipeCreated ? $" --pipe \"{pipePath}\"" : string.Empty;

        // Build model arguments: first model is positional, additional models use --load
        string primaryModel = $"\"{modelPaths[0]}\"";
        string additionalModels = string.Empty;
        if (modelPaths.Count > 1)
        {
            additionalModels = " " + string.Join(" ", modelPaths.Skip(1).Select(p => $"--load \"{p}\""));
        }

        string arguments = $"--slice 0 {arrangeFlag} --ensure-on-bed{transform.Flags}{pipeFlag} --load-settings \"{machineJson};{processJson}\" --load-filaments \"{filamentJson}\" --allow-newer-file --outputdir \"{gcodeOutputDir}\"{additionalModels} {primaryModel}";

        // OrcaSlicer requires a display even for headless CLI slicing; use xvfb-run if available
        string binaryPath = _orcaSlicerBinaryPath;
        bool useXvfb = File.Exists("/usr/bin/xvfb-run");
        if (useXvfb)
        {
            arguments = $"-a {_orcaSlicerBinaryPath} {arguments}";
            binaryPath = "/usr/bin/xvfb-run";
        }

        _logger.LogInformation("Launching OrcaSlicer: {BinaryPath} {Arguments}", binaryPath, arguments);

        using Process process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = binaryPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            }
        };
        _ = process.Start();
#pragma warning disable CA2025 // progressTask references process but completes before disposal (awaited explicitly)
        Task progressTask = pipeCreated
            ? MonitorSlicingProgressViaPipeAsync(job.Id, pipePath, process, cancellationToken)
            : MonitorSlicingProgressAsync(job.Id, process, cancellationToken);
#pragma warning restore CA2025
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await progressTask;
        string output = await outputTask;
        string error = await errorTask;

        _logger.LogInformation(
            "OrcaSlicer exited with code {ExitCode}. Stdout length={StdoutLen}, Stderr length={StderrLen}",
            process.ExitCode,
            output.Length,
            error.Length);

        if (!string.IsNullOrWhiteSpace(output))
        {
            _logger.LogInformation("OrcaSlicer stdout: {Output}", output.Length > 2000 ? output[..2000] : output);
        }

        if (process.ExitCode != 0)
        {
            _logger.LogError("OrcaSlicer stderr: {Error}", error);

            // Parse stdout for [error] lines — OrcaSlicer writes diagnostics to stdout, not stderr
            string detail = ExtractOrcaErrorDetail(output, error);

            throw new InvalidOperationException(
                $"OrcaSlicer failed with exit code {process.ExitCode}: {detail}");
        }

        // OrcaSlicer CLI always outputs plate_1.gcode (not {modelname}.gcode)
        if (!File.Exists(gcodeFilePath))
        {
            // Look for plate_1.gcode or any .gcode file in the output dir
            string plate1Path = Path.Combine(gcodeOutputDir, "plate_1.gcode");
            if (File.Exists(plate1Path))
            {
                gcodeFilePath = plate1Path;
            }
            else
            {
                string[] gcodeFiles = Directory.GetFiles(gcodeOutputDir, "*.gcode");
                gcodeFilePath = gcodeFiles.Length > 0
                    ? gcodeFiles[0]
                    : throw new InvalidOperationException("OrcaSlicer completed but no G-code produced");
            }
        }

        return gcodeFilePath;
    }

    private static string ExtractOrcaErrorDetail(string stdout, string stderr)
    {
        // OrcaSlicer writes errors to stdout as "[error] <message>" lines
        var errorLines = stdout
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("[error]", StringComparison.OrdinalIgnoreCase))
            .Select(l =>
            {
                // Strip timestamp prefix: "[2026-04-13 ...] [0x...] [error]   message"
                int idx = l.IndexOf("[error]", StringComparison.OrdinalIgnoreCase);
                return idx >= 0 ? l[(idx + 7)..].TrimStart(':', ' ') : l;
            })
            .Where(l => l.Length > 0)
            .ToList();

        if (errorLines.Count > 0)
        {
            return string.Join("; ", errorLines);
        }

        // Fall back to stderr if no [error] lines in stdout
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            return stderr.Length > 500 ? stderr[..500] : stderr;
        }

        // Last resort: grab lines containing "error" or "fail" from stdout
        string fallback = stdout
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                              || l.Contains("fail", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;

        return fallback.Length > 500 ? fallback[..500] : fallback;
    }

    private static string RenameGcodeFile(string gcodeFilePath, DistributedSlicingJob job, GcodeMetadata metadata)
    {
        string modelName = Path.GetFileNameWithoutExtension(job.ModelFileName);
        string printerModel = job.Profile?.MachineProfile?.PrinterModel
                           ?? job.Profile?.MachineProfile?.Name
                           ?? "Unknown";
        string material = job.Profile?.ExtruderFilamentProfiles is { Count: > 1 }
            ? string.Join("+", job.Profile.ExtruderFilamentProfiles.Select(f => f.Material ?? "PLA"))
            : job.Profile?.FilamentProfile?.Material ?? "PLA";
        string printTime = FormatPrintTime(metadata.PrintTimeSeconds);

        string newName = SanitizeFileName($"{modelName}_{printerModel}_{material}_{printTime}.gcode");
        string newPath = Path.Combine(Path.GetDirectoryName(gcodeFilePath)!, newName);

        if (string.Equals(gcodeFilePath, newPath, StringComparison.Ordinal))
        {
            return gcodeFilePath;
        }

        File.Move(gcodeFilePath, newPath);
        return newPath;
    }

    private static string FormatPrintTime(double totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "unknown";
        }

        TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h{ts.Minutes}m"
            : $"{ts.Minutes}m";
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
    }

    private async Task MonitorSlicingProgressViaPipeAsync(
        Guid jobId,
        string pipePath,
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            // Open the pipe for reading — blocks until OrcaSlicer opens it for writing.
            // Use a timeout so we fall back to time-based if OrcaSlicer never opens the pipe.
            using CancellationTokenSource pipeOpenCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pipeOpenCts.CancelAfter(TimeSpan.FromSeconds(15));

            FileStream? pipeStream = null;
            try
            {
                // FileStream.Open on a FIFO blocks until a writer opens it.
                // Run in a thread pool thread to avoid blocking the async context.
                pipeStream = await Task.Run(
                    () => new FileStream(pipePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                    pipeOpenCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Pipe open timed out for job {JobId}, falling back to time-based progress", jobId);
                await MonitorSlicingProgressAsync(jobId, process, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open progress pipe for job {JobId}, falling back to time-based progress", jobId);
                await MonitorSlicingProgressAsync(jobId, process, cancellationToken);
                return;
            }

            using (pipeStream)
            using (StreamReader reader = new StreamReader(pipeStream, Encoding.UTF8))
            {
                while (!process.HasExited && !cancellationToken.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break; // Writer closed the pipe
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(line);
                        JsonElement root = doc.RootElement;

                        int totalPercent = root.TryGetProperty("total_percent", out JsonElement tp) ? tp.GetInt32() : -1;
                        string message = root.TryGetProperty("message", out JsonElement msg) ? msg.GetString() ?? "Slicing..." : "Slicing...";

                        if (root.TryGetProperty("warning", out JsonElement warn))
                        {
                            _logger.LogWarning("OrcaSlicer warning for job {JobId}: {Warning}", jobId, warn.GetString());
                        }

                        if (totalPercent >= 0)
                        {
                            // Map OrcaSlicer's 0-100 to our 30-70 range
                            int mapped = 30 + (int)(totalPercent * 0.4);
                            mapped = Math.Clamp(mapped, 30, 70);
                            await _progressReporter.ReportProgressAsync(jobId, mapped, message, cancellationToken);
                        }
                    }
                    catch (JsonException)
                    {
                        // Non-JSON line — ignore
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading progress pipe for job {JobId}", jobId);
        }
    }

    private async Task MonitorSlicingProgressAsync(Guid jobId, Process process, CancellationToken cancellationToken)
    {
        try
        {
            DateTime startTime = DateTime.UtcNow;
            DateTime lastProgressReport = DateTime.UtcNow;
            int currentProgress = 30;
            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                TimeSpan elapsed = DateTime.UtcNow - startTime;
                if (elapsed.TotalSeconds > 10 && currentProgress < 70)
                {
                    currentProgress = Math.Min(70, 30 + (int)(elapsed.TotalSeconds * 2));
                    await _progressReporter.ReportProgressAsync(jobId, currentProgress, "Slicing in progress...", cancellationToken);
                    lastProgressReport = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastProgressReport > TimeSpan.FromSeconds(10))
                {
                    await _progressReporter.ReportProgressAsync(jobId, currentProgress, "Slicing in progress...", cancellationToken);
                    lastProgressReport = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error monitoring slicing progress for job {JobId}", jobId);
        }
    }

    private bool TryCreateNamedPipe(string pipePath)
    {
        try
        {
            if (File.Exists(pipePath))
            {
                File.Delete(pipePath);
            }

            using Process mkfifo = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "mkfifo",
                    Arguments = $"\"{pipePath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            _ = mkfifo.Start();
            mkfifo.WaitForExit(5000);

            if (mkfifo.ExitCode != 0)
            {
                string err = mkfifo.StandardError.ReadToEnd();
                _logger.LogWarning("mkfifo failed (exit {ExitCode}): {Error}", mkfifo.ExitCode, err);
                return false;
            }

            _logger.LogDebug("Created progress pipe at {PipePath}", pipePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create named pipe at {PipePath}", pipePath);
            return false;
        }
    }

    private static async Task<GcodeMetadata> ExtractGcodeMetadataAsync(string gcodeFilePath, CancellationToken cancellationToken)
    {
        FileInfo fileInfo = new FileInfo(gcodeFilePath);
        string[] lines = await File.ReadAllLinesAsync(gcodeFilePath, cancellationToken).ConfigureAwait(false);
        GcodeMetadata metadata = new GcodeMetadata();
        Regex printTimeRegex = MyRegex();
        Regex printTimeSecondsRegex = new Regex(@";\s*estimated printing time.*?(\d+)s", RegexOptions.IgnoreCase);
        Regex filamentRegex = new Regex(@";\s*filament used.*?(\d+\.?\d*)(?:mm|g)", RegexOptions.IgnoreCase);
        Regex layerRegex = new Regex(@";\s*layer_count\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        Regex layerCommentRegex = new Regex(@";\s*LAYER:(\d+)", RegexOptions.IgnoreCase);
        int maxLayer = 0;
        foreach (string line in lines)
        {
            Match tm = printTimeRegex.Match(line);
            if (tm.Success)
            {
                metadata.PrintTimeSeconds = (int.Parse(tm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 3600) + (int.Parse(tm.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * 60);
            }
            else
            {
                Match ts = printTimeSecondsRegex.Match(line);
                if (ts.Success)
                {
                    metadata.PrintTimeSeconds = int.Parse(ts.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            Match fm = filamentRegex.Match(line);
            if (fm.Success)
            {
                double amount = double.Parse(fm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                metadata.FilamentUsageGrams = line.Contains("mm", StringComparison.Ordinal) ? amount * 0.0025 : amount;
            }

            Match lc = layerRegex.Match(line);
            if (lc.Success)
            {
                metadata.LayerCount = int.Parse(lc.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            Match lcm = layerCommentRegex.Match(line);
            if (lcm.Success)
            {
                maxLayer = Math.Max(maxLayer, int.Parse(lcm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        if (metadata.LayerCount == 0 && maxLayer > 0)
        {
            metadata.LayerCount = maxLayer + 1;
        }

        const double epsilon = 0.0001;
        if (Math.Abs(metadata.PrintTimeSeconds) < epsilon)
        {
            metadata.PrintTimeSeconds = metadata.LayerCount > 0 ? metadata.LayerCount * 120 : 1800;
        }

        if (Math.Abs(metadata.FilamentUsageGrams) < epsilon)
        {
            metadata.FilamentUsageGrams = Math.Max(5.0, fileInfo.Length / 50000.0);
        }

        if (metadata.LayerCount == 0)
        {
            metadata.LayerCount = lines.Count(l => l.StartsWith("G1 Z", StringComparison.Ordinal) || l.StartsWith("G0 Z", StringComparison.Ordinal));
        }

        if (metadata.LayerCount == 0)
        {
            metadata.LayerCount = 100;
        }

        return metadata;
    }

    private async Task<string> UploadGcodeAsync(string gcodeFilePath, DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(gcodeFilePath);
        string mockUrl = $"{_storageEndpoint}/api/files/gcode/{job.Id}/{fileName}";
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        return mockUrl;
    }

    private sealed class GcodeMetadata
    {
        public double PrintTimeSeconds { get; set; }

        public double FilamentUsageGrams { get; set; }

        public int LayerCount { get; set; }
    }

    [GeneratedRegex(@";\s*estimated printing time.*?(\d+)h\s*(\d+)m", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex MyRegex();

    /// <summary>
    /// Parsed transform result: CLI flags and whether a custom position was specified.
    /// When <see cref="HasCustomPosition"/> is true, callers should use --arrange 0
    /// instead of --arrange 1 so OrcaSlicer respects the explicit placement.
    /// </summary>
    internal readonly record struct TransformResult(string Flags, bool HasCustomPosition);

    /// <summary>
    /// Parse model transform JSON from the UI and build OrcaSlicer CLI transform flags.
    /// Input: {"rotation":[rx,ry,rz],"scale":[sx,sy,sz],"position":[px,py,pz]}
    ///   — radians for rotation, Y-up coordinate system (Three.js/R3F).
    /// Output: OrcaSlicer flags in degrees, Z-up.
    /// Axis mapping (rotation): R3F X → --rotate-x, R3F Y (up) → --rotate (Z), R3F Z → --rotate-y.
    /// Axis mapping (position):  R3F X → OrcaSlicer X, R3F Z → OrcaSlicer Y (bed plane).
    /// </summary>
    internal static TransformResult BuildTransformFlags(string? modelTransformJson)
    {
        if (string.IsNullOrWhiteSpace(modelTransformJson))
        {
            return new TransformResult(string.Empty, false);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(modelTransformJson);
            JsonElement root = doc.RootElement;
            StringBuilder flags = new();
            bool hasCustomPosition = false;

            if (root.TryGetProperty("rotation", out JsonElement rotEl) && rotEl.ValueKind == JsonValueKind.Array)
            {
                double[] rot = new double[3];
                int i = 0;
                foreach (JsonElement el in rotEl.EnumerateArray())
                {
                    if (i < 3 && el.ValueKind == JsonValueKind.Number)
                    {
                        double v = el.GetDouble();
                        rot[i++] = double.IsFinite(v) ? v : 0;
                    }
                }

                const double radToDeg = 180.0 / Math.PI;
                const double epsilon = 0.001;

                // Workspace is Z-up with XY bed plane (camera.up = [0,0,1]).
                // rotation[0]=X, rotation[1]=Y, rotation[2]=Z — same axes as OrcaSlicer.
                double rotXDeg = rot[0] * radToDeg;
                if (Math.Abs(rotXDeg) > epsilon)
                {
                    flags.Append(CultureInfo.InvariantCulture, $" --rotate-x {rotXDeg:F2}");
                }

                double rotYDeg = rot[1] * radToDeg;
                if (Math.Abs(rotYDeg) > epsilon)
                {
                    flags.Append(CultureInfo.InvariantCulture, $" --rotate-y {rotYDeg:F2}");
                }

                // Z-rotation (around up axis) = OrcaSlicer --rotate (yaw)
                double rotZDeg = rot[2] * radToDeg;
                if (Math.Abs(rotZDeg) > epsilon)
                {
                    flags.Append(CultureInfo.InvariantCulture, $" --rotate {rotZDeg:F2}");
                }
            }

            if (root.TryGetProperty("scale", out JsonElement scaleEl) && scaleEl.ValueKind == JsonValueKind.Array)
            {
                double[] scale = new double[3] { 1, 1, 1 };
                int i = 0;
                foreach (JsonElement el in scaleEl.EnumerateArray())
                {
                    if (i < 3 && el.ValueKind == JsonValueKind.Number)
                    {
                        double v = el.GetDouble();
                        scale[i++] = double.IsFinite(v) ? v : 1;
                    }
                }

                // Use uniform scale (first component). 1.0 = no change.
                const double epsilon = 0.001;
                if (Math.Abs(scale[0] - 1.0) > epsilon)
                {
                    flags.Append(CultureInfo.InvariantCulture, $" --scale {scale[0]:F4}");
                }
            }

            // Workspace is Z-up with XY bed plane — same as OrcaSlicer.
            // position[0]=X (bed), position[1]=Y (bed), position[2]=Z (height, ignored for --center).
            if (root.TryGetProperty("position", out JsonElement posEl) && posEl.ValueKind == JsonValueKind.Array)
            {
                double[] pos = new double[3];
                int i = 0;
                foreach (JsonElement el in posEl.EnumerateArray())
                {
                    if (i < 3 && el.ValueKind == JsonValueKind.Number)
                    {
                        double v = el.GetDouble();
                        pos[i++] = double.IsFinite(v) ? v : 0;
                    }
                }

                const double epsilon = 0.001;
                double bedX = pos[0]; // X bed axis → OrcaSlicer X
                double bedY = pos[1]; // Y bed axis → OrcaSlicer Y

                if (Math.Abs(bedX) > epsilon || Math.Abs(bedY) > epsilon)
                {
                    hasCustomPosition = true;
                    flags.Append(CultureInfo.InvariantCulture, $" --center {bedX:F2},{bedY:F2}");
                }
            }

            return new TransformResult(flags.ToString(), hasCustomPosition);
        }
        catch (JsonException)
        {
            return new TransformResult(string.Empty, false);
        }
    }

    /// <summary>
    /// Converts a Settings dictionary to JSON for OrcaSlicer --load-settings.
    /// All scalars are written as JSON strings. Values that look like JSON arrays
    /// (start with '[') are written as native arrays.
    /// Keys with values that would fail OrcaSlicer's CLI validator are sanitized.
    /// </summary>
    internal static string SettingsDictToNativeJson(Dictionary<string, object>? settings)
    {
        if (settings == null || settings.Count == 0)
        {
            return "{}";
        }

        // OrcaSlicer --load-settings has stricter range checks than the profile format.
        // Clamp known speed/rate fields that use 0="auto" in profiles but require ≥1 in CLI.
        SanitizeForCli(settings);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, object> kvp in settings)
            {
                writer.WritePropertyName(kvp.Key);

                // List<string> values — write as native JSON array
                if (kvp.Value is IList<string> list)
                {
                    writer.WriteStartArray();
                    foreach (string item in list)
                    {
                        writer.WriteStringValue(item);
                    }

                    writer.WriteEndArray();
                    continue;
                }

                string value = kvp.Value?.ToString() ?? string.Empty;

                // Legacy: raw JSON array text (e.g. "[\"0.4\"]") — write as native array
                if (value.Length > 0 && value[0] == '[')
                {
                    try
                    {
                        using JsonDocument arr = JsonDocument.Parse(value);
                        arr.RootElement.WriteTo(writer);
                        continue;
                    }
                    catch (JsonException)
                    {
                        // Not valid JSON array — fall through to string
                    }
                }

                // OrcaSlicer CLI (both 2.3.1 and 2.3.2) expects all scalar values as
                // JSON strings — matching the native profile format. Arrays are the
                // only exception (written as native JSON arrays above).
                writer.WriteStringValue(value);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Clamp values that OrcaSlicer profiles store as 0 (meaning "auto/disabled")
    /// but the --load-settings CLI validator rejects as out of range.
    /// Also injects defaults for fields required by OrcaSlicer 2.3.2+ CLI
    /// that weren't present in older profiles.
    /// </summary>
    private static void SanitizeForCli(Dictionary<string, object> settings)
    {
        // Speed fields: 0 means "auto" in profiles but CLI requires ≥ 1
        string[] speedKeys =
        [
            "scarf_joint_speed",
            "skirt_speed",
        ];

        foreach (string key in speedKeys)
        {
            if (settings.TryGetValue(key, out object? val) && val?.ToString() == "0")
            {
                settings[key] = "1";
            }
        }

        // OrcaSlicer 2.3.2 requires extruder_type and nozzle_volume_type for
        // update_values_to_printer_extruders. Without them the CLI segfaults
        // (exit 139) when looking up extruder defaults.
        if (!settings.ContainsKey("extruder_type"))
        {
            settings["extruder_type"] = new List<string> { "Direct Drive" };
        }

        if (!settings.ContainsKey("nozzle_volume_type"))
        {
            settings["nozzle_volume_type"] = new List<string> { "Standard" };
        }
    }
}
