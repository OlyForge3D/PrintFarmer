using System.Diagnostics;
using System.Text.RegularExpressions;
using Farm.Web.Shared;

namespace Farm.OrcaSlicer.Worker.Services;

public class OrcaSlicingPipelineService : ISlicingPipelineService
{
    private readonly HttpClient _httpClient;
    private readonly IProgressReporter _progressReporter;
    private readonly ILogger<OrcaSlicingPipelineService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _workingDirectory;
    private readonly string _storageEndpoint;
    private readonly string _orcaSlicerBinaryPath;

    public OrcaSlicingPipelineService(HttpClient httpClient, IProgressReporter progressReporter, ILogger<OrcaSlicingPipelineService> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _progressReporter = progressReporter;
        _logger = logger;
        _configuration = configuration;
        _workingDirectory = configuration["Worker:WorkingDirectory"] ?? "/app/temp";
        _storageEndpoint = configuration["Worker:StorageEndpoint"] ?? "http://api:5245";
        _orcaSlicerBinaryPath = configuration["Worker:OrcaSlicerPath"] ?? "/usr/local/bin/orcaslicer";

        if (!Directory.Exists(_workingDirectory))
            Directory.CreateDirectory(_workingDirectory);
    }

    public async Task<SlicingPipelineResult> ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default)
    {
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
            return new SlicingPipelineResult
            {
                GcodeFileUrl = gcodeUrl,
                EstimatedPrintTimeSeconds = metadata.PrintTimeSeconds,
                EstimatedFilamentUsageGrams = metadata.FilamentUsageGrams,
                FileSizeBytes = new FileInfo(gcodeFilePath).Length,
                LayerCount = metadata.LayerCount,
                Metadata = new Dictionary<string, object>
                {
                    ["SlicerVersion"] = "OrcaSlicer 1.8.x",
                    ["ProcessedAt"] = DateTime.UtcNow.ToString("O"),
                    ["WorkerId"] = job.WorkerId ?? "unknown"
                }
            };
        }
        finally
        {
            try
            { Directory.Delete(jobWorkDir, true); }
            catch { /* ignore */ }
        }
    }

    private async Task<string> FetchStlFileAsync(DistributedSlicingJob job, string workDir, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync(job.ModelFileUrl, ct);
        response.EnsureSuccessStatusCode();
        var path = Path.Combine(workDir, job.ModelFileName);
        await using var fs = File.Create(path);
        await response.Content.CopyToAsync(fs, ct);
        job.InputFileSizeBytes = new FileInfo(path).Length;
        return path;
    }

    private Task<string> PrepareSlicerConfigAsync(DistributedSlicingJob job, string workDir, CancellationToken ct)
    {
        var config = GenerateOrcaSlicerConfig(job.Profile);
        var file = Path.Combine(workDir, "config.ini");
        return File.WriteAllTextAsync(file, config, ct).ContinueWith(_ => file, ct);
    }

    private async Task<string> RunOrcaSlicerAsync(string stlPath, string configPath, string workDir, DistributedSlicingJob job, CancellationToken ct)
    {
        var gcodeFilePath = Path.Combine(workDir, Path.GetFileNameWithoutExtension(job.ModelFileName) + ".gcode");
        if (!File.Exists(_orcaSlicerBinaryPath))
            throw new InvalidOperationException($"OrcaSlicer binary missing at {_orcaSlicerBinaryPath}");
        var args = $"--config \"{configPath}\" --output \"{gcodeFilePath}\" \"{stlPath}\"";
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _orcaSlicerBinaryPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            }
        };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(ct);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0 || !File.Exists(gcodeFilePath))
            throw new InvalidOperationException($"OrcaSlicer failed (exit {process.ExitCode}): {error}");
        _logger.LogDebug("OrcaSlicer output: {Output}", output);
        return gcodeFilePath;
    }

    private async Task<GcodeMetadata> ExtractGcodeMetadataAsync(string gcodeFilePath, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(gcodeFilePath, ct);
        var md = new GcodeMetadata();
        var timeHm = new Regex(@";\\s*estimated printing time.*?(\\d+)h\\s*(\\d+)m", RegexOptions.IgnoreCase);
        var timeS = new Regex(@";\\s*estimated printing time.*?(\\d+)s", RegexOptions.IgnoreCase);
        var filament = new Regex(@";\\s*filament used.*?(\\d+\\.?\\d*)(?:mm|g)", RegexOptions.IgnoreCase);
        var layerCount = new Regex(@";\\s*layer_count\\s*=\\s*(\\d+)", RegexOptions.IgnoreCase);
        var layerComment = new Regex(@";\\s*LAYER:(\\d+)", RegexOptions.IgnoreCase);
        var maxLayer = 0;
        foreach (var line in lines)
        {
            var m = timeHm.Match(line);
            if (m.Success)
                md.PrintTimeSeconds = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60;
            m = timeS.Match(line);
            if (m.Success)
                md.PrintTimeSeconds = int.Parse(m.Groups[1].Value);
            m = filament.Match(line);
            if (m.Success)
            {
                var amt = double.Parse(m.Groups[1].Value);
                md.FilamentUsageGrams = line.Contains("mm") ? amt * 0.0025 : amt;
            }
            m = layerCount.Match(line);
            if (m.Success)
                md.LayerCount = int.Parse(m.Groups[1].Value);
            m = layerComment.Match(line);
            if (m.Success)
                maxLayer = Math.Max(maxLayer, int.Parse(m.Groups[1].Value));
        }
        if (md.LayerCount == 0 && maxLayer > 0)
            md.LayerCount = maxLayer + 1;
        if (md.PrintTimeSeconds == 0)
            md.PrintTimeSeconds = (md.LayerCount > 0 ? md.LayerCount * 120 : 1800);
        if (md.FilamentUsageGrams == 0)
            md.FilamentUsageGrams = Math.Max(5.0, new FileInfo(gcodeFilePath).Length / 50000.0);
        if (md.LayerCount == 0)
            md.LayerCount = lines.Count(l => l.StartsWith("G1 Z") || l.StartsWith("G0 Z"));
        if (md.LayerCount == 0)
            md.LayerCount = 100;
        return md;
    }

    private Task<string> UploadGcodeAsync(string gcodeFilePath, DistributedSlicingJob job, CancellationToken ct)
    {
        var fileName = Path.GetFileName(gcodeFilePath);
        var url = $"{_storageEndpoint}/api/files/gcode/{job.Id}/{fileName}"; // Phase 1 mock
        return Task.FromResult(url);
    }

    private string GenerateOrcaSlicerConfig(SlicerProfileDto? profile)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Generated by PrintFarmer OrcaSlicer Worker");
        sb.AppendLine($"# {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("[print]");
        sb.AppendLine($"layer_height = {profile?.LayerHeight ?? 0.2}");
        sb.AppendLine($"first_layer_height = {(profile?.LayerHeight ?? 0.2) * 1.5}");
        sb.AppendLine("perimeters = 2");
        sb.AppendLine("top_solid_layers = 3");
        sb.AppendLine("bottom_solid_layers = 3");
        sb.AppendLine($"fill_density = {(profile?.InfillPercentage ?? 20) / 100.0:F2}");
        sb.AppendLine("fill_pattern = cubic");
        sb.AppendLine($"external_perimeter_speed = {(profile?.PrintSpeed ?? 50) * 0.8:F0}");
        sb.AppendLine($"perimeter_speed = {profile?.PrintSpeed ?? 50}");
        sb.AppendLine($"infill_speed = {(profile?.PrintSpeed ?? 50) * 1.2:F0}");
        sb.AppendLine("travel_speed = 120");
        sb.AppendLine($"first_layer_speed = {(profile?.PrintSpeed ?? 50) * 0.5:F0}");
        sb.AppendLine();
        sb.AppendLine("[filament]");
        sb.AppendLine($"temperature = {profile?.NozzleTemperature ?? 210}");
        sb.AppendLine($"first_layer_temperature = {(profile?.NozzleTemperature ?? 210) + 5}");
        sb.AppendLine($"bed_temperature = {profile?.BedTemperature ?? 60}");
        sb.AppendLine($"first_layer_bed_temperature = {(profile?.BedTemperature ?? 60) + 5}");
        sb.AppendLine("filament_diameter = 1.75");
        sb.AppendLine("extrusion_multiplier = 1.0");
        sb.AppendLine($"filament_type = {profile?.Material ?? "PLA"}");
        sb.AppendLine();
        sb.AppendLine("[printer]");
        sb.AppendLine("bed_shape = 0x0,200x0,200x200,0x200");
        sb.AppendLine("print_center = 100,100");
        sb.AppendLine("z_offset = 0");
        sb.AppendLine("nozzle_diameter = 0.4");
        sb.AppendLine("extruder_count = 1");
        sb.AppendLine();
        if (profile?.Supports == true)
        {
            sb.AppendLine("[support]");
            sb.AppendLine("support_material = 1");
            sb.AppendLine("support_material_auto = 1");
            sb.AppendLine("support_material_threshold = 45");
            sb.AppendLine("support_material_pattern = rectilinear");
            sb.AppendLine("support_material_spacing = 2.5");
            sb.AppendLine("support_material_interface_layers = 2");
            sb.AppendLine();
        }
        sb.AppendLine("[quality]");
        var quality = (profile?.LayerHeight ?? 0.2) switch
        {
            <= 0.15 => "fine",
            <= 0.25 => "normal",
            _ => "draft"
        };
        sb.AppendLine($"quality = {quality}");
        sb.AppendLine();
        return sb.ToString();
    }

    private sealed class GcodeMetadata
    {
        public double PrintTimeSeconds { get; set; }
        public double FilamentUsageGrams { get; set; }
        public int LayerCount { get; set; }
    }
}
