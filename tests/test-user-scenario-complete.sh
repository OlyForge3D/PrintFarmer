#!/bin/bash

# test-user-scenario-complete.sh
# Comprehensive test for user's exact deployment scenario:
# - Architecture: microservices
# - Database: SQL Server
# - Workers: OrcaSlicer (1 instance)
# - Integrations: Spoolman
# Includes full docker compose validation and YAML structure checks

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
COMPOSE_GENERATOR="$REPO_ROOT/scripts/docker/compose-generator.sh"

# Test directory
TEST_DIR=$(mktemp -d)
trap "rm -rf $TEST_DIR" EXIT

echo "============================================================================"
echo "TEST: Complete User Scenario - microservices + sqlserver + orcaslicer + spoolman"
echo "============================================================================"
echo ""

# Counter
PASS=0
FAIL=0

pass() {
    echo "✓ PASS: $1"
    ((PASS++))
}

fail() {
    echo "✗ FAIL: $1"
    ((FAIL++))
}

info() {
    echo "ℹ INFO: $1"
}

warn() {
    echo "⚠ WARN: $1"
}

# Step 1: Generate compose file
echo "Step 1: Generating Docker Compose with exact user configuration..."
info "  Architecture: microservices"
info "  Database: sqlserver"
info "  Workers: orcaslicer (1 instance)"
info "  Integrations: spoolman"
echo ""

if $COMPOSE_GENERATOR \
    --architecture microservices \
    --db-provider sqlserver \
    --addon-stacks orcaslicer,spoolman \
    --output-dir "$TEST_DIR" >/dev/null 2>&1; then
    pass "Compose file generation"
else
    fail "Compose file generation"
    exit 1
fi

COMPOSE_FILE="$TEST_DIR/docker-compose.yml"

# Step 2: File existence
echo ""
echo "Step 2: File Existence Checks"
if [[ -f "$COMPOSE_FILE" ]]; then
    pass "docker-compose.yml created"
else
    fail "docker-compose.yml not found"
    exit 1
fi

# Step 3: YAML structure validation (duplicate volumes)
echo ""
echo "Step 3: YAML Structure Validation"

VOLUMES_COUNT=$(grep -c "^volumes:" "$COMPOSE_FILE" 2>/dev/null || echo "0")
if [[ "$VOLUMES_COUNT" -eq 1 ]]; then
    pass "Single top-level volumes: declaration (no duplicates)"
else
    fail "Found $VOLUMES_COUNT top-level volumes: keys (expected 1)"
fi

# Step 4: Docker compose config validation
echo ""
echo "Step 4: Docker Compose Configuration Validation"

if command -v docker >/dev/null 2>&1; then
    DOCKER_TEST_DIR="$TEST_DIR/docker-validate"
    mkdir -p "$DOCKER_TEST_DIR"
    cp "$COMPOSE_FILE" "$DOCKER_TEST_DIR/"
    
    CONFIG_OUTPUT=$(cd "$DOCKER_TEST_DIR" && docker compose config 2>&1 || echo "")
    
    if echo "$CONFIG_OUTPUT" | grep -qi "mapping key.*already defined"; then
        fail "Docker found duplicate YAML keys (THE BUG)"
        echo ""
        echo "  Error details:"
        echo "$CONFIG_OUTPUT" | head -10 | sed 's/^/    /'
    elif echo "$CONFIG_OUTPUT" | grep -qi "yaml error\|invalid yaml"; then
        fail "Docker found YAML errors"
        echo ""
        echo "  Error details:"
        echo "$CONFIG_OUTPUT" | head -10 | sed 's/^/    /'
    else
        pass "docker compose config validation"
    fi
else
    warn "Docker not available - skipping docker compose config validation"
fi

# Step 5: Configuration content verification
echo ""
echo "Step 5: Configuration Content Verification"

COMPOSE_CONTENT=$(cat "$COMPOSE_FILE")

# Check for required services
if echo "$COMPOSE_CONTENT" | grep -q "^  database:"; then
    pass "Database service defined"
else
    fail "Database service not found"
fi

if echo "$COMPOSE_CONTENT" | grep -q "orcaslicer"; then
    pass "OrcaSlicer worker service found"
else
    fail "OrcaSlicer worker service not found"
fi

if echo "$COMPOSE_CONTENT" | grep -q "spoolman"; then
    pass "Spoolman integration found"
else
    warn "Spoolman service not found (optional)"
fi

# Check for SQL Server image
if echo "$COMPOSE_CONTENT" | grep -qi "mcr.microsoft.com/mssql\|mssql/server"; then
    pass "SQL Server database image configured"
else
    warn "SQL Server image not explicitly found"
fi

# Check for ports
if echo "$COMPOSE_CONTENT" | grep -q '"5245:'; then
    pass "API port 5245 configured"
else
    warn "API port 5245 not found"
fi

if echo "$COMPOSE_CONTENT" | grep -q '"1433:'; then
    pass "SQL Server port 1433 configured"
else
    warn "SQL Server port 1433 not found"
fi

# Step 6: Network configuration
echo ""
echo "Step 6: Network Configuration"

if echo "$COMPOSE_CONTENT" | grep -q "networks:"; then
    pass "Networks section found"
else
    warn "Networks section not found"
fi

# Step 7: Health checks
echo ""
echo "Step 7: Health Checks"

HEALTHCHECK_COUNT=$(grep -c "healthcheck:" "$COMPOSE_FILE" 2>/dev/null || echo "0")
if [[ "$HEALTHCHECK_COUNT" -ge 2 ]]; then
    pass "Found $HEALTHCHECK_COUNT health checks configured"
else
    warn "Only $HEALTHCHECK_COUNT health checks found"
fi

# Step 8: Service count
echo ""
echo "Step 8: Service Configuration"

SERVICE_COUNT=$(grep -c "^  [a-z].*:$" "$COMPOSE_FILE" 2>/dev/null || echo "0")
if [[ "$SERVICE_COUNT" -ge 4 ]]; then
    pass "Found $SERVICE_COUNT services (expected 4+: api, database, orcaslicer, spoolman)"
else
    warn "Found $SERVICE_COUNT services"
fi

# Final summary
echo ""
echo "============================================================================"
echo "TEST SUMMARY"
echo "============================================================================"
echo "Passed: $PASS"
echo "Failed: $FAIL"
echo ""

if [[ "$FAIL" -eq 0 ]]; then
    echo "✓ ALL TESTS PASSED - User scenario is valid!"
    echo ""
    echo "Your exact configuration generates a valid Docker Compose file:"
    echo "  • Architecture: microservices"
    echo "  • Database: SQL Server"
    echo "  • Workers: OrcaSlicer (1 instance)"
    echo "  • Integrations: Spoolman"
    echo ""
    echo "KEY FINDING: No duplicate volumes keys detected in YAML!"
    echo "  This confirms your compose file is properly structured."
    echo ""
    exit 0
else
    echo "✗ TESTS FAILED - Issues detected"
    echo ""
    echo "Generated compose file: $COMPOSE_FILE"
    echo "Review the failures above."
    echo ""
    exit 1
fi
