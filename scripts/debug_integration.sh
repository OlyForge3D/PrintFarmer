#!/bin/bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEST_TEMP_DIR=$(mktemp -d -t printfarmer-test-debug.XXXXXX)
echo "Using temp directory: $TEST_TEMP_DIR"

cd "$TEST_TEMP_DIR"

# Create config
cat > ".deploy-config" << 'EOC'
ARCHITECTURE=monolithic
DB_PROVIDER=postgres
NETWORK_MODE=bridge
API_PORT=5245
WEB_PORT=3000
DISCOVERY_RANGES=192.168.0.0/16
ENABLE_DISTRIBUTED_SLICING=true
ORCA_WORKER_COUNT=1
ENABLE_ORCA_WORKER=yes
ENABLE_SPOOLMAN=no
ORCASLICER_VERSION=2.4.0
EOC

echo "Config created:"
cat .deploy-config
echo ""

# Test the helper function logic
config_name=".deploy-config"
generate_files="true"

echo "Step 1: Current directory: $(pwd)"
echo "Step 2: Extract architecture from config"
arch_value="monolithic"
if grep -q "ARCHITECTURE=microservices" "$config_name" 2>/dev/null; then
    arch_value="microservices"
fi
echo "Detected architecture: $arch_value"

echo "Step 3: Run compose generator"
original_dir=$(pwd)
cd "$REPO_ROOT"
echo "Changed to repo root: $(pwd)"

echo "Running: $REPO_ROOT/scripts/docker/compose-generator.sh --architecture $arch_value --output-dir $REPO_ROOT"
"$REPO_ROOT/scripts/docker/compose-generator.sh" --architecture "$arch_value" --output-dir "$REPO_ROOT"
echo "Compose generator completed"

echo "Step 4: Check files in repo root"
ls -la docker-compose.yml || echo "docker-compose.yml not found in repo root"
ls -la Dockerfile.multistage || echo "Dockerfile.multistage not found in repo root"

echo "Step 5: Return to temp dir and copy files"
cd "$original_dir"
echo "Back in temp dir: $(pwd)"

if [[ -f "$REPO_ROOT/docker-compose.yml" ]]; then
    cp "$REPO_ROOT/docker-compose.yml" "./docker-compose.yml"
    echo "Copied docker-compose.yml to temp dir"
else
    echo "docker-compose.yml not found in repo root"
fi

if [[ -f "$REPO_ROOT/Dockerfile.multistage" ]]; then
    cp "$REPO_ROOT/Dockerfile.multistage" "./Dockerfile.multistage"
    echo "Copied Dockerfile.multistage to temp dir"
fi

echo "Step 6: Check files in temp dir"
ls -la docker-compose.yml || echo "docker-compose.yml not found in temp dir"
ls -la Dockerfile.multistage || echo "Dockerfile.multistage not found in temp dir"

# Clean up
cd "$REPO_ROOT"
rm -f docker-compose.yml Dockerfile.multistage .env .env.* docker-entrypoint-config.sh .deploy-config
cd /
rm -rf "$TEST_TEMP_DIR"
echo "Cleanup completed"
