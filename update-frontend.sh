#!/bin/bash

# Script to quickly update the frontend container without full rebuild
# Usage: ./update-frontend.sh
# This rebuilds the React app and copies the dist files into the running container

set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
REACT_APP_DIR="$SCRIPT_DIR/src/Web/ReactApp"
CONTAINER_NAME="printfarmer-frontend"
CONTAINER_DEST="/usr/share/nginx/html"

echo "=== PrintFarmer Frontend Quick Update ==="
echo ""

# Check if container is running
if ! docker ps --format '{{.Names}}' | grep -q "^${CONTAINER_NAME}$"; then
    echo "❌ Error: Container '$CONTAINER_NAME' is not running"
    echo "   Start it with: docker-compose up -d frontend"
    exit 1
fi

echo "✓ Container '$CONTAINER_NAME' is running"
echo ""

# Build the React app
echo "📦 Building React application..."
cd "$REACT_APP_DIR"

if ! npm run build; then
    echo "❌ React build failed!"
    exit 1
fi

echo "✓ React build completed successfully"
echo ""

# Copy dist files to container
echo "📤 Copying files to container..."
if docker cp "$REACT_APP_DIR/dist/." "$CONTAINER_NAME:$CONTAINER_DEST/"; then
    echo "✓ Files copied successfully"
else
    echo "❌ Error copying files to container"
    exit 1
fi

echo ""
echo "=== Update Complete ==="
echo ""
echo "✅ Frontend has been updated!"
echo "   Container: $CONTAINER_NAME"
echo "   Access at: http://localhost/"
echo ""
