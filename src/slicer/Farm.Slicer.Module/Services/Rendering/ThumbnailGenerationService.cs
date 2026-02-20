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
using Farm.Slicer.Module.Domain;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Farm.Slicer.Module.Services.Rendering;

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
        int? zoomPercent = null,
        string? view = null,
        string? viewMode = null,
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

            return await Task.Run(() => GenerateThumbnailInternal(modelFilePath, outputPath, width, height, zoomPercent, view, viewMode), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception during thumbnail generation for {modelFilePath}");
            return false;
        }
    }

    private bool GenerateThumbnailInternal(string modelFilePath, string outputPath, int width, int height, int? zoomPercent, string? view, string? viewMode)
    {
        try
        {
            string fileName = Path.GetFileName(modelFilePath);
            _logger.LogInformation($"Generating thumbnail for {fileName}...");
            _logger.LogInformation($"  Model file path: {modelFilePath}");
            _logger.LogInformation($"  Output path: {outputPath}");
            _logger.LogInformation($"  Using OrcaSlicerPreviewRenderer...");
            _logger.LogInformation($"  Zoom percent: {(zoomPercent.HasValue ? zoomPercent.Value.ToString() : "default")}");
            _logger.LogInformation($"  View: {view ?? "default(front)"}");
            _logger.LogInformation($"  View mode: {viewMode ?? "default(isometric)"}");

            // Use OrcaPreviewRenderer for high-quality rendering
            var renderer = new OrcaPreviewRenderer();

            RenderOptions options = OrcaPreset.Create();
            const int defaultZoomPercent = 40; // matches existing Orca default appearance

            if (zoomPercent.HasValue)
            {
                _logger.LogInformation($"    Applying zoom {zoomPercent.Value}% (default base: {defaultZoomPercent}%)");
                options.SetZoomPercent(defaultZoomPercent, zoomPercent.Value);
                _logger.LogInformation($"    OrthoSize after zoom: {options.OrthoSize:F4}");
            }
            else
            {
                _logger.LogInformation($"    Using default OrthoSize: {options.OrthoSize:F4}");
            }

            if (!string.IsNullOrWhiteSpace(viewMode))
            {
                _logger.LogInformation($"    Applying view mode: {viewMode}");
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
                    _logger.LogWarning($"    Unknown view mode '{viewMode}', using default 'isometric'");
                }
            }

            if (!string.IsNullOrWhiteSpace(view))
            {
                _logger.LogInformation($"    Applying camera view: {view}");
                Vector3 oldPos = options.CameraPosition;
                Vector3 oldTgt = options.CameraTarget;
                if (!options.SetCameraView(view))
                {
                    _logger.LogWarning($"    Unknown view '{view}', using default 'front'");
                    options.SetCameraView("front");
                }

                _logger.LogInformation($"    Camera: Pos({oldPos.X:F2},{oldPos.Y:F2},{oldPos.Z:F2}) -> ({options.CameraPosition.X:F2},{options.CameraPosition.Y:F2},{options.CameraPosition.Z:F2})");
                _logger.LogInformation($"    Target: ({oldTgt.X:F2},{oldTgt.Y:F2},{oldTgt.Z:F2}) -> ({options.CameraTarget.X:F2},{options.CameraTarget.Y:F2},{options.CameraTarget.Z:F2})");
            }
            else
            {
                _logger.LogInformation($"    Using default camera view (front): Pos({options.CameraPosition.X:F2},{options.CameraPosition.Y:F2},{options.CameraPosition.Z:F2})");
            }

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
