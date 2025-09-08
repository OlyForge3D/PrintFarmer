using System.Diagnostics;
using System.Text.RegularExpressions;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Mock implementation of PrusaSlicer engine for development and testing
/// In production, this would interface with actual PrusaSlicer binary
/// </summary>
public class MockPrusaSlicerEngine : ISlicerEngine
{
    private readonly MockSlicerOptions _options;
    private readonly ILogger<MockPrusaSlicerEngine> _logger;
    private readonly Random _random = new();

    public SlicerEngineType EngineType => SlicerEngineType.PrusaSlicer;
    public string Version => "2.8.0-mock";
    
    public IReadOnlyList<string> SupportedFileExtensions => new[]
    {
        ".stl", ".obj", ".3mf", ".amf", ".ply"
    };

    public MockPrusaSlicerEngine(IOptions<MockSlicerOptions> options, ILogger<MockPrusaSlicerEngine> logger)
    {
        _options = options?.Value ?? new MockSlicerOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        // Simulate health check
        await Task.Delay(_random.Next(10, 100), cancellationToken);
        
        // Randomly fail health check 5% of the time to simulate real-world conditions
        var isHealthy = _random.NextDouble() > 0.05;
        
        _logger.LogDebug("PrusaSlicer health check: {IsHealthy}", isHealthy);
        return isHealthy;
    }

