namespace Farm.Web.Api.Services.Interfaces;

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
    Task<VirusScanResult> ScanFileAsync(string filePath, CancellationToken cancellationToken = default);
}
