#!/usr/bin/env bash
# Render the Mermaid `.mmd` to PNG using mermaid-cli
# Usage: ./render-diagram.sh

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
INPUT="$HERE/slicer-profiles-db-diagram.mmd"
OUTPUT="$HERE/slicer-profiles-db-diagram.png"

if ! command -v npx >/dev/null 2>&1; then
  echo "npx not found. Install Node.js and npm first."
  exit 1
fi

echo "Rendering $INPUT -> $OUTPUT"
npx @mermaid-js/mermaid-cli -i "$INPUT" -o "$OUTPUT" --width 1600 || {
  echo "Rendering failed. If Puppeteer/Chromium fails in headless CI, try running with a real display or in a full dev environment." >&2
  exit 2
}

echo "Done: $OUTPUT"
