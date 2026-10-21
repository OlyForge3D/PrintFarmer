#!/bin/bash

# test-auto-admin-setup.sh - Tests for automatic initial admin setup functionality
# Tests auto-admin feature presence in deploy-docker.sh script

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEPLOY_SCRIPT="$REPO_ROOT/scripts/deploy-docker.sh"

# Source test framework
source "$SCRIPT_DIR/test-framework.sh"

# Test 1: Help text includes auto-admin options
test_help_includes_auto_admin_options() {
    start_test "help text includes auto-admin options"
    
    local help_output=$(bash "$DEPLOY_SCRIPT" --help 2>&1 || true)
    
    assert_contains "$help_output" "--auto-admin" "Help should mention --auto-admin flag"
    assert_contains "$help_output" "--auto-admin-username" "Help should mention --auto-admin-username option"
    assert_contains "$help_output" "--auto-admin-password" "Help should mention --auto-admin-password option"
    assert_contains "$help_output" "--auto-admin-email" "Help should mention --auto-admin-email option"
    
    pass_test
}

# Test 2: Help text includes examples for auto-admin
test_help_includes_auto_admin_examples() {
    start_test "help text includes auto-admin usage examples"
    
    local help_output=$(bash "$DEPLOY_SCRIPT" --help 2>&1 || true)
    
    assert_contains "$help_output" "INITIAL ADMIN SETUP OPTIONS" "Help should have dedicated section for auto-admin"
    assert_contains "$help_output" "./scripts/deploy-docker.sh --non-interactive --auto-admin" "Help should show non-interactive auto-admin usage example"
    
    pass_test
}

# Test 3: Script defines auto-admin variables
test_auto_admin_variables_defined() {
    start_test "auto-admin variables are defined in script"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    assert_contains "$script_content" "AUTO_ADMIN=" "Script should define AUTO_ADMIN variable"
    assert_contains "$script_content" "AUTO_ADMIN_USERNAME=" "Script should define AUTO_ADMIN_USERNAME variable"
    assert_contains "$script_content" "AUTO_ADMIN_PASSWORD=" "Script should define AUTO_ADMIN_PASSWORD variable"
    assert_contains "$script_content" "AUTO_ADMIN_EMAIL=" "Script should define AUTO_ADMIN_EMAIL variable"
    
    pass_test
}

# Test 4: Argument parsing for auto-admin options exists
test_auto_admin_argument_parsing() {
    start_test "auto-admin argument parsing code exists"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    # Check for flag parsing code
    assert_contains "$script_content" "--auto-admin)" "Script should parse --auto-admin flag"
    assert_contains "$script_content" "--auto-admin-username)" "Script should parse --auto-admin-username option"
    assert_contains "$script_content" "--auto-admin-password)" "Script should parse --auto-admin-password option"
    assert_contains "$script_content" "--auto-admin-email)" "Script should parse --auto-admin-email option"
    
    pass_test
}

# Test 5: setup_initial_admin function exists
test_setup_initial_admin_function_exists() {
    start_test "setup_initial_admin function is defined"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    assert_contains "$script_content" "setup_initial_admin()" "Script should define setup_initial_admin function"
    
    pass_test
}

# Test 6: setup_initial_admin delegates to the shared setup helper
test_setup_initial_admin_calls_api_endpoint() {
    start_test "setup_initial_admin delegates initial admin creation"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    assert_contains "$script_content" 'create_initial_admin "$api_url" "$admin_username" "$admin_password" "$admin_email" "$PFARM__Setup__BootstrapSecret"' "Function should pass the configured bootstrap proof to the shared helper"
    
    pass_test
}

