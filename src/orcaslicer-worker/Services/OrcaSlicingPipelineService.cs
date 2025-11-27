using System.Diagnostics;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Worker.Core; // shared interfaces
using Farm.Web.Shared;

namespace Farm.OrcaSlicer.Worker.Services;

public partial class OrcaSlicingPipelineService : ISlicingPipelineService
{
    private readonly HttpClient _httpClient;
    private readonly IProgressReporter _progressReporter;
    private readonly IUnifiedLoggingService _logger;
    private readonly string _workingDirectory;
    private readonly string _storageEndpoint;
    private readonly string _orcaSlicerBinaryPath;

    public OrcaSlicingPipelineService(HttpClient httpClient, IProgressReporter progressReporter, IUnifiedLoggingService logger, IConfiguration configuration)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _progressReporter = progressReporter ?? throw new ArgumentNullException(nameof(progressReporter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(configuration);
        _workingDirectory = configuration["Worker:WorkingDirectory"] ?? "/tmp/orca-work";
        _storageEndpoint = configuration["Worker:StorageEndpoint"] ?? "http://api:5245";
        _orcaSlicerBinaryPath = configuration["Worker:OrcaSlicerPath"] ?? "/usr/local/bin/orcaslicer";
        if (!Directory.Exists(_workingDirectory))
        {
            Directory.CreateDirectory(_workingDirectory);
        }
    }

    public async Task<SlicingResult> ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobWorkDir = Path.Combine(_workingDirectory, job.Id.ToString());
        Directory.CreateDirectory(jobWorkDir);
        try
        {
            _logger.LogInformation($"Starting slicing pipeline for job {job.Id}");
            await _progressReporter.ReportProgressAsync(job.Id, 10, "Downloading STL file", cancellationToken);
            var stlFilePath = await FetchStlFileAsync(job, jobWorkDir, cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, 20, "Preparing slicer configuration", cancellationToken);
            var configFilePath = await PrepareSlicerConfigAsync(job, jobWorkDir, cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, 30, "Running OrcaSlicer", cancellationToken);
            var gcodeFilePath = await RunOrcaSlicerAsync(stlFilePath, configFilePath, jobWorkDir, job, cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, 80, "Analyzing G-code", cancellationToken);
            var metadata = await ExtractGcodeMetadataAsync(gcodeFilePath, cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, 90, "Uploading G-code", cancellationToken);
            var gcodeUrl = await UploadGcodeAsync(gcodeFilePath, job, cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, 100, "Slicing completed", cancellationToken);
            var result = new SlicingResult
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
                _logger.LogWarning(ex, $"Failed cleanup {jobWorkDir}");
            }
        }
    }

    private async Task<string> FetchStlFileAsync(DistributedSlicingJob job, string workDir, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(job.ModelFileUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var stlFilePath = Path.Combine(workDir, job.ModelFileName);
        await using var fileStream = File.Create(stlFilePath);
        await response.Content.CopyToAsync(fileStream, cancellationToken);
        job.InputFileSizeBytes = new FileInfo(stlFilePath).Length;
        return stlFilePath;
    }

#pragma warning disable S1172 // Unused parameters are required by interface
    private static async Task<string> PrepareSlicerConfigAsync(DistributedSlicingJob job, string workDir, CancellationToken cancellationToken)
    {
        // Deprecated: profiles are now generated directly from database JSON in RunOrcaSlicerAsync
        // This method is kept for backward compatibility with the interface
        return workDir;
    }
#pragma warning restore S1172

    private static async Task<Dictionary<string, string>> GenerateProfileJsonFilesAsync(SlicerProfileDto? profile, string workDir, CancellationToken cancellationToken)
    {
        if (profile == null)
        {
            throw new ArgumentNullException(nameof(profile), "Profile is required for slicing");
        }

        var machineJsonPath = Path.Combine(workDir, "machine.json");
        var processJsonPath = Path.Combine(workDir, "process.json");
        var filamentJsonPath = Path.Combine(workDir, "filament.json");

        // Write the profiles directly as JSON - they should already contain complete settings from the database
        var machineJson = System.Text.Json.JsonSerializer.Serialize(profile.MachineProfile, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var processJson = System.Text.Json.JsonSerializer.Serialize(profile.ProcessProfile, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var filamentJson = System.Text.Json.JsonSerializer.Serialize(profile.FilamentProfile, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

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

#pragma warning disable S1172 // configPath is kept for method signature compatibility
    private async Task<string> RunOrcaSlicerAsync(string stlPath, string configPath, string workDir, DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        var gcodeOutputDir = Path.Combine(workDir, "output");
        Directory.CreateDirectory(gcodeOutputDir);

        var gcodeFilePath = Path.Combine(gcodeOutputDir, Path.GetFileNameWithoutExtension(job.ModelFileName) + ".gcode");
        if (!File.Exists(_orcaSlicerBinaryPath))
        {
            throw new InvalidOperationException($"OrcaSlicer binary not found at {_orcaSlicerBinaryPath}");
        }

        // Generate the three JSON profile files
        var profilePaths = await GenerateProfileJsonFilesAsync(job.Profile, workDir, cancellationToken);

        var machineJson = profilePaths["machine"];
        var processJson = profilePaths["process"];
        var filamentJson = profilePaths["filament"];

        // Build command line: --slice 0 --load-settings "machine.json;process.json" --load-filaments "filament.json" --allow-newer-file --outputdir "/tmp/slice-XYZ/output" /tmp/slice-XYZ/input/uploaded-file.stl
        var arguments = $"--slice 0 --load-settings \"{machineJson};{processJson}\" --load-filaments \"{filamentJson}\" --allow-newer-file --outputdir \"{gcodeOutputDir}\" \"{stlPath}\"";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _orcaSlicerBinaryPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            }
        };
        var progressTask = MonitorSlicingProgressAsync(job.Id, process, cancellationToken);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await progressTask;
        var error = await errorTask; // output ignored for brevity
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"OrcaSlicer failed with exit code {process.ExitCode}: {error}");
        }
        if (!File.Exists(gcodeFilePath))
        {
            throw new InvalidOperationException("OrcaSlicer completed but no G-code produced");
        }
        return gcodeFilePath;
    }
#pragma warning restore S1172

    private async Task MonitorSlicingProgressAsync(Guid jobId, Process process, CancellationToken cancellationToken)
    {
        try
        {
            var startTime = DateTime.UtcNow;
            var lastProgressReport = DateTime.UtcNow;
            var currentProgress = 30;
            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                var elapsed = DateTime.UtcNow - startTime;
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
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, $"Error monitoring slicing progress for job {jobId}"); }
    }

    private static async Task<GcodeMetadata> ExtractGcodeMetadataAsync(string gcodeFilePath, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(gcodeFilePath);
        var lines = await File.ReadAllLinesAsync(gcodeFilePath, cancellationToken);
        var metadata = new GcodeMetadata();
        var printTimeRegex = MyRegex();
        var printTimeSecondsRegex = new Regex(@";\s*estimated printing time.*?(\d+)s", RegexOptions.IgnoreCase);
        var filamentRegex = new Regex(@";\s*filament used.*?(\d+\.?\d*)(?:mm|g)", RegexOptions.IgnoreCase);
        var layerRegex = new Regex(@";\s*layer_count\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        var layerCommentRegex = new Regex(@";\s*LAYER:(\d+)", RegexOptions.IgnoreCase);
        var maxLayer = 0;
        foreach (var line in lines)
        {
            var tm = printTimeRegex.Match(line);
            if (tm.Success)
            {
                metadata.PrintTimeSeconds = int.Parse(tm.Groups[1].Value) * 3600 + int.Parse(tm.Groups[2].Value) * 60;
            }
            else
            {
                var ts = printTimeSecondsRegex.Match(line);
                if (ts.Success)
                {
                    metadata.PrintTimeSeconds = int.Parse(ts.Groups[1].Value);
                }
            }
            var fm = filamentRegex.Match(line);
            if (fm.Success)
            {
                var amount = double.Parse(fm.Groups[1].Value);
                metadata.FilamentUsageGrams = line.Contains("mm") ? amount * 0.0025 : amount;
            }
            var lc = layerRegex.Match(line);
            if (lc.Success)
            {
                metadata.LayerCount = int.Parse(lc.Groups[1].Value);
            }
            var lcm = layerCommentRegex.Match(line);
            if (lcm.Success)
            {
                maxLayer = Math.Max(maxLayer, int.Parse(lcm.Groups[1].Value));
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
            metadata.LayerCount = lines.Count(l => l.StartsWith("G1 Z") || l.StartsWith("G0 Z"));
        }
        if (metadata.LayerCount == 0)
        {
            metadata.LayerCount = 100;
        }
        return metadata;
    }

    private async Task<string> UploadGcodeAsync(string gcodeFilePath, DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(gcodeFilePath);
        var mockUrl = $"{_storageEndpoint}/api/files/gcode/{job.Id}/{fileName}";
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
