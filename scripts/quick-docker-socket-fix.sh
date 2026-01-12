#!/bin/bash
# Quick fix for Docker socket issue after reinstall

echo "=== Quick Docker Socket Fix ==="
echo "This script addresses the specific 'docker.socket' issue you encountered"
echo ""

# Stop Docker services in the correct order
echo "🛑 Stopping Docker services in correct order..."
sudo systemctl stop docker.socket
sudo systemctl stop docker.service  
sudo systemctl stop containerd.service

echo "⏳ Waiting for services to fully stop..."
sleep 5

# Kill any remaining processes
echo "🔪 Killing any remaining Docker processes..."
sudo pkill -f dockerd 2>/dev/null || true
sudo pkill -f containerd 2>/dev/null || true

# Clean up runtime state
echo "🧹 Cleaning up runtime state..."
sudo rm -rf /var/run/docker/* 2>/dev/null || true
sudo rm -rf /run/containerd/* 2>/dev/null || true

# This is the key step - remove stuck container metadata
echo "🗑️  Removing stuck container metadata..."
sudo rm -rf /var/lib/docker/containers/* 2>/dev/null || true

# Restart services in correct order
echo "▶️  Starting Docker services..."
sudo systemctl daemon-reload
sudo systemctl start containerd.service
sleep 2
sudo systemctl start docker.socket  
sleep 2
sudo systemctl start docker.service

# Wait for Docker to be ready
echo "⏳ Waiting for Docker to be ready..."
sleep 5

# Test Docker
if docker info >/dev/null 2>&1; then
    echo "✅ Docker is working!"
    echo ""
    echo "📊 Current status:"
    docker ps -a
else
    echo "❌ Docker still not working. Try extreme cleanup."
fi

echo ""
echo "🎯 This should have fixed your stuck Redis/SQL containers"
echo "   The containers should now be completely gone from 'docker ps -a'"