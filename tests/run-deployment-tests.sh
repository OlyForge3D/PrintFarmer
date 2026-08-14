#!/bin/bash

################################################################################
# run-deployment-tests.sh
#
# Note: as of issue #1308, tests/validate-deployment-scripts.sh,
# tests/test-deploy-docker.sh, tests/test-config-persistence.sh, and
# tests/interactive_prompt_harness.sh are also run directly by
# .github/workflows/deployment-tests.yml on every PR/push touching
# scripts/deploy-docker.sh, scripts/docker/**, or tests/**.
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
GUARD_TEMP_DIR=""
GUARD_BASELINE=""
GUARD_VERIFIED=false
QUICK_TEMP_DIR=""

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
# Repository Artifact Isolation Guard
################################################################################

repository_deployment_artifact_paths() {
    {
        printf '%s\n' \
            ".deploy-config" \
            ".env" \
            "Dockerfile.multistage" \
            "dockerfiles" \
            "docker-entrypoint-config.sh" \
            "monitoring" \
            "otel-collector-config.yaml" \
            "security-config.json" \
            "src/Web/ReactApp/.env.production"

        local path
        for path in \
            "$REPO_ROOT"/.env* \
            "$REPO_ROOT"/docker-compose*.yml \
            "$REPO_ROOT"/docker-compose*.yaml; do
            if [[ -e "$path" || -L "$path" ]]; then
                printf '%s\n' "${path#"$REPO_ROOT/"}"
            fi
        done
    } | LC_ALL=C sort -u
}

file_hash() {
    local path="$1"
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$path" | awk '{print $1}'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$path" | awk '{print $1}'
    else
        cksum "$path" | awk '{print $1 ":" $2}'
    fi
}

path_metadata() {
    local path="$1"
    stat -c '%s:%y:%a' "$path" 2>/dev/null \
        || stat -f '%z:%m:%Lp' "$path"
}

append_repository_artifact_snapshot() {
    local snapshot_file="$1"
    local artifact_path="$2"
    local relative_path="$3"

    if [[ -L "$artifact_path" ]]; then
        printf '%s\tsymlink\t%s\t%s\n' \
            "$relative_path" \
            "$(path_metadata "$artifact_path")" \
            "$(readlink "$artifact_path")" >> "$snapshot_file"
    elif [[ -f "$artifact_path" ]]; then
        printf '%s\tfile\t%s\t%s\n' \
            "$relative_path" \
            "$(path_metadata "$artifact_path")" \
            "$(file_hash "$artifact_path")" >> "$snapshot_file"
    elif [[ -d "$artifact_path" ]]; then
        printf '%s\tdirectory\t%s\n' \
            "$relative_path" \
            "$(path_metadata "$artifact_path")" >> "$snapshot_file"
    else
        printf '%s\tabsent\n' "$relative_path" >> "$snapshot_file"
    fi
}

snapshot_repository_deployment_artifacts() {
    local snapshot_file="$1"
    local relative_path
    : > "$snapshot_file"

    while IFS= read -r relative_path; do
        local artifact_path="$REPO_ROOT/$relative_path"
        append_repository_artifact_snapshot "$snapshot_file" "$artifact_path" "$relative_path"

        if [[ -d "$artifact_path" && ! -L "$artifact_path" ]]; then
            local descendant_path
            while IFS= read -r descendant_path; do
                append_repository_artifact_snapshot \
                    "$snapshot_file" \
                    "$descendant_path" \
                    "${descendant_path#"$REPO_ROOT/"}"
            done < <(find "$artifact_path" -mindepth 1 -print | LC_ALL=C sort)
        fi
    done < <(repository_deployment_artifact_paths)
}

