#!/usr/bin/env bash
set -euo pipefail

# Sync monorepo version consumers to the root VERSION file.
#
# Usage:
#   ./scripts/sync-monorepo-version.sh           # apply updates
#   ./scripts/sync-monorepo-version.sh --check   # verify only

readonly ROOT="$(git rev-parse --show-toplevel)"
readonly VERSION_FILE="$ROOT/VERSION"
readonly WEB_PACKAGE_JSON="$ROOT/src/Web/ReactApp/package.json"

MODE="apply"
if [[ "${1:-}" == "--check" ]]; then
  MODE="check"
fi

if [[ ! -f "$VERSION_FILE" ]]; then
  echo "VERSION file not found at $VERSION_FILE" >&2
  exit 1
fi

if [[ ! -f "$WEB_PACKAGE_JSON" ]]; then
  echo "Web package.json not found at $WEB_PACKAGE_JSON" >&2
  exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required for JSON-safe version sync" >&2
  exit 1
fi

RAW_VERSION="$(tr -d '[:space:]' < "$VERSION_FILE")"
SEMVER="${RAW_VERSION#v}"

if [[ ! "$SEMVER" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "VERSION must be semantic (vX.Y.Z or X.Y.Z). Found: $RAW_VERSION" >&2
  exit 1
fi

CURRENT_WEB_VERSION="$(python3 - <<'PY' "$WEB_PACKAGE_JSON"
import json
import sys
with open(sys.argv[1], encoding='utf-8') as f:
    data = json.load(f)
print(data.get('version', ''))
PY
)"

if [[ "$CURRENT_WEB_VERSION" != "$SEMVER" ]]; then
  if [[ "$MODE" == "check" ]]; then
    echo "Version drift: src/Web/ReactApp/package.json has $CURRENT_WEB_VERSION, expected $SEMVER" >&2
    exit 2
  fi

  python3 - <<'PY' "$WEB_PACKAGE_JSON" "$SEMVER"
import json
import sys

path, version = sys.argv[1], sys.argv[2]
with open(path, encoding='utf-8') as f:
    data = json.load(f)
data['version'] = version
with open(path, 'w', encoding='utf-8') as f:
    json.dump(data, f, indent=2)
    f.write('\n')
PY

  echo "Updated web package version to $SEMVER"
fi

if [[ "$MODE" == "check" ]]; then
  echo "Version sync check passed ($SEMVER)"
else
  echo "Version sync complete ($SEMVER)"
fi
