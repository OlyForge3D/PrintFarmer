namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Marker interface for backend clients that support file download functionality.
/// Used to provide capability-based abstraction instead of explicit backend type checks.
/// Implementing clients will have their own DownloadFileAsync method with appropriate signatures.
/// </summary>
public interface ISupportsFileDownload
{
    // Marker interface - implementing clients define their own DownloadFileAsync method
}

/// <summary>
/// Marker interface for backend clients that support file list retrieval.
/// Allows services to query which clients can provide file listings.
/// Implementing clients will have their own GetFileListAsync method with appropriate signatures.
/// </summary>
public interface ISupportsFileList
{
    // Marker interface - implementing clients define their own GetFileListAsync method
}

/// <summary>
/// Marker interface for backend clients that support file upload functionality.
/// Used to advertise file upload capabilities without explicit backend type checks.
/// Implementing clients will have their own UploadGcodeAsync method with appropriate signatures.
/// </summary>
public interface ISupportsFileUpload
{
    // Marker interface - implementing clients define their own UploadGcodeAsync method
}

/// <summary>
/// Marker interface for backend clients that support starting print jobs.
/// Allows services to check if a client can initiate printing without explicit backend checks.
/// Implementing clients will have their own StartPrintAsync method with appropriate signatures.
/// </summary>
public interface ISupportsStartPrint
{
    // Marker interface - implementing clients define their own StartPrintAsync method
}

/// <summary>
/// Marker interface for backend clients that support printer control operations.
/// Such as pause, resume, emergency stop, and temperature adjustments.
/// </summary>
public interface ISupportsControlOperations
{
    // Marker interface - implementing clients define their own control operation methods
}

