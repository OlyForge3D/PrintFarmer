using ApiITempPath = Farm.Web.Api.Infrastructure.Temp.ITempPathProvider;
using ModuleITempPath = Farm.Slicer.Module.Services.ITempPathProvider;

namespace Farm.Web.Api.Services.Adapters;

/// <summary>
/// Bridges <see cref="Farm.Slicer.Module.Services.ITempPathProvider"/> (module) to the
/// API's <see cref="Farm.Web.Api.Infrastructure.Temp.ITempPathProvider"/> for temp file path management.
/// </summary>
internal sealed class ModuleTempPathAdapter(ApiITempPath apiProvider) : ModuleITempPath
{
    private readonly ApiITempPath _apiProvider = apiProvider ?? throw new ArgumentNullException(nameof(apiProvider));

    /// <inheritdoc />
    public string TempPath => _apiProvider.GetTempRoot();

    /// <inheritdoc />
    public string GetTempFilePath(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".tmp";
        }

        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }

        return Path.Combine(_apiProvider.GetTempRoot(), $"{Guid.NewGuid()}{extension}");
    }
}
