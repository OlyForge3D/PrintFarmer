#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

README="README.md"
START_MARK="<!-- SOFT_FREEZE_BADGE_START -->"
END_MARK="<!-- SOFT_FREEZE_BADGE_END -->"

if ! grep -q "$START_MARK" "$README"; then
  echo "Markers not found in README; aborting." >&2
  exit 1
fi

if [[ -f .soft-freeze ]]; then
  BADGE='![Soft Freeze](https://img.shields.io/badge/soft%20freeze-active-red)'
else
  BADGE='![Soft Freeze](https://img.shields.io/badge/soft%20freeze-inactive-green)'
fi

CURRENT=$(awk -v start="$START_MARK" -v end="$END_MARK" 'BEGIN{found=0} $0~start{found=1;next} $0~end{found=0} found{print}' "$README" | tr -d '\n')
if [[ "$CURRENT" == "$BADGE" ]]; then
  echo "Badge already up to date ($BADGE)"
  exit 0
fi

tmp=$(mktemp)
awk -v start="$START_MARK" -v end="$END_MARK" -v badge="$BADGE" '
  $0~start {print; print badge; skip=1; next}
  $0~end {print; skip=0; next}
  skip!=1 {print}
' "$README" > "$tmp"
mv "$tmp" "$README"
echo "Updated soft freeze badge -> $BADGE"
