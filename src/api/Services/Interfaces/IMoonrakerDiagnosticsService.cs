using System.Threading.Tasks;

namespace Farm.Web.Api.Services.Interfaces;

public interface IMoonrakerDiagnosticsService
{
    Task<FileRoot[]?> GetFileRootsAsync(string url);

    Task<DirectoryInfo?> GetDirectoryAsync(string url, string path = "gcodes");

    Task<MoonrakerFileInfo[]?> GetDetailedFileListAsync(string url, string root = "gcodes", string? path = null);
}
