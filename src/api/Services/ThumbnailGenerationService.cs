using System.Diagnostics;
using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for generating thumbnails from 3D model files using Python and Open3D
/// </summary>
public class ThumbnailGenerationService : IThumbnailGenerationService
{
    private readonly ILogger<ThumbnailGenerationService> _logger;
    private readonly string _pythonPath;
    private readonly string _scriptPath;
    private readonly string _thumbnailsBasePath;

    public string ThumbnailFileExtension => ".png";

    public ThumbnailGenerationService(ILogger<ThumbnailGenerationService> logger, IConfiguration configuration)
    {
        _logger = logger;

        // Get Python path from configuration or use default
        _pythonPath = configuration["ThumbnailGeneration:PythonPath"] ?? "python3";

        // Script will be stored in the API directory
        string apiDirectory = AppContext.BaseDirectory;
        _scriptPath = Path.Combine(apiDirectory, "Scripts", "generate_thumbnail.py");

        // Thumbnails storage path
        _thumbnailsBasePath = configuration["ThumbnailGeneration:ThumbnailsPath"]
            ?? Path.Combine(Directory.GetCurrentDirectory(), "thumbnails");

        // Ensure thumbnails directory exists
        if (!Directory.Exists(_thumbnailsBasePath))
        {
            Directory.CreateDirectory(_thumbnailsBasePath);
        }

        // Ensure scripts directory exists
        string? scriptsDir = Path.GetDirectoryName(_scriptPath);
        if (scriptsDir != null && !Directory.Exists(scriptsDir))
        {
            Directory.CreateDirectory(scriptsDir);
        }

        // Create the Python script if it doesn't exist
        EnsurePythonScriptExists();
    }

    public async Task<bool> GenerateThumbnailAsync(
        string modelFilePath,
        ModelFileFormat fileFormat,
        string outputPath,
        int width = 256,
        int height = 256,
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
                Directory.CreateDirectory(outputDir);
            }

            // Build arguments for the Python script
            StringBuilder arguments = new();
            arguments.Append($"\"{_scriptPath}\" ");
            arguments.Append($"\"{modelFilePath}\" ");
            arguments.Append($"\"{outputPath}\" ");
            arguments.Append($"{width} ");
            arguments.Append($"{height}");

            // Configure process
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _pythonPath,
                    Arguments = arguments.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            _logger.LogDebug("Starting thumbnail generation: {FileName} {Arguments}",
                _pythonPath, arguments.ToString());

            process.Start();

            // Read output and error streams
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            // Wait for process to complete with cancellation support
            using CancellationTokenRegistration registration = ct.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to kill thumbnail generation process");
                }
            });

            await process.WaitForExitAsync(ct);

            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode == 0 && File.Exists(outputPath))
            {
                _logger.LogDebug("Thumbnail generated successfully: {OutputPath}", outputPath);
                return true;
            }
            else
            {
                _logger.LogWarning("Thumbnail generation failed. Exit code: {ExitCode}, Error: {Error}, Output: {Output}",
                    process.ExitCode, error, output);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during thumbnail generation for {ModelFilePath}", modelFilePath);
            return false;
        }
    }

    public bool IsFormatSupported(ModelFileFormat fileFormat)
    {
        // Open3D supports these formats well
        return fileFormat switch
        {
            ModelFileFormat.STL => true,
            ModelFileFormat.OBJ => true,
            ModelFileFormat.PLY => true,
            ModelFileFormat.TMF => false,  // 3MF support is limited in Open3D
            ModelFileFormat.STEP => false, // STEP files are CAD format, need specialized tools
            _ => false
        };
    }

    private void EnsurePythonScriptExists()
    {
        if (File.Exists(_scriptPath))
        {
            return;
        }

        string scriptContent = @"#!/usr/bin/env python3
""""""
3D Model Thumbnail Generation Script
Generates thumbnail images from 3D model files using Open3D
""""""

import sys
import os
import numpy as np
try:
    import open3d as o3d
    import open3d.visualization.rendering as rendering
except ImportError:
    print(""Error: Open3D is not installed. Please install it using: pip install open3d"", file=sys.stderr)
    sys.exit(1)

def generate_thumbnail(input_path, output_path, width=256, height=256):
    """"""Generate a thumbnail from a 3D model file""""""
    
    if not os.path.exists(input_path):
        print(f""Error: Input file does not exist: {input_path}"", file=sys.stderr)
        return False
    
    try:
        # Load the mesh
        mesh = None
        file_ext = os.path.splitext(input_path)[1].lower()
        
        if file_ext == '.stl':
            mesh = o3d.io.read_triangle_mesh(input_path)
        elif file_ext == '.obj':
            mesh = o3d.io.read_triangle_mesh(input_path)
        elif file_ext == '.ply':
            mesh = o3d.io.read_triangle_mesh(input_path)
        else:
            print(f""Error: Unsupported file format: {file_ext}"", file=sys.stderr)
            return False
        
        if len(mesh.vertices) == 0:
            print(""Error: Failed to load mesh or mesh is empty"", file=sys.stderr)
            return False
        
        # Compute normals for better rendering
        mesh.compute_vertex_normals()
        
        # Center and scale the mesh to fit in view
        mesh.translate(-mesh.get_center())
        scale = 1.0 / mesh.get_max_bound()
        mesh.scale(scale, mesh.get_center())
        
        # Create offscreen renderer
        render = rendering.OffscreenRenderer(width, height)
        
        # Set up material with nice colors
        mtl = o3d.visualization.rendering.MaterialRecord()
        mtl.base_color = [0.9, 0.9, 0.9, 1.0]
        mtl.shader = ""defaultLit""
        
        # Add geometry to scene
        render.scene.add_geometry(""mesh"", mesh, mtl)
        
        # Set up lighting
        render.scene.set_lighting(rendering.Open3DScene.LightingProfile.MED_SHADOWS, np.array([0.3, -1.0, -0.3]))
        
        # Set up camera
        center = mesh.get_center()
        bounds = mesh.get_axis_aligned_bounding_box()
        extent = bounds.get_extent().max()
        
        # Position camera to get a nice 3/4 view
        eye = center + np.array([extent * 1.2, extent * 0.8, extent * 1.5])
        up = np.array([0.0, 0.0, 1.0])
        render.setup_camera(60.0, center, eye, up)
        
        # Render and save
        image = render.render_to_image()
        o3d.io.write_image(output_path, image)
        print(f""Thumbnail saved to: {output_path}"")
        return True
        
    except Exception as e:
        print(f""Error generating thumbnail: {str(e)}"", file=sys.stderr)
        import traceback
        traceback.print_exc(file=sys.stderr)
        return False

def main():
    if len(sys.argv) < 3:
        print(""Usage: python generate_thumbnail.py <input_file> <output_file> [width] [height]"", file=sys.stderr)
        sys.exit(1)
    
    input_path = sys.argv[1]
    output_path = sys.argv[2]
    width = int(sys.argv[3]) if len(sys.argv) > 3 else 256
    height = int(sys.argv[4]) if len(sys.argv) > 4 else 256
    
    success = generate_thumbnail(input_path, output_path, width, height)
    sys.exit(0 if success else 1)

if __name__ == ""__main__"":
    main()
";

        try
        {
            File.WriteAllText(_scriptPath, scriptContent, Encoding.UTF8);
            _logger.LogInformation("Created thumbnail generation Python script at: {ScriptPath}", _scriptPath);

            // Make script executable on Unix systems
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    Process process = Process.Start("chmod", $"+x \"{_scriptPath}\"");
                    process?.WaitForExit();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to make Python script executable");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create thumbnail generation Python script");
        }
    }
}
