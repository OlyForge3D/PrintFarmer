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
    ISupportsPrinterInformation
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

    public async Task<PrusaCompositeStatus> GetCompositeStatusAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        try
        {
            StatusInfo? status = await _apiClient.GetStatusAsync(baseUrl, apiKey, ct);
            Job? job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);

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
                null  // Camera snapshot URL would need camera configuration
            );
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get composite status from {BaseUrl}", baseUrl);
            return new PrusaCompositeStatus(false, null, null, null, null, null, null);
        }
    }

    // Analyzer-friendly overloads that accept Uri and delegate to string versions
    public Task<PrusaCompositeStatus> GetCompositeStatusAsync(Uri baseUrl, string? apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetCompositeStatusAsync(baseUrl.ToString().TrimEnd('/'), apiKey, ct);
    }

    public async Task<PrusaStatus> GetStatusAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        try
        {
            StatusInfo? status = await _apiClient.GetStatusAsync(baseUrl, apiKey, ct);

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

    public Task<PrusaStatus> GetStatusAsync(Uri baseUrl, string? apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetStatusAsync(baseUrl.ToString().TrimEnd('/'), apiKey, ct);
    }

    public async Task<PrusaJob?> GetJobAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        try
        {
            Job? job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);
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
                null  // Camera snapshot URL would need camera configuration
            );
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get job from {BaseUrl}", baseUrl);
            return null;
        }
    }

    public Task<PrusaJob?> GetJobAsync(Uri baseUrl, string? apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetJobAsync(baseUrl.ToString().TrimEnd('/'), apiKey, ct);
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
            OriginalServerUrl: printer.OriginalServerUrl,
            IpAddress: printer.IpAddress,
            BackendPort: printer.BackendPort,
            FrontendPort: printer.FrontendPort,
            BackendUrl: printer.BackendUrl,
            FrontendUrl: printer.FrontendUrl
        );
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
    public async Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(fileName);
            ArgumentNullException.ThrowIfNull(fileContent);

            // Build a rooted path using Uri to avoid manual separators
            // Note: Uri(Uri, string) requires the baseUri to be absolute
            string filePath = new Uri(new Uri("http://localhost/"), fileName).LocalPath;
            return await _apiClient.UploadFileAsync(baseUrl, "/local", filePath, fileContent, apiKey, printAfterUpload: false, overwrite: true, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to upload G-code file {FileName} to {BaseUrl}", fileName, baseUrl);
            return false;
        }
    }

    public Task<bool> UploadGcodeAsync(Uri baseUrl, string fileName, Stream fileContent, string? apiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return UploadGcodeAsync(baseUrl.ToString().TrimEnd('/'), fileName, fileContent, apiKey, ct);
    }

    public async Task<bool> StartPrintAsync(string baseUrl, string fileName, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(fileName);

            // Build a rooted path using Uri to avoid manual separators
            // Note: Uri(Uri, string) requires the baseUri to be absolute
            string filePath = new Uri(new Uri("http://localhost/"), fileName).LocalPath;
            return await _apiClient.StartPrintAsync(baseUrl, "/local", filePath, apiKey, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start print of {FileName} on {BaseUrl}", fileName, baseUrl);
            return false;
        }
    }

    public Task<bool> StartPrintAsync(Uri baseUrl, string fileName, string? apiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return StartPrintAsync(baseUrl.ToString().TrimEnd('/'), fileName, apiKey, ct);
    }

    public async Task<string[]> GetFileListAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            // Try the v1 API first (more official, supports metadata)
            FileInfoBase folderInfo = await _apiClient.GetFileInfoAsync(baseUrl, "/local", "", apiKey, ct: ct);
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
            // This endpoint also requires API key authentication
            _logger?.LogWarning($"Failed to get file list from v1 API, trying legacy endpoint: {ex.Message}");
            try
            {
                List<FileChild> legacyFiles = await _apiClient.GetFilesLegacyAsync(baseUrl, apiKey, ct);
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

    public Task<string[]> GetFileListAsync(Uri baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileListAsync(baseUrl.ToString().TrimEnd('/'), apiKey, ct);
    }

    /// <summary>
    /// Gets a list of file details including names and paths for metadata retrieval.
    /// Used internally for thumbnail extraction.
    /// </summary>
    public async Task<List<(string Name, string Path)>> GetFileDetailsListAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        List<(string, string)> result = [];
        try
        {
            // Try the v1 API first (more official, supports metadata)
            FileInfoBase folderInfo = await _apiClient.GetFileInfoAsync(baseUrl, "/local", "", apiKey, ct: ct);
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
                List<FileChild> legacyFiles = await _apiClient.GetFilesLegacyAsync(baseUrl, apiKey, ct);
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

    public Task<List<(string Name, string Path)>> GetFileDetailsListAsync(Uri baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileDetailsListAsync(baseUrl.ToString().TrimEnd('/'), apiKey, ct);
    }

    /// <summary>
    /// Gets detailed file information including metadata and thumbnail URLs.
    /// Used for retrieving thumbnail information for display.
    /// </summary>
    public async Task<FileInfoBase> GetFileDetailsAsync(string baseUrl, string storagePath, string filePath, string? apiKey = null, CancellationToken ct = default)
    {
        return await _apiClient.GetFileInfoAsync(baseUrl, storagePath, filePath, apiKey, ct: ct);
    }

    public Task<FileInfoBase> GetFileDetailsAsync(Uri baseUrl, string storagePath, string filePath, string? apiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileDetailsAsync(baseUrl.ToString().TrimEnd('/'), storagePath, filePath, apiKey, ct);
    }

    // Convenience helpers previously provided as extensions
    public async Task<PrintJobProgress?> GetPrintProgressAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            Job? job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);
            if (job == null)
            {
                return null;
            }

            return new PrintJobProgress
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

    public async Task<SimplePrinterStatus?> GetPrinterStatusAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            StatusInfo status = await _apiClient.GetStatusAsync(baseUrl, apiKey, ct);

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

    public async Task<bool> IsPrintingAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            StatusInfo status = await _apiClient.GetStatusAsync(baseUrl, apiKey, ct);
            return string.Equals(status.Printer.State, PrinterStates.Printing, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PausePrintAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            Job? job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);
            if (job?.Id != null)
            {
                return await _apiClient.PauseJobAsync(baseUrl, job.Id, apiKey, ct);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ResumePrintAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            Job? job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);
            if (job?.Id != null)
            {
                return await _apiClient.ResumeJobAsync(baseUrl, job.Id, apiKey, ct);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StopPrintAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            Job? job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);
            if (job?.Id != null)
            {
                return await _apiClient.StopJobAsync(baseUrl, job.Id, apiKey, ct);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<PrinterInformation?> GetPrinterInformationAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var printerInfo = await _apiClient.GetInfoAsync(baseUrl, apiKey, ct);
            var versionInfo = await _apiClient.GetVersionAsync(baseUrl, apiKey, ct);

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

    public Task<PrinterInformation?> GetPrinterInformationAsync(Uri baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetPrinterInformationAsync(baseUrl.ToString().TrimEnd('/'), apiKey, ct);
    }

    public async Task<StorageInformation[]> GetStorageInformationAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            StorageListResponse storageList = await _apiClient.GetStorageAsync(baseUrl, apiKey, ct: ct);
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

    async Task<List<PrinterFileInfo>> ISupportsFileList.GetFileListAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        // Use the new method that extracts file metadata including size
        return await GetFileListWithMetadataAsync(baseUrl, apiKey, ct);
    }

    private async Task<List<PrinterFileInfo>> GetFileListWithMetadataAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            // Try the v1 API first (more official, supports metadata)
            FileInfoBase folderInfo = await _apiClient.GetFileInfoAsync(baseUrl, "/local", "", apiKey, ct: ct);
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

                return new List<PrinterFileInfo>();
            }
            return new List<PrinterFileInfo>();
        }
        catch (Exception ex)
        {
            // Fallback to legacy /api/files endpoint (OctoPrint compatibility)
            _logger?.LogWarning($"Failed to get file list from v1 API, trying legacy endpoint: {ex.Message}");
            try
            {
                List<FileChild> legacyFiles = await _apiClient.GetFilesLegacyAsync(baseUrl, apiKey, ct);
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
                return new List<PrinterFileInfo>();
            }
        }
    }

    async Task<bool> ISupportsFileUpload.UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, string? apiKey = null, CancellationToken ct = default)
        => await UploadGcodeAsync(baseUrl, fileName, fileContent, apiKey, ct);

    async Task<bool> ISupportsStartPrint.StartPrintAsync(string baseUrl, string fileName, string? apiKey = null, CancellationToken ct = default)
        => await StartPrintAsync(baseUrl, fileName, apiKey, ct);

    async Task<string?> ISupportsCamera.GetCameraStreamUrlAsync(string baseUrl, int? frontendPort = null, string? apiKey = null, CancellationToken ct = default)
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

    async Task<string?> ISupportsCamera.GetCameraSnapshotUrlAsync(string baseUrl, int? frontendPort = null, string? apiKey = null, CancellationToken ct = default)
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

    async Task<StandardPrinterInfo> ISupportsPrinterInformation.GetPrinterInformationAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var info = await GetPrinterInformationAsync(baseUrl, apiKey, ct);
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
}

