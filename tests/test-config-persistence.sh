#!/bin/bash

# test-config-persistence.sh - Test configuration persistence for monitoring/telemetry/security
# Tests that interactive choices are properly saved and loaded

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEPLOY_SCRIPT="$REPO_ROOT/scripts/deploy-docker.sh"

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
    cat > "$config_file" << 'EOF'
ARCHITECTURE=microservices
COMPOSE_FILE=docker-compose.yml
DB_PROVIDER=postgres
CONNECTION_STRING=
INCLUDE_POSTGRES=yes
INCLUDE_SQLSERVER=no
NETWORK_MODE=bridge
ENABLE_DISCOVERY=false
ALLOW_LOCAL_NETWORK=false
NETWORK_RANGES=
HTTP_PORT=8080
HTTPS_PORT=0
SERVER_HOST=localhost
API_PORT=5245
ENVIRONMENT=Development
OS=linux
ENABLE_SWAGGER=true
ENABLE_DETAILED_LOGGING=true
ENABLE_PGADMIN=false
DEVMODE_BYPASS_AUTH=false
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
INCLUDE_DISCOVERY=false
ENABLE_DISTRIBUTED_SLICING=false
ENABLE_ORCA_WORKER=no
ORCA_WORKER_COUNT=0
ENABLE_SPOOLMAN=no
USE_EXTERNAL_STORAGE=no
EOF
}

write_legacy_worker_config() {
    local config_file="$1"
    write_base_config "$config_file"
    sed -i \
        -e 's/^ENABLE_DISTRIBUTED_SLICING=.*/ENABLE_DISTRIBUTED_SLICING=true/' \
        -e '/^ENABLE_ORCA_WORKER=/d' \
        -e '/^ORCA_WORKER_COUNT=/d' \
        -e '/^ORCA_HOST_PORT=/d' \
        "$config_file"
}