assert_test_sources_use_isolated_artifacts() {
    local violations=""
    local test_file

    for test_file in "$SCRIPT_DIR"/test-*.sh "$SCRIPT_DIR"/validate-deployment-scripts.sh; do
        [[ "$(basename "$test_file")" == "test-run-deployment-tests-harness.sh" ]] && continue
        violations+=$(grep -nE \
            '\$\{?REPO_ROOT\}?/\.(deploy-config|env)(["'"'"'[:space:]]|$)|\$\{?REPO_ROOT\}?/docker-compose[^[:space:]"]*|backup_repository_deployment_artifacts|restore_repository_deployment_artifacts|(^|[;&|("'"'"'[:space:]])(cd|pushd)[[:space:]]+(--[[:space:]]+)?["'"'"']?\$\{?REPO_ROOT\}?([/"'"'"'[:space:];&|]|$)' \
            "$test_file" || true)
    done

    if [[ -n "$violations" ]]; then
        log_error "Deployment tests contain forbidden repo-root artifact access:"
        printf '%s\n' "$violations" >&2
        return 1
    fi
}

initialize_repository_artifact_guard() {
    GUARD_TEMP_DIR=$(mktemp -d -t "printfarmer-deployment-guard-XXXXXX")
    GUARD_BASELINE="$GUARD_TEMP_DIR/baseline"
    snapshot_repository_deployment_artifacts "$GUARD_BASELINE"
}

assert_no_repo_root_config_mutation() {
    local current_snapshot="$GUARD_TEMP_DIR/current"
    snapshot_repository_deployment_artifacts "$current_snapshot"

    if cmp -s "$GUARD_BASELINE" "$current_snapshot"; then
        return 0
    fi

    log_error "Deployment tests mutated repo-root deployment artifacts:"
    diff -u "$GUARD_BASELINE" "$current_snapshot" >&2 || true
    return 1
}

cleanup_repository_artifact_guard() {
    local exit_code=$?
    trap - EXIT

    if [[ "$GUARD_VERIFIED" != "true" && -n "$GUARD_BASELINE" ]]; then
        if ! assert_no_repo_root_config_mutation; then
            exit_code=1
        fi
    fi

    if [[ -n "$GUARD_TEMP_DIR" && -d "$GUARD_TEMP_DIR" ]]; then
        rm -rf "$GUARD_TEMP_DIR"
    fi

    if [[ -n "$QUICK_TEMP_DIR" && -d "$QUICK_TEMP_DIR" ]]; then
        rm -rf "$QUICK_TEMP_DIR"
    fi

    exit "$exit_code"
}

################################################################################
# Test Execution Functions
################################################################################

run_test_suite() {
    local suite_name="$1"
    local test_script="$2"
    
    if [[ ! -f "$test_script" ]]; then
        log_warn "Test script not found: $test_script"
        # NOTE: Use assignment form (VAR=$((VAR + 1))) instead of ((VAR++)) — the
        # post-increment form returns exit code 1 when VAR is 0, which under
        # `set -euo pipefail` aborts the orchestrator on the first counter bump.
        TESTS_SKIPPED=$((TESTS_SKIPPED + 1))
        return 0
    fi
    
    log_subsection "Running: $suite_name"
    
    if [[ "$VERBOSE" == "true" ]]; then
        if bash "$test_script"; then
            log_success "$suite_name passed"
            TESTS_PASSED=$((TESTS_PASSED + 1))
            TESTS_RUN=$((TESTS_RUN + 1))
            return 0
        else
            log_error "$suite_name failed"
            FAILED_TESTS+=("$suite_name")
            TESTS_FAILED=$((TESTS_FAILED + 1))
            TESTS_RUN=$((TESTS_RUN + 1))
            return 1
        fi
    else
        if bash "$test_script" >/dev/null 2>&1; then
            log_success "$suite_name passed"
            TESTS_PASSED=$((TESTS_PASSED + 1))
            TESTS_RUN=$((TESTS_RUN + 1))
            return 0
        else
            log_error "$suite_name failed (run with --verbose for details)"
            FAILED_TESTS+=("$suite_name")
            TESTS_FAILED=$((TESTS_FAILED + 1))
            TESTS_RUN=$((TESTS_RUN + 1))
            return 1
        fi
    fi
}

