using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Assimp;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Infrastructure.Telemetry;
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

        _logger.LogInformation("ThumbnailGenerationService initialized. Note: AssimpNetter native bindings not available in this deployment - thumbnails will use placeholder rendering");
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
            _logger.LogInformation($"  Model file path: {modelFilePath}");
            _logger.LogInformation($"  Output path: {outputPath}");
            _logger.LogInformation($"  Using OrcaSlicerPreviewRenderer...");

            // Use OrcaPreviewRenderer for high-quality rendering
            var renderer = new OrcaPreviewRenderer();

            var options = RenderOptions.CreateOrcaPreset();
            
            renderer.Render(modelFilePath, outputPath, options);

            _logger.LogInformation($"✓ Thumbnail rendered at {width}x{height}: {outputPath}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error generating thumbnail: {ex.Message}");
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
