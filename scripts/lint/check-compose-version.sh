#!/usr/bin/env bash
set -euo pipefail

# Fail if any Docker Compose file contains a top-level 'version:' key
# The 'version' key is deprecated in Docker Compose v2 and should be removed
# This script only checks actual Docker Compose files, not other YAML configs

repo_root=$(git rev-parse --show-toplevel 2>/dev/null || echo ".")
found=0

# Files that legitimately use 'version:' key and should be excluded
exclude_patterns=(
  ".github/dependabot.yml"
  ".github/workflows/"
  "openapi/"
  "grafana/"
  ".dive-ci.yml"
)

should_exclude() {
  local file="$1"
  local rel="${file#${repo_root}/}"
  
  for pattern in "${exclude_patterns[@]}"; do
    if [[ "$rel" == *"$pattern"* ]]; then
      return 0
    fi
  done
  return 1
}

echo "Scanning Docker Compose files for deprecated top-level 'version:' keys..."

while IFS= read -r -d '' file; do
  rel="${file#${repo_root}/}"
  
  # Skip excluded files
  if should_exclude "$file"; then
    continue
  fi
  
  # Check if file has top-level 'version:' key (on first line without leading whitespace)
  # For Docker Compose files, version must be on the very first line to be the compose version
  first_line=$(head -1 "$file")
  if [[ "$first_line" =~ ^[[:space:]]*version[[:space:]]*: ]]; then
    echo "Found deprecated version key in: $rel"
    echo "$rel:1:$first_line"
    found=1
  fi
done < <(find "$repo_root" -type f \( -name 'docker-compose*.yml' -o -name 'docker-compose*.yaml' -o -name '*.compose.yml' -o -name '*.compose.yaml' -o -path '*/docker/*.yml' -o -path '*/docker/*.yaml' \) -not -path '*/.git/*' -print0)

if [ "$found" -ne 0 ]; then
  echo "ERROR: Docker Compose files contain deprecated 'version:' keys. Please remove them." >&2
  echo "Note: The 'version' key is obsolete in Docker Compose v2. See: https://docs.docker.com/compose/compose-file/" >&2
  exit 2
else
  echo "No deprecated 'version:' keys found in Docker Compose files. ✅"
fi
