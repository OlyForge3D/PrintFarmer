#!/bin/bash
# Setup script for 3D model thumbnail generation dependencies

echo "🖼️  Setting up 3D Model Thumbnail Generation..."

# Check if Python 3 is available
if ! command -v python3 &> /dev/null; then
    echo "❌ Python 3 is required but not installed. Please install Python 3.8 or higher."
    exit 1
fi

PYTHON_VERSION=$(python3 -c 'import sys; print(".".join(map(str, sys.version_info[:2])))')
echo "✅ Python $PYTHON_VERSION found"

# Check if pip is available
if ! command -v pip3 &> /dev/null; then
    echo "❌ pip3 is required but not installed. Please install pip3."
    exit 1
fi

echo "📦 Installing Open3D for 3D model thumbnail generation..."

# Install Open3D
pip3 install open3d

if [ $? -eq 0 ]; then
    echo "✅ Open3D installed successfully"
else
    echo "❌ Failed to install Open3D. Please check your Python and pip installation."
    exit 1
fi

# Test the installation
echo "🧪 Testing thumbnail generation setup..."

python3 -c "
import open3d as o3d
print('✅ Open3D import successful')
print('📊 Open3D version:', o3d.__version__)

# Test basic functionality
mesh = o3d.geometry.TriangleMesh.create_sphere()
print('✅ Basic mesh creation successful')

print('🎉 Thumbnail generation setup complete!')
print('📝 Note: You may need to restart the PrintFarmer API server to enable thumbnail generation.')
"

if [ $? -eq 0 ]; then
    echo ""
    echo "🎉 Setup completed successfully!"
    echo ""
    echo "📋 What was installed:"
    echo "   • Open3D library for 3D model processing"
    echo "   • Python thumbnail generation script (auto-created)"
    echo ""
    echo "💡 Usage:"
    echo "   • Upload STL, OBJ, or PLY files to see automatic thumbnails"
    echo "   • Thumbnails are generated at 256x256 pixels"
    echo "   • 3MF and STEP files are not currently supported"
    echo ""
    echo "⚙️  Configuration:"
    echo "   • Python path: $(which python3)"
    echo "   • Thumbnails stored in: ./thumbnails/"
    echo "   • Configure in appsettings.json under ThumbnailGeneration section"
else
    echo "❌ Setup test failed. Please check the error messages above."
    exit 1
fi