    public async Task<SlicingResult> SliceAsync(DistributedSlicingJob job, IProgress<SlicingProgressUpdate>? progressCallback = null, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting mock slicing for job {JobId} using PrusaSlicer", job.Id);

            // Simulate initial validation
            progressCallback?.Report(new SlicingProgressUpdate
            {
                JobId = job.Id,
                Progress = 0,
                Status = SlicingJobStatus.Slicing,
                CurrentStep = "Loading model",
                Timestamp = DateTime.UtcNow
            });

            await Task.Delay(TimeSpan.FromSeconds(_options.InitialDelaySeconds), cancellationToken);

            // Simulate slicing process with progress updates
            var totalSteps = 100;
            var processingStartTime = DateTime.UtcNow;
            
            for (int step = 1; step <= totalSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stepDelay = _options.ProcessingTimeSeconds * 1000 / totalSteps;
                await Task.Delay(TimeSpan.FromMilliseconds(stepDelay + _random.Next(-50, 50)), cancellationToken);

                var currentStep = GetStepDescription(step, totalSteps);
                
                progressCallback?.Report(new SlicingProgressUpdate
                {
                    JobId = job.Id,
                    Progress = step,
                    Status = SlicingJobStatus.Slicing,
                    CurrentStep = currentStep,
                    Timestamp = DateTime.UtcNow
                });

                _logger.LogDebug("Job {JobId} progress: {Progress}% - {CurrentStep}", job.Id, step, currentStep);

                // Simulate random failures
                if (_options.FailureRate > 0 && _random.NextDouble() < _options.FailureRate)
                {
                    throw new InvalidOperationException($"Simulated slicing failure at step {step}: Random error occurred");
                }
            }

            var processingTime = DateTime.UtcNow - processingStartTime;
            
            // Generate mock G-code content
            var gcodeContent = GenerateMockGcode(job);
            var outputSizeBytes = gcodeContent.Length;

            // Simulate estimated print characteristics
            var estimatedPrintTime = CalculateEstimatedPrintTime(job);
            var estimatedFilament = CalculateEstimatedFilament(job);
            var layerCount = CalculateLayerCount(job);

            _logger.LogInformation("Completed mock slicing for job {JobId} in {ProcessingTime}s. Output: {OutputSize} bytes, {LayerCount} layers", 
                job.Id, processingTime.TotalSeconds, outputSizeBytes, layerCount);

            return new SlicingResult
            {
                Success = true,
                ProcessingTimeSeconds = processingTime.TotalSeconds,
                OutputFileSizeBytes = outputSizeBytes,
                EstimatedPrintTimeSeconds = estimatedPrintTime,
                EstimatedFilamentUsageGrams = estimatedFilament,
                LayerCount = layerCount,
                Metadata = new Dictionary<string, string>
                {
                    ["SlicerEngine"] = "PrusaSlicer",
                    ["SlicerVersion"] = Version,
                    ["ProfileUsed"] = $"{job.Profile?.Quality ?? "Standard"} - {job.Profile?.Material ?? "PLA"}",
                    ["LayerHeight"] = (job.Profile?.LayerHeight ?? 0.2).ToString("F2"),
                    ["InfillPercentage"] = (job.Profile?.InfillPercentage ?? 20).ToString(),
                    ["PrintSpeed"] = (job.Profile?.PrintSpeed ?? 50).ToString(),
                    ["MockedResult"] = "true"
                }
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Slicing job {JobId} was cancelled", job.Id);
            return new SlicingResult
            {
                Success = false,
                Error = "Job was cancelled",
                ProcessingTimeSeconds = 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mock slicing failed for job {JobId}", job.Id);
            return new SlicingResult
            {
                Success = false,
                Error = ex.Message,
                ProcessingTimeSeconds = 0
            };
        }
    }

    public async Task<SlicerValidationResult> ValidateModelAsync(Stream modelFile, CancellationToken cancellationToken = default)
    {
        try
        {
            // Simulate validation delay
            await Task.Delay(_random.Next(100, 500), cancellationToken);

            var fileSizeBytes = modelFile.Length;
            var issues = new List<string>();
            var warnings = new List<string>();

            // Simulate validation logic
            if (fileSizeBytes == 0)
            {
                issues.Add("File is empty");
            }
            else if (fileSizeBytes > 50_000_000) // 50MB
            {
                warnings.Add("Large file size may result in longer processing time");
            }

            // Randomly add some issues for testing
            if (_random.NextDouble() < 0.1) // 10% chance
            {
                warnings.Add("Model may have overlapping surfaces");
            }

            if (_random.NextDouble() < 0.05) // 5% chance
            {
                issues.Add("Model contains inverted normals");
            }

            var isValid = issues.Count == 0;

            _logger.LogDebug("Model validation result: Valid={IsValid}, Issues={IssueCount}, Warnings={WarningCount}", 
                isValid, issues.Count, warnings.Count);

            return new SlicerValidationResult
            {
                IsValid = isValid,
                Issues = issues,
                Warnings = warnings,
                FileSizeBytes = fileSizeBytes,
                FileType = "STL", // Simplified - would detect actual type
                Metadata = new Dictionary<string, object>
                {
                    ["ValidationEngine"] = "PrusaSlicer-Mock",
                    ["ValidationTime"] = DateTime.UtcNow.ToString("O")
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model validation failed");
            return new SlicerValidationResult
            {
                IsValid = false,
                Issues = new List<string> { $"Validation error: {ex.Message}" },
                Warnings = new List<string>(),
                FileSizeBytes = modelFile.Length
            };
        }
    }

    public async Task<TimeSpan> EstimateProcessingTimeAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default)
    {
        await Task.Delay(10, cancellationToken); // Simulate quick calculation
        
        // Base processing time from options
        var baseTime = TimeSpan.FromSeconds(_options.ProcessingTimeSeconds);
        
        // Adjust based on complexity factors
        var complexityFactor = 1.0;
        
        // Layer height affects processing time
        if ((job.Profile?.LayerHeight ?? 0.2) < 0.15)
            complexityFactor *= 1.5; // Finer layers take longer
        else if ((job.Profile?.LayerHeight ?? 0.2) > 0.3)
            complexityFactor *= 0.8; // Thicker layers are faster
            
        // Infill percentage affects time
        complexityFactor *= 1.0 + ((job.Profile?.InfillPercentage ?? 20) / 100.0 * 0.3);
        
        // Supports add time
        if (job.Profile?.Supports == true)
            complexityFactor *= 1.2;

        var estimatedTime = TimeSpan.FromMilliseconds(baseTime.TotalMilliseconds * complexityFactor);
        
        _logger.LogDebug("Estimated processing time for job {JobId}: {EstimatedTime}", job.Id, estimatedTime);
        
        return estimatedTime;
    }

    private static string GetStepDescription(int step, int totalSteps)
    {
        var percentage = (double)step / totalSteps;
        
        return percentage switch
        {
            < 0.1 => "Preparing model",
            < 0.2 => "Slicing geometry",
            < 0.4 => "Generating perimeters",
            < 0.6 => "Creating infill patterns",
            < 0.8 => "Computing supports",
            < 0.95 => "Generating G-code",
            _ => "Finalizing output"
        };
    }

    private static string GenerateMockGcode(DistributedSlicingJob job)
    {
        var gcode = new System.Text.StringBuilder();
        
        // G-code header with PrusaSlicer-specific comments
        gcode.AppendLine("; Generated by PrusaSlicer 2.8.0-mock on PrintFarmer");
        gcode.AppendLine($"; Print job: {job.Id}");
        gcode.AppendLine($"; Layer height: {job.Profile?.LayerHeight ?? 0.2}mm");
        gcode.AppendLine($"; Infill: {job.Profile?.InfillPercentage ?? 20}%");
        gcode.AppendLine($"; Print speed: {job.Profile?.PrintSpeed ?? 50}mm/s");
        gcode.AppendLine($"; Nozzle temperature: {job.Profile?.NozzleTemperature ?? 210}°C");
        gcode.AppendLine($"; Bed temperature: {job.Profile?.BedTemperature ?? 60}°C");
        gcode.AppendLine($"; Filament type: {job.Profile?.Material ?? "PLA"}");
        gcode.AppendLine(";");
        
        // Mock G-code content with PrusaSlicer format
        gcode.AppendLine("G21 ; set units to millimeters");
        gcode.AppendLine("G90 ; use absolute coordinates");
        gcode.AppendLine("M83 ; use relative distances for extrusion");
        gcode.AppendLine($"M104 S{job.Profile?.NozzleTemperature ?? 210} ; set nozzle temp");
        gcode.AppendLine($"M140 S{job.Profile?.BedTemperature ?? 60} ; set bed temp");
        gcode.AppendLine("G28 ; home all axes");
        gcode.AppendLine("G1 Z5 F5000 ; lift Z");
        
        // Simulate some layers with PrusaSlicer-style comments
        var layerCount = CalculateLayerCount(job);
        for (int layer = 0; layer < Math.Min(layerCount, 10); layer++) // Limit to first 10 layers for mock
        {
            var z = (job.Profile?.LayerHeight ?? 0.2) * layer;
            gcode.AppendLine($"; LAYER_CHANGE");
            gcode.AppendLine($"; Z:{z:F3}");
            gcode.AppendLine($"G1 Z{z:F3} F{(job.Profile?.PrintSpeed ?? 50) * 60:F0}");
            gcode.AppendLine($"G1 X50 Y50 F{(job.Profile?.PrintSpeed ?? 50) * 60:F0}");
            gcode.AppendLine("G1 E1.0 F1800");
            gcode.AppendLine($"G1 X150 Y50 E5.0 F{(job.Profile?.PrintSpeed ?? 50) * 60:F0}");
            gcode.AppendLine($"G1 X150 Y150 E5.0 F{(job.Profile?.PrintSpeed ?? 50) * 60:F0}");
        }
        
        if (layerCount > 10)
        {
            gcode.AppendLine($"; ... {layerCount - 10} more layers omitted in mock ...");
        }
        
        // G-code footer
        gcode.AppendLine("M104 S0 ; turn off nozzle");
        gcode.AppendLine("M140 S0 ; turn off bed");
        gcode.AppendLine("G28 X0 ; home X axis");
        gcode.AppendLine("M84 ; disable motors");
        gcode.AppendLine("; End of G-code");
        
        return gcode.ToString();
    }

    private static double CalculateEstimatedPrintTime(DistributedSlicingJob job)
    {
        // Mock calculation based on layer height and infill
        var baseTime = 1800; // 30 minutes base
        var layerFactor = 0.1 / job.Profile?.LayerHeight ?? 0.2; // Finer layers take longer
        var infillFactor = 1.0 + ((job.Profile?.InfillPercentage ?? 20) / 100.0);
        var speedFactor = 60.0 / (job.Profile?.PrintSpeed ?? 50); // Slower speeds take longer
        
        return baseTime * layerFactor * infillFactor * speedFactor;
    }

    private static double CalculateEstimatedFilament(DistributedSlicingJob job)
    {
        // Mock calculation - roughly 10-50g depending on settings
        var baseFilament = 20.0; // 20g base
        var infillFactor = (job.Profile?.InfillPercentage ?? 20) / 100.0;
        var layerFactor = 0.2 / (job.Profile?.LayerHeight ?? 0.2);
        
        return baseFilament * (0.5 + infillFactor) * layerFactor;
    }

    private static int CalculateLayerCount(DistributedSlicingJob job)
    {
        // Mock calculation - assume 50mm height object
        var objectHeight = 50.0;
        return (int)Math.Ceiling(objectHeight / (job.Profile?.LayerHeight ?? 0.2));
    }
}