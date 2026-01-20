#!/usr/bin/env bash
set -euo pipefail

# Fail if any Docker Compose file contains a top-level 'version:' key
# Docker Compose v2+ doesn't require the version key and it's deprecated
#
# Usage: check-compose-version.sh [path1] [path2] ...
#   Checks specified paths (files or directories) for deprecated version keys

if [ $# -eq 0 ]; then
  echo "Usage: $0 <path1> [path2] ..." >&2
  echo "Example: $0 scripts/docker/compose-templates/ .dive-ci.yml" >&2
  exit 1
fi

found=0

echo "Scanning Docker Compose files for deprecated top-level 'version:' keys..."

for path in "$@"; do
  if [ ! -e "$path" ]; then
    echo "Warning: Path does not exist: $path" >&2
    continue
  fi
  
  # If it's a directory, find all .yml and .yaml files
  if [ -d "$path" ]; then
    while IFS= read -r -d '' file; do
      if awk '/^[[:space:]]*version[[:space:]]*:/ {exit 0} END {exit 1}' "$file" 2>/dev/null; then
        echo "Found deprecated version key in: $file"
        awk '/^[[:space:]]*version[[:space:]]*:/ {print "  Line "NR": "$0; exit 0}' "$file"
        found=1
      fi
    done < <(find "$path" -type f \( -name '*.yml' -o -name '*.yaml' \) -print0)
  # If it's a file, check it directly
  elif [ -f "$path" ]; then
    if awk '/^[[:space:]]*version[[:space:]]*:/ {exit 0} END {exit 1}' "$path" 2>/dev/null; then
      echo "Found deprecated version key in: $path"
      awk '/^[[:space:]]*version[[:space:]]*:/ {print "  Line "NR": "$0; exit 0}' "$path"
      found=1
    fi
  fi
done

if [ "$found" -ne 0 ]; then
  echo "ERROR: Docker Compose files contain deprecated 'version:' keys. Please remove them." >&2
  echo "Note: The 'version' key is obsolete in Docker Compose v2. See: https://docs.docker.com/compose/compose-file/" >&2
  exit 2
else
  echo "No deprecated 'version:' keys found in Docker Compose files. ✅"
fi
