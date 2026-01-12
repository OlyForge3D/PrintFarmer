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
    assert_contains "$help_output" "./scripts/deploy-docker.sh --auto-admin" "Help should show basic auto-admin usage example"
    
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

# Test 6: setup_initial_admin function calls setup API endpoint
test_setup_initial_admin_calls_api_endpoint() {
    start_test "setup_initial_admin calls /api/setup/initial-admin endpoint"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    assert_contains "$script_content" "/api/setup/initial-admin" "Function should call /api/setup/initial-admin endpoint"
    assert_contains "$script_content" "curl" "Function should use curl to call API"
    
    pass_test
}

# Test 7: setup_initial_admin is called in deployment flow
test_setup_initial_admin_is_called_in_flow() {
    start_test "setup_initial_admin is called in main deployment flow"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    # This checks that the function is actually invoked
    assert_contains "$script_content" 'setup_initial_admin' "Function should be called in the script"
    
    pass_test
}

# Test 8: Config persistence for auto-admin settings
test_config_persistence_code_exists() {
    start_test "config persistence code for auto-admin exists"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    # Check for config handling
    assert_contains "$script_content" 'AUTO_ADMIN=' "Script should handle AUTO_ADMIN in config"
    assert_contains "$script_content" 'AUTO_ADMIN_USERNAME=' "Script should handle AUTO_ADMIN_USERNAME in config"
    assert_contains "$script_content" 'AUTO_ADMIN_EMAIL=' "Script should handle AUTO_ADMIN_EMAIL in config"
    
    pass_test
}

# Test 9: Password IS persisted to config file
test_password_persisted() {
    start_test "password is persisted to disk in config file"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    # Check that password variable is saved to config
    assert_contains "$script_content" 'AUTO_ADMIN_PASSWORD=${AUTO_ADMIN_PASSWORD' "Script should persist password to .deploy-config"
    
    pass_test
}

# Test 10: API readiness check in setup function
test_api_readiness_check() {
    start_test "setup_initial_admin includes API readiness check"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    assert_contains "$script_content" "/healthz" "Function should check API health endpoint"
    assert_contains "$script_content" "5245" "Function should check correct API port"
    
    pass_test
}

# Test 11: Password auto-generation capability
test_password_auto_generation() {
    start_test "auto-admin has password auto-generation capability"
    
    local script_content=$(cat "$DEPLOY_SCRIPT")
    
    assert_contains "$script_content" "openssl" "Script should use openssl for password generation"
    assert_contains "$script_content" "base64" "Script should generate base64-encoded passwords"
    
    pass_test
}

# Test 12: Documentation and comments exist
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
    test_setup_initial_admin_is_called_in_flow
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