#pragma warning disable CA1056 // URI-like properties should not be strings (transport records)
public record PrusaStatus(bool IsOnline, string? State);
public record PrusaJob(
    string? PrintState,
    double? Progress,
    string? JobName,
    [property: SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Transport model for JSON/UI; keep string and provide Uri accessors in shared DTOs")] string? ThumbnailUrl,
    [property: SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Transport model for JSON/UI; keep string and provide Uri accessors in shared DTOs")] string? CameraStreamUrl,
    [property: SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Transport model for JSON/UI; keep string and provide Uri accessors in shared DTOs")] string? CameraSnapshotUrl
);
public record PrusaCompositeStatus(
    bool IsOnline,
    string? State,
    double? Progress,
    string? JobName,
    [property: SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Transport model for JSON/UI; keep string and provide Uri accessors in shared DTOs")] string? ThumbnailUrl,
    [property: SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Transport model for JSON/UI; keep string and provide Uri accessors in shared DTOs")] string? CameraStreamUrl,
    [property: SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Transport model for JSON/UI; keep string and provide Uri accessors in shared DTOs")] string? CameraSnapshotUrl
);
#pragma warning restore CA1056

// Simplified models and exception previously in extensions
public class PrintJobProgress
{
    public int JobId { get; set; }
    public string State { get; set; } = string.Empty;
    public double Progress { get; set; }
    public int TimePrinting { get; set; }
    public int? TimeRemaining { get; set; }
    public string? FileName { get; set; }
    public bool InaccurateEstimates { get; set; }

    public bool IsActive => State is JobStates.Printing or JobStates.Paused;
    public bool IsFinished => State is JobStates.Finished or JobStates.Stopped;
    public bool HasError => State == JobStates.Error;

    public TimeSpan PrintingTime => TimeSpan.FromSeconds(TimePrinting);
    public TimeSpan? RemainingTime => TimeRemaining.HasValue ? TimeSpan.FromSeconds(TimeRemaining.Value) : null;
}

public class SimplePrinterStatus
{
    public string State { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public double? NozzleTemp { get; set; }
    public double? NozzleTarget { get; set; }
    public double? BedTemp { get; set; }
    public double? BedTarget { get; set; }
    public double? AxisX { get; set; }
    public double? AxisY { get; set; }
    public double? AxisZ { get; set; }
    public int? FanSpeed { get; set; }
    public int? FlowRate { get; set; }
    public int? SpeedMultiplier { get; set; }

    public bool IsPrinting => State == PrinterStates.Printing;
    public bool IsPaused => State == PrinterStates.Paused;
    public bool IsIdle => State == PrinterStates.Idle;
    public bool HasError => State == PrinterStates.Error;
    public bool NeedsAttention => State == PrinterStates.Attention;
}

public class PrinterInformation
{
    public string Name { get; set; } = string.Empty;
    public string? Location { get; set; }
    public string Serial { get; set; } = string.Empty;
    public string? Hostname { get; set; }
    public string FirmwareVersion { get; set; } = string.Empty;
    public string PrusaLinkVersion { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public double NozzleDiameter { get; set; }
    public int MinExtrusionTemp { get; set; }
    public bool HasMmu { get; set; }
    public bool SdCardReady { get; set; }
    public bool HasActiveCamera { get; set; }
    public bool SupportsUploadByPut { get; set; }
}

public class StorageInformation
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool Available { get; set; }
    public bool ReadOnly { get; set; }
    public long? FreeSpace { get; set; }
    public long? TotalSpace { get; set; }
    public long? PrintFileSize { get; set; }
    public long? SystemFileSize { get; set; }

    public double? UsagePercentage => TotalSpace.HasValue && TotalSpace > 0
        ? (double)(TotalSpace.Value - (FreeSpace ?? 0)) / TotalSpace.Value * 100
        : null;
}

public class PrusaLinkException : Exception
{
    public PrusaLinkError? ErrorDetails { get; }
    public int StatusCode { get; }

    public PrusaLinkException(string message) : base(message) { }
    public PrusaLinkException(string message, Exception innerException) : base(message, innerException) { }
    public PrusaLinkException(string message, int statusCode, PrusaLinkError? errorDetails = null) : base(message)
    {
        StatusCode = statusCode;
        ErrorDetails = errorDetails;
    }
    public PrusaLinkException() { }
}

#pragma warning restore CS1066
