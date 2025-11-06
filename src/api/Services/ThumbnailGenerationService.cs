using System.Diagnostics;
using System.Numerics;
using Assimp;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for generating thumbnails from 3D model files using assimp CLI tool
/// Supports 40+ 3D formats including STL, 3MF, OBJ, PLY, GLTF, STEP, and more
/// </summary>
public class ThumbnailGenerationService : IThumbnailGenerationService
{
    private readonly IUnifiedLoggingService _logger;
    private readonly string _thumbnailsBasePath;
    private static readonly AssimpContext _assimpContext = new();

    public string ThumbnailFileExtension => ".png";

    public ThumbnailGenerationService(IUnifiedLoggingService logger, IConfiguration configuration)
    {
        _logger = logger;

        // Thumbnails storage path
        _thumbnailsBasePath = configuration["ThumbnailGeneration:ThumbnailsPath"]
            ?? Path.Combine(Directory.GetCurrentDirectory(), "thumbnails");

        // Ensure thumbnails directory exists
        if (!Directory.Exists(_thumbnailsBasePath))
        {
            _ = Directory.CreateDirectory(_thumbnailsBasePath);
        }
    }

    public async Task<bool> GenerateThumbnailAsync(
        string modelFilePath,
        ModelFileFormat fileFormat,
        string outputPath,
        int width = 512,
        int height = 512,
        CancellationToken ct = default)
    {
        if (!IsFormatSupported(fileFormat))
        {
            _logger.LogWarning($"Thumbnail generation not supported for format: {fileFormat}");
            return false;
        }

        if (!File.Exists(modelFilePath))
        {
            _logger.LogWarning($"Model file not found: {modelFilePath}");
            return false;
        }

        try
        {
            // Ensure output directory exists
            string? outputDir = Path.GetDirectoryName(outputPath);
            if (outputDir != null && !Directory.Exists(outputDir))
            {
                _ = Directory.CreateDirectory(outputDir);
            }

            return await Task.Run(() => GenerateThumbnailInternal(modelFilePath, outputPath, width, height), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception during thumbnail generation for {modelFilePath}");
            return false;
        }
    }

    private bool GenerateThumbnailInternal(string modelFilePath, string outputPath, int width, int height)
    {
        try
        {
            string fileName = Path.GetFileName(modelFilePath);
            _logger.LogInformation($"Generating thumbnail for {fileName}...");

            // Load the 3D model using Assimp
            var scene = _assimpContext.ImportFile(modelFilePath, PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices);

            if (scene == null || scene.MeshCount == 0)
            {
                _logger.LogWarning($"Failed to load model: {fileName}");
                return GeneratePlaceholderThumbnail(outputPath, width, height, fileName, "No geometry");
            }

            // Collect all triangles with their vertices
            List<(Vector3 v0, Vector3 v1, Vector3 v2)> triangles = new();
            Vector3 minBounds = new Vector3(float.MaxValue);
            Vector3 maxBounds = new Vector3(float.MinValue);

            // Extract triangles from all meshes
            for (int m = 0; m < scene.MeshCount; m++)
            {
                var mesh = scene.Meshes[m];

                for (int f = 0; f < mesh.FaceCount; f++)
                {
                    var face = mesh.Faces[f];
                    if (face.IndexCount >= 3)
                    {
                        var v0 = mesh.Vertices[face.Indices[0]];
                        var v1 = mesh.Vertices[face.Indices[1]];
                        var v2 = mesh.Vertices[face.Indices[2]];

                        Vector3 vec0 = new(v0.X, v0.Y, v0.Z);
                        Vector3 vec1 = new(v1.X, v1.Y, v1.Z);
                        Vector3 vec2 = new(v2.X, v2.Y, v2.Z);

                        triangles.Add((vec0, vec1, vec2));

                        minBounds = Vector3.Min(minBounds, vec0);
                        minBounds = Vector3.Min(minBounds, vec1);
                        minBounds = Vector3.Min(minBounds, vec2);
                        maxBounds = Vector3.Max(maxBounds, vec0);
                        maxBounds = Vector3.Max(maxBounds, vec1);
                        maxBounds = Vector3.Max(maxBounds, vec2);
                    }
                }
            }

            _logger.LogInformation($"Loaded {triangles.Count} triangles from {scene.MeshCount} meshes");

            // Render the triangles
            return RenderTriangles(outputPath, width, height, fileName, triangles, minBounds, maxBounds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error generating thumbnail: {ex.Message}");
            return GeneratePlaceholderThumbnail(outputPath, width, height, Path.GetFileName(modelFilePath), ex.Message);
        }
    }

    private bool RenderTriangles(string outputPath, int width, int height, string modelName, List<(Vector3 v0, Vector3 v1, Vector3 v2)> triangles, Vector3 minBounds, Vector3 maxBounds)
    {
        try
        {
            using var image = new Image<Rgba32>(width, height);
            image.Mutate(ctx =>
            {
                // Transparent background
                ctx.Fill(new Color(new Rgba32(0, 0, 0, 0)));

                // Calculate bounds
                Vector3 size = maxBounds - minBounds;
                float maxDim = Math.Max(size.X, Math.Max(size.Y, size.Z));

                if (maxDim <= 0 || float.IsNaN(maxDim))
                    return;

                // Set up projection with better camera positioning
                // X maps left-right, Z maps top-bottom, Y provides depth
                float padding = 40;
                float scale = (Math.Min(width, height) - padding * 2) / maxDim;

                // Center the model properly
                float offsetX = (width - size.X * scale) / 2;
                float offsetY = (height - size.Z * scale) / 2 + padding / 2; // Extra padding at bottom

                _logger.LogDebug($"Render: scale={scale}, offset=({offsetX},{offsetY}), modelSize=({size.X},{size.Y},{size.Z})");

                // Sort triangles by average Y (depth) for back-to-front rendering
                var sortedTriangles = triangles
                    .Select((t, idx) => new { tri = t, avgY = (t.v0.Y + t.v1.Y + t.v2.Y) / 3, idx })
                    .OrderBy(x => x.avgY)
                    .ToList();

                _logger.LogDebug($"Rendering {sortedTriangles.Count} triangles with depth sorting");

                // Render triangles with high-quality depth-based shading
                foreach (var item in sortedTriangles)
                {
                    var (v0, v1, v2) = item.tri;
                    float depth = (item.avgY - minBounds.Y) / (size.Y > 0 ? size.Y : 1); // 0 to 1, normalized
                    depth = Math.Clamp(depth, 0, 1);

                    // High-quality color gradient based on depth
                    // Far = lighter blue, Near = darker blue
                    byte nearR = 40;
                    byte nearG = 80;
                    byte nearB = 150;
                    byte farR = 150;
                    byte farG = 180;
                    byte farB = 220;

                    byte r = (byte)(nearR + (farR - nearR) * depth);
                    byte g = (byte)(nearG + (farG - nearG) * depth);
                    byte b = (byte)(nearB + (farB - nearB) * depth);
                    var triangleColor = new Color(new Rgba32(r, g, b, 255));

                    // Project vertices to 2D
                    var p0 = ProjectVertexIsometric(v0, minBounds, size.X, size.Z, scale, offsetX, offsetY);
                    var p1 = ProjectVertexIsometric(v1, minBounds, size.X, size.Z, scale, offsetX, offsetY);
                    var p2 = ProjectVertexIsometric(v2, minBounds, size.X, size.Z, scale, offsetX, offsetY);

                    // Only draw if triangle is visible (has area)
                    if (IsTriangleVisible(p0, p1, p2))
                    {
                        try
                        {
                            // Draw filled triangle
                            ctx.FillPolygon(triangleColor, p0, p1, p2);

                            // Draw subtle outline for edge definition
                            byte outlineR = (byte)(r * 0.6);
                            byte outlineG = (byte)(g * 0.6);
                            byte outlineB = (byte)(b * 0.6);
                            var outlineColor = new Color(new Rgba32(outlineR, outlineG, outlineB, 255));
                            var outlinePen = Pens.Solid(outlineColor, 0.3f);
                            ctx.DrawPolygon(outlinePen, p0, p1, p2);
                        }
                        catch
                        {
                            // Skip invalid triangles
                        }
                    }
                }
            });

            image.SaveAsPng(outputPath);
            _logger.LogInformation($"✓ Thumbnail rendered at {width}x{height}: {outputPath}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to render thumbnail: {ex.Message}");
            return GeneratePlaceholderThumbnail(outputPath, width, height, modelName, "Render failed");
        }
    }

    private PointF ProjectVertexIsometric(Vector3 vertex, Vector3 minBounds, float sizeX, float sizeZ, float scale, float offsetX, float offsetY)
    {
        Vector3 relative = vertex - minBounds;
        // Isometric: X (width) maps to screen X (left-right), Z (depth) maps to screen Y (top-bottom)
        float screenX = offsetX + relative.X * scale;
        float screenY = offsetY + relative.Z * scale;
        return new PointF(screenX, screenY);
    }

    private bool IsTriangleVisible(PointF p0, PointF p1, PointF p2)
    {
        // Check if triangle has non-zero area
        float area = Math.Abs((p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y)) / 2;
        return area > 0.1f;
    }

    private bool GeneratePlaceholderThumbnail(string outputPath, int width, int height, string modelName, string errorMessage)
    {
        try
        {
            using var image = new Image<Rgba32>(width, height);
            image.Mutate(ctx =>
            {
                ctx.Fill(new Color(new Rgba32(255, 100, 100, 128))); // Semi-transparent red for errors
            });

            image.SaveAsPng(outputPath);
            _logger.LogInformation($"Placeholder thumbnail created for {modelName}: {errorMessage}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create placeholder thumbnail");
            return false;
        }
    }

    public bool IsFormatSupported(ModelFileFormat fileFormat)
    {
        // Assimp supports all these formats natively
        return fileFormat switch
        {
            ModelFileFormat.STL => true,
            ModelFileFormat.OBJ => true,
            ModelFileFormat.PLY => true,
            ModelFileFormat.TMF => true,   // 3MF
            ModelFileFormat.STEP => true,  // STEP CAD format now supported
            _ => false
        };
    }
}