test_worker_normalization_is_bash_3_2_compatible() {
    start_test "worker normalization avoids Bash 4-only syntax"

    local failures_before=$TESTS_FAILED

    if grep -Eq '\$\{[^}]*,,\}' "$DEPLOY_SCRIPT"; then
        fail_test "Deployment script must not use Bash 4 lowercase expansion"
    fi
    if grep -Eq '\[\[[[:space:]]+-v[[:space:]]' "$DEPLOY_SCRIPT"; then
        fail_test "Deployment script must not use Bash 4.2 variable-presence checks"
    fi

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

# Test that monitoring/telemetry/security settings are saved to config file
test_monitoring_config_persistence() {
    start_test "monitoring/telemetry/security configuration persistence"
    
    cd "$TEST_TEMP_DIR"
    
    write_base_config ".deploy-config"
    cat >> ".deploy-config" << 'EOF'
INCLUDE_MONITORING=true
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=true
INCLUDE_REGISTRY=false
EOF
    
    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --dry-run --batch 2>&1 || true"
    local output
    output=$(get_output)
    
    if [[ -f ".deploy-config" ]]; then
        local new_config_content
        new_config_content=$(cat ".deploy-config")
        
        # Check that monitoring/telemetry/security settings are saved
        assert_contains "$new_config_content" "INCLUDE_MONITORING=" "Config should contain INCLUDE_MONITORING setting"
        assert_contains "$new_config_content" "INCLUDE_TELEMETRY=" "Config should contain INCLUDE_TELEMETRY setting"
        assert_contains "$new_config_content" "INCLUDE_SECURITY=" "Config should contain INCLUDE_SECURITY setting"
        assert_contains "$new_config_content" "INCLUDE_REGISTRY=" "Config should contain INCLUDE_REGISTRY setting"
        
        # Check that the values are correct
        assert_contains "$new_config_content" "INCLUDE_MONITORING=true" "Config should save monitoring=true"
        assert_contains "$new_config_content" "INCLUDE_TELEMETRY=false" "Config should save telemetry=false"
        assert_contains "$new_config_content" "INCLUDE_SECURITY=true" "Config should save security=true"
        assert_contains "$new_config_content" "INCLUDE_REGISTRY=false" "Config should save registry=false"
        
    else
        fail_test "Config file was not created"
        return 1
    fi
    
    pass_test
}

# Test that CLI flags override config file settings
test_cli_flag_override() {
    start_test "CLI flags override config file settings"
    
    cd "$TEST_TEMP_DIR"
    
    write_base_config ".deploy-config"
    cat >> ".deploy-config" << 'EOF'
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
EOF
    
    # Run deploy script with CLI flags to enable monitoring
    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --include-monitoring --include-security --dry-run --batch 2>&1 || true"
    local output=$(get_output)
    
    # The CLI override must be visible and persisted for future non-interactive runs.
    if [[ "$output" != *"enabled via CLI flag"* ]]; then
        test_info "CLI override output: $output"
    fi

    assert_contains "$output" "enabled via CLI flag" "Should indicate CLI flag override"
    local persisted_config
    persisted_config=$(cat ".deploy-config")
    assert_contains "$persisted_config" "INCLUDE_MONITORING=true" "Should persist the monitoring CLI override"
    assert_contains "$persisted_config" "INCLUDE_SECURITY=true" "Should persist the security CLI override"
    
    pass_test
}

# Test configuration loading displays monitoring settings
test_config_loading_display() {
    start_test "configuration loading displays monitoring settings"
    
    cd "$TEST_TEMP_DIR"
    
    write_base_config ".deploy-config"
    cat >> ".deploy-config" << 'EOF'
INCLUDE_MONITORING=true
INCLUDE_TELEMETRY=true
EOF
    
    # Run deploy script to see if it loads and displays the config properly
    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --dry-run --batch 2>&1 || true"
    local output=$(get_output)
    
    # Should show that previous configuration was loaded
    assert_contains "$output" "Loaded configuration" "Should indicate config was loaded"
    
    pass_test
}

test_legacy_distributed_slicing_config_migrates_worker_defaults() {
    start_test "legacy distributed slicing config enables a safe worker default"

    cd "$TEST_TEMP_DIR"

    write_legacy_worker_config ".deploy-config"

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --dry-run --non-interactive 2>&1"
    local exit_code
    exit_code=$(get_output_exit_code)
    local output
    output=$(get_output)
    local failures_before=$TESTS_FAILED
    assert_equals "0" "$exit_code" "Legacy migration deployment should complete successfully" || true
    assert_contains "$output" "Legacy distributed slicing configuration has no OrcaSlicer worker settings" "Should explain the legacy worker migration" || true
    assert_contains "$output" "Effective OrcaSlicer worker configuration: enabled=yes, count=1" "Should display the effective migrated worker configuration" || true
    assert_contains "$output" "Includes slicer-host service (distributed slicing orchestrator)" "Should select the OrcaSlicer service configuration" || true
    assert_contains "$output" "--profile orca up -d" "Should activate the OrcaSlicer compose profile" || true
    assert_file_has_exact_line ".deploy-config" "ENABLE_ORCA_WORKER=yes" "Should persist the inferred worker enablement" || true
    assert_file_has_exact_line ".deploy-config" "ORCA_WORKER_COUNT=1" "Should persist one inferred worker" || true
    assert_file_has_exact_line ".deploy-config" "ORCA_HOST_PORT=8081" "Should persist the default worker host port" || true
    assert_file_has_exact_line ".env" "ENABLE_ORCA_WORKER=yes" "Generated environment should enable the inferred worker" || true
    assert_file_has_exact_line ".env" "ORCA_WORKER_COUNT=1" "Generated environment should contain the inferred worker count" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

test_explicit_worker_disable_remains_disabled() {
    start_test "explicit OrcaSlicer worker disable remains disabled"

    cd "$TEST_TEMP_DIR"

    write_base_config ".deploy-config"
    sed -i 's/^ENABLE_DISTRIBUTED_SLICING=.*/ENABLE_DISTRIBUTED_SLICING=true/' ".deploy-config"

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --dry-run --non-interactive 2>&1"
    local exit_code
    exit_code=$(get_output_exit_code)
    local output
    output=$(get_output)
    local failures_before=$TESTS_FAILED
    assert_equals "0" "$exit_code" "Explicit worker-disable deployment should complete successfully" || true
    assert_contains "$output" "Effective OrcaSlicer worker configuration: enabled=no, count=0" "Should display the explicit disabled worker configuration" || true
    assert_not_contains "$output" "Legacy distributed slicing configuration has no OrcaSlicer worker settings" "Should not migrate an explicit worker disable" || true
    assert_contains "$output" "Slicer workers disabled" "Should omit the OrcaSlicer worker service configuration" || true
    assert_not_contains "$output" "--profile orca" "Should not activate the OrcaSlicer compose profile" || true
    assert_file_has_exact_line ".deploy-config" "ENABLE_ORCA_WORKER=no" "Should preserve explicit worker disable" || true
    assert_file_has_exact_line ".deploy-config" "ORCA_WORKER_COUNT=0" "Should preserve explicit zero worker count" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

test_disabled_slicing_defaults_missing_worker_settings() {
    start_test "disabled slicing defaults omitted worker settings"

    cd "$TEST_TEMP_DIR"

    write_base_config ".deploy-config"
    sed -i \
        -e '/^ENABLE_ORCA_WORKER=/d' \
        -e '/^ORCA_WORKER_COUNT=/d' \
        ".deploy-config"

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --dry-run --non-interactive 2>&1"
    local exit_code
    exit_code=$(get_output_exit_code)
    local output
    output=$(get_output)
    local failures_before=$TESTS_FAILED

    assert_equals "0" "$exit_code" "Partial disabled-slicing config should complete successfully" || true
    assert_not_contains "$output" "unbound variable" "Partial disabled-slicing config should not dereference omitted worker settings" || true
    assert_contains "$output" "Effective OrcaSlicer worker configuration: enabled=no, count=0" "Omitted worker settings should use disabled defaults" || true
    assert_not_contains "$output" "Legacy distributed slicing configuration has no OrcaSlicer worker settings" "Disabled slicing should not trigger enabled-worker migration" || true
    assert_file_has_exact_line ".deploy-config" "ENABLE_DISTRIBUTED_SLICING=false" "Should preserve the operator's disabled slicing setting" || true
    assert_file_has_exact_line ".deploy-config" "ENABLE_ORCA_WORKER=no" "Should persist the safe worker default" || true
    assert_file_has_exact_line ".deploy-config" "ORCA_WORKER_COUNT=0" "Should persist the safe worker count" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

test_disabled_worker_defaults_missing_count() {
    start_test "disabled worker defaults omitted worker count"

    cd "$TEST_TEMP_DIR"

    write_base_config ".deploy-config"
    sed -i '/^ORCA_WORKER_COUNT=/d' ".deploy-config"

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --dry-run --non-interactive 2>&1"
    local exit_code
    exit_code=$(get_output_exit_code)
    local output
    output=$(get_output)
    local failures_before=$TESTS_FAILED

    assert_equals "0" "$exit_code" "Partial disabled-worker config should complete successfully" || true
    assert_not_contains "$output" "unbound variable" "Partial disabled-worker config should not dereference an omitted worker count" || true
    assert_contains "$output" "Effective OrcaSlicer worker configuration: enabled=no, count=0" "Omitted worker count should use the disabled default" || true
    assert_file_has_exact_line ".deploy-config" "ENABLE_DISTRIBUTED_SLICING=false" "Should preserve the operator's disabled slicing setting" || true
    assert_file_has_exact_line ".deploy-config" "ENABLE_ORCA_WORKER=no" "Should preserve the operator's disabled worker setting" || true
    assert_file_has_exact_line ".deploy-config" "ORCA_WORKER_COUNT=0" "Should persist the safe worker count" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

test_regenerate_config_migrates_legacy_worker_defaults() {
    start_test "config regeneration migrates legacy worker defaults"

    cd "$TEST_TEMP_DIR"
    write_legacy_worker_config ".deploy-config"

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --regenerate-config 2>&1"
    local exit_code
    exit_code=$(get_output_exit_code)
    local output
    output=$(get_output)
    local failures_before=$TESTS_FAILED

    if [[ "$exit_code" -ne 0 ]]; then
        test_info "Regeneration output: $output"
    fi
    assert_equals "0" "$exit_code" "Legacy config regeneration should complete successfully" || true
    assert_file_has_exact_line ".deploy-config" "ENABLE_ORCA_WORKER=yes" "Regeneration should persist worker enablement" || true
    assert_file_has_exact_line ".deploy-config" "ORCA_WORKER_COUNT=1" "Regeneration should persist one worker" || true
    assert_file_has_exact_line ".env" "ENABLE_ORCA_WORKER=yes" "Regeneration should enable the generated worker environment" || true
    assert_file_has_exact_line ".env" "ORCA_WORKER_COUNT=1" "Regeneration should generate one worker" || true
    assert_file_has_exact_line "docker-compose.yml" "  orcaslicer-worker:" "Regeneration should include the OrcaSlicer worker service" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

test_redeploy_migrates_legacy_worker_defaults() {
    start_test "redeploy migrates legacy worker defaults"

    cd "$TEST_TEMP_DIR"
    write_legacy_worker_config ".deploy-config"
    rm -f docker-compose.yml docker-compose.override.yml

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --redeploy --dry-run 2>&1"
    local exit_code
    exit_code=$(get_output_exit_code)
    local output
    output=$(get_output)
    local failures_before=$TESTS_FAILED

    if [[ "$exit_code" -ne 0 ]]; then
        test_info "Redeploy output: $output"
    fi
    assert_equals "0" "$exit_code" "Legacy redeploy should complete successfully" || true
    assert_file_has_exact_line ".deploy-config" "ENABLE_ORCA_WORKER=yes" "Redeploy should persist worker enablement" || true
    assert_file_has_exact_line ".deploy-config" "ORCA_WORKER_COUNT=1" "Redeploy should persist one worker" || true
    assert_file_has_exact_line ".env" "ENABLE_ORCA_WORKER=yes" "Redeploy should enable the generated worker environment" || true
    assert_file_has_exact_line ".env" "ORCA_WORKER_COUNT=1" "Redeploy should generate one worker" || true
    assert_file_not_exists "docker-compose.yml" "Dry-run redeploy should not create a compose artifact" || true
    assert_contains "$output" "--profile orca up -d" "Redeploy should select the OrcaSlicer profile" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

test_worker_boolean_forms_and_exact_count_are_normalized() {
    start_test "worker boolean forms and exact count are normalized"

    cd "$TEST_TEMP_DIR"
    write_base_config ".deploy-config"
    sed -i \
        -e 's/^ENABLE_DISTRIBUTED_SLICING=.*/ENABLE_DISTRIBUTED_SLICING=on/' \
        -e 's/^ENABLE_ORCA_WORKER=.*/ENABLE_ORCA_WORKER=TRUE/' \
        -e 's/^ORCA_WORKER_COUNT=.*/ORCA_WORKER_COUNT=10/' \
        ".deploy-config"

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --dry-run --non-interactive 2>&1"
    local exit_code
    exit_code=$(get_output_exit_code)
    local failures_before=$TESTS_FAILED

    assert_equals "0" "$exit_code" "Supported boolean forms should deploy successfully" || true
    assert_file_has_exact_line ".deploy-config" "ENABLE_DISTRIBUTED_SLICING=true" "Distributed slicing should normalize to true" || true
    assert_file_has_exact_line ".deploy-config" "ENABLE_ORCA_WORKER=yes" "Worker enablement should normalize to yes" || true
    assert_file_has_exact_line ".deploy-config" "ORCA_WORKER_COUNT=10" "Worker count should remain exactly 10" || true
    assert_file_has_exact_line ".env" "ORCA_WORKER_COUNT=10" "Generated worker count should remain exactly 10" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

test_malformed_and_empty_worker_booleans_fail_clearly() {
    start_test "malformed and empty worker booleans fail clearly"

    cd "$TEST_TEMP_DIR"
    write_base_config ".deploy-config"
    sed -i \
        -e 's/^ENABLE_DISTRIBUTED_SLICING=.*/ENABLE_DISTRIBUTED_SLICING=true/' \
        -e 's/^ENABLE_ORCA_WORKER=.*/ENABLE_ORCA_WORKER=maybe/' \
        ".deploy-config"

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --dry-run --non-interactive 2>&1"
    local malformed_exit_code
    malformed_exit_code=$(get_output_exit_code)
    local malformed_output
    malformed_output=$(get_output)

    sed -i 's/^ENABLE_ORCA_WORKER=.*/ENABLE_ORCA_WORKER=/' ".deploy-config"
    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --dry-run --non-interactive 2>&1"
    local empty_exit_code
    empty_exit_code=$(get_output_exit_code)
    local empty_output
    empty_output=$(get_output)

    sed -i \
        -e 's/^ENABLE_DISTRIBUTED_SLICING=.*/ENABLE_DISTRIBUTED_SLICING=/' \
        -e 's/^ENABLE_ORCA_WORKER=.*/ENABLE_ORCA_WORKER=no/' \
        ".deploy-config"
    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --dry-run --non-interactive 2>&1"
    local empty_distributed_exit_code
    empty_distributed_exit_code=$(get_output_exit_code)
    local empty_distributed_output
    empty_distributed_output=$(get_output)
    local failures_before=$TESTS_FAILED

    assert_not_equals "0" "$malformed_exit_code" "Malformed worker boolean should fail" || true
    assert_contains "$malformed_output" "Unsupported boolean value 'maybe' for ENABLE_ORCA_WORKER" "Malformed worker boolean should explain accepted values" || true
    assert_not_equals "0" "$empty_exit_code" "Empty worker boolean should fail" || true
    assert_contains "$empty_output" "ENABLE_ORCA_WORKER cannot be empty" "Empty worker boolean should have a clear error" || true
    assert_not_equals "0" "$empty_distributed_exit_code" "Empty distributed slicing boolean should fail" || true
    assert_contains "$empty_distributed_output" "ENABLE_DISTRIBUTED_SLICING cannot be empty" "Empty distributed slicing boolean should have a clear error" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

test_force_disable_overrides_legacy_worker_inference() {
    start_test "force-disable policy overrides legacy worker inference"

    cd "$TEST_TEMP_DIR"
    write_legacy_worker_config ".deploy-config"

    capture_output "DISABLE_SLICER_BUILDS=true timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --dry-run --non-interactive 2>&1"
    local exit_code
    exit_code=$(get_output_exit_code)
    local output
    output=$(get_output)
    local failures_before=$TESTS_FAILED

    assert_equals "0" "$exit_code" "Force-disabled legacy deployment should complete successfully" || true
    assert_file_has_exact_line ".deploy-config" "ENABLE_ORCA_WORKER=no" "Force-disable should persist worker disablement" || true
    assert_file_has_exact_line ".deploy-config" "ORCA_WORKER_COUNT=0" "Force-disable should persist zero workers" || true
    assert_file_has_exact_line ".env" "ENABLE_ORCA_WORKER=no" "Force-disable should generate disabled worker environment" || true
    assert_not_contains "$output" "--profile orca" "Force-disable should not select the OrcaSlicer profile" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

test_orcaslicer_release_is_repository_controlled() {
    start_test "OrcaSlicer release is repository controlled"

    cd "$TEST_TEMP_DIR"

    write_base_config ".deploy-config"
    cat >> ".deploy-config" << 'EOF'
ORCASLICER_VERSION=2.4.1
ORCASLICER_SHA256=d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd
EOF

    capture_output "ORCASLICER_VERSION=2.4.0 ORCASLICER_SHA256=46556197dcc2fb55140e0b1e70c28b4c4da3208f12a4a2522012837c9d77ee10 timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --dry-run --batch 2>&1 || true"
    local persisted_config
    persisted_config=$(cat ".deploy-config")
    local generated_env
    generated_env=$(cat ".env")

    assert_contains "$persisted_config" "ORCASLICER_VERSION=2.4.2" "Config should migrate to the supported OrcaSlicer version"
    assert_contains "$persisted_config" "ORCASLICER_SHA256=d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd" "Config should persist the supported checksum"
    assert_not_contains "$persisted_config" "ORCASLICER_VERSION=2.4.1" "Config should not retain a stale OrcaSlicer version"
    assert_contains "$generated_env" "ORCASLICER_VERSION=2.4.2" "Environment should use the supported OrcaSlicer version"
    assert_contains "$generated_env" "ORCASLICER_SHA256=d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd" "Environment should use the supported checksum"

    pass_test
}

test_regenerate_config_migrates_orcaslicer_release() {
    start_test "config regeneration migrates the OrcaSlicer release"

    cd "$TEST_TEMP_DIR"

    write_base_config ".deploy-config"
    cat >> ".deploy-config" << 'EOF'
ORCASLICER_VERSION=2.4.1
ORCASLICER_SHA256=7aff29a0ac6bb906f11c069eefe83459781c3364bac20ba9529eb9937a231402
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
EOF

    capture_output "ORCASLICER_VERSION=2.4.0 timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --regenerate-config 2>&1"
    local persisted_config
    persisted_config=$(cat ".deploy-config")
    local generated_env
    generated_env=$(cat ".env")

    assert_contains "$persisted_config" "ORCASLICER_VERSION=2.4.2" "Regeneration should migrate persisted OrcaSlicer version"
    assert_contains "$persisted_config" "ORCASLICER_SHA256=d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd" "Regeneration should migrate persisted OrcaSlicer checksum"
    assert_contains "$generated_env" "ORCASLICER_VERSION=2.4.2" "Regeneration should write the supported OrcaSlicer version"
    assert_contains "$generated_env" "ORCASLICER_SHA256=d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd" "Regeneration should write the supported OrcaSlicer checksum"

    pass_test
}

# Run all tests
run_all_tests() {
    setup

    test_worker_normalization_is_bash_3_2_compatible
    test_monitoring_config_persistence
    test_cli_flag_override
    test_config_loading_display
    test_legacy_distributed_slicing_config_migrates_worker_defaults
    test_explicit_worker_disable_remains_disabled
    test_disabled_slicing_defaults_missing_worker_settings
    test_disabled_worker_defaults_missing_count
    test_regenerate_config_migrates_legacy_worker_defaults
    test_redeploy_migrates_legacy_worker_defaults
    test_worker_boolean_forms_and_exact_count_are_normalized
    test_malformed_and_empty_worker_booleans_fail_clearly
    test_force_disable_overrides_legacy_worker_inference
    test_orcaslicer_release_is_repository_controlled
    test_regenerate_config_migrates_orcaslicer_release
    
    teardown
}

# Run the test suite
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    run_test_suite run_all_tests "Configuration Persistence Tests"
fi