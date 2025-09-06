#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

FREEZE_FILE=".soft-freeze"
if [[ ! -f $FREEZE_FILE ]]; then
  echo "No soft freeze active ($FREEZE_FILE missing)."
  exit 0
fi

BASE_REF=${BASE_REF:-origin/main}
if ! git rev-parse --verify "$BASE_REF" >/dev/null 2>&1; then
  echo "Fetching $BASE_REF..." >&2
  git fetch origin main:origin/main --depth=1 || true
fi

CHANGED=$(git diff --name-only "$BASE_REF"...HEAD)
if [[ -z "$CHANGED" ]]; then
  echo "No changes vs $BASE_REF"
  exit 0
fi

RESTRICTED_REGEX='^(package.json|package-lock.json|global.json|.*\\.csproj|Directory.Build.props|vite.config\\..*|vitest.config\\..*|tsconfig\\..*|Dockerfile.*|docker-compose.*\\.yml|.github/workflows/.*|scripts/.*)'
VIOLATIONS=()
while IFS= read -r f; do
  if [[ $f =~ $RESTRICTED_REGEX ]]; then
    VIOLATIONS+=("$f")
  fi
done <<< "$CHANGED"

if (( ${#VIOLATIONS[@]} == 0 )); then
  echo "Soft freeze: OK (no restricted files modified)"
  exit 0
fi

if git log -n 1 --pretty=%B | grep -q '\[freeze-exception\]'; then
  echo "Restricted changes present but commit contains [freeze-exception] marker — allowed."
  exit 0
fi

echo "Soft freeze violation: Restricted files modified without exception marker:" >&2
printf ' - %s\n' "${VIOLATIONS[@]}" >&2
echo "Add label allow-freeze-exception in PR or include [freeze-exception] in a commit message." >&2
exit 2
