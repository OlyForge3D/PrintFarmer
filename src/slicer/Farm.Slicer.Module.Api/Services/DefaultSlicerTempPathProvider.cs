using Farm.Slicer.Module.Services;
using Microsoft.Extensions.Configuration;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Default implementation of <see cref="ISlicerTempPathProvider"/> that resolves a
/// temp root using (descending precedence):
/// 1. Config value <c>TempStorage:Path</c>
/// 2. Environment variable <c>PRINTFARM_TEMP_ROOT</c>
/// 3. Current working directory / "temp" directory
/// </summary>
internal sealed class DefaultSlicerTempPathProvider : ISlicerTempPathProvider
{
    private readonly string _tempRoot;

    public DefaultSlicerTempPathProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? configured = configuration["TempStorage:Path"];
        string? env = Environment.GetEnvironmentVariable("PRINTFARM_TEMP_ROOT");
        _tempRoot = !string.IsNullOrWhiteSpace(configured)
            ? configured!
            : !string.IsNullOrWhiteSpace(env)
                ? env!
                : Path.Combine(Directory.GetCurrentDirectory(), "temp");

        try
        {
            _ = Directory.CreateDirectory(_tempRoot);
        }
        catch
        {
            _tempRoot = Path.GetTempPath();
        }
    }

    /// <inheritdoc />
    public string TempPath => _tempRoot;

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

        return Path.Combine(_tempRoot, $"{Guid.NewGuid()}{extension}");
    }
}
