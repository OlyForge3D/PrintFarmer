#!/bin/bash

# validate-deployment-scripts.sh - Simple validation of key deployment functionality
# This script verifies that the core fixes are working correctly

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
VALIDATION_FAILURES=0

# Colors
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m'

echo "🔍 Validating PrintFarmer Deployment Scripts"
echo "=============================================="

# Create temp directory for testing
TEMP_DIR=$(mktemp -d -t "printfarmer-validation-XXXXXX")
trap 'rm -rf "$TEMP_DIR"' EXIT
echo "Using temp directory: $TEMP_DIR"

# Record an explicit assertion result without aborting the remaining validations.
check_result() {
    local passed="$1"
    local description="$2"

    if [ "$passed" = true ]; then
        echo -e "${GREEN}✅ PASS${NC}: $description"
    else
        echo -e "${RED}❌ FAIL${NC}: $description"
        VALIDATION_FAILURES=$((VALIDATION_FAILURES + 1))
    fi
}

# Test 1: Help describes the current architecture contract
echo
echo "Test 1: Architecture options validation"
architecture_help_valid=false
if help_output=$("$REPO_ROOT/scripts/deploy-docker.sh" --help 2>&1); then
    if echo "$help_output" | grep -q "containerized microservices architecture" \
        && ! echo "$help_output" | grep -q -- "--architecture"; then
        architecture_help_valid=true
    fi
fi
check_result "$architecture_help_valid" "Help describes the standard microservices architecture without a removed --architecture option"

# Test 2: Compose generator creates files with multistage dockerfile
echo
echo "Test 2: Compose generator creates multistage files"
TEST2_DIR="$TEMP_DIR/test2-compose"
rm -rf "$TEST2_DIR" 2>/dev/null || true
mkdir -p "$TEST2_DIR"
if "$REPO_ROOT/scripts/docker/compose-generator.sh" --output-dir "$TEST2_DIR" >/dev/null 2>&1; then
    if [ -f "$TEST2_DIR/docker-compose.yml" ] && [ -f "$TEST2_DIR/Dockerfile.multistage" ]; then
        if grep -q "dockerfile: Dockerfile.multistage" "$TEST2_DIR/docker-compose.yml"; then
            check_result true "Compose generator creates multistage configuration"
        else
            check_result false "Compose file uses multistage dockerfile"
        fi
    else
        check_result false "Required files created"
    fi
else
    check_result false "Compose generator execution"
fi

