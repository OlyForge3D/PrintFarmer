#!/usr/bin/env bash
set -euo pipefail

# ============================================================================
# Sync Monorepo Version
# ============================================================================
# Ensures VERSION file is the single source of truth across the monorepo.
# Updates mobile Xcode project marketing version and any other version refs.
#
# Usage:
#   ./scripts/sync-monorepo-version.sh           # Sync version to all targets
#   ./scripts/sync-monorepo-version.sh --check   # Verify versions are in sync (no writes)

SCRIPT_NAME="$(basename "$0")"
readonly SCRIPT_NAME
ROOT="$(git rev-parse --show-toplevel)"
readonly ROOT
readonly VERSION_FILE="$ROOT/VERSION"
readonly MOBILE_PBXPROJ="$ROOT/mobile/PrintFarmer.xcodeproj/project.pbxproj"

CHECK_ONLY=false

usage() {
    echo "Usage: $SCRIPT_NAME [--check]"
    echo "  --check   Verify versions are in sync without modifying files"
    exit 0
}

get_version() {
    if [[ ! -f "$VERSION_FILE" ]]; then
        echo "Error: VERSION file not found at $VERSION_FILE" >&2
        exit 1
    fi
    local raw
    raw="$(tr -d '[:space:]' < "$VERSION_FILE")"
    # Strip leading 'v' if present
    echo "${raw#v}"
}

sync_xcode_version() {
    local version="$1"

    if [[ ! -f "$MOBILE_PBXPROJ" ]]; then
        echo "⚠️  Xcode project not found at $MOBILE_PBXPROJ — skipping mobile sync"
        return 0
    fi

    if "$CHECK_ONLY"; then
        # Check if MARKETING_VERSION matches
        local current
        current=$(grep -m1 'MARKETING_VERSION' "$MOBILE_PBXPROJ" | sed 's/.*= *//;s/ *;.*//' | tr -d '[:space:]')
        if [[ "$current" != "$version" ]]; then
            echo "❌ Mobile MARKETING_VERSION mismatch: got '$current', expected '$version'"
            return 1
        fi
        echo "✅ Mobile MARKETING_VERSION is in sync: $version"
        return 0
    fi

    # Update all MARKETING_VERSION entries in pbxproj
    sed -i.bak "s/MARKETING_VERSION = [^;]*/MARKETING_VERSION = $version/" "$MOBILE_PBXPROJ"
    rm -f "${MOBILE_PBXPROJ}.bak"
    echo "✅ Updated mobile MARKETING_VERSION to $version"
}

main() {
    local version
    version="$(get_version)"

    echo "📦 Monorepo version: $version"

    sync_xcode_version "$version"

    if ! "$CHECK_ONLY"; then
        echo "✅ All version targets synced to $version"
    fi
}

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --check)
            CHECK_ONLY=true
            shift
            ;;
        -h|--help)
            usage
            ;;
        *)
            echo "Unknown option: $1" >&2
            exit 1
            ;;
    esac
done

main
