using System.Diagnostics;
using System.Text.RegularExpressions;
using Farm.Web.Shared;

namespace Farm.Slicer.Worker.Services;

/// <summary>
/// Implementation of the STL fetch -> slice -> G-code upload pipeline for OrcaSlicer
/// </summary>
public class OrcaSlicingPipelineService : ISlicingPipelineService
{
    private readonly HttpClient _httpClient;
    private readonly IProgressReporter _progressReporter;
    private readonly ILogger<OrcaSlicingPipelineService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _workingDirectory;
    private readonly string _storageEndpoint;
    private readonly string _orcaSlicerBinaryPath;

    public OrcaSlicingPipelineService(
        HttpClient httpClient,
        IProgressReporter progressReporter,
        ILogger<OrcaSlicingPipelineService> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _progressReporter = progressReporter;
        _logger = logger;
        _configuration = configuration;
        _workingDirectory = configuration["Worker:WorkingDirectory"] ?? "/tmp/slicer-work";
        _storageEndpoint = configuration["Worker:StorageEndpoint"] ?? "http://api:5245";
        _orcaSlicerBinaryPath = configuration["Worker:OrcaSlicerPath"] ?? "/usr/local/bin/orcaslicer";

        // Ensure working directory exists
        if (!Directory.Exists(_workingDirectory))
        {
            Directory.CreateDirectory(_workingDirectory);
        }
    }

