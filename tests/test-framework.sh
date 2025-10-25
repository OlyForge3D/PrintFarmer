#!/bin/bash

# test-framework.sh - Simple test framework for bash scripts
# Provides assertion functions and test running infrastructure

set -euo pipefail

# Colors for test output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Test counters
TESTS_RUN=0
TESTS_PASSED=0
TESTS_FAILED=0
CURRENT_TEST=""

# Test output
TEST_OUTPUT=""

# Test logging functions
test_log() { echo -e "${BLUE}[TEST]${NC} $1" >&2; }
test_info() { echo -e "${CYAN}[INFO]${NC} $1" >&2; }
test_success() { echo -e "${GREEN}[PASS]${NC} $1" >&2; }
test_fail() { echo -e "${RED}[FAIL]${NC} $1" >&2; }
test_warning() { echo -e "${YELLOW}[WARN]${NC} $1" >&2; }

# Start a test
start_test() {
    local test_name="$1"
    CURRENT_TEST="$test_name"
    TESTS_RUN=$((TESTS_RUN + 1))
    test_log "Starting: $test_name"
}

# End a test with success
pass_test() {
    TESTS_PASSED=$((TESTS_PASSED + 1))
    test_success "✓ $CURRENT_TEST"
}

# End a test with failure
fail_test() {
    local message="${1:-}"
    TESTS_FAILED=$((TESTS_FAILED + 1))
    test_fail "✗ $CURRENT_TEST${message:+ - $message}"
}

# Assertion functions
assert_equals() {
    local expected="$1"
    local actual="$2"
    local message="${3:-Expected '$expected', got '$actual'}"
    
    if [ "$expected" = "$actual" ]; then
        return 0
    else
        fail_test "$message"
        return 1
    fi
}

assert_not_equals() {
    local not_expected="$1"
    local actual="$2"
    local message="${3:-Expected not '$not_expected', but got '$actual'}"
    
    if [ "$not_expected" != "$actual" ]; then
        return 0
    else
        fail_test "$message"
        return 1
    fi
}

assert_contains() {
    local haystack="$1"
    local needle="$2"
    local message="${3:-Expected to find '$needle' in '$haystack'}"
    
    if [[ "$haystack" == *"$needle"* ]]; then
        return 0
    else
        fail_test "$message"
        return 1
    fi
}

assert_not_contains() {
    local haystack="$1"
    local needle="$2"
    local message="${3:-Expected not to find '$needle' in '$haystack'}"
    
    if [[ "$haystack" != *"$needle"* ]]; then
        return 0
    else
        fail_test "$message"
        return 1
    fi
}

assert_file_exists() {
    local file="$1"
    local message="${2:-Expected file '$file' to exist}"
    
    if [ -f "$file" ]; then
        return 0
    else
        fail_test "$message"
        return 1
    fi
}

assert_file_not_exists() {
    local file="$1"
    local message="${2:-Expected file '$file' to not exist}"
    
    if [ ! -f "$file" ]; then
        return 0
    else
        fail_test "$message"
        return 1
    fi
}

assert_dir_exists() {
    local dir="$1"
    local message="${2:-Expected directory '$dir' to exist}"
    
    if [ -d "$dir" ]; then
        return 0
    else
        fail_test "$message"
        return 1
    fi
}

assert_command_success() {
    local command="$1"
    local message="${2:-Expected command '$command' to succeed}"
    
    if eval "$command" >/dev/null 2>&1; then
        return 0
    else
        fail_test "$message"
        return 1
    fi
}

assert_command_failure() {
    local command="$1"
    local message="${2:-Expected command '$command' to fail}"
    
    if ! eval "$command" >/dev/null 2>&1; then
        return 0
    else
        fail_test "$message"
        return 1
    fi
}

assert_exit_code() {
    local expected_code="$1"
    local command="$2"
    local message="${3:-Expected exit code $expected_code for command '$command'}"
    
    local actual_code=0
    eval "$command" >/dev/null 2>&1 || actual_code=$?
    
    if [ "$actual_code" -eq "$expected_code" ]; then
        return 0
    else
        fail_test "$message (got exit code $actual_code)"
        return 1
    fi
}

# Capture command output for testing
capture_output() {
    local command="$1"
    TEST_OUTPUT=$(eval "$command" 2>&1 || true)
}

# Get the captured output
get_output() {
    echo "$TEST_OUTPUT"
}

# Test suite management
run_test_suite() {
    local test_function="$1"
    local suite_name="${2:-$(basename "$0" .sh)}"
    
    echo
    test_log "Running test suite: $suite_name"
    echo "=========================================="
    
    # Reset counters
    TESTS_RUN=0
    TESTS_PASSED=0
    TESTS_FAILED=0
    
    # Run the test function
    "$test_function"
    
    # Print summary
    echo
    echo "=========================================="
    if [ $TESTS_FAILED -eq 0 ]; then
        test_success "All tests passed! ($TESTS_PASSED/$TESTS_RUN)"
        return 0
    else
        test_fail "Tests failed: $TESTS_FAILED, Passed: $TESTS_PASSED, Total: $TESTS_RUN"
        return 1
    fi
}

# Create temporary test directory
create_test_temp_dir() {
    local temp_dir
    temp_dir=$(mktemp -d -t "printfarmer-test-XXXXXX")
    echo "$temp_dir"
}

# Clean up test temp directory
cleanup_test_temp_dir() {
    local temp_dir="$1"
    if [ -d "$temp_dir" ]; then
        rm -rf "$temp_dir"
    fi
}

# Helper function to get deploy script command with correct paths
get_deploy_script_command() {
    local args=("$@")
    local script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    local repo_root="$(cd "$script_dir/.." && pwd)"
    local deploy_script="$repo_root/scripts/deploy-docker.sh"
    
    # Return command string that runs from repo root with proper timeout
    echo "cd '$repo_root' && timeout 60 '$deploy_script' ${args[*]} 2>&1 || true"
}

# Helper function to get compose generator command with correct paths
get_compose_generator_command() {
    local args=("$@")
    local script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    local repo_root="$(cd "$script_dir/.." && pwd)"
    local compose_generator="$repo_root/scripts/docker/compose-generator.sh"
    
    # Return command string for compose generator
    echo "'$compose_generator' ${args[*]} 2>&1 || true"
}

# Setup and teardown helpers
setup_test_environment() {
    # Override any environment variables that might interfere with tests
    export TESTING=1
    export NO_COLOR=1
}

teardown_test_environment() {
    unset TESTING
    unset NO_COLOR
}