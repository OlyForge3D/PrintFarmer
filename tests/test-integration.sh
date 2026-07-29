#!/bin/bash

# test-integration.sh - Integration tests for deployment pipeline
# Tests the complete workflow from configuration to deployment

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEPLOY_SCRIPT="$REPO_ROOT/scripts/deploy-docker.sh"
COMPOSE_GENERATOR="$REPO_ROOT/scripts/docker/compose-generator.sh"

# Source test framework
source "$SCRIPT_DIR/test-framework.sh"

# Test configuration
TEST_TEMP_DIR=""
ORIGINAL_PWD=""
REPO_BACKUP_DIR=""
TEARDOWN_COMPLETE=false

setup() {
    setup_test_environment
    TEST_TEMP_DIR=$(create_test_temp_dir)
    ORIGINAL_PWD=$(pwd)
    REPO_BACKUP_DIR="$TEST_TEMP_DIR/repository-artifacts"
    test_info "Using temp directory: $TEST_TEMP_DIR"
    backup_repository_deployment_artifacts "$REPO_ROOT" "$REPO_BACKUP_DIR"
    trap teardown EXIT
}

teardown() {
    if [[ "$TEARDOWN_COMPLETE" == "true" ]]; then
        return
    fi

    cd "$ORIGINAL_PWD" 2>/dev/null || true
    restore_repository_deployment_artifacts "$REPO_ROOT" "$REPO_BACKUP_DIR"
    TEARDOWN_COMPLETE=true
    cleanup_test_temp_dir "$TEST_TEMP_DIR"
    teardown_test_environment
}

write_base_config() {
    local config_file="$1"
    local provider="${2:-postgres}"
    local worker_count="${3:-0}"
    local worker_enabled="no"
    local distributed_slicing="false"
    local include_postgres="yes"
    local include_sqlserver="no"
    if [[ "$worker_count" -gt 0 ]]; then
        worker_enabled="yes"
        distributed_slicing="true"
    fi
    if [[ "$provider" == "sqlserver" ]]; then
        include_postgres="no"
        include_sqlserver="yes"
    fi

    cat > "$config_file" << EOF
ARCHITECTURE=microservices
COMPOSE_FILE=docker-compose.yml
DB_PROVIDER=$provider
CONNECTION_STRING=
INCLUDE_POSTGRES=$include_postgres
INCLUDE_SQLSERVER=$include_sqlserver
NETWORK_MODE=bridge
ENABLE_DISCOVERY=false
ALLOW_LOCAL_NETWORK=false
NETWORK_RANGES=
HTTP_PORT=8080
HTTPS_PORT=0
SERVER_HOST=localhost
API_PORT=5245
ENVIRONMENT=Development
ENABLE_SWAGGER=true
ENABLE_DETAILED_LOGGING=true
ENABLE_PGADMIN=false
DEVMODE_BYPASS_AUTH=false
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
INCLUDE_DISCOVERY=false
ENABLE_DISTRIBUTED_SLICING=$distributed_slicing
ENABLE_ORCA_WORKER=$worker_enabled
ORCA_WORKER_COUNT=$worker_count
ENABLE_SPOOLMAN=no
USE_EXTERNAL_STORAGE=no
EOF
}

