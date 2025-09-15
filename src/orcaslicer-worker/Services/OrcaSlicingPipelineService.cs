using System.Diagnostics;
using System.Text.RegularExpressions;
using Farm.Slicer.Worker.Core; // shared interfaces
using Farm.Web.Shared;

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
            _logger.LogInformation("Starting slicing pipeline for job {JobId}", job.Id);
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
                _logger.LogWarning(ex, "Failed cleanup {Dir}", jobWorkDir);
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

    private static async Task<string> PrepareSlicerConfigAsync(DistributedSlicingJob job, string workDir, CancellationToken cancellationToken)
    {
        var configContent = GenerateOrcaSlicerConfig(job.Profile);
        var configFilePath = Path.Combine(workDir, "config.ini");
        await File.WriteAllTextAsync(configFilePath, configContent, cancellationToken);
        return configFilePath;
    }

    private async Task<string> RunOrcaSlicerAsync(string stlPath, string configPath, string workDir, DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        var gcodeFilePath = Path.Combine(workDir, Path.GetFileNameWithoutExtension(job.ModelFileName) + ".gcode");
        if (!File.Exists(_orcaSlicerBinaryPath))
        {
            throw new InvalidOperationException($"OrcaSlicer binary not found at {_orcaSlicerBinaryPath}");
        }
        var arguments = $"--config \"{configPath}\" --output \"{gcodeFilePath}\" \"{stlPath}\"";
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
        catch (Exception ex) { _logger.LogWarning(ex, "Error monitoring slicing progress for job {JobId}", jobId); }
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

    private static string GenerateOrcaSlicerConfig(SlicerProfileDto? profile)
    {
        var config = new System.Text.StringBuilder();
        config.AppendLine("# Generated by PrintFarmer OrcaSlicer Worker");
        config.AppendLine($"# Generated at {DateTime.UtcNow:O}");
        config.AppendLine();
        config.AppendLine("[print]");
        config.AppendLine($"layer_height = {profile?.LayerHeight ?? 0.2}");
        config.AppendLine($"first_layer_height = {(profile?.LayerHeight ?? 0.2) * 1.5}");
        config.AppendLine("perimeters = 2");
        config.AppendLine("top_solid_layers = 3");
        config.AppendLine("bottom_solid_layers = 3");
        config.AppendLine($"fill_density = {(profile?.InfillPercentage ?? 20) / 100.0:F2}");
        config.AppendLine("fill_pattern = cubic");
        config.AppendLine($"external_perimeter_speed = {(profile?.PrintSpeed ?? 50) * 0.8:F0}");
        config.AppendLine($"perimeter_speed = {profile?.PrintSpeed ?? 50}");
        config.AppendLine($"infill_speed = {(profile?.PrintSpeed ?? 50) * 1.2:F0}");
        config.AppendLine("travel_speed = 120");
        config.AppendLine($"first_layer_speed = {(profile?.PrintSpeed ?? 50) * 0.5:F0}");
        config.AppendLine();
        config.AppendLine("[filament]");
        config.AppendLine($"temperature = {profile?.NozzleTemperature ?? 210}");
        config.AppendLine($"first_layer_temperature = {(profile?.NozzleTemperature ?? 210) + 5}");
        config.AppendLine($"bed_temperature = {profile?.BedTemperature ?? 60}");
        config.AppendLine($"first_layer_bed_temperature = {(profile?.BedTemperature ?? 60) + 5}");
        config.AppendLine("filament_diameter = 1.75");
        config.AppendLine("extrusion_multiplier = 1.0");
        config.AppendLine($"filament_type = {profile?.Material ?? "PLA"}");
        config.AppendLine();
        if (profile?.Supports == true)
        {
            config.AppendLine("[support]");
            config.AppendLine("support_material = 1");
            config.AppendLine("support_material_auto = 1");
            config.AppendLine("support_material_threshold = 45");
            config.AppendLine("support_material_pattern = rectilinear");
            config.AppendLine("support_material_spacing = 2.5");
            config.AppendLine("support_material_interface_layers = 2");
            config.AppendLine();
        }
        config.AppendLine("[printer]");
        config.AppendLine("bed_shape = 0x0,200x0,200x200,0x200");
        config.AppendLine("print_center = 100,100");
        config.AppendLine("z_offset = 0");
        config.AppendLine("nozzle_diameter = 0.4");
        config.AppendLine("extruder_count = 1");
        config.AppendLine();
        return config.ToString();
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
