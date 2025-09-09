namespace Farm.Web.Api.Infrastructure.Temp;

/// <summary>
/// Abstraction over temporary storage root so tests and production can control location
/// (e.g. avoid macOS TCC protected system temp folders during automated test runs).
/// </summary>
public interface ITempPathProvider
{
    /// <summary>
    /// Gets the root directory for temporary files. Implementations must ensure the directory exists.
    /// </summary>
    string GetTempRoot();
}
