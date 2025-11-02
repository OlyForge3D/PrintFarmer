#!/bin/bash

################################################################################
# run-deployment-tests.sh
# 
# Comprehensive test suite for Docker deployment scripts
# Runs all compose-generator and deploy-docker tests with detailed reporting
#
# Purpose: Validate all deployment script changes before committing
# Use case: Run this before committing any changes to:
#   - scripts/docker/compose-generator.sh
#   - scripts/deploy-docker.sh
#   - scripts/docker/compose-templates/*
#   - Any Docker configuration changes
#
# Usage:
#   ./run-deployment-tests.sh                    # Run all tests
#   ./run-deployment-tests.sh --verbose          # Show detailed output
#   ./run-deployment-tests.sh --quick            # Run quick sanity checks only
#   ./run-deployment-tests.sh --help             # Show help
#
# Exit codes:
#   0 = All tests passed
#   1 = Some tests failed
#   2 = Invalid arguments or setup error
#
################################################################################

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Script configuration
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
VERBOSE="${VERBOSE:-false}"
QUICK_MODE="${QUICK_MODE:-false}"
TEST_START_TIME=$(date +%s)

# Test result tracking
TESTS_RUN=0
TESTS_PASSED=0
TESTS_FAILED=0
TESTS_SKIPPED=0
FAILED_TESTS=()

################################################################################
# Logging Functions
################################################################################

log_info() {
    echo -e "${BLUE}ℹ INFO${NC}  $*" >&2
}

log_success() {
    echo -e "${GREEN}✓ SUCCESS${NC} $*" >&2
}

log_warn() {
    echo -e "${YELLOW}⚠ WARN${NC}  $*" >&2
}

log_error() {
    echo -e "${RED}✗ ERROR${NC}  $*" >&2
}

log_debug() {
    if [[ "$VERBOSE" == "true" ]]; then
        echo -e "${CYAN}🔍 DEBUG${NC}  $*" >&2
    fi
}

log_section() {
    echo ""
    echo -e "${BLUE}════════════════════════════════════════════════════════════${NC}"
    echo -e "${BLUE}$*${NC}"
    echo -e "${BLUE}════════════════════════════════════════════════════════════${NC}"
}

log_subsection() {
    echo ""
    echo -e "${CYAN}── $* ──${NC}"
}

################################################################################
# Test Execution Functions
################################################################################

run_test_suite() {
    local suite_name="$1"
    local test_script="$2"
    
    if [[ ! -f "$test_script" ]]; then
        log_warn "Test script not found: $test_script"
        ((TESTS_SKIPPED++))
        return 0
    fi
    
    log_subsection "Running: $suite_name"
    
    if [[ "$VERBOSE" == "true" ]]; then
        if bash "$test_script"; then
            log_success "$suite_name passed"
            ((TESTS_PASSED++))
            ((TESTS_RUN++))
            return 0
        else
            log_error "$suite_name failed"
            FAILED_TESTS+=("$suite_name")
            ((TESTS_FAILED++))
            ((TESTS_RUN++))
            return 1
        fi
    else
        if bash "$test_script" >/dev/null 2>&1; then
            log_success "$suite_name passed"
            ((TESTS_PASSED++))
            ((TESTS_RUN++))
            return 0
        else
            log_error "$suite_name failed (run with --verbose for details)"
            FAILED_TESTS+=("$suite_name")
            ((TESTS_FAILED++))
            ((TESTS_RUN++))
            return 1
        fi
    fi
}

check_dependencies() {
    log_info "Checking test dependencies..."
    
    local missing_deps=0
    
    # Check required commands
    for cmd in bash grep awk sed; do
        if ! command -v "$cmd" >/dev/null 2>&1; then
            log_error "Required command not found: $cmd"
            ((missing_deps++))
        fi
    done
    
    # Check docker (optional but recommended)
    if ! command -v docker >/dev/null 2>&1; then
        log_warn "Docker not found (optional but recommended for full validation)"
    fi
    
    # Check test scripts exist
    if [[ ! -f "$SCRIPT_DIR/test-compose-generator.sh" ]]; then
        log_error "Test script not found: $SCRIPT_DIR/test-compose-generator.sh"
        ((missing_deps++))
    fi
    
    if [[ ! -f "$SCRIPT_DIR/test-deploy-docker.sh" ]]; then
        log_error "Test script not found: $SCRIPT_DIR/test-deploy-docker.sh"
        ((missing_deps++))
    fi
    
    if [[ $missing_deps -gt 0 ]]; then
        log_error "Missing $missing_deps dependencies"
        return 2
    fi
    
    log_success "All dependencies satisfied"
    return 0
}

