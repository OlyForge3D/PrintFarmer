#!/bin/bash

set -euo pipefail

artifact_root="${PFARM__ArtifactStorage__RootPath:-${ArtifactStorage__RootPath:-/data/artifacts}}"

fail() {
    printf 'slicer-host entrypoint: %s\n' "$1" >&2
    exit 1
}

if [[ "$(id -u)" != "0" ]]; then
    fail "must start as root to repair the configured artifact storage directory"
fi

if [[ "$artifact_root" != /data/* ]]; then
    fail "ArtifactStorage__RootPath must be a child path of the persistent /data mount"
fi

if [[ "$artifact_root" =~ (^|/)\.\.?(/|$) ]]; then
    fail "ArtifactStorage__RootPath must not contain . or .. path segments"
fi
if [[ "$artifact_root" == *//* ]]; then
    fail "ArtifactStorage__RootPath must not contain repeated separators"
fi

artifact_root="${artifact_root%/}"
[[ "$artifact_root" != "/data" ]] || fail "ArtifactStorage__RootPath must be below /data"

check_parent_permissions() {
    local path="$1"
    local must_exist="$2"
    local metadata owner mode

    if metadata="$(stat -c '%u %a' -- "$path" 2>/dev/null)"; then
        read -r owner mode <<< "$metadata"
        [[ "$owner" == "0" && "$mode" =~ ^[0-7]{3,4}$ ]] ||
            fail "artifact parent must be owned by root: $path"
        (( (8#$mode & 0022) == 0 )) ||
            fail "artifact parent must not be writable by group or others: $path"
    elif [[ "$must_exist" == "yes" ]]; then
        fail "could not inspect artifact parent: $path"
    fi
}

check_path_components() {
    local path="$1"
    local must_exist="$2"
    local current="/"
    local component
    local -a components

    check_parent_permissions "/" "yes"
    IFS='/' read -r -a components <<< "${path#/}"
    for component in "${components[@]}"; do
        [[ -n "$component" ]] || continue
        current="${current%/}/${component}"
        [[ ! -L "$current" ]] || fail "refusing symlink in artifact storage path: $current"
        if [[ "$current" != "$artifact_root" ]]; then
            check_parent_permissions "$current" "$must_exist"
        fi
    done
}

check_path_components "$artifact_root" "no"
mkdir -p -- "$artifact_root" || fail "could not create artifact storage directory: $artifact_root"
check_path_components "$artifact_root" "yes"

if ! symlink_path="$(find -P "$artifact_root" -xdev -type l -print -quit)"; then
    fail "could not inspect artifact storage directory: $artifact_root"
fi
if [[ -n "$symlink_path" ]]; then
    fail "refusing symlinks inside artifact storage directory: $artifact_root"
fi

root_device="$(stat -c '%d' -- "$artifact_root")" ||
    fail "could not inspect artifact storage filesystem"
device_ids="$(find -P "$artifact_root" -xdev -printf '%D\n' | uniq)" ||
    fail "could not inspect artifact storage filesystem boundaries"
[[ "$device_ids" == "$root_device" ]] ||
    fail "refusing a nested filesystem mount inside artifact storage"

app_uid="$(id -u appuser)" || fail "appuser account is missing"
app_gid="$(id -g appuser)" || fail "appuser group is missing"
[[ "$app_uid" =~ ^[0-9]+$ && "$app_gid" =~ ^[0-9]+$ ]] || fail "appuser has an invalid numeric identity"
[[ "$app_uid" != "0" ]] || fail "appuser must not have UID 0"

# Work from each discovered parent directory and do not follow leaf symlinks.
# Nested device boundaries are rejected above; same-device bind mounts cannot
# be distinguished by this check.
find -P "$artifact_root" -xdev -depth -execdir chown -h -- "$app_uid:$app_gid" {} + ||
    fail "could not assign artifact storage ownership to appuser ($app_uid:$app_gid)"

if ! gosu appuser test -w "$artifact_root" || ! gosu appuser test -x "$artifact_root"; then
    fail "artifact storage is not writable and traversable by appuser"
fi
printf 'slicer-host entrypoint: artifact storage ready for appuser (%s:%s)\n' "$app_uid" "$app_gid" >&2

if (( $# == 0 )); then
    set -- dotnet Farm.Slicer.Host.dll
fi

exec gosu appuser "$@"
