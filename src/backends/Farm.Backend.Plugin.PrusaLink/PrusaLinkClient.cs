#pragma warning disable S1006, CS1998, S1939 // Default parameters, async methods, and explicit interface inheritance are intentional

using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Backend.Plugin.PrusaLink;

#pragma warning disable CS1066 // Default value for optional parameter not enforced for interface members

public class PrusaLinkClient : PrinterClientBase, IPrusaLinkClient,
    ISupportsFileList,
    ISupportsFileUpload,
    ISupportsStartPrint,
    ISupportsCamera,
    ISupportsPrinterInformation,
    ISupportsControlOperations,
    ISupportsMovement,
    ISupportsTemperatureControl
{
    private readonly IPrusaLinkApiClient _apiClient;
    private readonly IUnifiedLoggingService? _logger;

    public PrusaLinkClient(HttpClient http, IUnifiedLoggingService? logger = null)
    {
        _apiClient = new PrusaLinkApiClient(http, logger ?? new NullLoggingService());
        _logger = logger;
    }

    // For testability: allow injection of mock API client
    internal PrusaLinkClient(IPrusaLinkApiClient apiClient, IUnifiedLoggingService? logger = null)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task<PrusaCompositeStatus> GetCompositeStatusAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct = default)
    {
        try
        {
            StatusInfo? status = await _apiClient.GetStatusAsync(baseUrl, credential, ct);
            Job? job = await _apiClient.GetJobAsync(baseUrl, credential, ct);

            // Determine if printer is online:
            // - Check StatusPrinter/StatusConnect if available (newer firmware versions that include these fields)
            // - If those fields don't exist (null), just check that we got a valid status response
            bool isOnline = status != null &&
                           ((status.Printer.StatusPrinter != null && status.Printer.StatusConnect != null &&
                             status.Printer.StatusPrinter.Ok && status.Printer.StatusConnect.Ok) ||
                            (status.Printer.StatusPrinter == null && status.Printer.StatusConnect == null));

            // Extract thumbnail URL from job file refs if available
            string? thumbnailUrl = job?.File?.Refs?.Thumbnail;
            if (!string.IsNullOrEmpty(thumbnailUrl) && !thumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Convert relative thumbnail path to absolute URL
                thumbnailUrl = new Uri(new Uri(baseUrl.TrimEnd('/')), thumbnailUrl).ToString();
            }

            return new PrusaCompositeStatus(
                isOnline,
                status?.Printer?.State,
                job?.Progress,
                job?.File?.Name,
                thumbnailUrl,
                null, // Camera stream URL would need camera configuration
                null,  // Camera snapshot URL would need camera configuration
                status?.Printer?.TempNozzle,
                status?.Printer?.TempBed,
                status?.Printer?.TargetNozzle,
                status?.Printer?.TargetBed,
                status?.Printer?.AxisX,
                status?.Printer?.AxisY,
                status?.Printer?.AxisZ);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get composite status from {BaseUrl}", baseUrl);
            return new PrusaCompositeStatus(false, null, null, null, null, null, null);
        }
    }

    // Analyzer-friendly overloads that accept Uri and delegate to string versions
    public Task<PrusaCompositeStatus> GetCompositeStatusAsync(Uri baseUrl, PrinterCredential? credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetCompositeStatusAsync(baseUrl.ToString().TrimEnd('/'), credential, ct);
    }

    public async Task<PrusaStatus> GetStatusAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct = default)
    {
        try
        {
            StatusInfo? status = await _apiClient.GetStatusAsync(baseUrl, credential, ct);

            // Determine if printer is online:
            // - Check StatusPrinter/StatusConnect if available (newer firmware versions)
            // - If those fields don't exist (null), just check that we got a valid status response
            bool isOnline = status != null &&
                           ((status.Printer.StatusPrinter != null && status.Printer.StatusConnect != null &&
                             status.Printer.StatusPrinter.Ok && status.Printer.StatusConnect.Ok) ||
                            (status.Printer.StatusPrinter == null && status.Printer.StatusConnect == null));

            return new PrusaStatus(isOnline, status?.Printer?.State);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get status from {BaseUrl}", baseUrl);
            return new PrusaStatus(false, null);
        }
    }

    public Task<PrusaStatus> GetStatusAsync(Uri baseUrl, PrinterCredential? credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetStatusAsync(baseUrl.ToString().TrimEnd('/'), credential, ct);
    }

    public async Task<PrusaJob?> GetJobAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct = default)
    {
        try
        {
            Job? job = await _apiClient.GetJobAsync(baseUrl, credential, ct);
            if (job == null)
            {
                return null;
            }

            // Extract thumbnail URL from job file refs if available
            string? thumbnailUrl = job.File?.Refs?.Thumbnail;
            if (!string.IsNullOrEmpty(thumbnailUrl) && !thumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Convert relative thumbnail path to absolute URL
                thumbnailUrl = new Uri(new Uri(baseUrl.TrimEnd('/')), thumbnailUrl).ToString();
            }

            return new PrusaJob(
                job.State,
                job.Progress,
                job.File?.Name,
                thumbnailUrl,
                null, // Camera stream URL would need camera configuration
                null);  // Camera snapshot URL would need camera configuration
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get job from {BaseUrl}", baseUrl);
            return null;
        }
    }

    public Task<PrusaJob?> GetJobAsync(Uri baseUrl, PrinterCredential? credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetJobAsync(baseUrl.ToString().TrimEnd('/'), credential, ct);
    }

    public async Task<PrinterDto> CreatePrinterDtoAsync(
        Printer printer,
        PrusaCompositeStatus status,
        CancellationToken ct = default)
    {
        // Get camera URLs from PrusaLink client methods
        string? cameraSnapshotUrl = await GetCameraSnapshotUrlAsync(printer.ServerUrl, printer.FrontendPort, ct).ConfigureAwait(false);
        string? cameraStreamUrl = await GetCameraStreamUrlAsync(printer.ServerUrl, printer.FrontendPort, ct).ConfigureAwait(false);

        // Construct backend-specific PrinterDto
        return new PrinterDto(
            Id: printer.Id,
            Name: printer.Name,
            Notes: printer.Notes,
            IsOnline: status.IsOnline,
            State: status.State,
            ManufacturerName: printer.Manufacturer?.Name,
            ModelName: printer.Model?.Name,
            Progress: status.Progress,
            JobName: status.JobName,
            ThumbnailUrl: status.ThumbnailUrl,
            CameraStreamUrl: cameraStreamUrl,
            CameraSnapshotUrl: cameraSnapshotUrl,
            Backend: PrinterBackend.PrusaLink,
            ApiKey: printer.ApiKey,
            Username: printer.Username,
            Password: printer.Password,
            OriginalServerUrl: printer.OriginalServerUrl,
            BackendPort: printer.BackendPort,
            FrontendPort: printer.FrontendPort,
            BackendUrl: printer.BackendUrl,
            FrontendUrl: printer.FrontendUrl,
            Location: printer.Location == null ? null : new LocationSummaryDto(printer.Location.Id, printer.Location.Name, printer.Location.Description));
    }

    public Task<string?> GetCameraSnapshotUrlAsync(string baseUrl, int? frontendPort = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return Task.FromResult<string?>(null);
            }

            Uri baseUri = new(baseUrl.TrimEnd('/'));
            int port = frontendPort ?? (baseUri.Scheme == "https" ? 443 : 80);

            UriBuilder builder = new(baseUri)
            {
                Port = port,
                Path = "/webcam/",
                Query = "action=snapshot"
            };

            return Task.FromResult<string?>(builder.Uri.ToString());
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<string?> GetCameraSnapshotUrlAsync(Uri baseUrl, int? frontendPort = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetCameraSnapshotUrlAsync(baseUrl.ToString(), frontendPort, ct);
    }

    public Task<string?> GetCameraStreamUrlAsync(string baseUrl, int? frontendPort = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return Task.FromResult<string?>(null);
            }

            Uri baseUri = new(baseUrl.TrimEnd('/'));
            int port = frontendPort ?? (baseUri.Scheme == "https" ? 443 : 80);

            UriBuilder builder = new(baseUri)
            {
                Port = port,
                Path = "/webcam/",
                Query = "action=stream"
            };

            return Task.FromResult<string?>(builder.Uri.ToString());
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<string?> GetCameraStreamUrlAsync(Uri baseUrl, int? frontendPort = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetCameraStreamUrlAsync(baseUrl.ToString(), frontendPort, ct);
    }

    // File upload and management methods - Using comprehensive API client
    public async Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(fileName);
            ArgumentNullException.ThrowIfNull(fileContent);

            // Build a rooted path using Uri to avoid manual separators
            // Note: Uri(Uri, string) requires the baseUri to be absolute
            string filePath = new Uri(new Uri("http://localhost/"), fileName).LocalPath;
            return await _apiClient.UploadFileAsync(baseUrl, "/local", filePath, fileContent, credential, printAfterUpload: false, overwrite: true, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to upload G-code file {FileName} to {BaseUrl}", fileName, baseUrl);
            return false;
        }
    }

    public Task<bool> UploadGcodeAsync(Uri baseUrl, string fileName, Stream fileContent, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return UploadGcodeAsync(baseUrl.ToString().TrimEnd('/'), fileName, fileContent, credential, ct);
    }

    public async Task<bool> StartPrintAsync(string baseUrl, string fileName, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(fileName);

            // Build a rooted path using Uri to avoid manual separators
            // Note: Uri(Uri, string) requires the baseUri to be absolute
            string filePath = new Uri(new Uri("http://localhost/"), fileName).LocalPath;
            return await _apiClient.StartPrintAsync(baseUrl, "/local", filePath, credential, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start print of {FileName} on {BaseUrl}", fileName, baseUrl);
            return false;
        }
    }

    public Task<bool> StartPrintAsync(Uri baseUrl, string fileName, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return StartPrintAsync(baseUrl.ToString().TrimEnd('/'), fileName, credential, ct);
    }

    public async Task<string[]> GetFileListAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            // Try the v1 API first (more official, supports metadata)
            FileInfoBase folderInfo = await _apiClient.GetFileInfoAsync(baseUrl, "/local", string.Empty, credential, ct: ct);
            if (folderInfo is FolderInfo folder)
            {
                // Return names of non-folder entries; FolderInfo.Children is an array so prefer Length checks
                if (folder.Children != null && folder.Children.Length > 0)
                {
                    // Upstream API encodes file vs folder in the 'Type' property
                    return folder.Children.Where(f => f.Type != FileTypes.Folder).Select(f => f.Name).ToArray();
                }

                return Array.Empty<string>();
            }

            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            // Fallback to legacy /api/files endpoint (OctoPrint compatibility)
            // This endpoint also requires authentication
            _logger?.LogWarning($"Failed to get file list from v1 API, trying legacy endpoint: {ex.Message}");
            try
            {
                List<FileChild> legacyFiles = await _apiClient.GetFilesLegacyAsync(baseUrl, credential, ct);
                return legacyFiles
                    .Where(f => f.Type != "FOLDER" && !string.IsNullOrEmpty(f.Display))
                    .Select(f => f.Display)
                    .ToArray();
            }
            catch (Exception legacyEx)
            {
                _logger?.LogError(legacyEx, "Failed to get file list from legacy endpoint as well");
                return Array.Empty<string>();
            }
        }
    }

    public Task<string[]> GetFileListAsync(Uri baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileListAsync(baseUrl.ToString().TrimEnd('/'), credential, ct);
    }

    /// <summary>
    /// Gets a list of file details including names and paths for metadata retrieval.
    /// Used internally for thumbnail extraction.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="credential">Printer credential for digest authentication.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<List<(string Name, string Path)>> GetFileDetailsListAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        List<(string, string)> result = [];
        try
        {
            // Try the v1 API first (more official, supports metadata)
            FileInfoBase folderInfo = await _apiClient.GetFileInfoAsync(baseUrl, "/local", string.Empty, credential, ct: ct);
            if (folderInfo is FolderInfo folder && folder.Children != null)
            {
                foreach (FileInfoBase child in folder.Children)
                {
                    if (child.Type != FileTypes.Folder)
                    {
                        // For v1 API, use the display name or name, and the name as path
                        string displayName = child.DisplayName ?? child.Name;
                        result.Add((displayName, "/" + Uri.EscapeDataString(child.Name)));
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            // Fallback to legacy /api/files endpoint
            _logger?.LogWarning($"Failed to get file details from v1 API, trying legacy endpoint: {ex.Message}");
            try
            {
                List<FileChild> legacyFiles = await _apiClient.GetFilesLegacyAsync(baseUrl, credential, ct);
                foreach (FileChild file in legacyFiles)
                {
                    if (file.Type != "FOLDER" && !string.IsNullOrEmpty(file.Display) && !string.IsNullOrEmpty(file.Path))
                    {
                        result.Add((file.Display, file.Path));
                    }
                }
            }
            catch (Exception legacyEx)
            {
                _logger?.LogError(legacyEx, "Failed to get file details from legacy endpoint as well");
            }

            return result;
        }
    }

    public Task<List<(string Name, string Path)>> GetFileDetailsListAsync(Uri baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileDetailsListAsync(baseUrl.ToString().TrimEnd('/'), credential, ct);
    }

    /// <summary>
    /// Gets detailed file information including metadata and thumbnail URLs.
    /// Used for retrieving thumbnail information for display.
    /// </summary>
    /// <param name="baseUrl">The base URL of the PrusaLink API.</param>
    /// <param name="storagePath">The storage path (e.g., /local).</param>
    /// <param name="filePath">The path to the file within the storage.</param>
    /// <param name="credential">Printer credential for digest authentication.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<FileInfoBase> GetFileDetailsAsync(string baseUrl, string storagePath, string filePath, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        return await _apiClient.GetFileInfoAsync(baseUrl, storagePath, filePath, credential, ct: ct);
    }

    public Task<FileInfoBase> GetFileDetailsAsync(Uri baseUrl, string storagePath, string filePath, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileDetailsAsync(baseUrl.ToString().TrimEnd('/'), storagePath, filePath, credential, ct);
    }

    // Convenience helpers previously provided as extensions
    public async Task<PrintJobProgress?> GetPrintProgressAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            Job? job = await _apiClient.GetJobAsync(baseUrl, credential, ct);
            return job == null
                ? null
                : new PrintJobProgress
                {
                    JobId = job.Id,
                    State = job.State,
                    Progress = job.Progress,
                    TimePrinting = job.TimePrinting,
                    TimeRemaining = job.TimeRemaining,
                    FileName = job.File?.DisplayName ?? job.File?.Name,
                    InaccurateEstimates = job.InaccurateEstimates
                };
        }
        catch
        {
            return null;
        }
    }

    public async Task<SimplePrinterStatus?> GetPrinterStatusAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            StatusInfo status = await _apiClient.GetStatusAsync(baseUrl, credential, ct);

            return new SimplePrinterStatus
            {
                State = status.Printer.State,
                IsOnline = !string.Equals(status.Printer.State, PrinterStates.Error, StringComparison.OrdinalIgnoreCase),
                NozzleTemp = status.Printer.TempNozzle,
                NozzleTarget = status.Printer.TargetNozzle,
                BedTemp = status.Printer.TempBed,
                BedTarget = status.Printer.TargetBed,
                AxisX = status.Printer.AxisX,
                AxisY = status.Printer.AxisY,
                AxisZ = status.Printer.AxisZ,
                FanSpeed = status.Printer.FanPrint,
                FlowRate = status.Printer.Flow,
                SpeedMultiplier = status.Printer.Speed
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> IsPrintingAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            StatusInfo status = await _apiClient.GetStatusAsync(baseUrl, credential, ct);
            return string.Equals(status.Printer.State, PrinterStates.Printing, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PausePrintAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            Job? job = await _apiClient.GetJobAsync(baseUrl, credential, ct);
            return job?.Id != null ? await _apiClient.PauseJobAsync(baseUrl, job.Id, credential, ct) : false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ResumePrintAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            Job? job = await _apiClient.GetJobAsync(baseUrl, credential, ct);
            return job?.Id != null ? await _apiClient.ResumeJobAsync(baseUrl, job.Id, credential, ct) : false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StopPrintAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            Job? job = await _apiClient.GetJobAsync(baseUrl, credential, ct);
            return job?.Id != null ? await _apiClient.StopJobAsync(baseUrl, job.Id, credential, ct) : false;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> StopPrintAsync(Uri baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return StopPrintAsync(baseUrl.ToString(), credential, ct);
    }

    public async Task<PrinterInformation?> GetPrinterInformationAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            PrinterInfo printerInfo = await _apiClient.GetInfoAsync(baseUrl, credential, ct);
            VersionInfo versionInfo = await _apiClient.GetVersionAsync(baseUrl, credential, ct);

            return new PrinterInformation
            {
                Name = printerInfo.Name,
                Location = printerInfo.Location,
                Serial = printerInfo.Serial,
                Hostname = printerInfo.Hostname,
                FirmwareVersion = versionInfo.Firmware,
                PrusaLinkVersion = versionInfo.Version,
                ApiVersion = versionInfo.Api,
                NozzleDiameter = printerInfo.NozzleDiameter,
                MinExtrusionTemp = printerInfo.MinExtrusionTemp,
                HasMmu = printerInfo.Mmu,
                SdCardReady = printerInfo.SdReady,
                HasActiveCamera = printerInfo.ActiveCamera,
                SupportsUploadByPut = versionInfo.Capabilities.TryGetValue("upload-by-put", out object? flagObj)
                    && bool.TryParse(Convert.ToString(flagObj, System.Globalization.CultureInfo.InvariantCulture), out bool flag)
                    && flag
            };
        }
        catch
        {
            return null;
        }
    }

    public Task<PrinterInformation?> GetPrinterInformationAsync(Uri baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetPrinterInformationAsync(baseUrl.ToString().TrimEnd('/'), credential, ct);
    }

    public async Task<StorageInformation[]> GetStorageInformationAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            StorageListResponse storageList = await _apiClient.GetStorageAsync(baseUrl, credential, ct: ct);
            return storageList.StorageList.Select(s => new StorageInformation
            {
                Name = s.Name,
                Type = s.Type,
                Path = s.Path,
                Available = s.Available,
                ReadOnly = s.ReadOnly,
                FreeSpace = s.FreeSpace,
                TotalSpace = s.TotalSpace,
                PrintFileSize = s.PrintFiles,
                SystemFileSize = s.SystemFiles
            }).ToArray();
        }
        catch
        {
            return Array.Empty<StorageInformation>();
        }
    }

    /// <summary>
    /// Access the underlying API client for advanced operations
    /// </summary>
    public IPrusaLinkApiClient ApiClient => _apiClient;

    // ========== CAPABILITY INTERFACE IMPLEMENTATIONS ==========
    async Task<List<PrinterFileInfo>> ISupportsFileList.GetFileListAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        // Use the new method that extracts file metadata including size
        return await GetFileListWithMetadataAsync(baseUrl, credential, ct);
    }

    private async Task<List<PrinterFileInfo>> GetFileListWithMetadataAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            // Try the v1 API first (more official, supports metadata)
            FileInfoBase folderInfo = await _apiClient.GetFileInfoAsync(baseUrl, "/local", string.Empty, credential, ct: ct);
            if (folderInfo is FolderInfo folder)
            {
                // Return names of non-folder entries with size information
                if (folder.Children != null && folder.Children.Length > 0)
                {
                    // Upstream API encodes file vs folder in the 'Type' property
                    return folder.Children
                        .Where(f => f.Type != FileTypes.Folder)
                        .Select(f => new PrinterFileInfo
                        {
                            Name = f.Name,
                            Path = f.Name,
                            Size = f.Size,
                            Modified = f.MTimestamp > 0 ? f.MTimestamp : null,
                            ThumbnailUrl = null // PrusaLink doesn't expose thumbnail URLs yet
                        })
                        .ToList();
                }

                return [];
            }

            return [];
        }
        catch (Exception ex)
        {
            // Fallback to legacy /api/files endpoint (OctoPrint compatibility)
            _logger?.LogWarning($"Failed to get file list from v1 API, trying legacy endpoint: {ex.Message}");
            try
            {
                List<FileChild> legacyFiles = await _apiClient.GetFilesLegacyAsync(baseUrl, credential, ct);
                return legacyFiles
                    .Where(f => f.Type != "FOLDER" && !string.IsNullOrEmpty(f.Display))
                    .Select(f => new PrinterFileInfo
                    {
                        Name = f.Display,
                        Path = f.Display,

                        // Legacy API (FileChild model) doesn't provide size, modified timestamp, or thumbnails
                        Size = null,
                        Modified = null,
                        ThumbnailUrl = null
                    })
                    .ToList();
            }
            catch (Exception legacyEx)
            {
                _logger?.LogError(legacyEx, "Failed to get file list from legacy endpoint as well");
                return [];
            }
        }
    }

    async Task<bool> ISupportsFileUpload.UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, PrinterCredential? credential = null, CancellationToken ct = default)
        => await UploadGcodeAsync(baseUrl, fileName, fileContent, credential, ct);

    async Task<bool> ISupportsStartPrint.StartPrintAsync(string baseUrl, string fileName, PrinterCredential? credential = null, CancellationToken ct = default)
        => await StartPrintAsync(baseUrl, fileName, credential, ct);

    async Task<string?> ISupportsCamera.GetCameraStreamUrlAsync(string baseUrl, int? frontendPort = null, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            // PrusaLink doesn't expose camera URLs directly through main API
            // Would need custom implementation if camera is configured
            return null;
        }
        catch
        {
            return null;
        }
    }

    async Task<string?> ISupportsCamera.GetCameraSnapshotUrlAsync(string baseUrl, int? frontendPort = null, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            // PrusaLink doesn't expose camera URLs directly through main API
            return null;
        }
        catch
        {
            return null;
        }
    }

    async Task<StandardPrinterInfo> ISupportsPrinterInformation.GetPrinterInformationAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        try
        {
            PrinterInformation? info = await GetPrinterInformationAsync(baseUrl, credential, ct);
            return new StandardPrinterInfo
            {
                Name = info?.Name ?? "Unknown",
                Firmware = info?.FirmwareVersion ?? "Unknown",
                Model = "Prusa MK" // PrusaLink doesn't expose model info directly
            };
        }
        catch
        {
            return new StandardPrinterInfo { Name = "Unknown", Firmware = "Unknown", Model = "Unknown" };
        }
    }

    /// <summary>
    /// ISupportsControlOperations implementations - pause, resume, and cancel operations.
    /// PrusaLink supports job cancel via StopPrintAsync.
    /// Pause/Resume require HTTP Digest Auth via legacy /api/job endpoint.
    /// Credential should contain Username and Password for digest auth operations.
    /// </summary>
    public async Task<bool> PauseAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        if (credential?.HasDigestAuth != true)
        {
            _logger?.LogWarning($"[PrusaLink] Pause requires digest auth credentials (format: username:password) at {baseUrl}");
            return false;
        }

        return await _apiClient.PausePrintLegacyAsync(baseUrl, credential, ct);
    }

    public async Task<bool> ResumeAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        if (credential?.HasDigestAuth != true)
        {
            _logger?.LogWarning($"[PrusaLink] Resume requires digest auth credentials (format: username:password) at {baseUrl}");
            return false;
        }

        return await _apiClient.ResumePrintLegacyAsync(baseUrl, credential, ct);
    }

    public async Task<bool> CancelAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
        => await StopPrintAsync(baseUrl, credential, ct);

    /// <summary>
    /// ISupportsMovement implementations - home and jog operations.
    /// These require HTTP Digest Auth via legacy /api/printer/printhead endpoint.
    /// Credential should contain Username and Password for digest auth operations.
    /// </summary>
    public async Task<bool> HomeAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        if (credential?.HasDigestAuth != true)
        {
            _logger?.LogWarning($"[PrusaLink] Home requires digest auth credentials at {baseUrl}");
            return false;
        }

        return await _apiClient.HomePrintHeadLegacyAsync(baseUrl, homeX: true, homeY: true, homeZ: true, credential, ct);
    }

    public async Task<bool> SendHomeAsync(string baseUrl, CancellationToken ct = default)
    {
        // Without credentials, we can't perform the operation
        _logger?.LogWarning($"[PrusaLink] SendHome requires digest auth credentials at {baseUrl}");
        return false;
    }

    public async Task<bool> HomeXYAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        // Use PrinterCredential directly
        if (credential?.HasDigestAuth != true)
        {
            _logger?.LogWarning($"[PrusaLink] HomeXY requires digest auth credentials at {baseUrl}");
            return false;
        }

        return await _apiClient.HomePrintHeadLegacyAsync(baseUrl, homeX: true, homeY: true, homeZ: false, credential, ct);
    }

    public async Task<bool> HomeZAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        // Use PrinterCredential directly
        if (credential?.HasDigestAuth != true)
        {
            _logger?.LogWarning($"[PrusaLink] HomeZ requires digest auth credentials at {baseUrl}");
            return false;
        }

        return await _apiClient.HomePrintHeadLegacyAsync(baseUrl, homeX: false, homeY: false, homeZ: true, credential, ct);
    }

    public async Task<bool> MoveAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        // Use PrinterCredential directly
        if (credential?.HasDigestAuth != true)
        {
            _logger?.LogWarning($"[PrusaLink] Move requires digest auth credentials at {baseUrl}");
            return false;
        }

        return await _apiClient.JogPrintHeadLegacyAsync(baseUrl, x, y, z, f, credential, ct);
    }

    public async Task<bool> MoveToAsync(string baseUrl, double? x = null, double? y = null, double? z = null, double? f = null, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        // PrusaLink legacy API only supports relative jog, not absolute positioning
        _logger?.LogWarning($"[PrusaLink] MoveToAsync (absolute positioning) not supported via legacy API at {baseUrl}");
        return false;
    }

    /// <summary>
    /// ISupportsTemperatureControl implementation - set hotend and bed temperatures.
    /// Requires HTTP Digest Auth via legacy /api/printer/tool and /api/printer/bed endpoints.
    /// Credential should contain Username and Password for digest auth operations.
    /// </summary>
    public async Task<bool> SetTemperaturesAsync(string baseUrl, double? hotendTemp = null, double? bedTemp = null, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        // Use PrinterCredential directly
        if (credential?.HasDigestAuth != true)
        {
            _logger?.LogWarning($"[PrusaLink] SetTemperatures requires digest auth credentials at {baseUrl}");
            return false;
        }

        bool success = true;

        // Set hotend temperature if specified
        if (hotendTemp.HasValue)
        {
            bool toolResult = await _apiClient.SetToolTemperatureLegacyAsync(baseUrl, hotendTemp.Value, credential, toolIndex: 0, ct);
            if (!toolResult)
            {
                _logger?.LogWarning($"[PrusaLink] Failed to set hotend temperature to {hotendTemp.Value}°C at {baseUrl}");
                success = false;
            }
        }

        // Set bed temperature if specified
        if (bedTemp.HasValue)
        {
            bool bedResult = await _apiClient.SetBedTemperatureLegacyAsync(baseUrl, bedTemp.Value, credential, ct);
            if (!bedResult)
            {
                _logger?.LogWarning($"[PrusaLink] Failed to set bed temperature to {bedTemp.Value}°C at {baseUrl}");
                success = false;
            }
        }

        return success;
    }
}

#pragma warning restore CS1066
