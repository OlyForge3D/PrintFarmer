namespace Farm.Infrastructure.Security;

public enum VirusScanResult
{
    Clean = 0,
    Infected = 1,
    Unknown = 2
}

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
