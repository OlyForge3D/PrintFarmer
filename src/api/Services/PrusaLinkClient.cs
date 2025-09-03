using System.Diagnostics.CodeAnalysis;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services;

// Simple adapter to convert ILogger<T> to ILogger<U>
internal class LoggerAdapter<T>(ILogger logger) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => logger.Log(logLevel, eventId, state, exception, formatter);
}

public class PrusaLinkClient(HttpClient http, ILogger<PrusaLinkClient>? logger = null) : PrinterClientBase, IPrusaLinkClient
{
    private readonly PrusaLinkApiClient _apiClient = new(http, logger != null
        ? new LoggerAdapter<PrusaLinkApiClient>(logger)
        : Microsoft.Extensions.Logging.Abstractions.NullLogger<PrusaLinkApiClient>.Instance);
    private readonly ILogger? _logger = logger;

    public async Task<PrusaCompositeStatus> GetCompositeStatusAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        try
        {
            var status = await _apiClient.GetStatusAsync(baseUrl, apiKey, ct);
            var job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);

            return new PrusaCompositeStatus(
                status?.Printer != null,
                status?.Printer?.State,
                job?.Progress,
                job?.File?.Name,
                null, // Thumbnail handling would need additional endpoint
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
            var status = await _apiClient.GetStatusAsync(baseUrl, apiKey, ct);
            return new PrusaStatus(status?.Printer != null, status?.Printer?.State);
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
            var job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);
            if (job == null)
            {
                return null;
            }

            return new PrusaJob(
                job.State,
                job.Progress,
                job.File?.Name,
                null, // Thumbnail handling would need additional logic
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

    // File upload and management methods - Using comprehensive API client
    public async Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(fileName);
            ArgumentNullException.ThrowIfNull(fileContent);

            // Build a rooted path using Uri to avoid manual separators
            var filePath = new Uri(new Uri("/", UriKind.RelativeOrAbsolute), fileName).ToString();
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
            var filePath = new Uri(new Uri("/", UriKind.RelativeOrAbsolute), fileName).ToString();
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
            var folderInfo = await _apiClient.GetFileInfoAsync(baseUrl, "/local", "", apiKey, ct: ct);
            if (folderInfo is FolderInfo folder)
            {
                return [.. folder.Children
                    .Where(f => f.Type == FileTypes.PrintFile && f.Name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Name)];
            }
            return [];
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get file list from {BaseUrl}", baseUrl);
            return [];
        }
    }

    public Task<string[]> GetFileListAsync(Uri baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return GetFileListAsync(baseUrl.ToString().TrimEnd('/'), apiKey, ct);
    }

    // Convenience helpers previously provided as extensions
    public async Task<PrintJobProgress?> GetPrintProgressAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);
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
            var status = await _apiClient.GetStatusAsync(baseUrl, apiKey, ct);

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
            var status = await _apiClient.GetStatusAsync(baseUrl, apiKey, ct);
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
            var job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);
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
            var job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);
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
            var job = await _apiClient.GetJobAsync(baseUrl, apiKey, ct);
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
            var info = await _apiClient.GetInfoAsync(baseUrl, apiKey, ct);
            var version = await _apiClient.GetVersionAsync(baseUrl, apiKey, ct);

            return new PrinterInformation
            {
                Name = info.Name,
                Location = info.Location,
                Serial = info.Serial,
                Hostname = info.Hostname,
                FirmwareVersion = version.Firmware,
                PrusaLinkVersion = version.Version,
                ApiVersion = version.Api,
                NozzleDiameter = info.NozzleDiameter,
                MinExtrusionTemp = info.MinExtrusionTemp,
                HasMmu = info.Mmu,
                SdCardReady = info.SdReady,
                HasActiveCamera = info.ActiveCamera,
                SupportsUploadByPut = version.Capabilities.TryGetValue("upload-by-put", out var flagObj)
                    && bool.TryParse(Convert.ToString(flagObj, System.Globalization.CultureInfo.InvariantCulture), out var flag)
                    && flag
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<StorageInformation[]> GetStorageInformationAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var storageList = await _apiClient.GetStorageAsync(baseUrl, apiKey, ct: ct);
            return [.. storageList.StorageList.Select(s => new StorageInformation
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
            })];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Access the underlying comprehensive API client for advanced operations
    /// </summary>
    public PrusaLinkApiClient ApiClient => _apiClient;
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
