namespace Farm.Infrastructure.Services.Interfaces;

/// <summary>
/// Settings for G-code file upload constraints.
/// </summary>
public interface IGcodeUploadSettings
{
    /// <summary>Gets the list of allowed file extensions for G-code uploads.</summary>
    IReadOnlyCollection<string> GetAllowedExtensions();

    /// <summary>Updates the allowed file extensions.</summary>
    void UpdateAllowedExtensions(IEnumerable<string> extensions);
}
