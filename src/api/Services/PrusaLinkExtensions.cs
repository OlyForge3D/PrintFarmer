namespace Farm.Web.Api.Services;

/// <summary>
/// Extension methods for PrusaLink API client
/// </summary>
public static class PrusaLinkApiExtensions
{
    /// <summary>
    /// Get a list of G-code files from the local storage
    /// </summary>
    public static async Task<string[]> GetGcodeFilesAsync(this PrusaLinkApiClient client, string baseUrl,
        string? apiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        
        try
        {
            var folderInfo = await client.GetFileInfoAsync(baseUrl, "/local", "", apiKey, ct: ct);
            if (folderInfo is FolderInfo folder)
            {
                return [.. folder.Children
                    .Where(f => f.Type == FileTypes.PrintFile && f.Name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Name)];
            }
            return [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Upload a G-code file to the printer's local storage
    /// </summary>
    public static async Task<bool> UploadGcodeAsync(this PrusaLinkApiClient client, string baseUrl,
        string fileName, Stream fileStream, string? apiKey = null, bool startPrintAfterUpload = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(fileName);
        
        // Ensure the file path starts with /
        var filePath = fileName.StartsWith('/') ? fileName : "/" + fileName;

        return await client.UploadFileAsync(baseUrl, "/local", filePath, fileStream, apiKey,
            startPrintAfterUpload, overwrite: true, ct);
    }

    /// <summary>
    /// Start printing a G-code file
    /// </summary>
    public static async Task<bool> StartPrintAsync(this PrusaLinkApiClient client, string baseUrl,
        string fileName, string? apiKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(fileName);
        
        // Ensure the file path starts with /
        var filePath = fileName.StartsWith('/') ? fileName : "/" + fileName;

        return await client.StartPrintAsync(baseUrl, "/local", filePath, apiKey, ct);
    }

    /// <summary>
    /// Get the current print job progress and information
    /// </summary>
    public static async Task<PrintJobProgress?> GetPrintProgressAsync(this PrusaLinkApiClient client,
        string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var job = await client.GetJobAsync(baseUrl, apiKey, ct);
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

    /// <summary>
    /// Get printer temperatures and status
    /// </summary>
    public static async Task<SimplePrinterStatus?> GetPrinterStatusAsync(this PrusaLinkApiClient client,
        string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var status = await client.GetStatusAsync(baseUrl, apiKey, ct);

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

    /// <summary>
    /// Check if the printer is currently printing
    /// </summary>
    public static async Task<bool> IsPrintingAsync(this PrusaLinkApiClient client, string baseUrl,
        string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var status = await client.GetStatusAsync(baseUrl, apiKey, ct);
            return string.Equals(status.Printer.State, PrinterStates.Printing, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pause the current print job
    /// </summary>
    public static async Task<bool> PausePrintAsync(this PrusaLinkApiClient client, string baseUrl,
        string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var job = await client.GetJobAsync(baseUrl, apiKey, ct);
            if (job?.Id != null)
            {
                return await client.PauseJobAsync(baseUrl, job.Id, apiKey, ct);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resume the current print job
    /// </summary>
    public static async Task<bool> ResumePrintAsync(this PrusaLinkApiClient client, string baseUrl,
        string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var job = await client.GetJobAsync(baseUrl, apiKey, ct);
            if (job?.Id != null)
            {
                return await client.ResumeJobAsync(baseUrl, job.Id, apiKey, ct);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Stop the current print job
    /// </summary>
    public static async Task<bool> StopPrintAsync(this PrusaLinkApiClient client, string baseUrl,
        string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var job = await client.GetJobAsync(baseUrl, apiKey, ct);
            if (job?.Id != null)
            {
                return await client.StopJobAsync(baseUrl, job.Id, apiKey, ct);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get printer information and capabilities
    /// </summary>
    public static async Task<PrinterInformation?> GetPrinterInformationAsync(this PrusaLinkApiClient client,
        string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var info = await client.GetInfoAsync(baseUrl, apiKey, ct);
            var version = await client.GetVersionAsync(baseUrl, apiKey, ct);

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
                SupportsUploadByPut = version.Capabilities.ContainsKey("upload-by-put") &&
                                      Convert.ToBoolean(version.Capabilities["upload-by-put"])
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get storage information
    /// </summary>
    public static async Task<StorageInformation[]> GetStorageInformationAsync(this PrusaLinkApiClient client,
        string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            var storageList = await client.GetStorageAsync(baseUrl, apiKey, ct: ct);
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

    public static async Task<string[]> GetGcodeFilesAsync(this PrusaLinkApiClient client, Uri baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Simplified models for common operations
/// </summary>
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

/// <summary>
/// Exception thrown when PrusaLink API returns an error
/// </summary>
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
    public PrusaLinkException()
    {
    }
}

/// <summary>
/// Factory for creating configured PrusaLink API clients
/// </summary>
public static class PrusaLinkApiClientFactory
{
    public static PrusaLinkApiClient Create(HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        var client = httpClient ?? new HttpClient();

        if (timeout.HasValue)
        {
            client.Timeout = timeout.Value;
        }
        else if (client.Timeout == System.Threading.Timeout.InfiniteTimeSpan)
        {
            client.Timeout = TimeSpan.FromSeconds(30); // Default 30s timeout
        }

        return new PrusaLinkApiClient(client, Microsoft.Extensions.Logging.Abstractions.NullLogger<PrusaLinkApiClient>.Instance);
    }
}
