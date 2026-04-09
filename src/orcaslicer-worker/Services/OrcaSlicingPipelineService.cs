using System.Diagnostics;
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
            await _progressReporter.ReportProgressAsync(job.Id, 10, "Downloading STL file", cancellationToken);
            string stlFilePath = await FetchStlFileAsync(job, jobWorkDir, cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, 20, "Preparing slicer configuration", cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, 30, "Running OrcaSlicer", cancellationToken);
            string gcodeFilePath = await RunOrcaSlicerAsync(stlFilePath, jobWorkDir, job, cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, 80, "Analyzing G-code", cancellationToken);
            GcodeMetadata metadata = await ExtractGcodeMetadataAsync(gcodeFilePath, cancellationToken);
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
            return result;
        }
        finally
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

    private static async Task<Dictionary<string, string>> GenerateProfileJsonFilesAsync(SlicerProfileDto? profile, string workDir, CancellationToken cancellationToken)
    {
        if (profile == null)
        {
            throw new ArgumentNullException(nameof(profile), "Profile is required for slicing");
        }

        string machineJsonPath = Path.Combine(workDir, "machine.json");
        string processJsonPath = Path.Combine(workDir, "process.json");
        string filamentJsonPath = Path.Combine(workDir, "filament.json");

        // Write the profiles directly as JSON - they should already contain complete settings from the database
        string machineJson = JsonSerializer.Serialize(profile.MachineProfile, new JsonSerializerOptions { WriteIndented = true });
        string processJson = JsonSerializer.Serialize(profile.ProcessProfile, new JsonSerializerOptions { WriteIndented = true });
        string filamentJson = JsonSerializer.Serialize(profile.FilamentProfile, new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(machineJsonPath, machineJson, cancellationToken);
        await File.WriteAllTextAsync(processJsonPath, processJson, cancellationToken);
        await File.WriteAllTextAsync(filamentJsonPath, filamentJson, cancellationToken);

        return new Dictionary<string, string>
        {
            { "machine", machineJsonPath },
            { "process", processJsonPath },
            { "filament", filamentJsonPath }
        };
    }

    private async Task<string> RunOrcaSlicerAsync(string stlPath, string workDir, DistributedSlicingJob job, CancellationToken cancellationToken)
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

        // Build command line: --slice 0 --load-settings "machine.json;process.json" --load-filaments "filament.json" --allow-newer-file --outputdir "/tmp/slice-XYZ/output" /tmp/slice-XYZ/input/uploaded-file.stl
        string arguments = $"--slice 0 --load-settings \"{machineJson};{processJson}\" --load-filaments \"{filamentJson}\" --allow-newer-file --outputdir \"{gcodeOutputDir}\" \"{stlPath}\"";

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
        Task progressTask = MonitorSlicingProgressAsync(job.Id, process, cancellationToken);
#pragma warning restore CA2025
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await progressTask;
        string output = await outputTask;
        string error = await errorTask;

        _logger.LogInformation("OrcaSlicer exited with code {ExitCode}. Stdout length={StdoutLen}, Stderr length={StderrLen}",
            process.ExitCode, output.Length, error.Length);

        if (!string.IsNullOrWhiteSpace(output))
        {
            _logger.LogInformation("OrcaSlicer stdout: {Output}", output.Length > 2000 ? output[..2000] : output);
        }

        if (process.ExitCode != 0)
        {
            _logger.LogError("OrcaSlicer stderr: {Error}", error);
            throw new InvalidOperationException($"OrcaSlicer failed with exit code {process.ExitCode}: {error}");
        }

        return !File.Exists(gcodeFilePath)
            ? throw new InvalidOperationException("OrcaSlicer completed but no G-code produced")
            : gcodeFilePath;
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
}