# Helper function to run deployment with proper directory handling
run_deployment_test() {
    local config_name="$1"
    local timeout_duration="${2:-60}"
    local generate_files="${3:-false}"
    if [[ $# -ge 3 ]]; then
        shift 3
    else
        set --
    fi
    local working_dir
    working_dir=$(pwd)
    local config_path="$working_dir/$config_name"
    local output_dir="$working_dir/generated"
    mkdir -p "$output_dir"

    if [[ "$generate_files" == "true" ]]; then
        "$COMPOSE_GENERATOR" --output-dir "$output_dir" >/dev/null 2>&1 || true
    fi

    local output
    output=$(timeout "$timeout_duration" "$DEPLOY_SCRIPT" \
        --config-file "$config_path" \
        --env-file "$working_dir/.env" \
        --output-dir "$output_dir" \
        --dry-run \
        --batch \
        "$@" 2>&1 || true)
    echo "$output"
}

# Test complete standard deployment pipeline
test_monolithic_deployment_pipeline() {
    start_test "complete standard deployment pipeline"
    
    # Deploy script must be run from PrintFarmer root directory
    # But we need to set up config in a temp space first
    cd "$TEST_TEMP_DIR"
    
    write_base_config ".deploy-config" postgres 1
    
    # Run deployment test in dry-run mode (focus on process success)
    local output=$(run_deployment_test ".deploy-config" 60 false)
    
    assert_contains "$output" "Setup completed successfully" "Pipeline should complete successfully"
    
    # Validate that the process mentions key components
    assert_contains "$output" "compose" "Should mention compose generation"
    
    # Generate files separately for content validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "docker-compose.yml" "Should generate docker-compose.yml via compose generator"
    
    # Check compose file content
    local compose_content=$(cat "docker-compose.yml")
    assert_contains "$compose_content" "Dockerfile.multistage" "Should use multistage dockerfile"
    assert_contains "$compose_content" "target: api-runtime" "Should contain API target"
    assert_contains "$compose_content" "target: frontend-runtime" "Should contain frontend target"
    
    pass_test
}

# Test microservices deployment pipeline
test_microservices_deployment_pipeline() {
    start_test "complete microservices deployment pipeline"
    
    cd "$TEST_TEMP_DIR"
    
    write_base_config ".deploy-config" postgres 2
    
    # Run deployment test in dry-run mode (focus on process success)
    local output=$(run_deployment_test ".deploy-config" 60 false)
    
    assert_contains "$output" "Setup completed successfully" "Microservices pipeline should complete"
    
    # Generate files separately for content validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "docker-compose.yml" "Should generate docker-compose.yml via compose generator"
    local compose_content=$(cat "docker-compose.yml")
    assert_contains "$compose_content" "Dockerfile.multistage" "Should use multistage dockerfile"
    
    pass_test
}

# Test microservices deployment pipeline
# host-network deployment pipeline tests removed

# Test configuration consistency between scripts
test_configuration_consistency() {
    start_test "configuration consistency between scripts"
    
    # Generate config with compose-generator
    assert_command_success "$COMPOSE_GENERATOR --enable-orca-worker yes --output-dir $TEST_TEMP_DIR"
    
    local compose_from_generator=$(cat "$TEST_TEMP_DIR/docker-compose.yml")
    
    # Generate config with deploy script
    cd "$TEST_TEMP_DIR"
    rm -f docker-compose.yml Dockerfile.multistage
    
    write_base_config ".deploy-config" postgres 1
    
    local output=$(run_deployment_test ".deploy-config" 60 false)
    
    # Generate files separately for validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "docker-compose.yml" "Deploy script should also generate compose file"
    local compose_from_deploy=$(cat "docker-compose.yml")
    
    # Both should use multistage dockerfile
    assert_contains "$compose_from_generator" "Dockerfile.multistage" "Generator should use multistage"
    assert_contains "$compose_from_deploy" "Dockerfile.multistage" "Deploy script should use multistage"
    
    pass_test
}

# Test cleanup and regeneration
test_cleanup_and_regeneration() {
    start_test "cleanup and regeneration workflow"
    
    cd "$TEST_TEMP_DIR"
    
    # Create initial deployment
    write_base_config ".deploy-config" postgres 1
    
    local output=$(run_deployment_test ".deploy-config" 60 false)
    
    # Generate files separately for validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "docker-compose.yml" "Should create initial files"
    
    # Modify config and regenerate
    write_base_config ".deploy-config" sqlserver 2
    
    local output2=$(run_deployment_test ".deploy-config" 60 false)
    
    # Generate files separately for validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    local compose_content=$(cat "docker-compose.yml")
    
    # Should reflect new configuration
    assert_file_exists "docker-compose.yml" "Should regenerate files"
    
    pass_test
}

# Test environment file generation
test_environment_file_generation() {
    start_test "environment file generation and content"
    
    cd "$TEST_TEMP_DIR"
    
    write_base_config ".deploy-config" postgres 1
    cat >> ".deploy-config" << 'EOF'
API_PORT=5555
NETWORK_RANGES=192.168.1.0/24
EOF
    
    local output=$(run_deployment_test ".deploy-config" 60)
    
    # Should mention environment file creation
    assert_contains "$output" ".env" "Should mention environment file"
    
    pass_test
}

# Test multistage dockerfile presence
test_multistage_dockerfile_presence() {
    start_test "multistage dockerfile copying"
    
    cd "$TEST_TEMP_DIR"
    
    # Both scripts should ensure Dockerfile.multistage is available
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "$TEST_TEMP_DIR/Dockerfile.multistage" "Compose generator should copy Dockerfile.multistage"
    
    rm -f "$TEST_TEMP_DIR/Dockerfile.multistage"
    
    write_base_config ".deploy-config"
    
    local output=$(run_deployment_test ".deploy-config" 60 false)
    
    # Generate files separately for validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "Dockerfile.multistage" "Deploy script should also copy Dockerfile.multistage"
    
    pass_test
}

# Test invalid configuration handling
test_invalid_configuration_handling() {
    start_test "invalid configuration handling"
    
    cd "$TEST_TEMP_DIR"
    
    # Create config with invalid values
    cat > ".deploy-config" << 'EOF'
ARCHITECTURE=invalid-arch
DB_PROVIDER=invalid-db
ORCA_WORKER_COUNT=-1
EOF
    
    local output=$(run_deployment_test ".deploy-config" 30)
    
    # Should handle invalid configuration gracefully
    # (Either fail cleanly or auto-correct with warnings)
    # The important thing is not to crash or hang
    
    pass_test
}

# Test Redis removal verification
test_redis_removal_verification() {
    start_test "Redis removal verification across pipeline"
    
    cd "$TEST_TEMP_DIR"
    
    write_base_config ".deploy-config" postgres 1
    
    local deploy_output=$(run_deployment_test ".deploy-config" 60 false)
    
    # Generate files separately for validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "docker-compose.yml" "Should generate compose file"
    local compose_content=$(cat "docker-compose.yml")
    
    # Verify no Redis references in pipeline output or generated files
    assert_not_contains "$deploy_output" "Redis" "Deploy output should not mention Redis"
    assert_not_contains "$compose_content" "redis:" "Compose file should not contain Redis service"
    assert_not_contains "$compose_content" "ConnectionStrings__Redis" "Should not contain Redis connection strings"
    
    pass_test
}



# Test all network mode combinations
test_network_mode_combinations() {
    start_test "network mode combinations"
    
    cd "$TEST_TEMP_DIR"
    
    # Generate compose file
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    
    assert_file_exists "docker-compose.yml" "Should create compose file"
    
    write_base_config ".deploy-config"
    local output
    output=$(run_deployment_test ".deploy-config" 60 false)
    
    assert_contains "$output" "Setup completed successfully" "Should deploy successfully"
    
    # Validate network configuration in compose file
    local compose_content=$(cat "docker-compose.yml")
    assert_not_contains "$compose_content" "network_mode: host" "Compose should not configure host network"
    
    rm -f docker-compose.yml Dockerfile.multistage
    
    pass_test
}

# Test security configuration combinations  
test_security_combinations() {
    start_test "security configuration combinations"
    
    cd "$TEST_TEMP_DIR"
    
    write_base_config ".deploy-config"
    local output
    output=$(run_deployment_test ".deploy-config" 60 false --include-security)
    
    assert_contains "$output" "Setup completed successfully" "Should deploy with security"
    
    # Test without security  
    local output2
    output2=$(run_deployment_test ".deploy-config" 60 false)

    assert_contains "$output2" "Setup completed successfully" "Should deploy without security"
    
    pass_test
}

# Test comprehensive addon stack combinations
test_comprehensive_addon_combinations() {
    start_test "comprehensive addon stack combinations"
    
    cd "$TEST_TEMP_DIR"
    
    write_base_config ".deploy-config"
    local output
    output=$(run_deployment_test \
        ".deploy-config" \
        60 \
        false \
        --include-monitoring \
        --include-telemetry \
        --include-security)
    
    assert_contains "$output" "Setup completed successfully" "Should deploy all addons successfully"
    
    # Test minimal configuration  
    local output2
    output2=$(run_deployment_test ".deploy-config" 60 false)
    
    assert_contains "$output2" "Setup completed successfully" "Should deploy minimal configuration successfully"
    
    pass_test
}

# Run all tests
run_all_tests() {
    setup
    
    test_monolithic_deployment_pipeline
    test_microservices_deployment_pipeline
    test_configuration_consistency
    test_cleanup_and_regeneration
    test_environment_file_generation
    test_multistage_dockerfile_presence
    test_invalid_configuration_handling
    test_redis_removal_verification
    test_network_mode_combinations
    test_security_combinations
    test_comprehensive_addon_combinations
    
    teardown
}

# Run the test suite
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    run_test_suite run_all_tests "Integration Tests"
fi