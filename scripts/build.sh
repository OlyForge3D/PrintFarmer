#!/bin/bash
set -e

echo "🚀 Building PrintFarmer React Application..."

# Check if .env file exists, create from template if not
if [ ! -f .env ]; then
    echo "📋 Creating .env file from template..."
    cp .env.template .env
    echo "⚠️  Please update .env file with your configuration before deployment"
fi

# Build the Docker image
echo "🔨 Building Docker image..."
docker build -f Dockerfile.react -t printfarmer-react:latest .

# Optional: Tag for registry
if [ "$1" = "registry" ]; then
    REGISTRY_URL=${2:-"your-registry.com"}
    echo "🏷️  Tagging for registry: $REGISTRY_URL"
    docker tag printfarmer-react:latest $REGISTRY_URL/printfarmer-react:latest
    docker tag printfarmer-react:latest $REGISTRY_URL/printfarmer-react:$(date +%Y%m%d-%H%M%S)
fi

echo "✅ Build complete!"
echo "🐳 Run 'docker-compose up -d' to deploy the application"