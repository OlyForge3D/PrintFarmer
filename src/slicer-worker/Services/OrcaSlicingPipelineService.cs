using System.Diagnostics;
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

        // For Phase 1, simulate OrcaSlicer by creating a mock G-code file
        // In production, this would execute the actual OrcaSlicer binary
        var gcodeFilePath = Path.Combine(workDir, Path.GetFileNameWithoutExtension(job.ModelFileName) + ".gcode");
        
        // Simulate slicing time based on file size
        var simulatedDuration = TimeSpan.FromSeconds(Math.Max(5, (job.InputFileSizeBytes ?? 1000000) / 100000));
        _logger.LogInformation("Simulating OrcaSlicer processing for {Duration}", simulatedDuration);

        // Report progress during "slicing"
        var progressSteps = 10;
        for (int i = 0; i < progressSteps; i++)
        {
            await Task.Delay(simulatedDuration.Divide(progressSteps), cancellationToken);
            var progress = 30 + (i * 50 / progressSteps); // Progress from 30% to 80%
            await _progressReporter.ReportProgressAsync(job.Id, progress, $"Slicing layer {i * 100 / progressSteps}%", cancellationToken);
        }

        // Generate mock G-code
        var gcodeContent = GenerateMockGcode(job);
        await File.WriteAllTextAsync(gcodeFilePath, gcodeContent, cancellationToken);

        _logger.LogInformation("OrcaSlicer completed in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
        return gcodeFilePath;
    }

    private async Task<GcodeMetadata> ExtractGcodeMetadataAsync(string gcodeFilePath, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Extracting G-code metadata from {GcodePath}", gcodeFilePath);

        // Simple metadata extraction for Phase 1
        var fileInfo = new FileInfo(gcodeFilePath);
        var lines = await File.ReadAllLinesAsync(gcodeFilePath, cancellationToken);

        // Mock metadata extraction
        var metadata = new GcodeMetadata
        {
            PrintTimeSeconds = 3600, // 1 hour
            FilamentUsageGrams = 25.5,
            LayerCount = 250
        };

        _logger.LogInformation("Extracted G-code metadata in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
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
        // Mock OrcaSlicer configuration for Phase 1
        return $"""
            # OrcaSlicer configuration generated at {DateTime.UtcNow:O}
            layer_height = {profile?.LayerHeight ?? 0.2}
            infill_percentage = {profile?.InfillPercentage ?? 20}
            print_speed = {profile?.PrintSpeed ?? 50}
            nozzle_temperature = {profile?.NozzleTemperature ?? 210}
            bed_temperature = {profile?.BedTemperature ?? 60}
            """;
    }

    private string GenerateMockGcode(DistributedSlicingJob job)
    {
        return $"""
            ; Generated by OrcaSlicer 1.8.0 on {DateTime.UtcNow:O}
            ; Job ID: {job.Id}
            ; Model: {job.ModelFileName}
            ; Estimated print time: 1h 0m
            ; Filament used: 25.5g
            ; Layer count: 250
            
            G28 ; Home all axes
            G1 Z15.0 F6000 ; Move the platform down 15mm
            G92 E0 ; Reset extruder
            G1 F200 E3 ; Extrude 3mm of filament
            G92 E0 ; Reset extruder
            G1 F9000
            M104 S210 ; Set extruder temperature
            M140 S60 ; Set bed temperature
            
            ; Layer 1
            G1 X10 Y10 F3000
            G1 Z0.2 F1000
            G1 X50 Y10 E1 F1500
            G1 X50 Y50 E2
            G1 X10 Y50 E3
            G1 X10 Y10 E4
            
            ; End of print
            M104 S0 ; Turn off extruder
            M140 S0 ; Turn off bed
            G28 X0 ; Home X axis
            M84 ; Disable motors
            """;
    }

    private class GcodeMetadata
    {
        public double PrintTimeSeconds { get; set; }
        public double FilamentUsageGrams { get; set; }
        public int LayerCount { get; set; }
    }
}