################################################################################
# Quick Mode Tests (Sanity Checks)
################################################################################

run_quick_tests() {
    log_section "Quick Mode: Sanity Checks"
    
    local test_temp_dir="/tmp/deployment-tests-quick-$$"
    mkdir -p "$test_temp_dir"
    trap "rm -rf '$test_temp_dir'" EXIT
    
    log_subsection "Basic Compose Generation"
    
    # Test 1: Help output
    if bash "$REPO_ROOT/scripts/docker/compose-generator.sh" --help >/dev/null 2>&1; then
        log_success "Compose generator help works"
        ((TESTS_PASSED++))
    else
        log_error "Compose generator help failed"
        ((TESTS_FAILED++))
    fi
    ((TESTS_RUN++))
    
    # Test 2: Host-network generation
    if DB_PROVIDER=postgres bash "$REPO_ROOT/scripts/docker/compose-generator.sh" \
        --architecture host-network \
        --output-dir "$test_temp_dir/test1" >/dev/null 2>&1; then
        log_success "Host-network compose generation works"
        ((TESTS_PASSED++))
    else
        log_error "Host-network compose generation failed"
        ((TESTS_FAILED++))
    fi
    ((TESTS_RUN++))
    
    # Test 3: Microservices generation
    if DB_PROVIDER=postgres bash "$REPO_ROOT/scripts/docker/compose-generator.sh" \
        --architecture microservices \
        --output-dir "$test_temp_dir/test2" >/dev/null 2>&1; then
        log_success "Microservices compose generation works"
        ((TESTS_PASSED++))
    else
        log_error "Microservices compose generation failed"
        ((TESTS_FAILED++))
    fi
    ((TESTS_RUN++))
    
    # Test 4: Monolithic generation
    if DB_PROVIDER=postgres bash "$REPO_ROOT/scripts/docker/compose-generator.sh" \
        --architecture monolithic \
        --output-dir "$test_temp_dir/test3" >/dev/null 2>&1; then
        log_success "Monolithic compose generation works"
        ((TESTS_PASSED++))
    else
        log_error "Monolithic compose generation failed"
        ((TESTS_FAILED++))
    fi
    ((TESTS_RUN++))
    
    # Test 5: No duplicate volumes in generated files
    log_subsection "YAML Validation (No Duplicate Volumes)"
    
    local compose_file="$test_temp_dir/test1/docker-compose.yml"
    if [[ -f "$compose_file" ]]; then
        local volumes_count=$(grep -c "^volumes:" "$compose_file" 2>/dev/null || echo "0")
        if [[ "$volumes_count" -eq 1 ]]; then
            log_success "No duplicate volumes in host-network compose"
            ((TESTS_PASSED++))
        else
            log_error "Found $volumes_count 'volumes:' sections (expected 1)"
            ((TESTS_FAILED++))
        fi
    else
        log_error "Compose file not generated"
        ((TESTS_FAILED++))
    fi
    ((TESTS_RUN++))
}

################################################################################
# Full Test Suite
################################################################################

run_full_tests() {
    log_section "Full Test Suite"
    
    log_subsection "Test: Compose Generator"
    run_test_suite "compose-generator tests" "$SCRIPT_DIR/test-compose-generator.sh"
    
    log_subsection "Test: Deploy Docker"
    run_test_suite "deploy-docker tests" "$SCRIPT_DIR/test-deploy-docker.sh"
    
    log_subsection "Test: Configuration Persistence"
    run_test_suite "configuration persistence tests" "$SCRIPT_DIR/test-config-persistence.sh"
    
    log_subsection "Test: Integration Tests"
    run_test_suite "integration tests" "$SCRIPT_DIR/test-integration.sh"
    
    log_subsection "Test: User Scenario"
    run_test_suite "user scenario tests" "$SCRIPT_DIR/test-user-scenario-complete.sh"
}

