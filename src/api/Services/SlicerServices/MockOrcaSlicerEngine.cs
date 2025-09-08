using System.Diagnostics.CodeAnalysis;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Clean deterministic mock OrcaSlicer engine. Single authoritative implementation.
/// - 100 progress updates (1..100) after an initial 0% report
/// - Cancellation checked every &lt;=50ms
/// - No randomness (stable tests)
/// - Validation metadata (TriangleCount, HasManifoldErrors)
/// - Large file warning containing phrase "large file"
/// - Processing time estimation clamped to [31s, 29.5m]
/// - Generated G-code embedded in result metadata
/// </summary>
[SuppressMessage("Security", "CA5394", Justification = "Deterministic mock - no insecure randomness used")]
public sealed class MockOrcaSlicerEngine : ISlicerEngine
{
    private readonly MockSlicerOptions _options;
    private readonly ILogger<MockOrcaSlicerEngine> _logger;

    public SlicerEngineType EngineType => SlicerEngineType.OrcaSlicer;
    public string Version => "1.8.0-mock";
    public IReadOnlyList<string> SupportedFileExtensions { get; } = new[] { ".stl", ".obj", ".3mf", ".amf", ".ply" };

    public MockOrcaSlicerEngine(IOptions<MockSlicerOptions> options, ILogger<MockOrcaSlicerEngine> logger)
    {
        _options = options?.Value ?? new MockSlicerOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(25, cancellationToken);
        return true;
    }

    public async Task<SlicingResult> SliceAsync(DistributedSlicingJob job, IProgress<SlicingProgressUpdate>? progressCallback = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            // Initial validation / delay
            progressCallback?.Report(NewProgress(job.Id, 0, "Validating model"));
            await Task.Delay(TimeSpan.FromSeconds(_options.InitialDelaySeconds), cancellationToken);

            // Deterministic forced failure path for tests (FailureRate == 1.0 means always fail)
            if (_options.FailureRate >= 1.0)
            {
                _logger.LogWarning("Simulated slicing failure (forced) for job {JobId}", job.Id);
                return new SlicingResult
                {
                    Success = false,
                    Error = "Simulated slicing failure",
                    ProcessingTimeSeconds = 0,
                    LayerCount = 0,
                    EstimatedPrintTimeSeconds = 0,
                    EstimatedFilamentUsageGrams = 0,
                    OutputFileSizeBytes = 0,
                    Metadata = new Dictionary<string, string>
                    {
                        ["SlicerEngine"] = "OrcaSlicer",
                        ["SlicerVersion"] = Version,
                        ["MockedResult"] = "true"
                    }
                };
            }

            const int Steps = 100; // final reported progress = 100
            double totalMs = Math.Max(_options.ProcessingTimeSeconds * 1000.0, 2500); // min runtime 2.5s
            double perStepMs = Math.Max(20, totalMs / Steps); // pacing safeguard
            DateTime started = DateTime.UtcNow;

            for (int step = 1; step <= Steps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int remaining = (int)perStepMs;
                while (remaining > 0)
                {
                    int slice = Math.Min(50, remaining); // 50ms slice granularity
                    await Task.Delay(slice, cancellationToken);
                    remaining -= slice;
                }
                progressCallback?.Report(NewProgress(job.Id, step, DescribeStep(step)));
            }

            TimeSpan elapsed = DateTime.UtcNow - started;
            string gcode = GenerateGcode(job);
            int layers = CalculateLayerCount(job);

            return new SlicingResult
            {
                Success = true,
                ProcessingTimeSeconds = elapsed.TotalSeconds,
                OutputFileSizeBytes = gcode.Length,
                EstimatedPrintTimeSeconds = CalculateEstimatedPrintTime(job),
                EstimatedFilamentUsageGrams = CalculateEstimatedFilament(job),
                LayerCount = layers,
                Metadata = new Dictionary<string, string>
                {
                    ["SlicerEngine"] = "OrcaSlicer",
                    ["SlicerVersion"] = Version,
                    ["ProfileUsed"] = $"{job.Profile?.Quality ?? "Standard"} - {job.Profile?.Material ?? "PLA"}",
                    ["LayerHeight"] = (job.Profile?.LayerHeight ?? 0.2).ToString("F2"),
                    ["InfillPercentage"] = (job.Profile?.InfillPercentage ?? 20).ToString(),
                    ["PrintSpeed"] = (job.Profile?.PrintSpeed ?? 50).ToString(),
                    ["GeneratedGcode"] = gcode,
                    ["MockedResult"] = "true"
                }
            };
        }
        catch (TaskCanceledException)
        {
            // Normalize exact exception type for tests (they assert OperationCanceledException specifically)
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public async Task<SlicerValidationResult> ValidateModelAsync(Stream modelFile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelFile);
        await Task.Delay(50, cancellationToken);
        long size = modelFile.Length;
        var issues = new List<string>();
        var warnings = new List<string>();
        if (size == 0)
        {
            issues.Add("File is empty");
        }
        else if (size > 50_000_000)
        {
            warnings.Add("large file size may result in longer processing time");
        }
        return new SlicerValidationResult
        {
            IsValid = issues.Count == 0,
            Issues = issues,
            Warnings = warnings,
            FileSizeBytes = size,
            FileType = "STL",
            Metadata = new Dictionary<string, object>
            {
                ["ValidationEngine"] = "OrcaSlicer-Mock",
                ["ValidationTime"] = DateTime.UtcNow.ToString("O"),
                ["TriangleCount"] = 12,
                ["HasManifoldErrors"] = false
            }
        };
    }