# Test 7: Shared helper sends the bootstrap proof in the request body, not argv or logs.
test_shared_helper_sends_bootstrap_proof_without_logging_it() {
    start_test "shared helper sends bootstrap proof without logging it"

    local temp_dir mock_bin request_file args_file output proof
    temp_dir=$(mktemp -d)
    mock_bin="$temp_dir/bin"
    request_file="$temp_dir/request.json"
    args_file="$temp_dir/curl-args.txt"
    proof="test-bootstrap-proof-0123456789abcdef"
    mkdir -p "$mock_bin"

    cat > "$mock_bin/curl" <<'EOF'
#!/bin/bash
printf '%s\n' "$*" >> "$BOOTSTRAP_CURL_ARGS_FILE"
case "$*" in
    */api/setup/status*)
        printf '{"needsSetup":true}\n'
        ;;
    */api/setup/initial-admin*)
        cat > "$BOOTSTRAP_REQUEST_FILE"
        if [[ "${MOCK_CURL_ECHO_PROOF:-false}" == "true" ]]; then
            proof=$(sed -n 's/.*"bootstrapSecret": "\([^"]*\)".*/\1/p' "$BOOTSTRAP_REQUEST_FILE")
            printf '{"success":false,"error":"invalid proof %s"}\n' "$proof"
        else
            printf '{"success":true}\n'
        fi
        ;;
    *)
        exit 1
        ;;
esac
EOF
    chmod +x "$mock_bin/curl"

    source "$REPO_ROOT/scripts/common-utils.sh"
    output=$(PATH="$mock_bin:$PATH" \
        BOOTSTRAP_CURL_ARGS_FILE="$args_file" \
        BOOTSTRAP_REQUEST_FILE="$request_file" \
        create_initial_admin "http://localhost:5245" "admin" "test-password" "admin@example.test" "$proof" 2>&1)
    local failure_output failure_status=0
    failure_output=$(PATH="$mock_bin:$PATH" \
        BOOTSTRAP_CURL_ARGS_FILE="$args_file" \
        BOOTSTRAP_REQUEST_FILE="$request_file" \
        MOCK_CURL_ECHO_PROOF=true \
        create_initial_admin "http://localhost:5245" "admin" "test-password" "admin@example.test" "$proof" 2>&1) || failure_status=$?

    local request_body curl_args
    request_body=$(cat "$request_file")
    curl_args=$(cat "$args_file")
    cleanup_test_temp_dir "$temp_dir"

    assert_contains "$request_body" "\"bootstrapSecret\": \"$proof\"" "Request body should contain the bootstrap proof"
    assert_not_contains "$curl_args" "$proof" "Bootstrap proof must not be passed in curl arguments"
    assert_not_contains "$output" "$proof" "Bootstrap proof must not be printed to logs"
    assert_equals "1" "$failure_status" "Helper should return a failure when API rejects the bootstrap proof"
    assert_not_contains "$failure_output" "$proof" "Bootstrap proof must be redacted from echoed API errors"

    pass_test
}

# Test 8: setup_initial_admin is called in deployment flow
test_setup_initial_admin_is_called_in_flow() {
    start_test "setup_initial_admin is called in main deployment flow"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    # This checks that the function is actually invoked
    assert_contains "$script_content" 'setup_initial_admin' "Function should be called in the script"
    assert_contains "$script_content" 'Deployment completed, but initial admin setup failed.' "Main deployment should report auto-admin failure"
    assert_not_contains "$script_content" 'setup_initial_admin || true' "Main deployment must not suppress auto-admin failure"
    
    pass_test
}

# Test 9: setup_initial_admin is called in redeploy recovery flow
test_setup_initial_admin_is_called_in_redeploy_flow() {
    start_test "setup_initial_admin is called in redeploy recovery flow"

    local redeploy_function
    redeploy_function=$(sed -n '/^redeploy_existing() {/,/^}/p' "$DEPLOY_SCRIPT")

    assert_contains "$redeploy_function" 'deploy_containers' "Redeploy should start containers"
    assert_contains "$redeploy_function" 'if ! setup_initial_admin; then' "Redeploy should report and return on auto-admin setup failure"
    assert_not_contains "$redeploy_function" 'setup_initial_admin || true' "Redeploy must not suppress auto-admin setup failure"

    pass_test
}

