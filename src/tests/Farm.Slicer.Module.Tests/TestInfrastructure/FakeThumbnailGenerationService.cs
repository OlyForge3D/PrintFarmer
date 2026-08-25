using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Thumbnails;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Slicer.Module.Tests.TestInfrastructure;

/// <summary>
/// Fast test double for <see cref="IThumbnailGenerationService"/> used by
/// <see cref="CustomWebApplicationFactory"/> in place of the production
/// <c>ThumbnailGenerationService</c>.
/// </summary>
/// <remarks>
/// The production implementation loads the uploaded mesh via Assimp and rasterizes it with
/// <c>OrcaPreviewRenderer</c> - real, CPU-bound work whose cost scales with mesh complexity.
/// No test-host override existed for it, so every integration test that uploaded a model
/// without a client-supplied thumbnail exercised the real renderer; a single ~60k-triangle
/// upload alone measured well over a minute of wall-clock. This fake writes a trivial 1x1
/// placeholder PNG and returns immediately, preserving the "thumbnail file exists on disk /
/// GenerateThumbnailAsync returns true" contract the integration tests assert on without
/// paying for real rendering.
/// </remarks>
public class FakeThumbnailGenerationService : IThumbnailGenerationService
{
    public string ThumbnailFileExtension => ".png";

    public Task<bool> GenerateThumbnailAsync(
        string modelFilePath,
        ModelFileFormat fileFormat,
        string outputPath,
        int width = 512,
        int height = 512,
        int? zoomPercent = null,
        string? view = null,
        string? viewMode = null,
        CancellationToken ct = default)
    {
        if (!IsFormatSupported(fileFormat) || !File.Exists(modelFilePath))
        {
            return Task.FromResult(false);
        }

        string? outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            _ = Directory.CreateDirectory(outputDir);
        }

        using Image<Rgba32> placeholder = new(1, 1);
        placeholder.SaveAsPng(outputPath);

        return Task.FromResult(true);
    }

    public bool IsFormatSupported(ModelFileFormat fileFormat) => fileFormat switch
    {
        ModelFileFormat.STL => true,
        ModelFileFormat.OBJ => true,
        ModelFileFormat.PLY => true,
        ModelFileFormat.TMF => true,
        ModelFileFormat.STEP => true,
        _ => false
    };
}
