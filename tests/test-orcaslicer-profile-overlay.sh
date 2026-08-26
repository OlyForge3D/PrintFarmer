#!/bin/bash

# Focused tests for the OrcaSlicer worker's single-root profile overlay.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ENTRYPOINT="$REPO_ROOT/scripts/docker/orcaslicer-worker-entrypoint.sh"
TEST_TEMP_DIR=""

source "$SCRIPT_DIR/test-framework.sh"

pass() {
    printf '[PASS] %s\n' "$1"
}

fail() {
    printf '[FAIL] %s\n' "$1" >&2
    exit 1
}

setup() {
    TEST_TEMP_DIR=$(create_test_temp_dir)
}

teardown() {
    cleanup_test_temp_dir "$TEST_TEMP_DIR"
}

trap teardown EXIT
setup

stock_root="$TEST_TEMP_DIR/stock"
custom_root="$TEST_TEMP_DIR/custom"
overlay_root="$TEST_TEMP_DIR/overlay"
mkdir -p \
    "$stock_root/Vendor/machine" \
    "$custom_root/Custom/machine" \
    "$overlay_root/stale"
printf '{}\n' > "$stock_root/Vendor.json"
printf '{}\n' > "$stock_root/Vendor/machine/stock.json"
printf '{}\n' > "$custom_root/Custom.json"
printf '{}\n' > "$custom_root/Custom/machine/custom.json"

ORCA_STOCK_PROFILES_PATH="$stock_root" \
ORCA_CUSTOM_PROFILES_PATH="$custom_root" \
ORCA_PROFILES_PATH="$overlay_root" \
    "$ENTRYPOINT" /bin/true

assert_overlay_link() {
    local link_path="$1"
    local expected_target="$2"
    local description="$3"

    if [[ -L "$link_path" ]]; then
        if [[ "$(readlink "$link_path")" != "$expected_target" ]]; then
            fail "$description points to the wrong source"
        fi
        pass "$description"
        return
    fi

    case "$(uname -s)" in
        MINGW*|MSYS*|CYGWIN*)
            [[ -e "$link_path" ]] \
                || fail "$description did not resolve through Git Bash symlink emulation"
            pass "$description (Git Bash symlink emulation)"
            return
            ;;
        *)
            fail "$description is not a symbolic link"
            ;;
    esac
}

if ! grep -Fq 'ln -s "$entry" "$destination"' "$ENTRYPOINT"; then
    fail "Worker entrypoint must compose the overlay with symbolic links"
else
    pass "Worker entrypoint composes the overlay with symbolic links"
fi

assert_overlay_link \
    "$overlay_root/Vendor" \
    "$stock_root/Vendor" \
    "Stock manufacturer directory should be linked into the overlay"
assert_overlay_link \
    "$overlay_root/Vendor.json" \
    "$stock_root/Vendor.json" \
    "Stock manifest should be linked into the overlay"
assert_overlay_link \
    "$overlay_root/Custom" \
    "$custom_root/Custom" \
    "Custom manufacturer directory should be linked into the overlay"
assert_overlay_link \
    "$overlay_root/Custom.json" \
    "$custom_root/Custom.json" \
    "Custom manifest should be linked into the overlay"
[[ -f "$overlay_root/Vendor/machine/stock.json" ]] \
    || fail "Stock profile should resolve through the directory link"
pass "Stock profile resolves through the directory link"
[[ -f "$overlay_root/Custom/machine/custom.json" ]] \
    || fail "Custom profile should resolve through the directory link"
pass "Custom profile resolves through the directory link"
[[ ! -d "$overlay_root/stale" ]] \
    || fail "Overlay composition should remove stale entries"
pass "Overlay composition removes stale entries"

printf '{}\n' > "$custom_root/Vendor.json"
if ORCA_STOCK_PROFILES_PATH="$stock_root" \
    ORCA_CUSTOM_PROFILES_PATH="$custom_root" \
    ORCA_PROFILES_PATH="$overlay_root" \
    "$ENTRYPOINT" /bin/true >/dev/null 2>&1; then
    fail "Custom profile entries must not overwrite a stock bundle"
else
    pass "Custom profile entries cannot overwrite a stock bundle"
fi

current_compose="$REPO_ROOT/scripts/docker/compose-templates/docker-compose.orcaslicer-worker.yml"
previous_compose="$REPO_ROOT/scripts/docker/compose-templates/docker-compose.orcaslicer-worker-previous.yml"
grep -Fq \
    'name: printfarmer-custom-profiles-${ORCASLICER_VERSION:-2.4.2}' \
    "$current_compose" \
    || fail "Current worker volume should be keyed by its OrcaSlicer version"
pass "Current worker volume is keyed by its OrcaSlicer version"
grep -Fq \
    'name: printfarmer-custom-profiles-${ORCASLICER_VERSION_PREVIOUS:-2.3.1}' \
    "$previous_compose" \
    || fail "Previous worker volume should be keyed by its OrcaSlicer version"
pass "Previous worker volume is keyed by its OrcaSlicer version"
