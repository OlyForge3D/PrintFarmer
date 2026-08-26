#!/bin/bash

# Compose OrcaSlicer's immutable stock profiles and version-pinned custom
# profiles into the worker's single scanner root.

set -euo pipefail

readonly stock_root="${ORCA_STOCK_PROFILES_PATH:-/opt/orcaslicer/resources/profiles}"
readonly custom_root="${ORCA_CUSTOM_PROFILES_PATH:-/app/custom-profiles}"
readonly overlay_root="${ORCA_PROFILES_PATH:-/app/profiles}"

if [[ ! -d "$stock_root" ]]; then
    printf 'ERROR: OrcaSlicer stock profile root does not exist: %s\n' "$stock_root" >&2
    exit 1
fi

if [[ "$stock_root" == "$custom_root" \
    || "$stock_root" == "$overlay_root" \
    || "$custom_root" == "$overlay_root" ]]; then
    printf 'ERROR: stock, custom, and overlay profile roots must be distinct\n' >&2
    exit 1
fi

mkdir -p "$custom_root" "$overlay_root"
find "$overlay_root" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +

link_profile_entries() {
    local source_root="$1"
    local entry

    shopt -s nullglob
    for entry in "$source_root"/*; do
        local destination="$overlay_root/$(basename "$entry")"
        if [[ -e "$destination" || -L "$destination" ]]; then
            printf 'ERROR: duplicate OrcaSlicer profile overlay entry: %s\n' \
                "$(basename "$entry")" >&2
            exit 1
        fi

        ln -s "$entry" "$destination"
    done
    shopt -u nullglob
}

link_profile_entries "$stock_root"
link_profile_entries "$custom_root"

exec "$@"
