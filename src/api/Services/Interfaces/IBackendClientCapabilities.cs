namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Capability marker interface for backend clients that support file download functionality.
/// </summary>
public interface ISupportsFileDownload
{
    Task<byte[]?> DownloadFileAsync(string baseUrl, string filePath, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support file list retrieval.
/// </summary>
public interface ISupportsFileList
{
    Task<List<PrinterFileInfo>> GetFileListAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support file upload functionality.
/// </summary>
public interface ISupportsFileUpload
{
    Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, string? apiKey = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support starting print jobs.
/// </summary>
public interface ISupportsStartPrint
{
    Task<bool> StartPrintAsync(string baseUrl, string fileName, string? apiKey = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support printer control operations.
/// </summary>
public interface ISupportsControlOperations
{
    Task<bool> PauseAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
    Task<bool> ResumeAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
    Task<bool> CancelAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support camera operations.
/// </summary>
public interface ISupportsCamera
{
    Task<string?> GetCameraStreamUrlAsync(string baseUrl, int? frontendPort = null, string? apiKey = null, CancellationToken ct = default);
    Task<string?> GetCameraSnapshotUrlAsync(string baseUrl, int? frontendPort = null, string? apiKey = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support file metadata extraction.
/// </summary>
public interface ISupportsFileMetadata
{
    Task<PrinterFileMetadata?> GetFileMetadataAsync(string baseUrl, string filePath, string? apiKey = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support printer movement/positioning operations.
/// </summary>
public interface ISupportsMovement
{
    Task<bool> HomeAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
    Task<bool> MoveAsync(string baseUrl, double x, double y, double z, string? apiKey = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support temperature control.
/// </summary>
public interface ISupportsTemperatureControl
{
    Task<bool> SetTemperaturesAsync(string baseUrl, double? hotendTemp = null, double? bedTemp = null, string? apiKey = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support advanced printer information retrieval.
/// </summary>
public interface ISupportsPrinterInformation
{
    Task<StandardPrinterInfo> GetPrinterInformationAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
}

/// <summary>
/// Capability marker interface for backend clients that support job history retrieval.
/// </summary>
public interface ISupportsHistory
{
    Task<HistoryListResponse?> GetHistoryListAsync(string baseUrl, int? limit = null, int? start = null, string? apiKey = null, CancellationToken ct = default);
    Task<HistoryJob?> GetHistoryJobAsync(string baseUrl, string jobId, string? apiKey = null, CancellationToken ct = default);
    Task<HistoryTotals?> GetHistoryTotalsAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default);
    Task<bool> DeleteHistoryJobAsync(string baseUrl, string jobId, string? apiKey = null, CancellationToken ct = default);
}

// ========== STANDARDIZED DATA TYPES FOR CAPABILITY INTERFACES ==========
// These types normalize responses from different printer backends into common structures

/// <summary>
/// Standardized printer file information across all backend implementations.
/// </summary>
public class PrinterFileInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long? Size { get; set; }
    public DateTime? Modified { get; set; }
}

/// <summary>
/// Standardized printer file metadata across all backend implementations.
/// </summary>
public class PrinterFileMetadata
{
    public string FilePath { get; set; } = string.Empty;
    public double? PrintTime { get; set; }
    public double? LayerHeight { get; set; }
    public double? FirstLayerExtrTemp { get; set; }
    public double? FirstLayerBedTemp { get; set; }
    public double? ObjectHeight { get; set; }
    public double? ExtrUsedFilament { get; set; }
}

/// <summary>
/// Standardized printer information across all backend implementations.
/// Avoids naming conflicts with backend-specific PrinterInfo types.
/// </summary>
public class StandardPrinterInfo
{
    public string Name { get; set; } = string.Empty;
    public string Firmware { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}
