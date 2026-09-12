#!/bin/bash
# Compatibility entry point for the canonical regeneration/recreation repair.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec bash "$SCRIPT_DIR/fix-discovery-heartbeat.sh" "$@"
