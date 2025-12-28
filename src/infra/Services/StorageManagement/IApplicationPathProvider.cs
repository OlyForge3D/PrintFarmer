namespace Farm.Infrastructure.Services.StorageManagement;

/// <summary>
/// Abstraction for application path resolution to decouple from ASP.NET Core.
/// This allows Infrastructure layer to access application paths without depending on IWebHostEnvironment.
/// </summary>
public interface IApplicationPathProvider
{
    /// <summary>
    /// Gets the root path of the application content (where appsettings.json and Program.cs are located).
    /// </summary>
    string GetContentRootPath();

    /// <summary>
    /// Gets the root path for web-accessible static files (wwwroot).
    /// </summary>
    string GetWebRootPath();
}
