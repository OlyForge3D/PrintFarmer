using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Logging;
using Farm.OrcaSlicer.Worker.Services.Calibration;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Farm.Slicer.ProfileParsing;
using Farm.Slicer.Worker.Core;
using Microsoft.Extensions.Logging; // shared interfaces

namespace Farm.OrcaSlicer.Worker.Services;

public partial class OrcaSlicingPipelineService : ISlicingPipelineService
{
    private const int DownloadBufferSize = 81920;
    private const int LinuxInterruptedSystemCall = 4;
    private const int LinuxOpenNonBlocking = 0x800;
    private const int LinuxTryAgain = 11;
    private const long DefaultMaxModelDownloadBytes = 512L * 1024L * 1024L;
    private const int DefaultModelDownloadTimeoutSeconds = 120;
    private const int MaxModelDownloadTimeoutSeconds = 3600;
    private readonly HttpClient _httpClient;
    private readonly IProgressReporter _progressReporter;
    private readonly ILogger<OrcaSlicingPipelineService> _logger;
    private readonly IWorkerStateService _workerState;
    private readonly string _workingDirectory;
    private readonly string _orcaSlicerBinaryPath;
    private readonly string _engineVersion;
    private readonly Uri _apiBaseUri;
    private readonly long _maxModelDownloadBytes;
    private readonly TimeSpan _modelDownloadTimeout;
    private readonly CalibrationResourceResolver _calibrationResourceResolver;

    internal Uri ApiBaseUri => _apiBaseUri;

    internal long ModelDownloadMaxBytes => _maxModelDownloadBytes;

    internal TimeSpan ModelDownloadTimeout => _modelDownloadTimeout;

    internal TimeSpan ModelDownloadHttpClientTimeout => _httpClient.Timeout;

    internal string OrcaSlicerBinaryPath => _orcaSlicerBinaryPath;

    public OrcaSlicingPipelineService(HttpClient httpClient, IProgressReporter progressReporter, ILogger<OrcaSlicingPipelineService> logger, IConfiguration configuration, IWorkerStateService workerState)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _progressReporter = progressReporter ?? throw new ArgumentNullException(nameof(progressReporter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerState = workerState ?? throw new ArgumentNullException(nameof(workerState));
        ArgumentNullException.ThrowIfNull(configuration);
#pragma warning disable S5443 // Worker default is a container-local scratch directory; deployments can override Worker:WorkingDirectory.
        _workingDirectory = configuration["Worker:WorkingDirectory"] ?? "/tmp/orca-work";
#pragma warning restore S5443
        _orcaSlicerBinaryPath = OrcaBinaryDetector.ResolveExecutablePath(
            configuration["Worker:OrcaSlicerPath"] ?? OrcaBinaryDetector.DefaultBinaryPath);
        _engineVersion = (configuration["Worker:EngineVersion"]
            ?? configuration["SlicerRegistry:Version"]
            ?? WorkerConstants.SlicerVersion).Trim();
        string? apiBaseUrl = configuration["SlicerApi:BaseUrl"]
            ?? configuration["Worker:ApiBaseUrl"]
            ?? Environment.GetEnvironmentVariable("WORKER_API_BASE_URL");
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            throw new InvalidOperationException(
                "A trusted slicer API base address is required. Configure SlicerApi:BaseUrl.");
        }

        if (!Uri.TryCreate(apiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? apiBaseUri)
            || (apiBaseUri.Scheme != Uri.UriSchemeHttp && apiBaseUri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(apiBaseUri.Host)
            || apiBaseUri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(apiBaseUri.UserInfo)
            || !string.IsNullOrEmpty(apiBaseUri.Query)
            || !string.IsNullOrEmpty(apiBaseUri.Fragment))
        {
            throw new InvalidOperationException(
                $"The slicer API base URL '{apiBaseUrl}' must be an absolute HTTP(S) origin without credentials, query, or fragment.");
        }

        _apiBaseUri = apiBaseUri;
        _maxModelDownloadBytes = ReadPositiveSetting(
            configuration,
            "Worker:ModelDownloadMaxBytes",
            DefaultMaxModelDownloadBytes);
        long timeoutSeconds = ReadPositiveSetting(
            configuration,
            "Worker:ModelDownloadTimeoutSeconds",
            DefaultModelDownloadTimeoutSeconds);
        if (timeoutSeconds > MaxModelDownloadTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"Worker:ModelDownloadTimeoutSeconds must be between 1 and {MaxModelDownloadTimeoutSeconds}.");
        }

        _modelDownloadTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        if (!Directory.Exists(_workingDirectory))
        {
            _ = Directory.CreateDirectory(_workingDirectory);
        }

        _calibrationResourceResolver = new CalibrationResourceResolver(configuration["Worker:CalibrationResourcesPath"]);
    }

    private static long ReadPositiveSetting(
        IConfiguration configuration,
        string key,
        long defaultValue)
    {
        string? configuredValue = configuration[key];
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return defaultValue;
        }

        if (!long.TryParse(
                configuredValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long value)
            || value <= 0)
        {
            throw new InvalidOperationException($"{key} must be a positive integer.");
        }