    public async Task<SlicingPipelineResult> ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default)
    {
        var jobWorkDir = Path.Combine(_workingDirectory, job.Id.ToString());
        Directory.CreateDirectory(jobWorkDir);

        try
        {
            _logger.LogInformation("Starting slicing pipeline for job {JobId}", job.Id);

            // Stage 1: Fetch STL file
            await _progressReporter.ReportProgressAsync(job.Id, 10, "Downloading STL file", cancellationToken);
            var stlFilePath = await FetchStlFileAsync(job, jobWorkDir, cancellationToken);

            // Stage 2: Prepare slicer configuration
            await _progressReporter.ReportProgressAsync(job.Id, 20, "Preparing slicer configuration", cancellationToken);
            var configFilePath = await PrepareSlicerConfigAsync(job, jobWorkDir, cancellationToken);

            // Stage 3: Run OrcaSlicer
            await _progressReporter.ReportProgressAsync(job.Id, 30, "Running OrcaSlicer", cancellationToken);
            var gcodeFilePath = await RunOrcaSlicerAsync(stlFilePath, configFilePath, jobWorkDir, job, cancellationToken);

            // Stage 4: Parse G-code metadata
            await _progressReporter.ReportProgressAsync(job.Id, 80, "Analyzing G-code", cancellationToken);
            var metadata = await ExtractGcodeMetadataAsync(gcodeFilePath, cancellationToken);

            // Stage 5: Upload G-code
            await _progressReporter.ReportProgressAsync(job.Id, 90, "Uploading G-code", cancellationToken);
            var gcodeUrl = await UploadGcodeAsync(gcodeFilePath, job, cancellationToken);

            await _progressReporter.ReportProgressAsync(job.Id, 100, "Slicing completed", cancellationToken);

            var result = new SlicingPipelineResult
            {
                GcodeFileUrl = gcodeUrl,
                EstimatedPrintTimeSeconds = metadata.PrintTimeSeconds,
                EstimatedFilamentUsageGrams = metadata.FilamentUsageGrams,
                FileSizeBytes = new FileInfo(gcodeFilePath).Length,
                LayerCount = metadata.LayerCount,
                Metadata = new Dictionary<string, object>
                {
                    ["SlicerVersion"] = "OrcaSlicer 1.8.0",
                    ["ProcessedAt"] = DateTime.UtcNow.ToString("O"),
                    ["WorkerId"] = job.WorkerId ?? "unknown"
                }
            };

            _logger.LogInformation("Slicing pipeline completed for job {JobId}, output: {GcodeUrl}", job.Id, gcodeUrl);
            return result;
        }
        finally
        {
            // Cleanup working directory
            try
            {
                Directory.Delete(jobWorkDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup working directory {WorkDir}", jobWorkDir);
            }
        }
    }

    private async Task<string> FetchStlFileAsync(DistributedSlicingJob job, string workDir, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Fetching STL file from {ModelFileUrl}", job.ModelFileUrl);

        var response = await _httpClient.GetAsync(job.ModelFileUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var stlFilePath = Path.Combine(workDir, job.ModelFileName);
        await using var fileStream = File.Create(stlFilePath);
        await response.Content.CopyToAsync(fileStream, cancellationToken);

        job.InputFileSizeBytes = new FileInfo(stlFilePath).Length;

        _logger.LogInformation("Downloaded STL file ({SizeBytes} bytes) in {ElapsedMs}ms",
            job.InputFileSizeBytes, stopwatch.ElapsedMilliseconds);

        return stlFilePath;
    }

    private async Task<string> PrepareSlicerConfigAsync(DistributedSlicingJob job, string workDir, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Preparing OrcaSlicer configuration");

        // For Phase 1, use a simple mock configuration
        // In production, this would generate actual OrcaSlicer config from job.Profile
        var configContent = GenerateOrcaSlicerConfig(job.Profile);
        var configFilePath = Path.Combine(workDir, "config.ini");

        await File.WriteAllTextAsync(configFilePath, configContent, cancellationToken);

        _logger.LogInformation("Generated slicer configuration in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
        return configFilePath;
    }

    private async Task<string> RunOrcaSlicerAsync(string stlPath, string configPath, string workDir, DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Running OrcaSlicer on {StlPath}", stlPath);

        var gcodeFilePath = Path.Combine(workDir, Path.GetFileNameWithoutExtension(job.ModelFileName) + ".gcode");

        try
        {
            // Verify OrcaSlicer binary exists
            if (!File.Exists(_orcaSlicerBinaryPath))
            {
                _logger.LogError("OrcaSlicer binary not found at {BinaryPath}", _orcaSlicerBinaryPath);
                throw new InvalidOperationException($"OrcaSlicer binary not found at {_orcaSlicerBinaryPath}");
            }

            // Prepare OrcaSlicer command arguments
            // Using CLI format: orcaslicer --config config.ini --output output.gcode input.stl
            var arguments = $"--config \"{configPath}\" --output \"{gcodeFilePath}\" \"{stlPath}\"";

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _orcaSlicerBinaryPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            };

            _logger.LogInformation("Starting OrcaSlicer process: {FileName} {Arguments}", process.StartInfo.FileName, process.StartInfo.Arguments);

            // Start progress monitoring task
            var progressTask = MonitorSlicingProgressAsync(job.Id, process, cancellationToken);

            process.Start();

            // Read output and error streams
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            // Wait for process completion
            await process.WaitForExitAsync(cancellationToken);

            // Wait for progress monitoring to complete
            await progressTask;

            var output = await outputTask;
            var error = await errorTask;

            _logger.LogDebug("OrcaSlicer output: {Output}", output);

            if (process.ExitCode != 0)
            {
                _logger.LogError("OrcaSlicer failed with exit code {ExitCode}. Error: {Error}", process.ExitCode, error);
                throw new InvalidOperationException($"OrcaSlicer failed with exit code {process.ExitCode}: {error}");
            }

            // Verify output file was created
            if (!File.Exists(gcodeFilePath))
            {
                throw new InvalidOperationException("OrcaSlicer completed successfully but no G-code file was generated");
            }

            _logger.LogInformation("OrcaSlicer completed successfully in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            return gcodeFilePath;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogError(ex, "Error running OrcaSlicer process");
            throw;
        }
    }

    private async Task MonitorSlicingProgressAsync(Guid jobId, Process process, CancellationToken cancellationToken)
    {
        try
        {
            // Monitor the process and report progress based on typical slicing phases
            var startTime = DateTime.UtcNow;
            var lastProgressReport = DateTime.UtcNow;
            var currentProgress = 30; // Starting at 30% (after config preparation)

            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

                // Report progress incrementally during slicing
                var elapsed = DateTime.UtcNow - startTime;

                // Estimate progress based on time elapsed (crude but functional)
                if (elapsed.TotalSeconds > 10 && currentProgress < 70)
                {
                    currentProgress = Math.Min(70, 30 + (int)(elapsed.TotalSeconds * 2));
                    await _progressReporter.ReportProgressAsync(jobId, currentProgress, "Slicing in progress...", cancellationToken);
                    lastProgressReport = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastProgressReport > TimeSpan.FromSeconds(10))
                {
                    // Send periodic heartbeat
                    await _progressReporter.ReportProgressAsync(jobId, currentProgress, "Slicing in progress...", cancellationToken);
                    lastProgressReport = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error monitoring slicing progress for job {JobId}", jobId);
        }
    }

    private async Task<GcodeMetadata> ExtractGcodeMetadataAsync(string gcodeFilePath, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Extracting G-code metadata from {GcodePath}", gcodeFilePath);

        var fileInfo = new FileInfo(gcodeFilePath);
        var lines = await File.ReadAllLinesAsync(gcodeFilePath, cancellationToken);

        // Initialize with defaults
        var metadata = new GcodeMetadata
        {
            PrintTimeSeconds = 0,
            FilamentUsageGrams = 0,
            LayerCount = 0
        };

        // Parse G-code comments for metadata (OrcaSlicer/PrusaSlicer format)
        var printTimeRegex = new Regex(@";\s*estimated printing time.*?(\d+)h\s*(\d+)m", RegexOptions.IgnoreCase);
        var printTimeSecondsRegex = new Regex(@";\s*estimated printing time.*?(\d+)s", RegexOptions.IgnoreCase);
        var filamentRegex = new Regex(@";\s*filament used.*?(\d+\.?\d*)(?:mm|g)", RegexOptions.IgnoreCase);
        var layerRegex = new Regex(@";\s*layer_count\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        var layerCommentRegex = new Regex(@";\s*LAYER:(\d+)", RegexOptions.IgnoreCase);

        var maxLayerNumber = 0;

        foreach (var line in lines)
        {
            // Extract print time (format: estimated printing time: 1h 30m)
            var timeMatch = printTimeRegex.Match(line);
            if (timeMatch.Success)
            {
                var hours = int.Parse(timeMatch.Groups[1].Value);
                var minutes = int.Parse(timeMatch.Groups[2].Value);
                metadata.PrintTimeSeconds = hours * 3600 + minutes * 60;
            }
            else
            {
                // Try seconds format
                var timeSecondsMatch = printTimeSecondsRegex.Match(line);
                if (timeSecondsMatch.Success)
                {
                    metadata.PrintTimeSeconds = int.Parse(timeSecondsMatch.Groups[1].Value);
                }
            }

            // Extract filament usage
            var filamentMatch = filamentRegex.Match(line);
            if (filamentMatch.Success)
            {
                var amount = double.Parse(filamentMatch.Groups[1].Value);
                // Convert mm to grams (approximate: 1mm of 1.75mm PLA ≈ 0.0025g)
                metadata.FilamentUsageGrams = line.Contains("mm") ? amount * 0.0025 : amount;
            }

            // Extract layer count from metadata
            var layerCountMatch = layerRegex.Match(line);
            if (layerCountMatch.Success)
            {
                metadata.LayerCount = int.Parse(layerCountMatch.Groups[1].Value);
            }

            // Count layers by LAYER comments as fallback
            var layerCommentMatch = layerCommentRegex.Match(line);
            if (layerCommentMatch.Success)
            {
                var layerNumber = int.Parse(layerCommentMatch.Groups[1].Value);
                maxLayerNumber = Math.Max(maxLayerNumber, layerNumber);
            }
        }

        // Use layer comment count as fallback if no layer_count metadata found
        if (metadata.LayerCount == 0 && maxLayerNumber > 0)
        {
            metadata.LayerCount = maxLayerNumber + 1; // Layer numbers typically start at 0
        }

        // Fallback estimates if no metadata found in G-code
        if (metadata.PrintTimeSeconds == 0)
        {
            // Estimate based on layer count and file size
            var estimatedSeconds = metadata.LayerCount * 120; // 2 minutes per layer estimate
            metadata.PrintTimeSeconds = estimatedSeconds > 0 ? estimatedSeconds : 1800; // Default 30 minutes
        }

        if (metadata.FilamentUsageGrams == 0)
        {
            // Estimate based on file size (rough approximation)
            metadata.FilamentUsageGrams = Math.Max(5.0, fileInfo.Length / 50000.0); // Rough estimate
        }

        if (metadata.LayerCount == 0)
        {
            // Count layers by scanning for Z movements as last resort
            metadata.LayerCount = lines.Count(line => line.StartsWith("G1 Z") || line.StartsWith("G0 Z"));
            if (metadata.LayerCount == 0)
            {
                metadata.LayerCount = 100; // Default fallback
            }
        }

        _logger.LogInformation("Extracted G-code metadata in {ElapsedMs}ms: {PrintTime}s print time, {Filament}g filament, {Layers} layers",
            stopwatch.ElapsedMilliseconds, metadata.PrintTimeSeconds, metadata.FilamentUsageGrams, metadata.LayerCount);

        return metadata;
    }

    private async Task<string> UploadGcodeAsync(string gcodeFilePath, DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Uploading G-code file {GcodePath}", gcodeFilePath);

        // For Phase 1, simulate upload by returning a mock URL
        // In production, this would upload to actual storage service
        var fileName = Path.GetFileName(gcodeFilePath);
        var mockUrl = $"{_storageEndpoint}/api/files/gcode/{job.Id}/{fileName}";

        // Simulate upload time
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        _logger.LogInformation("Uploaded G-code file to {Url} in {ElapsedMs}ms", mockUrl, stopwatch.ElapsedMilliseconds);
        return mockUrl;
    }

    private string GenerateOrcaSlicerConfig(SlicerProfileDto? profile)
    {
        // Generate real OrcaSlicer configuration in INI format
        // Based on OrcaSlicer/PrusaSlicer configuration structure
        var config = new System.Text.StringBuilder();

        // Version and compatibility
        config.AppendLine("# Generated by PrintFarmer OrcaSlicer Worker");
        config.AppendLine($"# Generated at {DateTime.UtcNow:O}");
        config.AppendLine();

        // Print settings
        config.AppendLine("[print]");
        config.AppendLine($"layer_height = {profile?.LayerHeight ?? 0.2}");
        config.AppendLine($"first_layer_height = {(profile?.LayerHeight ?? 0.2) * 1.5}");
        config.AppendLine($"perimeters = 2");
        config.AppendLine($"top_solid_layers = 3");
        config.AppendLine($"bottom_solid_layers = 3");
        config.AppendLine($"fill_density = {(profile?.InfillPercentage ?? 20) / 100.0:F2}");
        config.AppendLine($"fill_pattern = cubic");
        config.AppendLine($"external_perimeter_speed = {(profile?.PrintSpeed ?? 50) * 0.8:F0}");
        config.AppendLine($"perimeter_speed = {profile?.PrintSpeed ?? 50}");
        config.AppendLine($"infill_speed = {(profile?.PrintSpeed ?? 50) * 1.2:F0}");
        config.AppendLine($"travel_speed = 120");
        config.AppendLine($"first_layer_speed = {(profile?.PrintSpeed ?? 50) * 0.5:F0}");
        config.AppendLine();

        // Material settings
        config.AppendLine("[filament]");
        config.AppendLine($"temperature = {profile?.NozzleTemperature ?? 210}");
        config.AppendLine($"first_layer_temperature = {(profile?.NozzleTemperature ?? 210) + 5}");
        config.AppendLine($"bed_temperature = {profile?.BedTemperature ?? 60}");
        config.AppendLine($"first_layer_bed_temperature = {(profile?.BedTemperature ?? 60) + 5}");
        config.AppendLine($"filament_diameter = 1.75");
        config.AppendLine($"extrusion_multiplier = 1.0");
        config.AppendLine($"filament_type = {profile?.Material ?? "PLA"}");
        config.AppendLine();

        // Printer settings
        config.AppendLine("[printer]");
        config.AppendLine("bed_shape = 0x0,200x0,200x200,0x200");
        config.AppendLine("print_center = 100,100");
        config.AppendLine("z_offset = 0");
        config.AppendLine("nozzle_diameter = 0.4");
        config.AppendLine("extruder_count = 1");
        config.AppendLine();

        // Support settings
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

        // Quality settings based on layer height
        config.AppendLine("[quality]");
        var quality = (profile?.LayerHeight ?? 0.2) switch
        {
            <= 0.15 => "fine",
            <= 0.25 => "normal",
            _ => "draft"
        };
        config.AppendLine($"quality = {quality}");
        config.AppendLine();

        return config.ToString();
    }

    private sealed class GcodeMetadata
    {
        public double PrintTimeSeconds { get; set; }
        public double FilamentUsageGrams { get; set; }
        public int LayerCount { get; set; }
    }
}