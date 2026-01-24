namespace Farm.Infrastructure.Security;

/// <summary>
/// Result of a virus/malware scan operation.
/// </summary>
public enum VirusScanResult
{
    /// <summary>The file is clean with no threats detected.</summary>
    Clean = 0,

    /// <summary>The file contains detected malware or viruses.</summary>
    Infected = 1,

    /// <summary>The scan result is unknown, typically when the scanner is unavailable.</summary>
    Unknown = 2
}

/// <summary>
/// Service for scanning files for viruses and malware using external antivirus tools.
/// </summary>
public interface IVirusScanner
{
    /// <summary>
    /// Scan a file for viruses/malware using an external scanner. Returns Clean, Infected, or Unknown.
    /// Implementations should be robust in the face of missing scanners and return Unknown when unavailable.
    /// </summary>
    /// <param name="filePath">The absolute path to the file to scan.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    Task<VirusScanResult> ScanFileAsync(string filePath, CancellationToken cancellationToken = default);
}