        return value;
    }

    public async Task<SlicingResult> ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        string jobWorkDir = PrepareJobWorkDirectory(
            _workingDirectory,
            job.Id,
            job.ClaimToken);
        _workerState.SetJobWorkDirectory(job.Id, jobWorkDir);
        bool preserveResultForUpload = false;
        try
        {
            _logger.LogInformation("Starting slicing pipeline for job {JobId}", job.Id);

            // Download model file(s)
            List<string> modelFilePaths;
            if (!string.IsNullOrWhiteSpace(job.CalibrationMethod))
            {
                // Calibration jobs (issue #1938) resolve their model from the worker's own bundled
                // OrcaSlicer resources instead of downloading a client-uploaded model.
                await _progressReporter.ReportProgressAsync(job.Id, job.ClaimToken, 10, "Preparing calibration model", cancellationToken);
                string preparedModelPath = PrepareCalibrationModel(job, jobWorkDir);
                modelFilePaths = [preparedModelPath];
                job.InputFileSizeBytes = new FileInfo(preparedModelPath).Length;
            }
            else if (job.ModelFileUrls is { Count: > 0 })
            {
                await _progressReporter.ReportProgressAsync(job.Id, job.ClaimToken, 5, $"Downloading {job.ModelFileUrls.Count} model files", cancellationToken);
                modelFilePaths = await FetchMultipleModelsAsync(
                    job.Id,
                    job.ModelFileUrls,
                    job.ClaimToken,
                    job.LeaseToken,
                    job.LeaseFence,
                    jobWorkDir,
                    cancellationToken);
                job.InputFileSizeBytes = modelFilePaths.Sum(p => new FileInfo(p).Length);
                _logger.LogInformation("Downloaded {Count} model files for job {JobId}", modelFilePaths.Count, job.Id);
            }
            else
            {
                await _progressReporter.ReportProgressAsync(job.Id, job.ClaimToken, 10, "Downloading STL file", cancellationToken);
                string singlePath = await FetchStlFileAsync(job, jobWorkDir, cancellationToken);
                modelFilePaths = [singlePath];
            }

            await _progressReporter.ReportProgressAsync(job.Id, job.ClaimToken, 20, "Preparing slicer configuration", cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, job.ClaimToken, 30, "Running OrcaSlicer", cancellationToken);
            (string gcodeFilePath, LayoutDegradationReason? layoutDegradation) =
                await RunOrcaSlicerAsync(modelFilePaths, jobWorkDir, job, cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, job.ClaimToken, 80, "Analyzing G-code", cancellationToken);
            GcodeMetadata metadata = await ExtractGcodeMetadataAsync(gcodeFilePath, cancellationToken);

            // Rename gcode to descriptive filename: {model}_{printer}_{material}_{time}.gcode
            gcodeFilePath = RenameGcodeFile(gcodeFilePath, job, metadata);

            await _progressReporter.ReportProgressAsync(job.Id, job.ClaimToken, 90, "Preparing G-code artifact", cancellationToken);
            await _progressReporter.ReportProgressAsync(job.Id, job.ClaimToken, 100, "Slicing completed", cancellationToken);
            SlicingResult result = new SlicingResult
            {
                // A bare UriBuilder defaults its host to "localhost", which yields a UNC-style
                // local path on Windows and breaks artifact upload and cleanup. Build the file URI
                // from the absolute path so the local path round-trips on every platform.
                ResultFileUrl = new Uri(Path.GetFullPath(gcodeFilePath)),
                EstimatedPrintTimeSeconds = metadata.PrintTimeSeconds,
                EstimatedFilamentUsageGrams = metadata.FilamentUsageGrams,
                OutputFileSizeBytes = new FileInfo(gcodeFilePath).Length,
                LayerCount = metadata.LayerCount,
                Success = true,
                LayoutDegradation = layoutDegradation,
            };
            PopulateResultMetadata(result, job, modelFilePaths.Count);

            preserveResultForUpload = true;
            return result;
        }
        finally
        {
            // Preserve the workdir on success so the poller can upload the produced artifact
            // through the authenticated slicer API; otherwise clean up the temp files.
            try
            {
                if (Directory.Exists(jobWorkDir) && !preserveResultForUpload)
                {
                    Directory.Delete(jobWorkDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed cleanup {JobWorkDir}", jobWorkDir);
            }
        }
    }

    internal static string PrepareJobWorkDirectory(
        string workingDirectory,
        Guid jobId,
        Guid claimToken)
    {
        if (claimToken == Guid.Empty)
        {
            throw new ArgumentException("A claim token is required.", nameof(claimToken));
        }

        string attemptWorkDirectory = Path.Join(
            workingDirectory,
            jobId.ToString(),
            claimToken.ToString());
        if (Directory.Exists(attemptWorkDirectory))
        {
            Directory.Delete(attemptWorkDirectory, recursive: true);
        }

        _ = Directory.CreateDirectory(attemptWorkDirectory);
        return attemptWorkDirectory;
    }

    internal void PopulateResultMetadata(SlicingResult result, DistributedSlicingJob job, int modelCount)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(job);

        result.Metadata["SlicerVersion"] = $"OrcaSlicer {_engineVersion}";
        result.Metadata["ProcessedAt"] = DateTime.UtcNow.ToString("O");
        result.Metadata["WorkerId"] = job.WorkerId ?? "unknown";
        if (modelCount > 1)
        {
            result.Metadata["ModelCount"] = modelCount.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Resolves and prepares the local calibration model for <paramref name="job"/> (issue #1938),
    /// applying per-object flow-ratio overrides for the flow-rate methods. The temperature tower
    /// and max volumetric speed and pressure advance tower (issue #2136) methods need no
    /// per-model changes here; their per-band/permissive configuration is injected into the
    /// process/filament profile in <see cref="RunOrcaSlicerAsync"/> (see
    /// <see cref="ApplyTemperatureTowerGcodeAsync"/> and
    /// <see cref="ApplyPressureAdvanceTowerGcodeAsync"/>). Max volumetric speed's bundled
    /// resource (<c>SpeedTestStructure.drc</c>) is an opaque OrcaSlicer binary format (confirmed
    /// by magic bytes, not a ZIP/3MF archive), so — like the temperature tower's <c>.drc</c>
    /// resource — it cannot be parsed and rewritten the way
    /// <see cref="FlowRateCalibrationConfigurator"/> rewrites 3MF metadata; it falls through to
    /// the generic copy below.
    /// </summary>
    internal string PrepareCalibrationModel(DistributedSlicingJob job, string workDir)
    {
        if (!CalibrationMethods.TryParse(job.CalibrationMethod, out CalibrationMethod method))
        {
            throw new InvalidOperationException(
                $"Worker cannot resolve unsupported calibration method '{job.CalibrationMethod}'.");
        }

        string sourcePath = _calibrationResourceResolver.ResolveModelPath(method);
        if (!File.Exists(sourcePath))
        {
            // Log the resolved path server-side only; the exception message (which can surface to
            // callers via job failure reason) intentionally omits the internal filesystem layout.
            _logger.LogError(
                "Calibration resource file not found at '{SourcePath}' for method '{Method}'.",
                sourcePath,
                job.CalibrationMethod);
            throw new InvalidOperationException(
                $"Calibration resource for method '{job.CalibrationMethod}' is not available on this worker. " +
                "Confirm the OrcaSlicer installation ships resources/calib and Worker:CalibrationResourcesPath/ORCA_CALIB_PATH is correct.");
        }

        if (method is CalibrationMethod.FlowRatePass1 or CalibrationMethod.FlowRatePass2)
        {
            return FlowRateCalibrationConfigurator.ApplyPerObjectFlowRatios(sourcePath, workDir, _logger);
        }

        if (method is CalibrationMethod.FlowRateYoloRecommended or CalibrationMethod.FlowRateYoloPerfectionist)
        {
            // The YOLO flow-ratio resources encode per-object flow ratios as baseline-relative
            // deltas (e.g. "flowrate_0.01", "flowrate_m0.01"), not the absolute percentages (e.g.
            // "flowrate_95") FlowRateCalibrationConfigurator parses for pass1/pass2. Reusing that
            // parser here would silently mis-scale or skip every object, producing a job that
            // "succeeds" while emitting near-identical, uncalibrated G-code for every block —
            // see CalibrationMethod.cs for the full investigation (issue #2051). Fail loudly
            // instead of slicing an uncalibrated result until a delta-aware configurator exists.
            throw new InvalidOperationException(
                $"Calibration method '{job.CalibrationMethod}' is catalogued but not yet slicer-supported: " +
                "its bundled resource uses a delta-based per-object naming scheme the worker cannot apply " +
                "overrides for. A dedicated configurator is required before this method can be used.");
        }

        string destinationPath = Path.Combine(workDir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return destinationPath;
    }

    /// <summary>
    /// Injects the temperature tower's per-band <c>layer_change_gcode</c> hook (issue #1938) into
    /// the process profile on disk and recomputes <see cref="DistributedSlicingJob.ProcessProfileSha256"/>
    /// so the recorded digest matches the mutated content.
    /// </summary>
    internal static async Task ApplyTemperatureTowerGcodeAsync(
        DistributedSlicingJob job,
        string processJsonPath,
        CancellationToken cancellationToken)
    {
        CalibrationParameters parameters = CalibrationParameters.Parse(job.CalibrationParamsJson, CalibrationMethod.TemperatureTower);
        string layerChangeGcode = TemperatureTowerGcodeBuilder.BuildLayerChangeGcode(
            parameters.StartTemperatureC,
            parameters.TemperatureStepC,
            parameters.BandHeightMm,
            parameters.BandCount);

        string processJsonContent = await File.ReadAllTextAsync(processJsonPath, cancellationToken);
        string updatedProcessJsonContent = InjectLayerChangeGcode(processJsonContent, layerChangeGcode);
        await File.WriteAllTextAsync(processJsonPath, updatedProcessJsonContent, cancellationToken);
        job.ProcessProfileSha256 = NativeSlicerProfiles.ComputeSha256(updatedProcessJsonContent);
    }

    /// <summary>
    /// Injects the pressure advance tower's per-band <c>layer_change_gcode</c> hook (issue #2136)
    /// into the process profile on disk and recomputes <see cref="DistributedSlicingJob.ProcessProfileSha256"/>
    /// so the recorded digest matches the mutated content.
    /// </summary>
    /// <remarks>
    /// Firmware-flavour decision: pressure advance's command syntax differs by firmware
    /// (<c>SET_PRESSURE_ADVANCE ADVANCE=...</c> on Klipper, <c>M900 K...</c> on Marlin/Marlin2), so
    /// this reads the machine profile's <c>gcode_flavor</c> field — the pipeline's existing
    /// firmware-flavour notion (see <c>OrcaProfilesService.GcodeDialect</c>) — and resolves it via
    /// <see cref="PressureAdvanceTowerGcodeBuilder.TryResolveFirmwareFlavor"/>. Any other flavour
    /// (or a missing/unparsable machine profile) is refused with an explicit
    /// <see cref="InvalidOperationException"/> here, before the OrcaSlicer binary ever runs — this
    /// calibration method must never silently slice a tower that changes nothing. Bambu Lab (BBL)
    /// machines that resolve to the Marlin flavour are refused separately, by <c>printer_model</c>:
    /// upstream OrcaSlicer inherits <c>gcode_flavor: "marlin"</c> for BBL machines but its own
    /// gcode writer branches on a distinct <c>is_bbl_printers</c> flag ahead of flavour, emitting a
    /// different command (<c>M900 K{v} L1000 M10</c>) than generic Marlin — see
    /// <see cref="PressureAdvanceTowerGcodeBuilder.IsBambuLabPrinterModel"/>. The BBL check is
    /// intentionally scoped to the Marlin flavour only: a Klipper-flashed BBL machine is a real,
    /// supported configuration whose <c>gcode_flavor</c> already resolves to Klipper, so
    /// <c>SET_PRESSURE_ADVANCE</c> is the correct command for it and must not be refused.
    /// </remarks>
    internal static async Task ApplyPressureAdvanceTowerGcodeAsync(
        DistributedSlicingJob job,
        string processJsonPath,
        string machineJsonPath,
        CancellationToken cancellationToken)
    {
        string machineJsonContent = await File.ReadAllTextAsync(machineJsonPath, cancellationToken);

        string? gcodeFlavor = PressureAdvanceTowerGcodeBuilder.ReadGcodeFlavor(machineJsonContent);
        CalibrationFirmwareFlavor? flavor = PressureAdvanceTowerGcodeBuilder.TryResolveFirmwareFlavor(gcodeFlavor);
        if (flavor is null)
        {
            // Truncate the untrusted, client-influenced gcode_flavor value before echoing it into
            // the exception message: an adversarial machine profile could otherwise smuggle an
            // arbitrarily large string into job failure telemetry/logs.
            string flavorForMessage = gcodeFlavor is null ? "(unset)" : TruncateForMessage(gcodeFlavor);
            throw new InvalidOperationException(
                $"Pressure advance tower calibration requires a Klipper or Marlin/Marlin2 machine profile " +
                $"(gcode_flavor); the resolved firmware flavour '{flavorForMessage}' is not supported.");
        }

        string? printerModel = PressureAdvanceTowerGcodeBuilder.ReadPrinterModel(machineJsonContent);
        if (flavor.Value == CalibrationFirmwareFlavor.Marlin && PressureAdvanceTowerGcodeBuilder.IsBambuLabPrinterModel(printerModel))
        {
            // Bambu Lab (BBL) machine profiles inherit gcode_flavor: "marlin" from upstream
            // OrcaSlicer's fdm_machine_common, so they resolve to the generic Marlin branch below
            // and would emit a bare "M900 K{v}" -- but upstream OrcaSlicer's own gcode writer
            // branches on a distinct is_bbl_printers flag before gcode_flavor and emits
            // "M900 K{v} L1000 M10" for BBL specifically. Since this builder does not (yet) emit
            // that dialect, refuse explicitly rather than silently slicing a tower with the wrong
            // command for this hardware. A Klipper-flashed BBL machine resolves to the Klipper
            // branch above instead, and is not affected by this check.
            // See PressureAdvanceTowerGcodeBuilder.IsBambuLabPrinterModel.
            throw new InvalidOperationException(
                $"Pressure advance tower calibration does not yet support Bambu Lab (BBL) machine " +
                $"profiles (printer_model '{TruncateForMessage(printerModel!)}'); BBL requires a distinct " +
                $"M900 K{{v}} L1000 M10 dialect that this calibration method does not emit.");
        }

        CalibrationParameters parameters = CalibrationParameters.Parse(job.CalibrationParamsJson, CalibrationMethod.PressureAdvanceTower);
        string layerChangeGcode = PressureAdvanceTowerGcodeBuilder.BuildLayerChangeGcode(
            flavor.Value,
            parameters.StartAdvance,
            parameters.AdvanceStep,
            parameters.BandHeightMm,
            parameters.BandCount);

        string processJsonContent = await File.ReadAllTextAsync(processJsonPath, cancellationToken);
        string updatedProcessJsonContent = InjectLayerChangeGcode(processJsonContent, layerChangeGcode);
        await File.WriteAllTextAsync(processJsonPath, updatedProcessJsonContent, cancellationToken);
        job.ProcessProfileSha256 = NativeSlicerProfiles.ComputeSha256(updatedProcessJsonContent);
    }

    /// <summary>
    /// Truncates an untrusted, machine-profile-supplied string before it is embedded into an
    /// exception message: an adversarial machine profile could otherwise smuggle an arbitrarily
    /// large value into job failure telemetry/logs.
    /// </summary>
    private static string TruncateForMessage(string value) =>
        value.Length > 64 ? string.Concat(value.AsSpan(0, 64), "…") : value;

    /// <summary>
    /// Sets the max volumetric speed calibration's permissive <c>filament_max_volumetric_speed</c>
    /// ceiling (issue #2135) on the filament profile(s) on disk and recomputes
    /// <see cref="DistributedSlicingJob.FilamentProfileSha256"/> so the recorded digest matches the
    /// mutated content. The ceiling keeps OrcaSlicer's own flow-based auto speed-limiting from
    /// clamping the print below the range the calibration tower's own width-increasing geometry
    /// needs, mirroring upstream's <c>CalibUtils::calib_max_vol_speed</c> permissive-ceiling
    /// write. Unlike the temperature tower, no <c>layer_change_gcode</c> injection is attempted
    /// here: it would have no effect, since a slicer-emitted <c>F</c> parameter on the next
    /// extrusion move always overrides one from injected custom gcode. Upstream also applies a
    /// separate, additional per-layer <c>outer_wall_speed</c> override
    /// (<c>GCode.cpp</c>'s <c>Calib_Vol_speed_Tower</c> case) that is set in-process by the GUI
    /// wizard and is not reachable at all from this worker's CLI-driven pipeline — see the
    /// <c>CalibrationMethod</c> type remarks for the full citation trail. This worker therefore
    /// applies only the ceiling and relies on the bundled geometry plus the client-selected
    /// process profile's own constant wall speed; it does not reproduce upstream's deliberate
    /// per-layer ramp.
    /// </summary>
    /// <param name="job">The claimed job whose <see cref="DistributedSlicingJob.FilamentProfileSha256"/> is updated.</param>
    /// <param name="filamentJsonPath">
    /// The value of <c>profilePaths["filament"]</c>: either a single filament JSON path, or — for a
    /// multi-extruder job — a <c>;</c>-joined list of per-extruder paths, matching the
    /// <c>--load-filaments</c> argument format produced by <see cref="GenerateProfileJsonFilesAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task ApplyMaxVolumetricSpeedCeilingAsync(
        DistributedSlicingJob job,
        string filamentJsonPath,
        CancellationToken cancellationToken)
    {
        CalibrationParameters parameters = CalibrationParameters.Parse(job.CalibrationParamsJson, CalibrationMethod.MaximumVolumetricSpeed);

        string[] paths = filamentJsonPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var updatedDocuments = new List<string>(paths.Length);
        foreach (string path in paths)
        {
            string filamentJsonContent = await File.ReadAllTextAsync(path, cancellationToken);
            string updatedFilamentJsonContent = InjectMaxVolumetricSpeedCeiling(filamentJsonContent, parameters.MaxVolumetricSpeedCeilingMm3s);
            await File.WriteAllTextAsync(path, updatedFilamentJsonContent, cancellationToken);
            updatedDocuments.Add(updatedFilamentJsonContent);
        }

        job.FilamentProfileSha256 = ComputeProfileSetSha256(updatedDocuments);
    }

    /// <summary>
    /// Sets the <c>filament_max_volumetric_speed</c> key on a filament profile JSON document to
    /// <paramref name="ceilingMm3s"/>. OrcaSlicer stores this (like most filament settings) as a
    /// single-element array, e.g. <c>["50"]</c>, so the value is written the same way regardless
    /// of whether the key was previously present.
    /// </summary>
    internal static string InjectMaxVolumetricSpeedCeiling(string filamentJson, double ceilingMm3s)
    {
        JsonNode rootNode = JsonNode.Parse(filamentJson)
            ?? throw new InvalidOperationException("Filament profile JSON is empty.");
        if (rootNode is not JsonObject rootObject)
        {
            throw new InvalidOperationException("Filament profile JSON root must be an object.");
        }

        rootObject["filament_max_volumetric_speed"] = new JsonArray(
            JsonValue.Create(ceilingMm3s.ToString(CultureInfo.InvariantCulture)));

        return rootObject.ToJsonString();
    }

    /// <summary>
    /// Sets (or appends to any existing) <c>layer_change_gcode</c> key on a process profile JSON
    /// document. Appending rather than clobbering preserves any custom gcode the selected process
    /// profile already carries.
    /// </summary>
    internal static string InjectLayerChangeGcode(string processJson, string layerChangeGcode)
    {
        JsonNode rootNode = JsonNode.Parse(processJson)
            ?? throw new InvalidOperationException("Process profile JSON is empty.");
        if (rootNode is not JsonObject rootObject)
        {
            throw new InvalidOperationException("Process profile JSON root must be an object.");
        }

        string? existingGcode = rootObject["layer_change_gcode"]?.GetValue<string>();
        rootObject["layer_change_gcode"] = string.IsNullOrWhiteSpace(existingGcode)
            ? layerChangeGcode
            : existingGcode.TrimEnd() + "\n" + layerChangeGcode;

        return rootObject.ToJsonString();
    }

    internal async Task<string> FetchStlFileAsync(
        DistributedSlicingJob job,
        string workDir,
        CancellationToken cancellationToken)
    {
        string stlFilePath = ResolveModelDestinationPath(workDir, job.ModelFileName);
        string modelRoute = ResolveClaimModelRoute(job.ModelFileUrl);
        using HttpRequestMessage request =
            CreateModelDownloadRequest(
                modelRoute,
                job.Id,
                null,
                job.ClaimToken,
                job.LeaseToken,
                job.LeaseFence);
        await DownloadModelAsync(request, stlFilePath, cancellationToken);

        // The model digest published with the claim is authoritative: refuse anything else.
        if (!string.IsNullOrWhiteSpace(job.ModelSha256))
        {
            string actual = await ComputeFileSha256Async(stlFilePath, cancellationToken);
            if (!string.Equals(actual, job.ModelSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                DeletePartialModelFile(stlFilePath);
                throw new InvalidOperationException("The downloaded model does not match the digest published with the claim.");
            }
        }

        job.InputFileSizeBytes = new FileInfo(stlFilePath).Length;
        return stlFilePath;
    }

    private string ResolveClaimModelRoute(Uri modelFileUri)
    {
        if (!modelFileUri.IsAbsoluteUri)
        {
            return modelFileUri.OriginalString;
        }

        bool isTrustedOrigin = Uri.Compare(
            _apiBaseUri,
            modelFileUri,
            UriComponents.SchemeAndServer,
            UriFormat.Unescaped,
            StringComparison.OrdinalIgnoreCase) == 0;

        if (!isTrustedOrigin || !string.IsNullOrEmpty(modelFileUri.Fragment))
        {
            throw new InvalidOperationException(
                "Model URL must use the configured slicer API origin and an exact claim-scoped model route.");
        }

        return modelFileUri.PathAndQuery;
    }

    /// <summary>
    /// Computes the uppercase hexadecimal SHA-256 of a file.
    /// </summary>
    /// <param name="path">Path to the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The uppercase hexadecimal digest.</returns>
    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    internal static string ResolveModelDestinationPath(string workDir, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string normalizedFileName = fileName.Replace('\\', '/');
        bool hasWindowsDrivePrefix = normalizedFileName.Length >= 2
            && char.IsAsciiLetter(normalizedFileName[0])
            && normalizedFileName[1] == ':';
        string baseName = Path.GetFileName(normalizedFileName);
        if (hasWindowsDrivePrefix
            || Path.IsPathFullyQualified(normalizedFileName)
            || normalizedFileName.Contains('/')
            || !string.Equals(baseName, normalizedFileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The model file name must be a bare file name.");
        }

        string sanitizedFileName = SanitizeModelFileName(baseName);
        if (string.IsNullOrWhiteSpace(sanitizedFileName) || sanitizedFileName is "." or "..")
        {
            throw new InvalidOperationException("The model file name does not contain a safe file name.");
        }

        string fullWorkDirectory = Path.GetFullPath(workDir);
        string destinationPath = Path.GetFullPath(Path.Join(fullWorkDirectory, sanitizedFileName));
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        StringComparison pathComparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (destinationDirectory is null
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(destinationDirectory),
                Path.TrimEndingDirectorySeparator(fullWorkDirectory),
                pathComparison))
        {
            throw new InvalidOperationException("The model destination must remain inside the job working directory.");
        }

        return destinationPath;
    }

    private static string SanitizeModelFileName(string fileName)
    {
        return string.Concat(fileName.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_'));
    }

    internal async Task<List<string>> FetchMultipleModelsAsync(
        Guid jobId,
        List<string> modelUrls,
        Guid claimToken,
        Guid leaseToken,
        long leaseFence,
        string workDir,
        CancellationToken cancellationToken)
    {
        List<string> downloadedPaths = new(modelUrls.Count);
        for (int i = 0; i < modelUrls.Count; i++)
        {
            string url = modelUrls[i];

            // Safely extract filename — Uri.LocalPath throws on relative URIs
            string fileName;
            fileName = Uri.TryCreate(url, UriKind.Absolute, out Uri? parsedUri)
                ? Path.GetFileName(parsedUri.LocalPath)
                : string.Empty;

            if (string.IsNullOrEmpty(fileName))
            {
                fileName = $"model_{i}{(url.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase) ? ".3mf" : url.EndsWith(".step", StringComparison.OrdinalIgnoreCase) ? ".step" : url.EndsWith(".stp", StringComparison.OrdinalIgnoreCase) ? ".stp" : ".stl")}";
            }

            fileName = SanitizeModelFileName(fileName);

            // Ensure unique filenames when multiple models share the same name
            string destPath = ResolveModelDestinationPath(workDir, fileName);
            if (File.Exists(destPath))
            {
                string baseName = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);
                destPath = ResolveModelDestinationPath(workDir, $"{baseName}_{i}{ext}");
            }

            using HttpRequestMessage request =
                CreateModelDownloadRequest(url, jobId, i, claimToken, leaseToken, leaseFence);
            await DownloadModelAsync(request, destPath, cancellationToken);
            downloadedPaths.Add(destPath);
            _logger.LogInformation("Downloaded model {Index}/{Total}: {Path}", i + 1, modelUrls.Count, destPath);
        }

        return downloadedPaths;
    }

    internal HttpRequestMessage CreateModelDownloadRequest(
        string modelRoute,
        Guid jobId,
        int? modelIndex,
        Guid claimToken,
        Guid leaseToken,
        long leaseFence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRoute);
        if (jobId == Guid.Empty)
        {
            throw new InvalidOperationException("A model download requires a valid job identity.");
        }

        if (modelIndex is < 0)
        {
            throw new InvalidOperationException("A model download index cannot be negative.");
        }

        string expectedRoute = modelIndex is null
            ? $"/api/slice/{jobId:D}/model"
            : $"/api/slice/{jobId:D}/models/{modelIndex.Value}";
        if (!string.Equals(modelRoute, expectedRoute, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The claimed model location must be the exact API-relative route for this job.");
        }

        if (claimToken == Guid.Empty || leaseToken == Guid.Empty || leaseFence <= 0)
        {
            throw new InvalidOperationException("Active claim and lease credentials are required to download a model.");
        }

        WorkerState workerState = _workerState.GetWorkerState();
        Guid? serviceId = workerState.RegisteredServiceId;
        if (serviceId is null || string.IsNullOrWhiteSpace(workerState.RegisteredServiceApiKey))
        {
            throw new InvalidOperationException("Authenticated worker identity is unavailable.");
        }

        Uri requestUri = new(_apiBaseUri, expectedRoute);
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add(WorkerLeaseHeaders.WorkerKey, workerState.RegisteredServiceApiKey);
        request.Headers.Add(WorkerLeaseHeaders.WorkerId, serviceId.Value.ToString());
        request.Headers.Add(WorkerClaimHeaders.ClaimToken, claimToken.ToString());
        request.Headers.Add(WorkerLeaseHeaders.LeaseToken, leaseToken.ToString());
        request.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            leaseFence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }

    private async Task DownloadModelAsync(
        HttpRequestMessage request,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource downloadCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadCancellation.CancelAfter(_modelDownloadTimeout);

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                downloadCancellation.Token);
            int statusCode = (int)response.StatusCode;
            if (statusCode is >= 300 and < 400)
            {
                throw new InvalidOperationException("Model download redirects are not allowed.");
            }

            _ = response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength
                && contentLength > _maxModelDownloadBytes)
            {
                throw new InvalidDataException(
                    $"The model response exceeds the configured {_maxModelDownloadBytes}-byte limit.");
            }

            await using Stream contentStream =
                await response.Content.ReadAsStreamAsync(downloadCancellation.Token);
            await using FileStream fileStream = new(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DownloadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[DownloadBufferSize];
            long downloadedBytes = 0;
            while (true)
            {
                int bytesRead = await contentStream.ReadAsync(
                    buffer.AsMemory(),
                    downloadCancellation.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                if (downloadedBytes > _maxModelDownloadBytes - bytesRead)
                {
                    throw new InvalidDataException(
                        $"The model response exceeds the configured {_maxModelDownloadBytes}-byte limit.");
                }

                await fileStream.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    downloadCancellation.Token);
                downloadedBytes += bytesRead;
            }

            await fileStream.FlushAsync(downloadCancellation.Token);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested && downloadCancellation.IsCancellationRequested)
        {
            DeletePartialModelFile(destinationPath);
            throw new TimeoutException(
                $"The model download exceeded the configured {_modelDownloadTimeout.TotalSeconds:0}-second timeout.",
                ex);
        }
        catch
        {
            DeletePartialModelFile(destinationPath);
            throw;
        }
    }

    private void DeletePartialModelFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to remove partial model download {Path}", path);
        }
    }

    /// <summary>
    /// Writes the exact native slicer profile documents delivered with the claim, verifying each
    /// digest before the bytes are handed to OrcaSlicer.
    /// </summary>
    /// <param name="profiles">The native documents plus expected digests.</param>
    /// <param name="workDir">The per-job working directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Absolute paths of the written machine, process and filament files.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no profiles were delivered or when a document fails its digest check.
    /// </exception>
    private static async Task<Dictionary<string, string>> WriteNativeProfilesAsync(
        NativeSlicerProfiles? profiles,
        string workDir,
        CancellationToken cancellationToken)
    {
        if (profiles is null)
        {
            throw new InvalidOperationException(
                "The claimed job did not carry native machine/process/filament profiles.");
        }

        VerifyDigest("machine", profiles.MachineJson, profiles.MachineSha256);
        VerifyDigest("process", profiles.ProcessJson, profiles.ProcessSha256);
        VerifyDigest("filament", profiles.FilamentJson, profiles.FilamentSha256);

        string machineJsonPath = Path.Join(workDir, "machine.json");
        string processJsonPath = Path.Join(workDir, "process.json");
        string filamentJsonPath = Path.Join(workDir, "filament.json");

        // Written verbatim: OrcaSlicer consumes its own profile schema, so re-serializing a CLR DTO
        // shape here would produce files the slicer cannot load.
        await File.WriteAllTextAsync(machineJsonPath, profiles.MachineJson, cancellationToken);
        await File.WriteAllTextAsync(processJsonPath, profiles.ProcessJson, cancellationToken);
        await File.WriteAllTextAsync(filamentJsonPath, profiles.FilamentJson, cancellationToken);

        return new Dictionary<string, string>
        {
            { "machine", machineJsonPath },
            { "process", processJsonPath },
            { "filament", filamentJsonPath }
        };
    }

    private static void VerifyDigest(string kind, string content, string expectedSha256)
    {
        string actual = NativeSlicerProfiles.ComputeSha256(content);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The delivered {kind} profile does not match its published digest.");
        }
    }

    /// <summary>
    /// Materializes profile files for a job that resolved its profiles from the worker's local
    /// profile cache instead of carrying native documents with the claim.
    /// </summary>
    /// <param name="job">The claimed job with its resolved profile set.</param>
    /// <param name="workDir">The per-job working directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Absolute paths of the written machine, process and filament files.</returns>
    private async Task<Dictionary<string, string>> GenerateProfileJsonFilesAsync(
        DistributedSlicingJob job,
        string workDir,
        CancellationToken cancellationToken)
    {
        SlicerProfileDto? profile = job.Profile;
        if (profile is null)
        {
            throw new InvalidOperationException(
                "The claimed job did not carry native profiles and its named profile selection could not be resolved.");
        }

        if (profile.MachineProfile is null ||
            profile.ProcessProfile is null ||
            (profile.FilamentProfile is null && profile.ExtruderFilamentProfiles is not { Count: > 0 }))
        {
            throw new InvalidOperationException(
                "The claimed job's named profile selection did not resolve machine, process and filament settings.");
        }

        string machineJsonPath = Path.Join(workDir, "machine.json");
        string processJsonPath = Path.Join(workDir, "process.json");

        // Write the profiles directly as JSON - they should already contain complete settings from the database
        // OrcaSlicer expects flat key-value JSON (native settings), not our DTO wrapper.
        // The Settings dictionary stores raw JSON text per key (from GetRawText()),
        // so we reconstruct proper JSON by writing the raw values directly.
        // The guard above already proved MachineProfile non-null, so no null-conditional here.
        // The machine document is materialized FIRST, because the value OrcaSlicer will match
        // `compatible_printers` against is read out of this exact document — including the
        // `inherits` rewrite below (issue #1768). Deriving it from the cached settings instead
        // would name the vendor base for a `from`: "User" preset while OrcaSlicer compared
        // against the rewritten name, and the pairing would still be rejected.
        Dictionary<string, object> emittedMachineSettings =
            WithSystemPresetInherits(profile.MachineProfile.Settings, profile.MachineProfile.Name);
        string machineJson = SettingsDictToNativeJson(emittedMachineSettings);

        // OrcaSlicer gates machine/process compatibility on the process document's
        // `compatible_printers` array alone; `compatible_printers_condition` is never evaluated
        // on the --load-settings path. Reconcile the two before emitting. See issue #1795.
        ProcessCompatibilityResolution compatibility = ResolveProcessCompatiblePrinters(
            profile.ProcessProfile!,
            profile.MachineProfile,
            emittedMachineSettings);
        LogProcessCompatibilityResolution(job, profile, compatibility);

        string processJson = SettingsDictToNativeJson(compatibility.Settings);

        await File.WriteAllTextAsync(machineJsonPath, machineJson, cancellationToken);
        await File.WriteAllTextAsync(processJsonPath, processJson, cancellationToken);

        var result = new Dictionary<string, string>
        {
            { "machine", machineJsonPath },
            { "process", processJsonPath }
        };

        // Multi-extruder: write one filament JSON per extruder, semicolon-separated for --load-filaments
        var filamentJsonDocuments = new List<string>();
        if (profile.ExtruderFilamentProfiles is { Count: > 1 })
        {
            var filamentPaths = new List<string>();
            for (int i = 0; i < profile.ExtruderFilamentProfiles.Count; i++)
            {
                string path = Path.Join(workDir, $"filament_{i}.json");
                string json = SettingsDictToNativeJson(profile.ExtruderFilamentProfiles[i].Settings);
                await File.WriteAllTextAsync(path, json, cancellationToken);
                filamentPaths.Add(path);
                filamentJsonDocuments.Add(json);
            }

            result["filament"] = string.Join(";", filamentPaths);
        }
        else
        {
            string filamentJsonPath = Path.Join(workDir, "filament.json");
            Dictionary<string, object> filamentSettings =
                profile.ExtruderFilamentProfiles is { Count: 1 }
                    ? profile.ExtruderFilamentProfiles[0].Settings
                    : profile.FilamentProfile!.Settings;
            string filamentJson = SettingsDictToNativeJson(filamentSettings);
            await File.WriteAllTextAsync(filamentJsonPath, filamentJson, cancellationToken);
            result["filament"] = filamentJsonPath;
            filamentJsonDocuments.Add(filamentJson);
        }

        job.MachineProfileSha256 = NativeSlicerProfiles.ComputeSha256(machineJson);
        job.ProcessProfileSha256 = NativeSlicerProfiles.ComputeSha256(processJson);
        job.FilamentProfileSha256 = ComputeProfileSetSha256(filamentJsonDocuments);

        return result;
    }

    private static string ComputeProfileSetSha256(IEnumerable<string> profileJsonDocuments) =>
        NativeSlicerProfiles.ComputeSha256(string.Join("\0", profileJsonDocuments));

    /// <summary>
    /// Returns a copy of a machine profile's settings whose <c>inherits</c> key names the system
    /// preset the document represents.
    /// </summary>
    /// <remarks>
    /// OrcaSlicer decides whether a process preset may be used with a machine preset by comparing
    /// each entry of the process document's <c>compatible_printers</c> against the machine
    /// document's <b>system preset name</b>. When the machine document's <c>from</c> is not
    /// <c>"system"</c>, OrcaSlicer derives that name from <c>inherits</c> rather than <c>name</c>.
    /// See <c>CLI::run</c> in OrcaSlicer.cpp, the branch taken when <c>--load-settings</c> supplies
    /// both a machine and a process document.
    /// <para>
    /// A flattened stock profile still carries the vendor bundle's internal base in that key (e.g.
    /// <c>fdm_machine_common</c>), which process profiles do not list among their compatible
    /// printers, so OrcaSlicer rejected those submissions with <c>CLI_PROCESS_NOT_COMPATIBLE</c>
    /// (-17) roughly a second in, before slicing a single layer. Dropping the key entirely does not
    /// help: the system name then resolves to the empty string, which matches nothing either.
    /// </para>
    /// <para>
    /// The document written here is a fully flattened snapshot of the named system preset, so it
    /// must declare that preset as its ancestor. Only the machine document needs this: the gate
    /// reads the printer's system name alone, and the process/filament documents are untouched.
    /// </para>
    /// <para>
    /// Scope: presets shipping <c>from</c>: <c>"system"</c> resolve by name and are unaffected by
    /// this rewrite, and process profiles expressing compatibility only through
    /// <c>compatible_printers_condition</c> fail for a separate reason tracked in issue #1795.
    /// See issue #1768.
    /// </para>
    /// </remarks>
    /// <param name="settings">The resolved machine settings bag.</param>
    /// <param name="presetName">The machine profile's name, i.e. the system preset it snapshots.</param>
    /// <returns>A copy carrying the corrected <c>inherits</c> value; key order is preserved.</returns>
    internal static Dictionary<string, object> WithSystemPresetInherits(
        Dictionary<string, object>? settings,
        string? presetName)
    {
        Dictionary<string, object> copy = settings is null
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            : new Dictionary<string, object>(settings, StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(presetName))
        {
            copy["inherits"] = presetName;
        }

        return copy;
    }

    /// <summary>
    /// How the emitted process document was reconciled with OrcaSlicer's compatibility gate.
    /// </summary>
    internal enum ProcessCompatibilityOutcome
    {
        /// <summary>The document already declared at least one compatible printer; left untouched.</summary>
        AlreadyDeclared,

        /// <summary>The profile constrains no printer at all, so the pairing was materialized.</summary>
        InjectedUnconditional,

        /// <summary>The profile's condition holds for this machine, so the pairing was materialized.</summary>
        InjectedFromCondition,

        /// <summary>The profile's condition does not hold for this machine; left untouched so OrcaSlicer rejects it.</summary>
        ConditionNotSatisfied,

        /// <summary>The machine's system preset name could not be derived; left untouched.</summary>
        MachineSystemNameUnknown,
    }

    /// <summary>
    /// The reconciled process settings plus the evidence behind the decision.
    /// </summary>
    /// <param name="Settings">The settings bag to emit as <c>process.json</c>.</param>
    /// <param name="Outcome">Which branch of the reconciliation was taken.</param>
    /// <param name="MachineSystemPresetName">The name OrcaSlicer will match against, when derivable.</param>
    /// <param name="Condition">The profile's <c>compatible_printers_condition</c>, when it carries one.</param>
    internal sealed record ProcessCompatibilityResolution(
        Dictionary<string, object> Settings,
        ProcessCompatibilityOutcome Outcome,
        string? MachineSystemPresetName,
        string? Condition);

    /// <summary>
    /// Derives the system preset name OrcaSlicer will match <c>compatible_printers</c> entries
    /// against for a given machine document.
    /// </summary>
    /// <remarks>
    /// This mirrors <c>CLI::run</c> in <c>OrcaSlicer.cpp</c>, which sets
    /// <c>new_printer_system_name</c> from the machine document's <c>name</c> when its
    /// <c>from</c> is exactly <c>"system"</c>, and from its <c>inherits</c> otherwise. Reading
    /// the emitted document rather than assuming one of the two keys keeps this correct for both
    /// stock (<c>from</c>: <c>"system"</c>) and user (<c>from</c>: <c>"User"</c>) presets, and
    /// therefore independent of the sibling <c>inherits</c> fix for issue #1768.
    /// </remarks>
    /// <param name="machineSettings">The resolved machine settings bag.</param>
    /// <param name="fallbackName">The machine profile's name, used when the document is malformed.</param>
    /// <returns>The system preset name, or <see langword="null"/> when none can be derived.</returns>
    internal static string? ResolveMachineSystemPresetName(
        Dictionary<string, object>? machineSettings,
        string? fallbackName)
    {
        string? from = ReadSettingString(machineSettings, "from");
        string? derived = string.Equals(from, "system", StringComparison.Ordinal)
            ? ReadSettingString(machineSettings, "name")
            : ReadSettingString(machineSettings, "inherits");

        // A document that derives nothing here is one OrcaSlicer rejects outright before reaching
        // the gate, so the fallback cannot mask a compatibility decision — it only keeps the
        // emitted pair self-consistent.
        if (string.IsNullOrWhiteSpace(derived))
        {
            derived = fallbackName;
        }

        return string.IsNullOrWhiteSpace(derived) ? null : derived;
    }

    /// <summary>
    /// Returns a copy of a process profile's settings whose <c>compatible_printers</c> array
    /// satisfies OrcaSlicer's compatibility gate for the machine it is being paired with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the <c>--load-settings</c> path where both a machine and a process document are
    /// supplied, <c>CLI::run</c> decides compatibility by iterating the process document's
    /// <c>compatible_printers</c> array and comparing each entry against the machine's system
    /// preset name. It never evaluates <c>compatible_printers_condition</c> there, and the
    /// empty-array auto-pass sits in a different branch, so a profile that expresses
    /// compatibility only through the condition can never satisfy the gate: every such job exits
    /// <c>CLI_PROCESS_NOT_COMPATIBLE</c> (-17, surfacing as 239) about a second in, before
    /// slicing any geometry. That is the whole Prusa MK4S and CORE One family. See issue #1795.
    /// </para>
    /// <para>
    /// Materializing the condition's result closes that gap, but blindly injecting the machine
    /// name whenever the array is empty would force <em>any</em> pairing through. The injection
    /// is therefore gated on the machine actually satisfying the profile's condition, evaluated
    /// with the same <see cref="PrinterExpressionParser"/> that
    /// <c>OrcaProfilesService.ListAvailableProcessProfilesAsync</c> uses to resolve the condition
    /// into <c>CompatiblePrinters</c>, and that <c>SlicerProfilesController</c>'s
    /// <c>process/for-machines</c> endpoint uses to decide which presets the wizard may offer for
    /// a machine. The worker's emit-time decision is thus exactly the one the UI already made
    /// when it offered the pairing. A machine that fails the condition gets no injection and is
    /// still rejected downstream.
    /// </para>
    /// <para>
    /// A profile carrying neither an array nor a condition constrains no printer at all; the
    /// hierarchy endpoint likewise treats those as universally available, so the pairing is
    /// materialized. A profile that already declares compatible printers is left untouched, so
    /// this cannot regress the profiles that work today.
    /// </para>
    /// </remarks>
    /// <param name="processProfile">The resolved process profile.</param>
    /// <param name="machineProfile">The resolved machine profile it is paired with.</param>
    /// <param name="emittedMachineSettings">
    /// The machine settings exactly as they will be written to <c>machine.json</c>. The system
    /// preset name is derived from these rather than from the cached profile, so the value injected
    /// here is always the value OrcaSlicer will actually read back — including after the
    /// <c>inherits</c> rewrite for issue #1768, which changes that derivation for a
    /// <c>from</c>: <c>"User"</c> preset.
    /// </param>
    /// <returns>The reconciled settings plus the evidence behind the decision.</returns>
    internal static ProcessCompatibilityResolution ResolveProcessCompatiblePrinters(
        ProcessProfileDto processProfile,
        MachineProfileDto? machineProfile,
        Dictionary<string, object>? emittedMachineSettings = null)
    {
        ArgumentNullException.ThrowIfNull(processProfile);

        Dictionary<string, object> copy = processProfile.Settings is null
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            : new Dictionary<string, object>(processProfile.Settings, StringComparer.Ordinal);

        // The profile's OWN declared condition wins over the settings bag. A submission's
        // `overrides` object writes arbitrary keys into that bag
        // (HttpJobPollerService.ResolveProfileFromJsonAsync), so reading the bag first would let a
        // submission relax — or delete — the very constraint being enforced. The DTO property is
        // populated from the resolved profile document and is never written by overrides. The bag
        // is still consulted as a fallback, which can only ever add a constraint that was not
        // declared, never remove one.
        string? condition = processProfile.CompatiblePrintersCondition
            ?? ReadSettingString(copy, "compatible_printers_condition");

        if (HasDeclaredCompatiblePrinters(copy))
        {
            return new ProcessCompatibilityResolution(
                copy, ProcessCompatibilityOutcome.AlreadyDeclared, null, condition);
        }

        string? systemPresetName = ResolveMachineSystemPresetName(
            emittedMachineSettings ?? machineProfile?.Settings, machineProfile?.Name);
        if (systemPresetName is null)
        {
            return new ProcessCompatibilityResolution(
                copy, ProcessCompatibilityOutcome.MachineSystemNameUnknown, null, condition);
        }

        if (string.IsNullOrWhiteSpace(condition))
        {
            copy["compatible_printers"] = new List<string> { systemPresetName };
            return new ProcessCompatibilityResolution(
                copy, ProcessCompatibilityOutcome.InjectedUnconditional, systemPresetName, condition);
        }

        // EvaluateCondition returns the subset of the supplied machines the expression holds for,
        // or null when it holds for none (including when the expression cannot be parsed). With a
        // single candidate, a non-empty result means "this machine satisfies the condition".
        List<string>? matches = machineProfile is null
            ? null
            : PrinterExpressionParser.EvaluateCondition(condition, [machineProfile]);

        if (matches is not { Count: > 0 })
        {
            return new ProcessCompatibilityResolution(
                copy, ProcessCompatibilityOutcome.ConditionNotSatisfied, systemPresetName, condition);
        }

        copy["compatible_printers"] = new List<string> { systemPresetName };
        return new ProcessCompatibilityResolution(
            copy, ProcessCompatibilityOutcome.InjectedFromCondition, systemPresetName, condition);
    }

    /// <summary>
    /// Reports whether a process settings bag already names at least one compatible printer.
    /// </summary>
    /// <remarks>
    /// Array values normally arrive as <see cref="List{T}"/> of <see cref="string"/> from
    /// <c>OrcaProfilesService.SerializeElementToDict</c>, but the same bag also accepts the legacy
    /// raw-JSON-array text form that <see cref="SettingsDictToNativeJson"/> still understands, so
    /// both are recognised here.
    /// </remarks>
    /// <param name="settings">The process settings bag.</param>
    /// <returns><see langword="true"/> when the gate already has something to match against.</returns>
    private static bool HasDeclaredCompatiblePrinters(Dictionary<string, object> settings)
    {
        if (!settings.TryGetValue("compatible_printers", out object? value) || value is null)
        {
            return false;
        }

        if (value is IList<string> list)
        {
            return list.Any(entry => !string.IsNullOrWhiteSpace(entry));
        }

        string text = value.ToString() ?? string.Empty;
        if (text.StartsWith('['))
        {
            try
            {
                using JsonDocument parsed = JsonDocument.Parse(text);
                return parsed.RootElement.ValueKind == JsonValueKind.Array
                    && parsed.RootElement.EnumerateArray().Any(
                        element => !string.IsNullOrWhiteSpace(element.ToString()));
            }
            catch (JsonException)
            {
                // Not a JSON array after all — fall through to the scalar reading below.
            }
        }

        return !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>
    /// Reads a scalar settings value as a string, ignoring array-valued keys.
    /// </summary>
    /// <param name="settings">The settings bag, which may be <see langword="null"/>.</param>
    /// <param name="key">The settings key to read.</param>
    /// <returns>The trimmed value, or <see langword="null"/> when absent, empty or not scalar.</returns>
    private static string? ReadSettingString(Dictionary<string, object>? settings, string key)
    {
        if (settings is null || !settings.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        if (value is IList<string>)
        {
            return null;
        }

        string text = value.ToString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Records why the emitted process document will or will not satisfy OrcaSlicer's gate.
    /// </summary>
    /// <remarks>
    /// The failing cases are logged at error level on purpose: without this the job's only symptom
    /// is an opaque exit 239 with no indication of which of the two documents was at fault.
    /// The run is still handed to OrcaSlicer rather than pre-empted, because a job whose input is
    /// a 3MF carrying its own printer configuration can legitimately pass the gate through
    /// <c>CLI::run</c>'s machine-switch branch, which this worker cannot evaluate.
    /// </remarks>
    /// <param name="job">The job being prepared, for correlation.</param>
    /// <param name="profile">The resolved profile selection.</param>
    /// <param name="resolution">The reconciliation result to report.</param>
    private void LogProcessCompatibilityResolution(
        DistributedSlicingJob job,
        SlicerProfileDto profile,
        ProcessCompatibilityResolution resolution)
    {
        switch (resolution.Outcome)
        {
            case ProcessCompatibilityOutcome.AlreadyDeclared:
                break;

            case ProcessCompatibilityOutcome.InjectedUnconditional:
            case ProcessCompatibilityOutcome.InjectedFromCondition:
                _logger.LogInformation(
                    "Job {JobId}: process profile '{ProcessName}' declares no compatible printers; " +
                    "materialized '{SystemPresetName}' for machine '{MachineName}' ({Outcome})",
                    job.Id,
                    LogSanitizer.Sanitize(profile.ProcessProfile?.Name),
                    LogSanitizer.Sanitize(resolution.MachineSystemPresetName),
                    LogSanitizer.Sanitize(profile.MachineProfile?.Name),
                    resolution.Outcome);
                break;

            case ProcessCompatibilityOutcome.ConditionNotSatisfied:
                _logger.LogError(
                    "Job {JobId}: process profile '{ProcessName}' is not compatible with machine " +
                    "'{MachineName}' (system preset '{SystemPresetName}') — its " +
                    "compatible_printers_condition '{Condition}' does not hold. OrcaSlicer will " +
                    "reject this pairing with CLI_PROCESS_NOT_COMPATIBLE (-17).",
                    job.Id,
                    LogSanitizer.Sanitize(profile.ProcessProfile?.Name),
                    LogSanitizer.Sanitize(profile.MachineProfile?.Name),
                    LogSanitizer.Sanitize(resolution.MachineSystemPresetName),
                    LogSanitizer.Sanitize(resolution.Condition));
                break;

            default:
                _logger.LogError(
                    "Job {JobId}: could not derive a system preset name for machine '{MachineName}', " +
                    "so process profile '{ProcessName}' cannot be shown to satisfy OrcaSlicer's " +
                    "compatibility gate.",
                    job.Id,
                    LogSanitizer.Sanitize(profile.MachineProfile?.Name),
                    LogSanitizer.Sanitize(profile.ProcessProfile?.Name));
                break;
        }
    }

    /// <summary>
    /// Describes why the pair of emitted profile documents cannot satisfy OrcaSlicer's
    /// process/machine compatibility gate, or <see langword="null"/> when they can.
    /// </summary>
    /// <remarks>
    /// The gate compares each entry of the process document's <c>compatible_printers</c> against
    /// the machine document's system preset name. Checking that invariant on the documents that
    /// are about to be handed to the CLI turns an otherwise opaque exit 239 into a statement of
    /// which document was at fault, and covers both the generated and the verbatim native path.
    /// Anything unreadable yields <see langword="null"/>: this is a diagnostic, so a parsing
    /// problem here must never become the reported cause of a job failure.
    /// </remarks>
    /// <param name="machineDocument">The emitted machine document's JSON text.</param>
    /// <param name="processDocument">The emitted process document's JSON text.</param>
    /// <returns>A human-readable explanation, or <see langword="null"/> when the gate is satisfiable.</returns>
    internal static string? DescribeUnsatisfiableCompatibilityGate(
        string machineDocument,
        string processDocument)
    {
        try
        {
            using JsonDocument machine = JsonDocument.Parse(machineDocument);
            using JsonDocument process = JsonDocument.Parse(processDocument);

            // A profile document is an object. Anything else is a shape OrcaSlicer rejects before
            // the gate, and would make the JsonElement accessors below throw, so bail out rather
            // than let a diagnostic become the reported cause of a job failure.
            if (machine.RootElement.ValueKind != JsonValueKind.Object
                || process.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? systemPresetName = ResolveMachineSystemPresetName(
                OrcaProfilesService.SerializeElementToDict(machine.RootElement), fallbackName: null);
            if (systemPresetName is null)
            {
                return "the machine document declares neither a system preset name via `name` " +
                    "(when `from` is \"system\") nor one via `inherits`";
            }

            if (!process.RootElement.TryGetProperty("compatible_printers", out JsonElement compatible)
                || compatible.ValueKind != JsonValueKind.Array
                || compatible.GetArrayLength() == 0)
            {
                return $"the process document declares no compatible printers, so the machine's " +
                    $"system preset '{systemPresetName}' cannot match any of them";
            }

            bool matched = compatible
                .EnumerateArray()
                .Any(entry => entry.ValueKind == JsonValueKind.String
                    && string.Equals(entry.GetString(), systemPresetName, StringComparison.Ordinal));

            return matched
                ? null
                : $"the process document's compatible printers do not include the machine's " +
                    $"system preset '{systemPresetName}'";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Logs, immediately before invoking OrcaSlicer, when the emitted documents cannot satisfy its
    /// compatibility gate.
    /// </summary>
    /// <remarks>
    /// Deliberately a warning rather than a pre-emptive failure: a job whose input is a 3MF
    /// carrying its own printer configuration can still pass through <c>CLI::run</c>'s
    /// machine-switch branch, which this worker cannot evaluate, so refusing to run would reject
    /// pairings OrcaSlicer would have accepted.
    /// </remarks>
    /// <param name="job">The job being prepared, for correlation.</param>
    /// <param name="machineJsonPath">Path of the emitted machine document.</param>
    /// <param name="processJsonPath">Path of the emitted process document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task WarnIfProcessCannotSatisfyGateAsync(
        DistributedSlicingJob job,
        string machineJsonPath,
        string processJsonPath,
        CancellationToken cancellationToken)
    {
        string? reason;
        try
        {
            reason = DescribeUnsatisfiableCompatibilityGate(
                await File.ReadAllTextAsync(machineJsonPath, cancellationToken),
                await File.ReadAllTextAsync(processJsonPath, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A diagnostic must never be the reported cause of a job failure.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(
                ex, "Job {JobId}: could not pre-check profile compatibility", job.Id);
            return;
        }

        if (reason is not null)
        {
            _logger.LogWarning(
                "Job {JobId}: OrcaSlicer is expected to reject this profile pairing with " +
                "CLI_PROCESS_NOT_COMPATIBLE (-17, reported as exit 239) because {Reason}.",
                job.Id,
                LogSanitizer.Sanitize(reason));
        }
    }

    private async Task<(string GcodeFilePath, LayoutDegradationReason? LayoutDegradation)> RunOrcaSlicerAsync(List<string> modelPaths, string workDir, DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        string gcodeOutputDir = Path.Join(workDir, "output");
        _ = Directory.CreateDirectory(gcodeOutputDir);

        string gcodeFilePath = Path.Join(gcodeOutputDir, Path.GetFileNameWithoutExtension(job.ModelFileName) + ".gcode");
        if (!File.Exists(_orcaSlicerBinaryPath))
        {
            throw new InvalidOperationException($"OrcaSlicer binary not found at {_orcaSlicerBinaryPath}");
        }

        // Canonical jobs carry native profile documents with published digests; those are written
        // verbatim after verification. Jobs that resolved profiles from the worker's local cache
        // fall back to materializing them (including per-extruder filaments).
        Dictionary<string, string> profilePaths;
        if (job.NativeProfiles is not null)
        {
            profilePaths = await WriteNativeProfilesAsync(job.NativeProfiles, workDir, cancellationToken);
            job.MachineProfileSha256 = job.NativeProfiles.MachineSha256;
            job.ProcessProfileSha256 = job.NativeProfiles.ProcessSha256;
            job.FilamentProfileSha256 = job.NativeProfiles.FilamentSha256;
        }
        else
        {
            profilePaths = await GenerateProfileJsonFilesAsync(job, workDir, cancellationToken);
        }

        string machineJson = profilePaths["machine"];
        string processJson = profilePaths["process"];
        string filamentJson = profilePaths["filament"];

        // Temperature tower calibration (issue #1938) and pressure advance tower calibration
        // (issue #2136): inject the per-band layer_change_gcode hook here, before the profile is
        // handed to OrcaSlicer, so the gate check below and the slice itself both see the final,
        // calibration-aware process profile.
        bool isKnownCalibrationMethod = CalibrationMethods.TryParse(job.CalibrationMethod, out CalibrationMethod calibrationMethod);
        if (isKnownCalibrationMethod && (calibrationMethod == CalibrationMethod.TemperatureTower || calibrationMethod == CalibrationMethod.PressureAdvanceTower))
        {
            if (calibrationMethod == CalibrationMethod.TemperatureTower)
            {
                await ApplyTemperatureTowerGcodeAsync(job, processJson, cancellationToken);
            }
            else if (calibrationMethod == CalibrationMethod.PressureAdvanceTower)
            {
                await ApplyPressureAdvanceTowerGcodeAsync(job, processJson, machineJson, cancellationToken);
            }
        }

        // Max volumetric speed calibration (issue #2135): apply the permissive
        // filament_max_volumetric_speed ceiling here, before the profile is handed to
        // OrcaSlicer, so the slice itself sees the final, calibration-aware filament profile(s).
        // Note WarnIfProcessCannotSatisfyGateAsync below only inspects the machine/process
        // documents, never the filament document, so this ceiling never participates in that gate.
        if (isKnownCalibrationMethod && calibrationMethod == CalibrationMethod.MaximumVolumetricSpeed)
        {
            await ApplyMaxVolumetricSpeedCeilingAsync(job, filamentJson, cancellationToken);
        }

        await WarnIfProcessCannotSatisfyGateAsync(job, machineJson, processJson, cancellationToken);

        // Build command line: --slice 0 --arrange 1 --ensure-on-bed --load-settings ...
        // --arrange 1: auto-center model on build plate (CLI loads STL at origin)
        // --ensure-on-bed: lift objects partially below Z=0
        //
        // Placement: OrcaSlicer 2.4.2 has NO CLI flag that can put a model at an absolute bed
        // coordinate (both --center and --align-xy are commented out of CLITransformConfigDef),
        // so any model carrying a custom position has its placement embedded in a 3MF project
        // instead. See PlanPlacement.
        (double X, double Y)? bedCenter = await TryReadBedCenterAsync(machineJson, job.Id, cancellationToken);

        PlacementPlan placement = PlanPlacement(
            job.ModelTransformJson,
            job.ModelFileTransforms,
            modelPaths,
            bedCenter.HasValue);

        List<string> effectiveModelPaths = modelPaths;

        switch (placement.Strategy)
        {
            case PlacementStrategy.ThreeMfProject:
                _logger.LogInformation(
                    "Job {JobId}: embedding transforms for {Count} model(s) in a 3MF project (bed centre {BedX},{BedY})",
                    job.Id, modelPaths.Count, bedCenter!.Value.X, bedCenter.Value.Y);

                var entries = new List<ThreeMfProjectBuilder.ModelEntry>(modelPaths.Count);
                for (int i = 0; i < modelPaths.Count; i++)
                {
                    entries.Add(new ThreeMfProjectBuilder.ModelEntry(modelPaths[i], placement.ModelTransforms[i]));
                }

                try
                {
                    effectiveModelPaths = [ThreeMfProjectBuilder.Build(entries, workDir, bedCenter.Value)];
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    // Only a binary STL within the triangle budget can be re-meshed into a
                    // project. Rather than failing the whole job, let OrcaSlicer auto-arrange —
                    // the layout is lost but the slice succeeds, and the reason is recorded here.
                    _logger.LogError(
                        ex,
                        "Job {JobId}: could not build the 3MF project; falling back to auto-arrange, so the requested layout is lost.",
                        job.Id);
                    placement = DowngradeToAutoArrange(placement);
                }

                break;

            default:
                break;
        }

        // Extracted as a pure function (DescribePlacementWarnings) so the logging decision
        // itself is unit-testable, not just the PlacementPlan flags it reads — issue #1799 was
        // partly about a degradation that shipped with no way to prove it was ever surfaced.
        IReadOnlyList<PlacementWarningKind> placementWarnings = DescribePlacementWarnings(placement);
        foreach (PlacementWarningKind warningKind in placementWarnings)
        {
            switch (warningKind)
            {
                case PlacementWarningKind.SourcePlacementFallback:
                    _logger.LogWarning(
                        "Job {JobId}: inputs are 3MF, so the workspace layout cannot be re-embedded. " +
                        "Falling back to the placement stored in the source file.",
                        job.Id);
                    break;

                case PlacementWarningKind.LayoutNotEmbedded:
                    _logger.LogWarning(
                        "Job {JobId}: the requested layout could not be embedded (inputs are not all STL, or the " +
                        "bed centre could not be determined); letting OrcaSlicer auto-arrange instead.",
                        job.Id);
                    break;

                case PlacementWarningKind.NonUniformScaleFlattened:
                    _logger.LogWarning(
                        "Job {JobId}: model has non-uniform scale but it could not be embedded in a 3MF project " +
                        "(inputs are not all STL, the bed centre could not be determined, or the project could " +
                        "not be built); OrcaSlicer will apply an isotropic scale instead of the requested " +
                        "per-axis scale.",
                        job.Id);
                    break;
            }
        }

        // The redacted, client-safe signal reported in the slice job result contract (issue
        // #1800) — never the raw log message above. Only the position/layout half of the
        // warnings maps to it; NonUniformScaleFlattened is a separate concern (issue #1799).
        LayoutDegradationReason? layoutDegradation = ToLayoutDegradationReason(placementWarnings);

        // Create a named pipe for real-time progress from OrcaSlicer
        string pipePath = Path.Join(workDir, "progress.pipe");
        bool pipeCreated = TryCreateNamedPipe(pipePath);
        string pipeFlag = pipeCreated ? $" --pipe \"{pipePath}\"" : string.Empty;

        string plateFlag = job.PlateIndex.HasValue ? $" --plate {job.PlateIndex.Value + 1}" : string.Empty;

        string arguments = BuildOrcaSlicerArguments(
            placement.ArrangeFlag,
            placement.TransformFlags,
            pipeFlag,
            plateFlag,
            machineJson,
            processJson,
            filamentJson,
            gcodeOutputDir,
            effectiveModelPaths);

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
            ? MonitorSlicingProgressViaPipeAsync(job.Id, job.ClaimToken, pipePath, process, cancellationToken)
            : MonitorSlicingProgressAsync(job.Id, job.ClaimToken, process, cancellationToken);
#pragma warning restore CA2025
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await progressTask;
        string output = await outputTask;
        string error = await errorTask;

        // xvfb-run execs the wrapped command as `"$@" 2>&1`, so when the wrapper is in use (which is
        // every containerized deployment) the child's stderr is folded into stdout before this
        // process ever sees it and StandardError is always empty. Treating the two as one combined
        // stream is therefore the only reading that is correct both under the wrapper and when
        // OrcaSlicer is executed directly — the previous stderr-only fallback could never run on the
        // deployed path. See issue #1811.
        string consoleOutput = string.IsNullOrWhiteSpace(error)
            ? output
            : string.Concat(output, "\n", error);

        _logger.LogInformation(
            "OrcaSlicer exited with code {ExitCode}. Stdout length={StdoutLen}, Stderr length={StderrLen} " +
            "(stderr is expected to be empty when xvfb-run is used: it merges stderr into stdout)",
            process.ExitCode,
            output.Length,
            error.Length);

        if (!string.IsNullOrWhiteSpace(consoleOutput))
        {
            _logger.LogInformation(
                "OrcaSlicer console output: {Output}",
                consoleOutput.Length > 2000 ? consoleOutput[..2000] : consoleOutput);
        }

        if (process.ExitCode != 0)
        {
            // OrcaSlicer's own result.json is the authoritative diagnostic: the console carries only
            // the bare word "Errors" plus its exit line on the slicing-failure path.
            OrcaSlicerFailureDiagnostics.OrcaResult? orcaResult =
                OrcaSlicerFailureDiagnostics.TryReadResult(gcodeOutputDir);

            OrcaSlicerFailureDiagnostics.Diagnosis diagnosis =
                OrcaSlicerFailureDiagnostics.Describe(process.ExitCode, consoleOutput, orcaResult);

            _logger.LogError(
                "Job {JobId}: OrcaSlicer failed. Reason={Reason}. Detail={Detail}",
                job.Id,
                diagnosis.Reason,
                diagnosis.Detail);

            throw new SlicerEngineFailureException(diagnosis.Reason, diagnosis.Detail);
        }

        // OrcaSlicer CLI always outputs plate_X.gcode (not {modelname}.gcode)
        if (!File.Exists(gcodeFilePath))
        {
            string expectedPlateName = job.PlateIndex.HasValue ? $"plate_{job.PlateIndex.Value + 1}.gcode" : "plate_1.gcode";
            string platePath = Path.Join(gcodeOutputDir, expectedPlateName);
            if (File.Exists(platePath))
            {
                gcodeFilePath = platePath;
            }
            else
            {
                string plate1Path = Path.Join(gcodeOutputDir, "plate_1.gcode");
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
        }

        return (gcodeFilePath, layoutDegradation);
    }

    /// <summary>
    /// Maps the position/layout half of <see cref="DescribePlacementWarnings"/> to the redacted,
    /// client-safe signal reported in <see cref="SlicingResult.LayoutDegradation"/> (issue #1800).
    /// Deliberately excludes <see cref="PlacementWarningKind.NonUniformScaleFlattened"/> — that is
    /// a scale concern (issue #1799), not a layout one, and would misrepresent a scale-only
    /// degradation as a dropped layout.
    /// </summary>
    internal static LayoutDegradationReason? ToLayoutDegradationReason(IReadOnlyList<PlacementWarningKind> warnings)
    {
        if (warnings.Contains(PlacementWarningKind.SourcePlacementFallback))
        {
            return LayoutDegradationReason.SourcePlacementFallback;
        }

        if (warnings.Contains(PlacementWarningKind.LayoutNotEmbedded))
        {
            return LayoutDegradationReason.LayoutNotEmbedded;
        }

        return null;
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
        string newPath = Path.Join(Path.GetDirectoryName(gcodeFilePath)!, newName);

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
        Guid claimToken,
        string pipePath,
        Process process,
        CancellationToken cancellationToken)
    {
        int fileDescriptor = -1;
        IntPtr unmanagedBuffer = IntPtr.Zero;
        try
        {
            fileDescriptor = OpenFileDescriptor(pipePath, LinuxOpenNonBlocking);
            if (fileDescriptor < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                _logger.LogWarning(
                    "Failed to open progress pipe for job {JobId} (errno {Error}), falling back to time-based progress",
                    jobId,
                    error);
                await MonitorSlicingProgressAsync(jobId, claimToken, process, cancellationToken);
                return;
            }

            const int bufferSize = 4096;
            unmanagedBuffer = Marshal.AllocHGlobal(bufferSize);
            byte[] buffer = new byte[bufferSize];
            char[] characters = new char[Encoding.UTF8.GetMaxCharCount(bufferSize)];
            Decoder decoder = Encoding.UTF8.GetDecoder();
            var pending = new StringBuilder();

            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                nint bytesRead = ReadFileDescriptor(
                    fileDescriptor,
                    unmanagedBuffer,
                    (nuint)bufferSize);
                if (bytesRead > 0)
                {
                    int count = checked((int)bytesRead);
                    Marshal.Copy(unmanagedBuffer, buffer, 0, count);
                    int characterCount = decoder.GetChars(
                        buffer,
                        0,
                        count,
                        characters,
                        0,
                        flush: false);
                    _ = pending.Append(characters, 0, characterCount);
                    await ReportCompletePipeLinesAsync(
                        jobId,
                        claimToken,
                        pending,
                        cancellationToken);
                    continue;
                }

                if (bytesRead < 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == LinuxInterruptedSystemCall)
                    {
                        continue;
                    }

                    if (error != LinuxTryAgain)
                    {
                        throw new IOException(
                            $"Reading the OrcaSlicer progress pipe failed with errno {error}.");
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading progress pipe for job {JobId}", jobId);
        }
        finally
        {
            if (unmanagedBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(unmanagedBuffer);
            }

            if (fileDescriptor >= 0)
            {
                _ = CloseFileDescriptor(fileDescriptor);
            }
        }
    }

    private async Task ReportCompletePipeLinesAsync(
        Guid jobId,
        Guid claimToken,
        StringBuilder pending,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string buffered = pending.ToString();
            int lineBreak = buffered.IndexOf('\n', StringComparison.Ordinal);
            if (lineBreak < 0)
            {
                return;
            }

            string line = buffered[..lineBreak].TrimEnd('\r');
            _ = pending.Remove(0, lineBreak + 1);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    // OrcaSlicer can emit valid but non-object JSON (e.g. a bare scalar) on the
                    // progress channel; there is nothing to extract, so treat it like the
                    // non-JSON diagnostics handled by the catch below.
                    continue;
                }

                int totalPercent = root.TryGetProperty("total_percent", out JsonElement tp)
                    && tp.ValueKind == JsonValueKind.Number
                    ? tp.GetInt32()
                    : -1;
                string message = root.TryGetProperty("message", out JsonElement msg)
                    && msg.ValueKind == JsonValueKind.String
                    ? msg.GetString() ?? "Slicing..."
                    : "Slicing...";

                if (root.TryGetProperty("warning", out JsonElement warn)
                    && warn.ValueKind == JsonValueKind.String)
                {
                    _logger.LogWarning(
                        "OrcaSlicer warning for job {JobId}: {Warning}",
                        jobId,
                        warn.GetString());
                }

                if (totalPercent >= 0)
                {
                    int mapped = Math.Clamp(30 + (int)(totalPercent * 0.4), 30, 70);
                    await _progressReporter.ReportProgressAsync(
                        jobId,
                        claimToken,
                        mapped,
                        message,
                        cancellationToken);
                }
            }
            catch (JsonException)
            {
                // OrcaSlicer can emit non-JSON diagnostics on the progress channel.
            }
        }
    }

    private async Task MonitorSlicingProgressAsync(
        Guid jobId,
        Guid claimToken,
        Process process,
        CancellationToken cancellationToken)
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
                    await _progressReporter.ReportProgressAsync(jobId, claimToken, currentProgress, "Slicing in progress...", cancellationToken);
                    lastProgressReport = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastProgressReport > TimeSpan.FromSeconds(10))
                {
                    await _progressReporter.ReportProgressAsync(jobId, claimToken, currentProgress, "Slicing in progress...", cancellationToken);
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
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/mkfifo"))
        {
            return false;
        }

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
                    FileName = "/usr/bin/mkfifo",
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

#pragma warning disable CA2101, SYSLIB1054 // libc open requires a UTF-8 char*; LibraryImport requires unsafe blocks.
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFileDescriptor(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadFileDescriptor(
        int fileDescriptor,
        IntPtr buffer,
        nuint count);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseFileDescriptor(int fileDescriptor);
#pragma warning restore CA2101, SYSLIB1054

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
                // Multiply in double space (not int) before assigning to the double PrintTimeSeconds field,
                // so an unexpectedly large parsed hour/minute value cannot silently overflow as int arithmetic.
                metadata.PrintTimeSeconds = ((double)int.Parse(tm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 3600) + ((double)int.Parse(tm.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * 60);
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

    private sealed class GcodeMetadata
    {
        public double PrintTimeSeconds { get; set; }

        public double FilamentUsageGrams { get; set; }

        public int LayerCount { get; set; }
    }

    [GeneratedRegex(@";\s*estimated printing time.*?(\d+)h\s*(\d+)m", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex MyRegex();

    /// <summary>
    /// Parsed transform result: CLI flags and whether a custom position or a non-uniform scale
    /// was specified.
    /// <see cref="Flags"/> must never contain a positional flag — OrcaSlicer 2.4.2 has none,
    /// and passing one aborts the run before slicing (issue #1794). When
    /// <see cref="HasCustomPosition"/> is true the caller embeds the placement in a 3MF project
    /// instead; see <see cref="PlanPlacement"/>. Likewise, OrcaSlicer's CLI <c>--scale</c> is a
    /// single value (<c>coFloat</c>) — <c>scale_to_fit</c> (<c>coPoint3</c>) is commented out of
    /// <c>CLITransformConfigDef</c> in 2.4.2 — so <see cref="HasNonUniformScale"/> being true
    /// means <see cref="Flags"/> can only carry an isotropic approximation
    /// (<c>scale[0]</c>) and <see cref="PlanPlacement"/> should prefer embedding the real
    /// per-axis scale in a 3MF project matrix instead (issue #1799).
    /// </summary>
    internal readonly record struct TransformResult(string Flags, bool HasCustomPosition, bool HasNonUniformScale);

    /// <summary>How a job's model placement is delivered to OrcaSlicer.</summary>
    internal enum PlacementStrategy
    {
        /// <summary>No custom placement needed — let OrcaSlicer auto-arrange (<c>--arrange 1</c>).</summary>
        AutoArrange,

        /// <summary>Placement embedded in a generated 3MF project (<c>--arrange 0</c>).</summary>
        ThreeMfProject,

        /// <summary>Non-STL input: honour the placement already stored in the source file (<c>--arrange 0</c>).</summary>
        SourcePlacement,
    }

    /// <summary>
    /// The kinds of placement-degradation warnings <see cref="DescribePlacementWarnings"/> can
    /// report, kept as an enum rather than free-form strings so each is logged with a
    /// compile-time constant message template (CA2254) at the single call site in
    /// <c>RunOrcaSlicerAsync</c>, while the *decision* of which kinds apply remains a plain,
    /// unit-testable function of a <see cref="PlacementPlan"/>.
    /// </summary>
    internal enum PlacementWarningKind
    {
        /// <summary>The requested layout/position could not be embedded; auto-arrange was used instead.</summary>
        LayoutNotEmbedded,

        /// <summary>3MF inputs cannot be re-embedded, so the placement stored in the source file was kept.</summary>
        SourcePlacementFallback,

        /// <summary>A non-uniform per-axis scale could not be embedded, so it was flattened to an isotropic CLI <c>--scale</c>.</summary>
        NonUniformScaleFlattened,
    }

    /// <summary>
    /// Resolved placement for a slice job: the arrange flag, the (position-free) CLI transform
    /// flags, and the per-model transform JSON to embed when a 3MF project is built.
    /// </summary>
    /// <param name="Strategy">Chosen placement mechanism.</param>
    /// <param name="ArrangeFlag">Either <c>--arrange 0</c> or <c>--arrange 1</c>.</param>
    /// <param name="TransformFlags">Rotation/scale CLI flags; empty when a 3MF project is used.</param>
    /// <param name="ModelTransforms">Transform JSON per model, aligned with the model paths.</param>
    /// <param name="PositionDropped">
    /// True when a custom position existed but could not be honoured, so the model is
    /// auto-arranged instead. Callers should log this.
    /// </param>
    /// <param name="NonUniformScaleDropped">
    /// True when a non-uniform per-axis scale existed but could not be embedded in a 3MF
    /// project, so <see cref="TransformFlags"/> carries only an isotropic approximation
    /// (<c>scale[0]</c>) via the CLI <c>--scale</c> flag. Callers should log this (issue #1799).
    /// </param>
    internal readonly record struct PlacementPlan(
        PlacementStrategy Strategy,
        string ArrangeFlag,
        string TransformFlags,
        IReadOnlyList<string?> ModelTransforms,
        bool PositionDropped,
        bool NonUniformScaleDropped);

    /// <summary>
    /// Decide how to place the job's models.
    /// <para>
    /// OrcaSlicer 2.4.2 exposes no CLI option for absolute placement — <c>center</c> and
    /// <c>align_xy</c> are both commented out of <c>CLITransformConfigDef</c>, so passing
    /// <c>--center</c> aborts the run with <c>CLI_INVALID_PARAMS</c> before anything is sliced
    /// (issue #1794). Placement must therefore be embedded in a 3MF project, which is only
    /// possible when every input is an STL we can re-mesh and when the machine profile tells us
    /// where the bed centre is.
    /// </para>
    /// </summary>
    /// <param name="modelTransformJson">Primary model transform for single-model jobs.</param>
    /// <param name="modelFileTransforms">Per-model transforms for multi-model jobs.</param>
    /// <param name="modelPaths">Downloaded model files, in order.</param>
    /// <param name="bedCenterKnown">True when the machine profile yielded a bed centre.</param>
    /// <returns>The placement plan.</returns>
    internal static PlacementPlan PlanPlacement(
        string? modelTransformJson,
        IReadOnlyList<string?>? modelFileTransforms,
        IReadOnlyList<string> modelPaths,
        bool bedCenterKnown)
    {
        ArgumentNullException.ThrowIfNull(modelPaths);

        // Multi-model jobs carry a per-file transform list; single-model jobs carry one blob.
        bool perModel = modelFileTransforms is { Count: > 0 } && modelPaths.Count > 1;

        var transforms = new List<string?>(modelPaths.Count);
        for (int i = 0; i < modelPaths.Count; i++)
        {
            transforms.Add(perModel
                ? (i < modelFileTransforms!.Count ? modelFileTransforms[i] : null)
                : (i == 0 ? modelTransformJson : null));
        }

        var transformResults = new TransformResult[transforms.Count];
        bool anyCustomPosition = false;
        bool anyNonUniformScale = false;
        for (int i = 0; i < transforms.Count; i++)
        {
            transformResults[i] = BuildTransformFlags(transforms[i]);
            anyCustomPosition |= transformResults[i].HasCustomPosition;
            anyNonUniformScale |= transformResults[i].HasNonUniformScale;
        }

        bool secondaryTransforms = transforms.Skip(1).Any(t => !string.IsNullOrWhiteSpace(t));

        // Non-uniform scale needs embedding for the same reason a custom position does:
        // OrcaSlicer 2.4.2's CLI --scale is a single value, so per-axis scale can only survive
        // via the 3MF project matrix (issue #1799).
        bool needsEmbedding = anyCustomPosition || secondaryTransforms || anyNonUniformScale;

        string primaryFlags = transformResults.Length > 0
            ? transformResults[0].Flags
            : string.Empty;

        // Only STL can be re-meshed into a project we control.
        bool inputsAreStl = modelPaths.Count > 0
            && modelPaths.All(p => p.EndsWith(".stl", StringComparison.OrdinalIgnoreCase));

        // Only 3MF carries its own bed placement. OBJ/PLY/STEP/STP load at raw mesh or CAD
        // coordinates, so "--arrange 0" would strand them wherever the file happens to sit.
        bool inputsCarryOwnPlacement = modelPaths.Count > 0
            && modelPaths.All(p => p.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase));

        if (needsEmbedding && inputsAreStl && bedCenterKnown)
        {
            // Rotation and scale (including non-uniform scale) are baked into the 3MF matrix,
            // so no CLI transform flags — and nothing is dropped.
            return new PlacementPlan(PlacementStrategy.ThreeMfProject, "--arrange 0", string.Empty, transforms, false, false);
        }

        if (needsEmbedding && inputsCarryOwnPlacement)
        {
            // A 3MF input already carries its own placement; keep it rather than re-arranging.
            // Non-uniform scale still can't be embedded here (there's no project to embed it
            // in), so it is flattened to isotropic via the CLI flags in primaryFlags.
            return new PlacementPlan(PlacementStrategy.SourcePlacement, "--arrange 0", primaryFlags, transforms, true, anyNonUniformScale);
        }

        // Nothing to place (or nowhere to place it): auto-arrange. Never "--arrange 0" without
        // an embedded placement — that would leave the model at raw mesh coordinates, which
        // lands it off the bed and trips OrcaSlicer's CLI_OBJECTS_PARTLY_INSIDE check. This is
        // also the path for mixed STL+3MF jobs and for OBJ/PLY/STEP, none of which can be
        // placed faithfully. Any non-uniform scale here is likewise flattened to isotropic.
        //
        // PositionDropped intentionally reports only the position/layout half of needsEmbedding
        // (anyCustomPosition || secondaryTransforms), not anyNonUniformScale — a scale-only job
        // reaching this branch has no layout to lose, and NonUniformScaleDropped already covers
        // the scale degradation on its own. Conflating the two would produce a misleading
        // "requested layout could not be embedded" warning for a job that never asked for one.
        return new PlacementPlan(
            PlacementStrategy.AutoArrange,
            "--arrange 1",
            primaryFlags,
            transforms,
            anyCustomPosition || secondaryTransforms,
            anyNonUniformScale);
    }

    /// <summary>
    /// Rewrite a plan to plain auto-arrange, used when the chosen mechanism turned out to be
    /// unavailable at runtime (for example the 3MF project could not be built). The requested
    /// layout is lost, which <see cref="PlacementPlan.PositionDropped"/> records so the caller
    /// can say so in the log; rotation and scale are recovered as CLI flags, though a
    /// non-uniform scale is flattened to isotropic in the process
    /// (<see cref="PlacementPlan.NonUniformScaleDropped"/>).
    /// </summary>
    internal static PlacementPlan DowngradeToAutoArrange(PlacementPlan plan)
    {
        TransformResult primary = plan.ModelTransforms.Count > 0
            ? BuildTransformFlags(plan.ModelTransforms[0])
            : default;

        // Only the primary model's flags are recovered — OrcaSlicer's CLI transform flags
        // apply to a single model, so a secondary model's transform was never emitted here even
        // before this fix. But a non-uniform scale on ANY model in the job is lost by this
        // downgrade (the 3MF project that would have baked it per-model no longer exists), so
        // the drop must be reported even when it is a secondary model's scale, not the
        // primary's.
        bool anyNonUniformScale = plan.ModelTransforms.Any(t => BuildTransformFlags(t).HasNonUniformScale);

        return plan with
        {
            Strategy = PlacementStrategy.AutoArrange,
            ArrangeFlag = "--arrange 1",
            TransformFlags = primary.Flags,
            PositionDropped = true,
            NonUniformScaleDropped = anyNonUniformScale,
        };
    }

    /// <summary>
    /// Decide which degradation warnings, if any, apply to a resolved <see cref="PlacementPlan"/>.
    /// Extracted as a pure function — separate from the <c>_logger.LogWarning</c> calls in the
    /// caller — specifically so the logging *decision* is unit-testable on its own, rather than
    /// only the <see cref="PlacementPlan"/> flags it reads. Issue #1799's acceptance criteria
    /// required the scale degradation to be logged, not merely tracked internally.
    /// </summary>
    /// <param name="placement">The (possibly downgraded) placement plan actually used.</param>
    /// <returns>
    /// The kinds of degradation warnings, if any, that apply to this plan. Empty when the plan
    /// needed no degradation warning (in particular, always empty for
    /// <see cref="PlacementStrategy.ThreeMfProject"/>, since that strategy means nothing was
    /// dropped).
    /// </returns>
    internal static IReadOnlyList<PlacementWarningKind> DescribePlacementWarnings(PlacementPlan placement)
    {
        var warnings = new List<PlacementWarningKind>();

        if (placement.Strategy == PlacementStrategy.SourcePlacement)
        {
            warnings.Add(PlacementWarningKind.SourcePlacementFallback);
        }
        else if (placement.PositionDropped)
        {
            warnings.Add(PlacementWarningKind.LayoutNotEmbedded);
        }

        if (placement.NonUniformScaleDropped)
        {
            warnings.Add(PlacementWarningKind.NonUniformScaleFlattened);
        }

        return warnings;
    }

    /// <summary>
    /// Compose the OrcaSlicer CLI argument string. The first model is positional; any
    /// additional models are passed with <c>--load</c>.
    /// </summary>
    internal static string BuildOrcaSlicerArguments(
        string arrangeFlag,
        string transformFlags,
        string pipeFlag,
        string plateFlag,
        string machineJson,
        string processJson,
        string filamentJson,
        string gcodeOutputDir,
        IReadOnlyList<string> effectiveModelPaths)
    {
        ArgumentNullException.ThrowIfNull(effectiveModelPaths);
        if (effectiveModelPaths.Count == 0)
        {
            throw new ArgumentException("At least one model path is required.", nameof(effectiveModelPaths));
        }

        string primaryModel = $"\"{effectiveModelPaths[0]}\"";
        string additionalModels = effectiveModelPaths.Count > 1
            ? " " + string.Join(" ", effectiveModelPaths.Skip(1).Select(p => $"--load \"{p}\""))
            : string.Empty;

        return $"--slice 0 {arrangeFlag} --ensure-on-bed{transformFlags}{pipeFlag}{plateFlag} --load-settings \"{machineJson};{processJson}\" --load-filaments \"{filamentJson}\" --allow-newer-file --outputdir \"{gcodeOutputDir}\"{additionalModels} {primaryModel}";
    }

    /// <summary>
    /// Read the bed centre, in OrcaSlicer bed coordinates, from a machine profile file.
    /// Workspace model positions are relative to the bed centre, so this offset is what maps
    /// them into the coordinate space OrcaSlicer places 3MF build items in.
    /// </summary>
    /// <returns>The bed centre, or <see langword="null"/> when the profile cannot be read or has
    /// no usable <c>printable_area</c>.</returns>
    private async Task<(double X, double Y)?> TryReadBedCenterAsync(
        string machineProfilePath,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(machineProfilePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Distinguished from "profile parsed but has no printable_area" so the placement
            // warning downstream is not blamed on profile content that was never read.
            _logger.LogWarning(ex, "Job {JobId}: could not read the machine profile to determine the bed centre.", jobId);
            return null;
        }

        (double X, double Y)? center = TryReadBedCenter(json);
        if (center is null)
        {
            _logger.LogWarning(
                "Job {JobId}: machine profile has no usable printable_area, so the bed centre is unknown.",
                jobId);
        }

        return center;
    }

    /// <summary>
    /// Bed centre derived from a machine profile's <c>printable_area</c> polygon: the centre of
    /// its bounding box. This is origin-convention agnostic — a corner-origin rectangular bed
    /// yields (width/2, depth/2) while a delta bed centred on (0,0) yields (0,0).
    /// </summary>
    internal static (double X, double Y)? TryReadBedCenter(string? machineProfileJson)
    {
        if (string.IsNullOrWhiteSpace(machineProfileJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(machineProfileJson);
            List<(double X, double Y)>? points = OrcaMachineProfileFields.ParsePrintableAreaPoints(doc.RootElement);
            if (points is null || points.Count == 0)
            {
                return null;
            }

            double minX = points.Min(p => p.X);
            double maxX = points.Max(p => p.X);
            double minY = points.Min(p => p.Y);
            double maxY = points.Max(p => p.Y);

            return ((minX + maxX) / 2, (minY + maxY) / 2);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Convert the workspace's rotation into the triple OrcaSlicer's CLI can actually express.
    /// <para>
    /// The viewer is three.js Euler order <c>'XYZ'</c> — column-vector <c>R = Rx·Ry·Rz</c>.
    /// OrcaSlicer does NOT compose CLI rotations. <c>ModelVolume::rotate</c> (Model.cpp) does
    /// <c>set_rotation(get_rotation() + extract_euler_angles(...))</c>, i.e. it ADDS each
    /// single-axis angle into an Euler triple — so flag order is irrelevant, addition being
    /// commutative — and <c>Geometry::rotation_transform</c> then rebuilds the matrix as
    /// <c>AngleAxisd(z,Z) * AngleAxisd(y,Y) * AngleAxisd(x,X)</c>, i.e. <c>Rz·Ry·Rx</c>, order
    /// <c>'ZYX'</c>. OrcaSlicer's own comment notes the triple "is not equivalent to Euler angles
    /// in the usual sense".
    /// </para>
    /// <para>
    /// Emitting the workspace angles verbatim therefore orients the part differently from what
    /// the user approved on screen whenever more than one component is non-zero — which is the
    /// normal output of auto-orient, since it derives its Euler from a quaternion. The rotation
    /// is instead re-parameterised: build the viewer's matrix, then extract the <c>'ZYX'</c>
    /// triple that reproduces it.
    /// </para>
    /// <para>
    /// This round-trips through OrcaSlicer's additive accumulation, but only because of the
    /// negative-Z correction below. <c>Geometry::extract_euler_angles</c> is Eigen's
    /// <c>eulerAngles(2,1,0)</c> with the first and last components swapped, and Eigen normalises
    /// that first angle — the Z one — into <c>[0, π]</c>. So <c>extract(Rz(γ))</c> for
    /// <c>γ &lt; 0</c> is NOT <c>(0,0,γ)</c>; it is <c>(π, -π, γ+π)</c>. Since
    /// <c>ModelVolume::rotate</c> SUMS these triples and <c>Ry(-π)</c> does not commute with a
    /// non-trivial X/Y contribution, a negative Z combined with any X or Y rotation would
    /// accumulate into the wrong orientation.
    /// </para>
    /// </summary>
    /// <returns>Rotation about X, Y and Z in radians, to be summed by OrcaSlicer as 'ZYX'.</returns>
    internal static (double X, double Y, double Z) ToOrcaRotation(double rx, double ry, double rz)
    {
        double cosX = Math.Cos(rx), sinX = Math.Sin(rx);
        double cosY = Math.Cos(ry), sinY = Math.Sin(ry);
        double cosZ = Math.Cos(rz), sinZ = Math.Sin(rz);

        // Viewer orientation, column-vector R = Rx·Ry·Rz (three.js Matrix4.makeRotationFromEuler,
        // case 'XYZ'). Only the entries the extraction needs are computed.
        double r00 = cosY * cosZ;
        double r01 = -cosY * sinZ;
        double r02 = sinY;
        double r10 = (cosX * sinZ) + (sinX * cosZ * sinY);
        double r20 = (sinX * sinZ) - (cosX * cosZ * sinY);
        double r21 = (sinX * cosZ) + (cosX * sinZ * sinY);
        double r22 = cosX * cosY;

        double outX;

        // Math.Clamp is a defensive guard, not dead weight: if -r20 ever rounded outside [-1,1],
        // Math.Asin would return NaN, and NaN fails SILENTLY here — Math.Abs(NaN) > epsilon is
        // false, so --rotate-y would be dropped rather than throwing, i.e. a silent
        // mis-orientation, the exact failure class this code exists to prevent.
        //
        // It is deliberately not covered by a test: r20 is a rotation-matrix entry, and a
        // targeted 20M-sample scan along the gimbal-lock boundary (where |r20| → 1) found no
        // input for which this expression rounds past 1. Do not delete it as "untested" — a
        // future change to how r20 is computed could easily make it reachable.
        double outY = Math.Asin(Math.Clamp(-r20, -1.0, 1.0));
        double outZ;

        // Gimbal lock: cos(outY) == 0 collapses r00/r10/r21/r22 to the residue of a catastrophic
        // cancellation, so deriving X and Z from them is meaningless. Read the O(1) terms
        // instead, pinning Z at zero and solving for X.
        const double lockEpsilon = 1e-9;
        if (Math.Abs(r20) >= 1.0 - lockEpsilon)
        {
            outX = r20 <= 0 ? Math.Atan2(r01, r02) : Math.Atan2(-r01, -r02);
            outZ = 0.0;
        }
        else
        {
            outX = Math.Atan2(r21, r22);
            outZ = Math.Atan2(r10, r00);
        }

        // Every 'ZYX' triple has a second representative, (x-π, π-y, z+π). Take it when Z is
        // negative: that makes Z non-negative, and gives |y'| > 90°, which is exactly the case
        // where extract(Ry(y')) returns (π, y, π) — so the X, Y and Z contributions sum back to
        // the intended triple modulo 2π.
        //
        // That extract(Ry(y')) → π step reads atan2(m10, cos y') with cos y' < 0, so it depends
        // on m10 being +0 rather than -0: atan2(-0, negative) would return -π, trip Eigen's
        // res[0] < 0 branch, and yield 0 instead of π — rotating the result by Rz(π).
        //
        // It holds unconditionally, and by IEEE rule rather than by luck. Eigen builds the
        // quaternion as vec() = sin(θ/2)·axis, so for UnitY the x and z components are ±0
        // carrying sin(θ/2)'s sign. m10 is then 2·x·y + 2·z·w: the first term's factors BOTH
        // carry sign(sin(θ/2)), so it is +0 for every θ, while the second may be -0 — and
        // round-to-nearest gives (+0) + (-0) = +0. Eigen's direct AngleAxis::toRotationMatrix
        // path reaches +0 the same way, though Model.cpp takes the quaternion path. Verified by
        // execution over both paths and both signs of θ.
        //
        // NormalizeAngle is therefore NOT required for correctness, and nothing here depends on
        // the emitted angle's range: q(θ±2π) = -q(θ) and the matrix is quadratic in the
        // quaternion, so Ry(θ) and Ry(θ±2π) are bit-identical including zero signs. It is kept
        // only so the emitted flags stay legible rather than values like "--rotate-y 270.00".
        if (outZ < 0)
        {
            outX = NormalizeAngle(outX - Math.PI);
            outY = NormalizeAngle(Math.PI - outY);
            outZ += Math.PI;
        }

        return (outX, outY, outZ);
    }

    /// <summary>Wrap an angle into (-π, π].</summary>
    private static double NormalizeAngle(double radians)
    {
        double wrapped = Math.IEEERemainder(radians, 2 * Math.PI);
        return wrapped <= -Math.PI ? wrapped + (2 * Math.PI) : wrapped;
    }

    /// <summary>
    /// Parse model transform JSON from the UI and build OrcaSlicer CLI transform flags.
    /// Input: {"rotation":[rx,ry,rz],"scale":[sx,sy,sz],"position":[px,py,pz]}
    ///   — radians, three.js Euler order 'XYZ', Z-up with the XY bed plane (camera.up = [0,0,1]).
    /// Output: OrcaSlicer flags in degrees. rotation[0]=X, rotation[1]=Y, rotation[2]=Z map to
    /// the same axes, but the angles are re-parameterised — see <see cref="ToOrcaRotation"/>.
    /// Position is deliberately not mapped to a flag; see <see cref="TransformResult"/>.
    /// </summary>
    internal static TransformResult BuildTransformFlags(string? modelTransformJson)
    {
        if (string.IsNullOrWhiteSpace(modelTransformJson))
        {
            return new TransformResult(string.Empty, false, false);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(modelTransformJson);
            JsonElement root = doc.RootElement;
            StringBuilder flags = new();
            bool hasCustomPosition = false;
            bool hasNonUniformScale = false;

            if (root.TryGetProperty("rotation", out JsonElement rotEl) && rotEl.ValueKind == JsonValueKind.Array)
            {
                double[] rot = new double[3];
                int i = 0;
                foreach (JsonElement el in rotEl.EnumerateArray().Where(el => i < 3 && el.ValueKind == JsonValueKind.Number))
                {
                    double v = el.GetDouble();
                    rot[i++] = double.IsFinite(v) ? v : 0;
                }

                const double radToDeg = 180.0 / Math.PI;
                const double epsilon = 0.001;

                // The workspace angles cannot be emitted verbatim: OrcaSlicer sums --rotate*
                // into an Euler triple and rebuilds it as Rz·Ry·Rx ('ZYX'), while the viewer is
                // three.js 'XYZ'. Re-parameterise first. See ToOrcaRotation.
                //
                // Flag ORDER is irrelevant — ModelVolume::rotate adds into separate components
                // and addition commutes — so these are emitted X, Y, Z purely for readability.
                (double orcaX, double orcaY, double orcaZ) = ToOrcaRotation(rot[0], rot[1], rot[2]);

                double rotXDeg = orcaX * radToDeg;
                if (Math.Abs(rotXDeg) > epsilon)
                {
                    flags.Append(CultureInfo.InvariantCulture, $" --rotate-x {rotXDeg:F2}");
                }

                double rotYDeg = orcaY * radToDeg;
                if (Math.Abs(rotYDeg) > epsilon)
                {
                    flags.Append(CultureInfo.InvariantCulture, $" --rotate-y {rotYDeg:F2}");
                }

                double rotZDeg = orcaZ * radToDeg;
                if (Math.Abs(rotZDeg) > epsilon)
                {
                    flags.Append(CultureInfo.InvariantCulture, $" --rotate {rotZDeg:F2}");
                }
            }

            if (root.TryGetProperty("scale", out JsonElement scaleEl) && scaleEl.ValueKind == JsonValueKind.Array)
            {
                double[] scale = new double[3] { 1, 1, 1 };
                int i = 0;
                foreach (JsonElement el in scaleEl.EnumerateArray().Where(el => i < 3 && el.ValueKind == JsonValueKind.Number))
                {
                    double v = el.GetDouble();
                    scale[i++] = double.IsFinite(v) ? v : 1;
                }

                const double epsilon = 0.001;

                // OrcaSlicer 2.4.2's CLI --scale takes a single value (coFloat); per-axis scale
                // ("scale_to_fit", coPoint3) is commented out of CLITransformConfigDef. Report a
                // non-uniform scale so PlanPlacement can route the job through a 3MF project,
                // where ThreeMfProjectBuilder.ComputeLinear bakes sx/sy/sz independently instead
                // of flattening to a single axis (issue #1799).
                hasNonUniformScale = Math.Abs(scale[1] - scale[0]) > epsilon || Math.Abs(scale[2] - scale[0]) > epsilon;

                // Isotropic approximation (first component). Used verbatim when uniform, and as
                // the degraded fallback when PlanPlacement cannot embed a non-uniform scale in a
                // 3MF project (e.g. non-STL inputs or unknown bed centre) — that degradation is
                // logged by the caller via PlacementPlan.NonUniformScaleDropped. 1.0 = no change.
                if (Math.Abs(scale[0] - 1.0) > epsilon)
                {
                    flags.Append(CultureInfo.InvariantCulture, $" --scale {scale[0]:F4}");
                }
            }

            // Workspace is Z-up with XY bed plane — same as OrcaSlicer.
            // position[0]=X (bed), position[1]=Y (bed), position[2]=Z (height).
            //
            // DO NOT add a positional flag here. OrcaSlicer 2.4.2 compiles both `center` and
            // `align_xy` out of CLITransformConfigDef, so passing either is fatal: the CLI
            // answers "Invalid option --center", dumps its usage, and exits 254
            // (CLI_INVALID_PARAMS) without slicing anything (issue #1794).
            //
            // A custom position is reported through HasCustomPosition instead, and embedded in
            // a 3MF project by PlanPlacement — the only placement mechanism this CLI supports.
            // If a future OrcaSlicer restores a positional option, adding it back here is NOT
            // sufficient on its own: placement is coupled to the arrange flag, so PlanPlacement
            // must change in the same commit.
            //
            // Guarded by BuildTransformFlagsTests.BuildTransformFlags_NeverEmitsUnsupportedPositionalFlags
            // and OrcaSlicerArgumentsTests. Those tests failing means the defect is back — they
            // are not stale, so do not "fix" them by relaxing the assertion.
            if (root.TryGetProperty("position", out JsonElement posEl) && posEl.ValueKind == JsonValueKind.Array)
            {
                double[] pos = new double[3];
                int i = 0;
                foreach (JsonElement el in posEl.EnumerateArray().Where(el => i < 3 && el.ValueKind == JsonValueKind.Number))
                {
                    double v = el.GetDouble();
                    pos[i++] = double.IsFinite(v) ? v : 0;
                }

                const double epsilon = 0.001;
                if (Math.Abs(pos[0]) > epsilon || Math.Abs(pos[1]) > epsilon)
                {
                    hasCustomPosition = true;
                }
            }

            return new TransformResult(flags.ToString(), hasCustomPosition, hasNonUniformScale);
        }
        catch (JsonException)
        {
            return new TransformResult(string.Empty, false, false);
        }
    }

    /// <summary>
    /// Converts a Settings dictionary to JSON for OrcaSlicer --load-settings.
    /// All scalars are written as JSON strings. Values that look like JSON arrays
    /// (start with '[') are written as native arrays.
    /// Keys with values that would fail OrcaSlicer's CLI validator are sanitized.
    /// </summary>
    /// <remarks>
    /// The caller's dictionary is never modified. Resolved profiles come from a shared cache and
    /// several jobs can be prepared concurrently, so sanitizing in place would both leak one job's
    /// CLI fix-ups into every later job and write to a <see cref="Dictionary{TKey, TValue}"/> that
    /// another thread may be reading — which is undefined behaviour, not merely stale data.
    /// </remarks>
    /// <param name="settings">The settings bag to serialize.</param>
    /// <returns>The native OrcaSlicer JSON document text.</returns>
    internal static string SettingsDictToNativeJson(Dictionary<string, object>? settings)
    {
        if (settings == null || settings.Count == 0)
        {
            return "{}";
        }

        // OrcaSlicer --load-settings has stricter range checks than the profile format.
        // Clamp known speed/rate fields that use 0="auto" in profiles but require ≥1 in CLI.
        Dictionary<string, object> sanitized = new(settings, StringComparer.Ordinal);
        SanitizeForCli(sanitized);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, object> kvp in sanitized)
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

        foreach (string key in speedKeys.Where(key => settings.TryGetValue(key, out object? val) && val?.ToString() == "0"))
        {
            settings[key] = "1";
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
