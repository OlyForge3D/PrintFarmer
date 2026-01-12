# 3D Model Thumbnail Generation

PrintFarmer now supports automatic thumbnail generation for uploaded 3D model files. This feature provides visual previews of your models in the web interface, making it easier to identify and manage your 3D models.

## Supported Formats

- ✅ **STL** - Full support with high-quality thumbnails
- ✅ **OBJ** - Full support with high-quality thumbnails  
- ✅ **PLY** - Full support with high-quality thumbnails
- ❌ **3MF** - Currently not supported (requires specialized libraries)
- ❌ **STEP** - Currently not supported (CAD format, complex to render)

## Quick Setup

1. **Install Dependencies:**
   ```bash
   ./setup-thumbnails.sh
   ```

2. **Restart PrintFarmer API:**
   ```bash
   # If running with dotnet
   cd src
   dotnet run --project api/Farm.Web.Api.csproj
   ```

3. **Upload a Model:**
   Navigate to the Models page in the web interface and upload an STL, OBJ, or PLY file. Thumbnails will be generated automatically.

## How It Works

1. **Upload**: When you upload a 3D model file, PrintFarmer saves it to the models directory
2. **Generate**: The system automatically generates a 256x256 pixel thumbnail using Open3D
3. **Display**: The thumbnail appears in the models list for easy visual identification
4. **Storage**: Thumbnails are stored in the `thumbnails/` subdirectory and served via the API

## Configuration

Thumbnail generation can be configured in `appsettings.json`:

```json
{
  "ThumbnailGeneration": {
    "PythonPath": "python3",           // Path to Python executable
    "ThumbnailsPath": "thumbnails"     // Directory for thumbnail storage
  }
}
```

### Configuration Options

| Setting | Default | Description |
|---------|---------|-------------|
| `PythonPath` | `python3` | Path to Python 3.8+ executable |
| `ThumbnailsPath` | `thumbnails` | Directory where thumbnails are stored |

## API Endpoints

The following endpoints support thumbnail functionality:

- `POST /api/3d-models` - Upload model (generates thumbnail automatically)
- `GET /api/3d-models` - List models (includes thumbnailUrl if available)
- `GET /api/3d-models/{id}` - Get model details (includes thumbnailUrl if available)
- `GET /api/3d-models/{id}/thumbnail` - Download thumbnail image
- `DELETE /api/3d-models/{id}` - Delete model and thumbnail

## Troubleshooting

### Thumbnails Not Generating

1. **Check Python Installation:**
   ```bash
   python3 --version  # Should be 3.8 or higher
   ```

2. **Check Open3D Installation:**
   ```bash
   python3 -c "import open3d; print('Open3D version:', open3d.__version__)"
   ```

3. **Check API Logs:**
   Look for thumbnail generation errors in the API logs. Common issues:
   - Python path not found
   - Open3D import errors
   - File permission issues

### Manual Installation

If the setup script doesn't work, install manually:

```bash
# Install Open3D
pip3 install open3d

# Verify installation
python3 -c "import open3d as o3d; print('Open3D installed:', o3d.__version__)"
```

### File Permissions

Ensure the API has write permissions to the thumbnails directory:

```bash
chmod 755 thumbnails/
chown www-data:www-data thumbnails/  # If running under web server
```

## Technical Details

### Thumbnail Generation Process

1. **File Analysis**: The system checks if the uploaded file format is supported
2. **Python Script**: A Python script using Open3D loads and renders the 3D model
3. **Image Generation**: The model is rendered from an optimal viewing angle at 256x256 resolution
4. **Storage**: The thumbnail is saved as a PNG file in the thumbnails directory
5. **Database Update**: The model record is updated with the thumbnail path

### Performance

- **Generation Time**: 1-5 seconds per model depending on complexity
- **Image Size**: ~10-50KB per thumbnail (PNG format)
- **Memory Usage**: Minimal impact on API server (Python process is short-lived)
- **Storage**: Thumbnails are stored locally alongside model files

### Security

- All file paths are validated to prevent directory traversal attacks
- Python script execution is sandboxed and time-limited
- Only supported 3D formats are processed
- Generated thumbnails are served through the API with proper access controls

## Web Interface

Thumbnails are automatically displayed in the Models page:

- **Grid View**: Visual previews make it easy to identify models at a glance
- **Fallback**: If no thumbnail is available, a default 3D model icon is shown
- **Loading**: Thumbnails are lazy-loaded for better performance
- **Scaling**: Images are responsive and scale appropriately on different screen sizes

## Development

### Adding New Formats

To add support for new 3D file formats:

1. Update `ModelFileFormat` enum in `Domain/Entities.cs`
2. Add format support in `ThumbnailGenerationService.IsFormatSupported()`
3. Update the Python script to handle the new format
4. Add the format to allowed uploads in `ModelController`

### Custom Rendering

The Python thumbnail generation script can be customized for different rendering styles:

- Camera angles and zoom levels
- Lighting and materials
- Background colors
- Image dimensions
- Output formats

The script is automatically created at `Scripts/generate_thumbnail.py` and can be modified as needed.