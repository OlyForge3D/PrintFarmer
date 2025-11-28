using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Printers.Moonraker;

namespace Farm.Web.Api.Services.Interfaces;

public interface IMoonrakerDiagnosticsService
{
    Task<FileRoot[]?> GetFileRootsAsync(string url);

    Task<MoonrakerDirectoryInfo?> GetDirectoryAsync(string url, string path = "gcodes");

    Task<MoonrakerFileInfo[]?> GetDetailedFileListAsync(string url, string root = "gcodes", string? path = null);
}