    public async Task<TimeSpan> EstimateProcessingTimeAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await Task.Delay(5, cancellationToken);
        double baseSeconds = Math.Max(_options.ProcessingTimeSeconds, 60); // baseline >= 60s
        double factor = 1.0;
        double lh = job.Profile?.LayerHeight ?? 0.2;
        if (lh < 0.15)
        {
            factor *= 1.5;
        }
        else if (lh > 0.3)
        {
            factor *= 0.8;
        }
        factor *= 1.0 + ((job.Profile?.InfillPercentage ?? 20) / 100.0 * 0.3);
        if (job.Profile?.Supports == true)
        {
            factor *= 1.2;
        }
        if (job.InputFileSizeBytes is > 0)
        {
            factor *= Math.Min(2.0, 1.0 + job.InputFileSizeBytes.Value / (50.0 * 1024 * 1024));
        }
        TimeSpan estimate = TimeSpan.FromMilliseconds(TimeSpan.FromSeconds(baseSeconds).TotalMilliseconds * factor);
        if (estimate < TimeSpan.FromSeconds(31))
        {
            estimate = TimeSpan.FromSeconds(31);
        }
        if (estimate > TimeSpan.FromMinutes(29.5))
        {
            estimate = TimeSpan.FromMinutes(29.5);
        }
        return estimate;
    }

    private static SlicingProgressUpdate NewProgress(Guid id, int pct, string step) => new()
    {
        JobId = id,
        Progress = pct,
        Status = SlicingJobStatus.Slicing,
        CurrentStep = step,
        Timestamp = DateTime.UtcNow
    };

    private static string DescribeStep(int step)
    {
        double pct = step / 100.0;
        if (pct >= 0.2 && pct < 0.95)
        {
            return $"Slicing layer {step} of 100";
        }
        return pct switch
        {
            < 0.05 => "Validating model",
            < 0.10 => "Analyzing model geometry",
            < 0.15 => "Generating support structures",
            >= 0.95 => "Completing slicing process",
            _ => "Finalizing G-code"
        };
    }

    private static string GenerateGcode(DistributedSlicingJob job)
    {
        var sb = new System.Text.StringBuilder();
    // Backward compatibility: include legacy header line expected by older tests
    sb.AppendLine("; Generated by MockOrcaSlicerEngine");
    sb.AppendLine("; Generated by PrintFarmer Mock OrcaSlicer");
        sb.AppendLine($"; Print job: {job.Id}");
        sb.AppendLine($"; Layer height: {job.Profile?.LayerHeight ?? 0.2}mm");
        sb.AppendLine($"; Infill: {job.Profile?.InfillPercentage ?? 20}%");
        sb.AppendLine($"; Print speed: {job.Profile?.PrintSpeed ?? 50}mm/s");
        sb.AppendLine($"; Nozzle temp: {job.Profile?.NozzleTemperature ?? 210}°C");
        sb.AppendLine($"; Bed temp: {job.Profile?.BedTemperature ?? 60}°C");
        sb.AppendLine($"; Material: {job.Profile?.Material ?? "PLA"}");
        sb.AppendLine();
        sb.AppendLine("G21");
        sb.AppendLine("G90");
        sb.AppendLine("M83");
        sb.AppendLine($"M104 S{job.Profile?.NozzleTemperature ?? 210}");
        sb.AppendLine($"M140 S{job.Profile?.BedTemperature ?? 60}");
        sb.AppendLine("G28");
        sb.AppendLine("G1 Z5 F5000");
        int layers = CalculateLayerCount(job);
        for (int i = 0; i < Math.Min(layers, 10); i++)
        {
            double z = (job.Profile?.LayerHeight ?? 0.2) * i;
            sb.AppendLine($"; LAYER:{i}");
            sb.AppendLine($"G1 Z{z:F3}");
            sb.AppendLine("G1 X50 Y50");
            sb.AppendLine("G1 E1.0 F1800");
            sb.AppendLine("G1 X150 Y50 E5.0");
            sb.AppendLine("G1 X150 Y150 E5.0");
        }
        if (layers > 10)
        {
            sb.AppendLine($"; ... {layers - 10} more layers omitted ...");
        }
        sb.AppendLine("M104 S0");
        sb.AppendLine("M140 S0");
        sb.AppendLine("G28 X0");
        sb.AppendLine("M84");
        sb.AppendLine("; End of G-code");
        return sb.ToString();
    }

    private static double CalculateEstimatedPrintTime(DistributedSlicingJob job)
    {
        double baseTime = 1800; // 30m baseline
        double lh = job.Profile?.LayerHeight ?? 0.2;
        double layerFactor = 0.1 / lh;
        double infillFactor = 1.0 + ((job.Profile?.InfillPercentage ?? 20) / 100.0);
        double speedFactor = 60.0 / (job.Profile?.PrintSpeed ?? 50);
        return baseTime * layerFactor * infillFactor * speedFactor;
    }

    private static double CalculateEstimatedFilament(DistributedSlicingJob job)
    {
        double baseFilament = 20.0;
        double infillFactor = (job.Profile?.InfillPercentage ?? 20) / 100.0;
        double layerFactor = 0.2 / (job.Profile?.LayerHeight ?? 0.2);
        return baseFilament * (0.5 + infillFactor) * layerFactor;
    }

    private static int CalculateLayerCount(DistributedSlicingJob job)
    {
        const double ObjectHeight = 50.0;
        double lh = job.Profile?.LayerHeight ?? 0.2;
        return (int)Math.Ceiling(ObjectHeight / lh);
    }
}

public sealed class MockSlicerOptions
{
    public double InitialDelaySeconds { get; set; } = 0.5;
    public double ProcessingTimeSeconds { get; set; } = 10.0;
    // Kept for backward compatibility with existing tests; currently unused in deterministic engine
    public double FailureRate { get; set; } // default 0.0
}