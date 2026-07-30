#!/bin/bash

set -euo pipefail

readonly MODE="${1:-}"
readonly EXPECTED_VERSION="${2:-}"
readonly PROJECT_FILE="PrintFarmer.xcodeproj/project.pbxproj"
readonly SOURCE_PLIST="PrintFarmer/Info.plist"

if [[ ! "$EXPECTED_VERSION" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
    echo "Invalid expected marketing version: ${EXPECTED_VERSION:-<missing>}" >&2
    exit 1
fi

case "$MODE" in
    source)
        readonly EXPECTED_TARGET_CONFIGURATIONS=6
        total_marketing_version_count="$(grep -F -c "MARKETING_VERSION = " "$PROJECT_FILE" || true)"
        marketing_version_count="$(grep -F -c "MARKETING_VERSION = ${EXPECTED_VERSION};" "$PROJECT_FILE" || true)"
        versioning_system_count="$(grep -F -c "VERSIONING_SYSTEM = apple-generic;" "$PROJECT_FILE" || true)"
        plist_version="$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$SOURCE_PLIST")"

        if [[ "$total_marketing_version_count" -ne "$EXPECTED_TARGET_CONFIGURATIONS" || "$marketing_version_count" -ne "$EXPECTED_TARGET_CONFIGURATIONS" ]]; then
            echo "Expected all ${EXPECTED_TARGET_CONFIGURATIONS} target configurations at MARKETING_VERSION ${EXPECTED_VERSION}; found ${marketing_version_count} of ${total_marketing_version_count}" >&2
            exit 1
        fi
        if [[ "$versioning_system_count" -ne "$EXPECTED_TARGET_CONFIGURATIONS" ]]; then
            echo "Expected ${EXPECTED_TARGET_CONFIGURATIONS} target configurations using apple-generic versioning; found ${versioning_system_count}" >&2
            exit 1
        fi
        if [[ "$plist_version" != "\$(MARKETING_VERSION)" ]]; then
            echo "Source CFBundleShortVersionString must resolve from MARKETING_VERSION; found ${plist_version}" >&2
            exit 1
        fi
        ;;
    resolved)
        readonly PLIST_PATH="${3:-}"
        if [[ -z "$PLIST_PATH" || ! -f "$PLIST_PATH" ]]; then
            echo "Resolved Info.plist not found: ${PLIST_PATH:-<missing>}" >&2
            exit 1
        fi

        plist_version="$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$PLIST_PATH")"
        if [[ "$plist_version" != "$EXPECTED_VERSION" ]]; then
            echo "Marketing version mismatch in ${PLIST_PATH}: expected ${EXPECTED_VERSION}, got ${plist_version}" >&2
            exit 1
        fi
        ;;
    *)
        echo "Usage: $0 source <expected-version> | resolved <expected-version> <Info.plist>" >&2
        exit 1
        ;;
esac
