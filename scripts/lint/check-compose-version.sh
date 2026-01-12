#!/usr/bin/env bash
set -euo pipefail

# Fail if any YAML/compose file contains a top-level 'version:' key (case-insensitive)
# This scans tracked compose and YAML files under the repository.

repo_root=$(git rev-parse --show-toplevel 2>/dev/null || echo ".")
found=0

echo "Scanning repository for top-level 'version:' keys in YAML files..."

while IFS= read -r -d '' file; do
  # skip files under .git
  rel=${file#${repo_root}/}
  # Use awk to find lines that start with optional whitespace followed by 'version:'
  if awk 'BEGIN{IGNORECASE=1} /^[[:space:]]*version[[:space:]]*:/ {print FILENAME":"FNR":"$0; exit 0}' "$file" >/dev/null 2>&1; then
    echo "Found version key in: $rel"
    awk 'BEGIN{IGNORECASE=1} /^[[:space:]]*version[[:space:]]*:/ {print FILENAME":"FNR":"$0; exit 0}' "$file"
    found=1
  fi
done < <(find "$repo_root" -type f \( -name '*.yml' -o -name '*.yaml' -o -name 'docker-compose*' \) -not -path '*/.git/*' -print0)

if [ "$found" -ne 0 ]; then
  echo "ERROR: One or more YAML files contain a top-level 'version:' key. Please remove it." >&2
  exit 2
else
  echo "No top-level 'version:' keys found. ✅"
fi