# Test 10: Local auto-admin setup failures are visible
test_local_auto_admin_failure_is_not_suppressed() {
    start_test "local auto-admin setup failure is not suppressed"

    local script_content
    script_content=$(cat "$REPO_ROOT/scripts/start-all-local.sh")

    assert_contains "$script_content" 'if [[ -n "${PFARM__Setup__BootstrapSecret:-}" ]]; then' "Local startup should export a configured proof for API manual setup"
    assert_contains "$script_content" 'log_error "Could not create the configured initial admin user."' "Local startup should report admin setup failure"
    assert_contains "$script_content" 'exit 1' "Local startup should stop after configured admin setup fails"
    assert_not_contains "$script_content" 'Could not create initial admin user - this may be expected' "Local startup must not hide an auto-admin failure as expected"

    pass_test
}

# Test 11: Config persistence for auto-admin settings
test_config_persistence_code_exists() {
    start_test "config persistence code for auto-admin exists"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    local base_compose monolith_compose
    base_compose=$(cat "$REPO_ROOT/scripts/docker/compose-templates/docker-compose.yml")
    monolith_compose=$(cat "$REPO_ROOT/scripts/docker/compose-templates/docker-compose.monolith.yml")
    
    # Check for config handling
    assert_contains "$script_content" 'AUTO_ADMIN=' "Script should handle AUTO_ADMIN in config"
    assert_contains "$script_content" 'AUTO_ADMIN_USERNAME=' "Script should handle AUTO_ADMIN_USERNAME in config"
    assert_contains "$script_content" 'AUTO_ADMIN_EMAIL=' "Script should handle AUTO_ADMIN_EMAIL in config"
    assert_contains "$script_content" 'PFARM__Setup__BootstrapSecret=' "Script should persist the bootstrap proof in deployment config"
    assert_contains "$base_compose" 'PFARM__Setup__BootstrapSecret=${PFARM__Setup__BootstrapSecret:-}' "Base API service should receive the bootstrap proof"
    assert_contains "$monolith_compose" 'PFARM__Setup__BootstrapSecret=${PFARM__Setup__BootstrapSecret:-}' "Monolith API service should receive the bootstrap proof"
    
    pass_test
}

# Test 12: Password IS persisted to config file
test_password_persisted() {
    start_test "password is persisted to disk in config file"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    # Check that password variable is saved to config
    assert_contains "$script_content" 'AUTO_ADMIN_PASSWORD=${AUTO_ADMIN_PASSWORD' "Script should persist password to .deploy-config"
    
    pass_test
}

# Test 13: API readiness check in setup function
test_api_readiness_check() {
    start_test "setup_initial_admin includes API readiness check"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    assert_contains "$script_content" "/healthz" "Function should check API health endpoint"
    assert_contains "$script_content" "5245" "Function should check correct API port"
    
    pass_test
}

# Test 14: Password auto-generation capability
test_password_auto_generation() {
    start_test "auto-admin has password auto-generation capability"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    assert_contains "$script_content" "openssl" "Script should use openssl for password generation"
    assert_contains "$script_content" "base64" "Script should generate base64-encoded passwords"
    
    pass_test
}

# Test 15: Documentation and comments exist
test_script_documentation() {
    start_test "script includes documentation for auto-admin feature"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    # Check for comments explaining the feature
    assert_contains "$script_content" "auto-admin" "Script should document auto-admin functionality"
    
    pass_test
}

# Main test execution
main() {
    test_info "🧪 Testing Auto-Admin Setup Functionality"
    test_info "=================================================="
    
    # Run all tests
    test_help_includes_auto_admin_options
    test_help_includes_auto_admin_examples
    test_auto_admin_variables_defined
    test_auto_admin_argument_parsing
    test_setup_initial_admin_function_exists
    test_setup_initial_admin_calls_api_endpoint
    test_shared_helper_sends_bootstrap_proof_without_logging_it
    test_setup_initial_admin_is_called_in_flow
    test_setup_initial_admin_is_called_in_redeploy_flow
    test_local_auto_admin_failure_is_not_suppressed
    test_config_persistence_code_exists
    test_password_persisted
    test_api_readiness_check
    test_password_auto_generation
    test_script_documentation
    
    test_info "=================================================="
    test_info "Total: $TESTS_RUN | Passed: $TESTS_PASSED | Failed: $TESTS_FAILED"
}

# Run tests
main "$@"
exit $TESTS_FAILED
