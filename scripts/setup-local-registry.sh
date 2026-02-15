#!/bin/bash
# Setup script for local Docker registry in home network
# This creates a private Docker registry that can be accessed by multiple machines

set -e

REGISTRY_PORT=${REGISTRY_PORT:-5001}  # macOS AirPlay uses 5000, so default to 5001
REGISTRY_DATA_DIR=${REGISTRY_DATA_DIR:-"$HOME/docker-registry-data"}
REGISTRY_NAME="local-registry"

echo "=== Setting up Local Docker Registry ==="
echo "Registry will be available at: localhost:${REGISTRY_PORT}"
echo "Data directory: ${REGISTRY_DATA_DIR}"
echo ""

# Check if port is available (handle macOS AirPlay conflict)
if command -v lsof >/dev/null 2>&1 && lsof -i :${REGISTRY_PORT} >/dev/null 2>&1; then
    echo "⚠️  Port ${REGISTRY_PORT} is already in use"
    if [[ "$OSTYPE" == "darwin"* ]]; then
        echo "   This is likely macOS AirPlay Receiver (common on macOS Monterey+)"
        echo "   Falling back to port 5001..."
        REGISTRY_PORT=5001
        echo "   Registry will be available at: localhost:${REGISTRY_PORT}"
    else
        echo "   Please stop the service using port ${REGISTRY_PORT} or set REGISTRY_PORT to a different value"
        echo "   Example: REGISTRY_PORT=5001 $0"
        exit 1
    fi
fi

# Create data directory
mkdir -p "${REGISTRY_DATA_DIR}"

# Stop and remove existing registry if running
if docker ps -q -f name=${REGISTRY_NAME} | grep -q .; then
    echo "Stopping existing registry..."
    docker stop ${REGISTRY_NAME}
fi

if docker ps -aq -f name=${REGISTRY_NAME} | grep -q .; then
    echo "Removing existing registry container..."
    docker rm ${REGISTRY_NAME}
fi

echo "Starting Docker registry on port ${REGISTRY_PORT}..."

# Start registry with persistent storage
docker run -d \
    --name ${REGISTRY_NAME} \
    --restart=always \
    -p ${REGISTRY_PORT}:5000 \
    -v "${REGISTRY_DATA_DIR}:/var/lib/registry" \
    -e REGISTRY_STORAGE_DELETE_ENABLED=true \
    registry:2

# Wait for registry to start
echo "Waiting for registry to start..."
sleep 3

# Test registry
if curl -f http://localhost:${REGISTRY_PORT}/v2/ >/dev/null 2>&1; then
    echo "✅ Registry is running successfully!"
else
    echo "❌ Registry failed to start"
    exit 1
fi

echo ""
echo "=== Registry Setup Complete ==="
echo ""
echo "🔗 Registry URL: localhost:${REGISTRY_PORT}"
echo "📁 Data Directory: ${REGISTRY_DATA_DIR}"
echo "🌐 Network Access: $(hostname -I | awk '{print $1}'):${REGISTRY_PORT}"
echo ""
echo "📋 Next Steps:"
echo "1. Configure insecure registries on all client machines (see instructions below)"
echo "2. Tag and push images: docker tag my-image localhost:${REGISTRY_PORT}/my-image"
echo "3. Push to registry: docker push localhost:${REGISTRY_PORT}/my-image"
echo "4. Pull on other machines: docker pull your-registry-ip:${REGISTRY_PORT}/my-image"
echo ""
echo "🔧 To configure other machines in your network:"
echo "   Add '\"$(hostname -I | awk '{print $1}'):${REGISTRY_PORT}\"' to insecure-registries in Docker daemon.json"
echo ""
echo "🛑 To stop registry: docker stop ${REGISTRY_NAME}"
echo "🗑️  To remove registry: docker rm ${REGISTRY_NAME}"