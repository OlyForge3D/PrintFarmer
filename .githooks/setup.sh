#!/usr/bin/env bash
# =============================================================================
# Setup script — configure git to use .githooks/ for hooks
# Idempotent, safe to run multiple times. macOS + Linux compatible.
# =============================================================================

set -euo pipefail

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
readonly HOOKS_DIR="$SCRIPT_DIR"

# ---------------------------------------------------------------------------
# Color helpers
# ---------------------------------------------------------------------------
if [[ -t 1 ]]; then
  readonly GREEN='\033[0;32m'
  readonly YELLOW='\033[0;33m'
  readonly BOLD='\033[1m'
  readonly RESET='\033[0m'
else
  readonly GREEN='' YELLOW='' BOLD='' RESET=''
fi

info()    { printf "${GREEN}✅ %s${RESET}\n" "$1"; }
warn()    { printf "${YELLOW}⚠️  %s${RESET}\n" "$1"; }
heading() { printf "\n${BOLD}%s${RESET}\n" "$1"; }

# ---------------------------------------------------------------------------
# 1. Set core.hooksPath
# ---------------------------------------------------------------------------
heading "Git hooks setup"

CURRENT_PATH="$(git -C "$REPO_ROOT" config --local core.hooksPath 2>/dev/null || true)"
if [[ "$CURRENT_PATH" == ".githooks" ]]; then
  info "core.hooksPath already set to .githooks/"
else
  git -C "$REPO_ROOT" config --local core.hooksPath .githooks
  info "Set core.hooksPath → .githooks/"
fi

# ---------------------------------------------------------------------------
# 2. Make hooks executable
# ---------------------------------------------------------------------------
chmod +x "$HOOKS_DIR/pre-commit"
info "pre-commit hook is executable"

# ---------------------------------------------------------------------------
# 3. Check for optional tools
# ---------------------------------------------------------------------------
heading "Tool check"

check_tool() {
  local name="$1" install_hint="$2"
  if command -v "$name" >/dev/null 2>&1; then
    info "$name found"
  else
    warn "$name not found — $install_hint"
  fi
}

check_tool shellcheck "brew install shellcheck  (or apt install shellcheck)"
check_tool yamllint   "pip install yamllint"
check_tool node       "install Node.js 24+ from https://nodejs.org"
check_tool dotnet     "install .NET 10 SDK from https://dot.net"

echo ""
info "Done — pre-commit hook is active for this repo."