check_dependencies() {
    log_info "Checking test dependencies..."
    
    local missing_deps=0
    
    # Check required commands
    for cmd in bash grep awk sed find; do
        if ! command -v "$cmd" >/dev/null 2>&1; then
            log_error "Required command not found: $cmd"
            missing_deps=$((missing_deps + 1))
        fi
    done
    
    # Check docker (optional but recommended)
    if ! command -v docker >/dev/null 2>&1; then
        log_warn "Docker not found (optional but recommended for full validation)"
    fi
    
    # Check test scripts exist
    if [[ ! -f "$SCRIPT_DIR/test-compose-generator.sh" ]]; then
        log_error "Test script not found: $SCRIPT_DIR/test-compose-generator.sh"
        missing_deps=$((missing_deps + 1))
    fi
    
    if [[ ! -f "$SCRIPT_DIR/test-deploy-docker.sh" ]]; then
        log_error "Test script not found: $SCRIPT_DIR/test-deploy-docker.sh"
        missing_deps=$((missing_deps + 1))
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
    
    local test_temp_dir
    test_temp_dir=$(mktemp -d -t "printfarmer-deployment-quick-XXXXXX")
    QUICK_TEMP_DIR="$test_temp_dir"
    mkdir -p "$test_temp_dir"
    
    log_subsection "Basic Compose Generation"
    
    # Test 1: Help output
    if bash "$REPO_ROOT/scripts/docker/compose-generator.sh" --help >/dev/null 2>&1; then
        log_success "Compose generator help works"
        TESTS_PASSED=$((TESTS_PASSED + 1))
    else
        log_error "Compose generator help failed"
        TESTS_FAILED=$((TESTS_FAILED + 1))
    fi
    TESTS_RUN=$((TESTS_RUN + 1))
    
    # Test 2: Standard generation
    if DB_PROVIDER=postgres bash "$REPO_ROOT/scripts/docker/compose-generator.sh" \
        --output-dir "$test_temp_dir/test1" >/dev/null 2>&1; then
        log_success "Standard compose generation works"
        TESTS_PASSED=$((TESTS_PASSED + 1))
    else
        log_error "Monolithic compose generation failed"
        TESTS_FAILED=$((TESTS_FAILED + 1))
    fi
    TESTS_RUN=$((TESTS_RUN + 1))
    
    # Test 3: Generation with explicit options
    if DB_PROVIDER=postgres bash "$REPO_ROOT/scripts/docker/compose-generator.sh" \
        --output-dir "$test_temp_dir/test2" >/dev/null 2>&1; then
        log_success "Compose generation with options works"
        TESTS_PASSED=$((TESTS_PASSED + 1))
    else
        log_error "Microservices compose generation failed"
        TESTS_FAILED=$((TESTS_FAILED + 1))
    fi
    TESTS_RUN=$((TESTS_RUN + 1))
    
    # Test 4: No duplicate volumes in generated files
    log_subsection "YAML Validation (No Duplicate Volumes)"
    
    local compose_file="$test_temp_dir/test1/docker-compose.yml"
    if [[ -f "$compose_file" ]]; then
        local volumes_count=$(grep -c "^volumes:" "$compose_file" 2>/dev/null || echo "0")
        if [[ "$volumes_count" -eq 1 ]]; then
            log_success "No duplicate volumes in monolithic compose"
            TESTS_PASSED=$((TESTS_PASSED + 1))
        else
            log_error "Found $volumes_count 'volumes:' sections (expected 1)"
            TESTS_FAILED=$((TESTS_FAILED + 1))
        fi
    else
        log_error "Compose file not generated"
        TESTS_FAILED=$((TESTS_FAILED + 1))
    fi
    TESTS_RUN=$((TESTS_RUN + 1))
    
    # Test 5: No duplicate volumes in generated files
    log_subsection "YAML Validation (No Duplicate Volumes)"
    
    compose_file="$test_temp_dir/test2/docker-compose.yml"
    if [[ -f "$compose_file" ]]; then
        volumes_count=$(grep -c "^volumes:" "$compose_file" 2>/dev/null || echo "0")
        if [[ "$volumes_count" -eq 1 ]]; then
            log_success "No duplicate volumes in microservices compose"
            TESTS_PASSED=$((TESTS_PASSED + 1))
        else
            log_error "Found $volumes_count 'volumes:' sections (expected 1)"
            TESTS_FAILED=$((TESTS_FAILED + 1))
        fi
    else
        log_error "Microservices compose file not generated"
        TESTS_FAILED=$((TESTS_FAILED + 1))
    fi
    TESTS_RUN=$((TESTS_RUN + 1))
}

