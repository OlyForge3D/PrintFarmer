using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            if (job.ModelFileUrls is { Count: > 0 })
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
            string gcodeFilePath = await RunOrcaSlicerAsync(modelFilePaths, jobWorkDir, job, cancellationToken);
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
                Success = true
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
    private static async Task<Dictionary<string, string>> GenerateProfileJsonFilesAsync(
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
        string machineJson = SettingsDictToNativeJson(
            WithSystemPresetInherits(profile.MachineProfile.Settings, profile.MachineProfile.Name));
        string processJson = SettingsDictToNativeJson(profile.ProcessProfile?.Settings);

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

    private async Task<string> RunOrcaSlicerAsync(List<string> modelPaths, string workDir, DistributedSlicingJob job, CancellationToken cancellationToken)
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

            case PlacementStrategy.SourcePlacement:
                _logger.LogWarning(
                    "Job {JobId}: inputs are 3MF, so the workspace layout cannot be re-embedded. " +
                    "Falling back to the placement stored in the source file.",
                    job.Id);
                break;

            default:
                if (placement.PositionDropped)
                {
                    _logger.LogWarning(
                        "Job {JobId}: the requested layout could not be embedded (inputs are not all STL, or the " +
                        "bed centre could not be determined); letting OrcaSlicer auto-arrange instead.",
                        job.Id);
                }

                break;
        }

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

                int totalPercent = root.TryGetProperty("total_percent", out JsonElement tp)
                    ? tp.GetInt32()
                    : -1;
                string message = root.TryGetProperty("message", out JsonElement msg)
                    ? msg.GetString() ?? "Slicing..."
                    : "Slicing...";

                if (root.TryGetProperty("warning", out JsonElement warn))
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
    /// Parsed transform result: CLI flags and whether a custom position was specified.
    /// <see cref="Flags"/> must never contain a positional flag — OrcaSlicer 2.4.2 has none,
    /// and passing one aborts the run before slicing (issue #1794). When
    /// <see cref="HasCustomPosition"/> is true the caller embeds the placement in a 3MF project
    /// instead; see <see cref="PlanPlacement"/>.
    /// </summary>
    internal readonly record struct TransformResult(string Flags, bool HasCustomPosition);

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
    internal readonly record struct PlacementPlan(
        PlacementStrategy Strategy,
        string ArrangeFlag,
        string TransformFlags,
        IReadOnlyList<string?> ModelTransforms,
        bool PositionDropped);

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

        bool anyCustomPosition = transforms.Any(t => BuildTransformFlags(t).HasCustomPosition);
        bool secondaryTransforms = transforms.Skip(1).Any(t => !string.IsNullOrWhiteSpace(t));
        bool needsEmbedding = anyCustomPosition || secondaryTransforms;

        string primaryFlags = transforms.Count > 0
            ? BuildTransformFlags(transforms[0]).Flags
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
            // Rotation and scale are baked into the 3MF matrix, so no CLI transform flags.
            return new PlacementPlan(PlacementStrategy.ThreeMfProject, "--arrange 0", string.Empty, transforms, false);
        }

        if (needsEmbedding && inputsCarryOwnPlacement)
        {
            // A 3MF input already carries its own placement; keep it rather than re-arranging.
            return new PlacementPlan(PlacementStrategy.SourcePlacement, "--arrange 0", primaryFlags, transforms, true);
        }

        // Nothing to place (or nowhere to place it): auto-arrange. Never "--arrange 0" without
        // an embedded placement — that would leave the model at raw mesh coordinates, which
        // lands it off the bed and trips OrcaSlicer's CLI_OBJECTS_PARTLY_INSIDE check. This is
        // also the path for mixed STL+3MF jobs and for OBJ/PLY/STEP, none of which can be
        // placed faithfully.
        return new PlacementPlan(PlacementStrategy.AutoArrange, "--arrange 1", primaryFlags, transforms, needsEmbedding);
    }

    /// <summary>
    /// Rewrite a plan to plain auto-arrange, used when the chosen mechanism turned out to be
    /// unavailable at runtime (for example the 3MF project could not be built). The requested
    /// layout is lost, which <see cref="PlacementPlan.PositionDropped"/> records so the caller
    /// can say so in the log; rotation and scale are recovered as CLI flags.
    /// </summary>
    internal static PlacementPlan DowngradeToAutoArrange(PlacementPlan plan) =>
        plan with
        {
            Strategy = PlacementStrategy.AutoArrange,
            ArrangeFlag = "--arrange 1",
            TransformFlags = plan.ModelTransforms.Count > 0
                ? BuildTransformFlags(plan.ModelTransforms[0]).Flags
                : string.Empty,
            PositionDropped = true,
        };

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

                // Use uniform scale (first component). 1.0 = no change.
                const double epsilon = 0.001;
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