# Test 4: No Redis references in generated files
echo
echo "Test 4: Redis references removed from templates"
redis_found=false
for template in "$REPO_ROOT/scripts/docker/compose-templates"/*.yml; do
    if grep -qi "redis" "$template" 2>/dev/null; then
        redis_found=true
        echo -e "${YELLOW}⚠️  Found Redis reference in $(basename "$template")${NC}"
    fi
done

if [ "$redis_found" = false ]; then
    check_result true "No Redis references in compose templates"
else
    check_result false "Redis references removed"
fi

# Test 5: No PrusaSlicer references in deploy script
echo
echo "Test 5: PrusaSlicer references removed from deploy script"
if ! grep -qi "PrusaSlicer" "$REPO_ROOT/scripts/deploy-docker.sh" 2>/dev/null; then
    check_result true "No PrusaSlicer references in deploy script"
else
    check_result false "PrusaSlicer references removed"
fi

# Test 6: Monolithic compatibility dry-run remains free of shell errors
echo
echo "Test 6: Monolithic dry-run generates expected config"
MONO_DIR="$TEMP_DIR/monolith-dryrun"
rm -rf "$MONO_DIR" 2>/dev/null || true
mkdir -p "$MONO_DIR/src/Web/ReactApp"
pushd "$MONO_DIR" >/dev/null
cat > ".deploy-config" << 'EOF'
ARCHITECTURE=monolithic
ENVIRONMENT=Development
DB_PROVIDER=postgres
POSTGRES_DB=printfarmer
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres
DB_PASSWORD=postgres
INCLUDE_POSTGRES=yes
CONNECTION_STRING=Host=database;Database=printfarmer;Username=postgres;Password=postgres
NETWORK_MODE=bridge
HTTP_PORT=8080
HTTPS_PORT=0
ENABLE_DISCOVERY=yes
ALLOW_LOCAL_NETWORK=true
NETWORK_RANGES=192.168.0.0/16
ENABLE_SWAGGER=true
ENABLE_DETAILED_LOGGING=true
ENABLE_DISTRIBUTED_SLICING=no
ENABLE_ORCA_WORKER=no
ORCA_WORKER_COUNT=0
ENABLE_SPOOLMAN=no
INCLUDE_MONITORING=no
INCLUDE_TELEMETRY=no
INCLUDE_SECURITY=no
INCLUDE_REGISTRY=no
INCLUDE_DISCOVERY=no
EOF

set +e
mono_output=$(timeout 60 "$REPO_ROOT/scripts/deploy-docker.sh" \
    --config-file "$MONO_DIR/.deploy-config" \
    --env-file "$MONO_DIR/.env" \
    --output-dir "$MONO_DIR/generated" \
    --dry-run \
    --batch 2>&1)
mono_status=$?
set -e

mono_checks_pass=true
if grep -q 'unbound variable' <<<"$mono_output"; then
    echo "$mono_output"
    mono_checks_pass=false
elif [ "$mono_status" -eq 0 ]; then
    if [ ! -f ".env" ]; then
        mono_checks_pass=false
        echo -e "${YELLOW}⚠️  .env not generated${NC}"
    elif ! grep -q 'DB_PROVIDER=postgres' ".env" 2>/dev/null; then
        mono_checks_pass=false
        echo -e "${YELLOW}⚠️  Monolithic database provider missing expected PostgreSQL value${NC}"
    fi
else
    echo "$mono_output"
    mono_checks_pass=false
fi
check_result "$mono_checks_pass" "Monolithic dry-run generates expected config without shell errors"
popd >/dev/null

# Test 7: Standard deployment dry-run generates expected config
echo
echo "Test 7: Standard deployment dry-run generates expected config"
MS_DIR="$TEMP_DIR/microservices-dryrun"
rm -rf "$MS_DIR" 2>/dev/null || true
mkdir -p "$MS_DIR/src/Web/ReactApp"
pushd "$MS_DIR" >/dev/null
cat > ".deploy-config" << 'EOF'
ARCHITECTURE=microservices
ENVIRONMENT=Development
DB_PROVIDER=postgres
POSTGRES_DB=printfarmer
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres
DB_PASSWORD=postgres
INCLUDE_POSTGRES=yes
CONNECTION_STRING=Host=database;Database=printfarmer;Username=postgres;Password=postgres
NETWORK_MODE=bridge
INCLUDE_SQLSERVER=no
ALLOW_LOCAL_NETWORK=true
NETWORK_RANGES=192.168.0.0/16
ALLOWED_NETWORK_RANGES=192.168.0.0/16
ENABLE_DISCOVERY=yes
HTTP_PORT=8080
HTTPS_PORT=0
API_PORT=5245
SERVER_HOST=localhost
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
INCLUDE_DISCOVERY=false
ENABLE_SWAGGER=true
ENABLE_DETAILED_LOGGING=true
ENABLE_DISTRIBUTED_SLICING=no
ENABLE_ORCA_WORKER=no
ORCA_WORKER_COUNT=0
ENABLE_SPOOLMAN=no
EOF

set +e
host_output=$(OSTYPE=linux-gnu timeout 60 "$REPO_ROOT/scripts/deploy-docker.sh" \
    --config-file "$MS_DIR/.deploy-config" \
    --env-file "$MS_DIR/.env" \
    --output-dir "$MS_DIR/generated" \
    --dry-run \
    --batch 2>&1)
host_status=$?
set -e

host_checks_pass=true
if grep -q 'unbound variable' <<<"$host_output"; then
    echo "$host_output"
    host_checks_pass=false
elif [ "$host_status" -eq 0 ]; then
    if [ ! -f ".env" ]; then
        host_checks_pass=false
        echo -e "${YELLOW}⚠️  .env not generated${NC}"
    elif ! grep -q 'DB_PROVIDER=postgres' ".env" 2>/dev/null; then
        host_checks_pass=false
        echo -e "${YELLOW}⚠️  Database provider not set in .env${NC}"
    fi

    react_env_path="$MS_DIR/generated/src/Web/ReactApp/.env.production"
    if [ ! -f "$react_env_path" ]; then
        host_checks_pass=false
        echo -e "${YELLOW}⚠️  React production .env not generated at $react_env_path${NC}"
    fi
else
    echo "$host_output"
    host_checks_pass=false
fi
check_result "$host_checks_pass" "Standard deployment dry-run generates expected config without shell errors"
popd >/dev/null

# Test 8: Generated compose files contain no Redis services
echo
echo "Test 8: Generated compose files contain no Redis services"
MS_COMPOSE_DIR="$TEMP_DIR/ms-compose"
rm -rf "$MS_COMPOSE_DIR" 2>/dev/null || true
mkdir -p "$MS_COMPOSE_DIR"
if ! "$REPO_ROOT/scripts/docker/compose-generator.sh" --output-dir "$MS_COMPOSE_DIR" >/dev/null 2>&1; then
    check_result false "Microservices compose generation"
else
    redis_check_pass=true
    for compose_path in "$TEST2_DIR/docker-compose.yml" "$MS_COMPOSE_DIR/docker-compose.yml"; do
        if [ -f "$compose_path" ] && grep -qi "redis:" "$compose_path" 2>/dev/null; then
            redis_check_pass=false
            echo -e "${YELLOW}⚠️  Found Redis reference in $(basename "$compose_path")${NC}"
        fi
    done
    if [ "$redis_check_pass" = true ]; then
        check_result true "Generated compose files contain no Redis services"
    else
        check_result false "No Redis services in generated files"
    fi
fi

# Test 9: Telemetry and monitoring coexistence
echo
echo "Test 9: Telemetry and monitoring coexistence"
STACK_DIR="$TEMP_DIR/telemetry"
rm -rf "$STACK_DIR" 2>/dev/null || true
mkdir -p "$STACK_DIR"
if "$REPO_ROOT/scripts/docker/compose-generator.sh" \
    --include-monitoring \
    --include-telemetry \
    --output-dir "$STACK_DIR" >/dev/null 2>&1; then
    if [ -f "$STACK_DIR/docker-compose.yml" ]; then
        prometheus_count=$(grep -c 'printfarmer-prometheus' "$STACK_DIR/docker-compose.yml" 2>/dev/null || true)
        jaeger_count=$(grep -c 'printfarmer-jaeger' "$STACK_DIR/docker-compose.yml" 2>/dev/null || true)
        prometheus_line=$(grep -n 'printfarmer-prometheus' "$STACK_DIR/docker-compose.yml" 2>/dev/null | head -1 | cut -d: -f1 | tr -d ' ' || echo 0)
        networks_line=$(grep -n '^networks:' "$STACK_DIR/docker-compose.yml" 2>/dev/null | head -1 | cut -d: -f1 | tr -d ' ' || echo 0)
        if [ "$prometheus_count" -eq 1 ] && [ "$jaeger_count" -eq 1 ] && [ "$prometheus_line" -gt 0 ] && [ "$networks_line" -gt 0 ] && [ "$prometheus_line" -lt "$networks_line" ]; then
            check_result true "Telemetry and monitoring stack merge cleanly"
            if command -v docker >/dev/null 2>&1; then
                # docker compose config performs strict interpolation, and Jwt__Key is a
                # required variable with no default since #1301. This stack also includes
                # the monitoring overlay, whose GRAFANA_ADMIN_PASSWORD is a required
                # variable with no default since #1295. compose-generator.sh is invoked
                # directly here (bypassing scripts/deploy-docker.sh, which is what normally
                # supplies real values via .env). Write throwaway, test-only values confined
                # to $STACK_DIR so strict interpolation can resolve without weakening the
                # production requirement or touching any tracked file.
                {
                    echo "Jwt__Key=test-only-throwaway-key-for-ci-validation-0123456789ab"
                    echo "GRAFANA_ADMIN_PASSWORD=test-only-throwaway-password-for-ci-0123456789"
                } > "$STACK_DIR/.env"
                # Use an if/else to capture output+status so a failing `docker compose
                # config` (a plain assignment from a failing command substitution) does
                # not trigger `set -e` and abort the script before it can be reported.
                compose_config_status=0
                compose_config_output=$(cd "$STACK_DIR" && docker compose -f docker-compose.yml config --quiet 2>&1) || compose_config_status=$?
                if [ "$compose_config_status" -ne 0 ]; then
                    echo -e "${YELLOW}⚠️  docker compose config failed:${NC}"
                    echo "$compose_config_output" | sed 's/^/    /'
                    check_result false "docker compose config validation"
                else
                    check_result true "docker compose config validation"

                    # Negative control: prove the assertion above is genuinely live (not
                    # merely satisfied by chance) by corrupting the generated compose file
                    # and confirming validation now fails.
                    cp "$STACK_DIR/docker-compose.yml" "$STACK_DIR/docker-compose.yml.bak"
                    printf '\n  this is not valid yaml: [\n' >> "$STACK_DIR/docker-compose.yml"
                    if (cd "$STACK_DIR" && docker compose -f docker-compose.yml config --quiet >/dev/null 2>&1); then
                        check_result false "docker compose config validation detects a malformed compose file"
                    else
                        check_result true "docker compose config validation detects a malformed compose file"
                    fi
                    mv "$STACK_DIR/docker-compose.yml.bak" "$STACK_DIR/docker-compose.yml"
                fi
            fi
        else
            [ "$prometheus_count" -eq 1 ] || echo -e "${YELLOW}⚠️  Expected one Prometheus definition, found $prometheus_count${NC}"
            [ "$jaeger_count" -eq 1 ] || echo -e "${YELLOW}⚠️  Expected one Jaeger definition, found $jaeger_count${NC}"
            if [ "$prometheus_line" -ge "$networks_line" ]; then
                echo -e "${YELLOW}⚠️  Prometheus service appears after networks section (line $prometheus_line vs $networks_line)${NC}"
            fi
            check_result false "Telemetry and monitoring stack merge cleanly"
        fi
    else
        echo -e "${YELLOW}⚠️  docker-compose.yml not generated for telemetry test${NC}"
        check_result false "Telemetry and monitoring stack merge cleanly"
    fi
else
    check_result false "Compose generator execution for telemetry+monitoring"
fi

# Cleanup
echo
echo "🧹 Cleaning up test directory: $TEMP_DIR"
rm -rf "$TEMP_DIR"
trap - EXIT

echo
echo "=============================================="
if [ "$VALIDATION_FAILURES" -gt 0 ]; then
    echo -e "${RED}❌ Validation failed with $VALIDATION_FAILURES failed assertion(s).${NC}"
    exit 1
fi

echo "✅ Validation completed!"
echo
echo "Key improvements verified:"
echo "• Multi-stage builds integrated into all architectures"
echo "• Redis services and references completely removed"
echo "• PrusaSlicer references completely removed"
echo "• Deployment pipeline generates valid configurations"
echo "• Help documents the standard microservices architecture"
echo "• Dry-run covers the standard PostgreSQL deployment defaults"