using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Assimp;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Slicer.Module.Domain;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Services.Rendering;

/// <summary>
/// Service for generating thumbnails from 3D model files using assimp CLI tool
/// Supports 40+ 3D formats including STL, 3MF, OBJ, PLY, GLTF, STEP, and more
/// </summary>
public class ThumbnailGenerationService : IThumbnailGenerationService
{
    private readonly ILogger<ThumbnailGenerationService> _logger;
    private readonly string _thumbnailsBasePath;

    public string ThumbnailFileExtension => ".png";

    public ThumbnailGenerationService(ILogger<ThumbnailGenerationService> logger, IConfiguration configuration)
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
        int? zoomPercent = null,
        string? view = null,
        string? viewMode = null,
        CancellationToken ct = default)
    {
        if (!IsFormatSupported(fileFormat))
        {
            _logger.LogWarning("Thumbnail generation not supported for format: {FileFormat}", fileFormat);
            return false;
        }

        if (!File.Exists(modelFilePath))
        {
            _logger.LogWarning("Model file not found: {ModelFilePath}", modelFilePath);
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

            return await Task.Run(() => GenerateThumbnailInternal(modelFilePath, outputPath, width, height, zoomPercent, view, viewMode), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during thumbnail generation for {ModelFilePath}", modelFilePath);
            return false;
        }
    }

    private bool GenerateThumbnailInternal(string modelFilePath, string outputPath, int width, int height, int? zoomPercent, string? view, string? viewMode)
    {
        try
        {
            string fileName = Path.GetFileName(modelFilePath);
            _logger.LogInformation("Generating thumbnail for {FileName}...", fileName);
            _logger.LogInformation("  Model file path: {ModelFilePath}", modelFilePath);
            _logger.LogInformation("  Output path: {OutputPath}", outputPath);
            _logger.LogInformation($"  Using OrcaSlicerPreviewRenderer...");
            _logger.LogInformation("  Zoom percent: {ZoomPercent}", zoomPercent.HasValue ? zoomPercent.Value.ToString() : "default");
            _logger.LogInformation("  View: {View}", view ?? "default(front)");
            _logger.LogInformation("  View mode: {ViewMode}", viewMode ?? "default(isometric)");

            // Use OrcaPreviewRenderer for high-quality rendering
            var renderer = new OrcaPreviewRenderer();

            RenderOptions options = OrcaPreset.Create();
            const int defaultZoomPercent = 40; // matches existing Orca default appearance

            if (zoomPercent.HasValue)
            {
                _logger.LogInformation("    Applying zoom {ZoomPercentValue}% (default base: {DefaultZoomPercent}%)", zoomPercent.Value, defaultZoomPercent);
                options.SetZoomPercent(defaultZoomPercent, zoomPercent.Value);
                _logger.LogInformation("    OrthoSize after zoom: {OptionsOrthoSize:F4}", options.OrthoSize);
            }
            else
            {
                _logger.LogInformation("    Using default OrthoSize: {OptionsOrthoSize:F4}", options.OrthoSize);
            }

            if (!string.IsNullOrWhiteSpace(viewMode))
            {
                _logger.LogInformation("    Applying view mode: {ViewMode}", viewMode);
                if (viewMode.Equals("straight", StringComparison.OrdinalIgnoreCase))
                {
                    options.CameraViewMode = RenderOptions.ViewMode.Straight;
                    _logger.LogInformation($"    Camera view mode set to: Straight (perpendicular)");
                }
                else if (viewMode.Equals("isometric", StringComparison.OrdinalIgnoreCase))
                {
                    options.CameraViewMode = RenderOptions.ViewMode.Isometric;
                    _logger.LogInformation($"    Camera view mode set to: Isometric (diagonal)");
                }
                else
                {
                    _logger.LogWarning("    Unknown view mode '{ViewMode}', using default 'isometric'", viewMode);
                }
            }

            if (!string.IsNullOrWhiteSpace(view))
            {
                _logger.LogInformation("    Applying camera view: {View}", view);
                Vector3 oldPos = options.CameraPosition;
                Vector3 oldTgt = options.CameraTarget;
                if (!options.SetCameraView(view))
                {
                    _logger.LogWarning("    Unknown view '{View}', using default 'front'", view);
                    options.SetCameraView("front");
                }

                _logger.LogInformation("    Camera: Pos({OldPosX:F2},{OldPosY:F2},{OldPosZ:F2}) -> ({X:F2},{Y:F2},{Z:F2})", oldPos.X, oldPos.Y, oldPos.Z, options.CameraPosition.X, options.CameraPosition.Y, options.CameraPosition.Z);
                _logger.LogInformation("    Target: ({OldTgtX:F2},{OldTgtY:F2},{OldTgtZ:F2}) -> ({X:F2},{Y:F2},{Z:F2})", oldTgt.X, oldTgt.Y, oldTgt.Z, options.CameraTarget.X, options.CameraTarget.Y, options.CameraTarget.Z);
            }
            else
            {
                _logger.LogInformation("    Using default camera view (front): Pos({X:F2},{Y:F2},{Z:F2})", options.CameraPosition.X, options.CameraPosition.Y, options.CameraPosition.Z);
            }

            renderer.Render(modelFilePath, outputPath, options);

            _logger.LogInformation("✓ Thumbnail rendered at {Width}x{Height}: {OutputPath}", width, height, outputPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating thumbnail: {Message}", ex.Message);
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
