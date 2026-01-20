namespace Farm.Web.Api.Services;

public interface IGcodeUploadSettings
{
    IReadOnlyCollection<string> GetAllowedExtensions();

    void UpdateAllowedExtensions(IEnumerable<string> extensions);
}
