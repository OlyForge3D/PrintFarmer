namespace Farm.Slicer.Module.Services.Configuration;

/// <summary>
/// Configuration options for local slicer file storage.
/// </summary>
public class LocalFileStorageOptions
{
    /// <summary>Gets or sets the base path for file storage.</summary>
    public string BasePath { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "storage");

    /// <summary>Gets or sets the optional base URL for serving stored files.</summary>
    public string? BaseUrl { get; set; }
}