################################################################################
# Full Test Suite
################################################################################

run_full_tests() {
    log_section "Full Test Suite"
    
    # NOTE: `|| true` on each run_test_suite call keeps the orchestrator from
    # aborting under `set -euo pipefail` when a single suite fails. Failed
    # suites are already recorded in FAILED_TESTS and reflected by
    # print_summary's exit code, so continuing runs the remaining suites
    # and surfaces the full pass/fail picture instead of stopping at the
    # first failure.
    log_subsection "Test: Compose Generator"
    run_test_suite "compose-generator tests" "$SCRIPT_DIR/test-compose-generator.sh" || true
    
    log_subsection "Test: Deploy Docker"
    run_test_suite "deploy-docker tests" "$SCRIPT_DIR/test-deploy-docker.sh" || true

    log_subsection "Test: Deployment Validator Result Handling"
    run_test_suite "deployment validator result tests" "$SCRIPT_DIR/test-validate-deployment-scripts.sh" || true

    log_subsection "Test: TLS Certificate Cleanup"
    run_test_suite "TLS certificate cleanup tests" "$SCRIPT_DIR/test-tls-certificate-cleanup.sh" || true

    log_subsection "Test: OrcaSlicer Binary Metadata"
    run_test_suite "OrcaSlicer binary metadata tests" "$SCRIPT_DIR/test-orcaslicer-binary-metadata.sh" || true

    log_subsection "Test: BuildKit Snapshot Corruption Auto-Repair (#1527)"
    run_test_suite "BuildKit snapshot corruption auto-repair tests" "$SCRIPT_DIR/test-buildkit-snapshot-repair.sh" || true
    
    log_subsection "Test: Configuration Persistence"
    run_test_suite "configuration persistence tests" "$SCRIPT_DIR/test-config-persistence.sh" || true

    log_subsection "Test: Database Credential Migration"
    run_test_suite "database credential migration tests" "$SCRIPT_DIR/test-db-credential-migration.sh" || true
    
    log_subsection "Test: Integration Tests"
    run_test_suite "integration tests" "$SCRIPT_DIR/test-integration.sh" || true
    
    log_subsection "Test: User Scenario"
    run_test_suite "user scenario tests" "$SCRIPT_DIR/test-user-scenario-complete.sh" || true
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

    if ! assert_test_sources_use_isolated_artifacts; then
        exit 1
    fi

    initialize_repository_artifact_guard
    trap cleanup_repository_artifact_guard EXIT
    
    # Run tests
    if [[ "$QUICK_MODE" == "true" ]]; then
        run_quick_tests
    else
        run_full_tests
    fi

    if ! assert_no_repo_root_config_mutation; then
        FAILED_TESTS+=("repository artifact isolation guard")
        TESTS_FAILED=$((TESTS_FAILED + 1))
        TESTS_RUN=$((TESTS_RUN + 1))
    fi
    GUARD_VERIFIED=true
    
    # Print summary
    if print_summary; then
        exit 0
    else
        exit 1
    fi
}

# Run main function
main "$@"