################################################################################
# Summary Report
################################################################################

print_summary() {
    local elapsed=$(($(date +%s) - TEST_START_TIME))
    local elapsed_min=$((elapsed / 60))
    local elapsed_sec=$((elapsed % 60))
    
    log_section "Test Summary Report"
    
    echo ""
    echo "Test Execution Statistics:"
    echo -e "  • Total Tests Run:    ${CYAN}$TESTS_RUN${NC}"
    echo -e "  • Passed:             ${GREEN}$TESTS_PASSED${NC}"
    echo -e "  • Failed:             $([ $TESTS_FAILED -gt 0 ] && echo "${RED}" || echo "${GREEN}")$TESTS_FAILED${NC}"
    echo -e "  • Skipped:            ${YELLOW}$TESTS_SKIPPED${NC}"
    echo -e "  • Execution Time:     ${CYAN}${elapsed_min}m ${elapsed_sec}s${NC}"
    echo ""
    
    if [[ $TESTS_FAILED -gt 0 ]]; then
        echo -e "${RED}Failed Tests:${NC}"
        for test in "${FAILED_TESTS[@]}"; do
            echo "  • $test"
        done
        echo ""
    fi
    
    # Success/Failure indicator
    if [[ $TESTS_FAILED -eq 0 ]]; then
        echo -e "${GREEN}════════════════════════════════════════════════════════════${NC}"
        echo -e "${GREEN}✓ ALL TESTS PASSED - Ready to commit!${NC}"
        echo -e "${GREEN}════════════════════════════════════════════════════════════${NC}"
        return 0
    else
        echo -e "${RED}════════════════════════════════════════════════════════════${NC}"
        echo -e "${RED}✗ SOME TESTS FAILED - Fix issues before committing${NC}"
        echo -e "${RED}════════════════════════════════════════════════════════════${NC}"
        return 1
    fi
}

show_help() {
    cat << EOF
Usage: $0 [OPTIONS]

Comprehensive test suite for Docker deployment scripts

OPTIONS:
    --quick                Run quick sanity checks only (faster)
    --verbose              Show detailed test output
    --help                 Show this help message

EXAMPLES:
    $0                     # Run full test suite
    $0 --quick             # Run quick sanity checks
    $0 --verbose           # Run with detailed output

WHEN TO RUN THIS:
    Before committing changes to:
    • scripts/docker/compose-generator.sh
    • scripts/deploy-docker.sh
    • scripts/docker/compose-templates/*
    • Any Docker configuration files

EXIT CODES:
    0 = All tests passed
    1 = Some tests failed
    2 = Invalid arguments or setup error

ENVIRONMENT VARIABLES:
    VERBOSE                Set to 'true' for verbose output
    QUICK_MODE             Set to 'true' for quick mode

For more information, see docs/DEPLOYMENT_TESTING.md

EOF
}

################################################################################
# Main Execution
################################################################################

main() {
    # Parse arguments
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --quick)
                QUICK_MODE="true"
                shift
                ;;
            --verbose)
                VERBOSE="true"
                shift
                ;;
            --help|-h)
                show_help
                exit 0
                ;;
            *)
                log_error "Unknown option: $1"
                show_help
                exit 2
                ;;
        esac
    done
    
    # Header
    echo ""
    log_section "PrintFarmer Deployment Test Suite"
    log_info "Repository: $REPO_ROOT"
    log_info "Test Mode: $([ "$QUICK_MODE" = "true" ] && echo "QUICK" || echo "FULL")"
    log_info "Verbose: $VERBOSE"
    
    # Check dependencies
    if ! check_dependencies; then
        exit 2
    fi
    
    # Run tests
    if [[ "$QUICK_MODE" == "true" ]]; then
        run_quick_tests
    else
        run_full_tests
    fi
    
    # Print summary
    if print_summary; then
        exit 0
    else
        exit 1
    fi
}

# Run main function
main "$@"
