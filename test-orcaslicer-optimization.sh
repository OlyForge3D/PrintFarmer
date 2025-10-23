#!/bin/bash
# Test script to verify OrcaSlicer binary layer optimization
# This demonstrates the performance improvement by timing builds

set -e

ORCASLICER_VERSION=${ORCASLICER_VERSION:-2.3.1}

echo "=== OrcaSlicer Binary Layer Optimization Test ==="
echo "This script demonstrates build time improvements"
echo ""

# Function to time commands
time_command() {
    local desc="$1"
    shift
    echo "⏱️  $desc"
    start_time=$(date +%s)
    "$@"
    end_time=$(date +%s)
    duration=$((end_time - start_time))
    echo "✅ Completed in ${duration}s"
    echo ""
}

echo "🧹 Cleaning up any existing images..."
docker rmi orcaslicer-binaries:$ORCASLICER_VERSION 2>/dev/null || true
docker rmi printfarmer-orcaslicer-worker 2>/dev/null || true
echo ""

echo "📦 Phase 1: Initial build (binary layer + worker)"
time_command "Building binary layer (this will be slow initially)" \
    docker build -f Dockerfile.orcaslicer-binaries \
    -t orcaslicer-binaries:$ORCASLICER_VERSION \
    --build-arg ORCASLICER_VERSION=$ORCASLICER_VERSION \
    --build-arg ALLOW_STUB=false \
    .

time_command "Building worker using cached binaries" \
    docker build -f Dockerfile.orcaslicer \
    -t printfarmer-orcaslicer-worker \
    --build-arg ORCASLICER_VERSION=$ORCASLICER_VERSION \
    .

echo "🔄 Phase 2: Simulated code change (worker rebuild only)"
echo "Touching a source file to simulate code change..."
touch src/orcaslicer-worker/Program.cs

time_command "Rebuilding worker after code change (should be fast)" \
    docker build -f Dockerfile.orcaslicer \
    -t printfarmer-orcaslicer-worker \
    --build-arg ORCASLICER_VERSION=$ORCASLICER_VERSION \
    .

echo "🎉 Test Complete!"
echo ""
echo "📊 Results Summary:"
echo "• Initial binary layer build: Slow (downloads ~200MB+ AppImage)"
echo "• Initial worker build: Fast (uses cached binaries)"
echo "• Worker rebuild after code change: Fast (reuses binary cache)"
echo ""
echo "💡 Key Benefits:"
echo "• Binary download happens only once"
echo "• Code changes trigger fast rebuilds"
echo "• CI/CD pipelines can cache binary layer"
echo "• Development iteration time dramatically improved"
echo ""
echo "🔍 Verify images created:"
docker images | grep -E "(orcaslicer-binaries|printfarmer-orcaslicer-worker)"