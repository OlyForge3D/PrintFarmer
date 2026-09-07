#!/usr/bin/env bash
# Shared commit identity resolution for local production image builds.

BUILD_METADATA_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./common-utils.sh
source "$BUILD_METADATA_SCRIPT_DIR/common-utils.sh"
unset BUILD_METADATA_SCRIPT_DIR

# In a Git checkout, an explicitly supplied SHA must match HEAD so current
# source cannot be mislabeled. Source archives may supply a full SHA because
# they have no Git metadata to inspect.
resolve_local_build_git_sha() {
    local repo_root="$1"
    local requested_sha="${2:-}"
    local repository_sha

    repository_sha="$(git -C "$repo_root" rev-parse HEAD 2>/dev/null || true)"
    if [[ "$repository_sha" =~ ^[0-9a-fA-F]{40}$ ]]; then
        repository_sha="$(printf '%s' "$repository_sha" | tr '[:upper:]' '[:lower:]')"
        if [[ -n "$requested_sha" ]]; then
            if [[ ! "$requested_sha" =~ ^[0-9a-fA-F]{40}$ ]]; then
                print_error "A full 40-character GIT_SHA is required to build identifiable images." >&2
                return 1
            fi
            requested_sha="$(printf '%s' "$requested_sha" | tr '[:upper:]' '[:lower:]')"
            if [[ "$requested_sha" != "$repository_sha" ]]; then
                print_error "GIT_SHA must match the checked-out source commit ($repository_sha)." >&2
                return 1
            fi
        fi
        printf '%s\n' "$repository_sha"
        return 0
    fi

    if [[ ! "$requested_sha" =~ ^[0-9a-fA-F]{40}$ ]]; then
        print_error "A full 40-character GIT_SHA is required when Git metadata is unavailable." >&2
        return 1
    fi
    printf '%s' "$requested_sha" | tr '[:upper:]' '[:lower:]'
    printf '\n'
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    print_error "This script provides shared functions and must be sourced." >&2
    exit 1
fi
