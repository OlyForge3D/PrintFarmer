#!/usr/bin/env bash
# Bootstrap script for macOS (Monterey/Big Sur/Apple Silicon/Intel)
# Installs Homebrew (if missing), .NET 9.x (via dotnet-install or Homebrew), Node.js >=20.19 (recommended v20.19.0), git
# Designed to be idempotent.

set -euo pipefail

REQ_DOTNET_VERSION=${DOTNET_VERSION:-9.0.302}
# Default to Node 20.x to match frontend toolchain (Vite requires Node >=20.19)
NODE_VERSION=${NODE_VERSION:-20}

# If running inside a VS Code devcontainer, skip host-level package installation.
if [ -n "${DEVCONTAINER:-}" ] || [ -f "/.devcontainer" ] || [ -d ".devcontainer" ]; then
  echo "[bootstrap] Detected devcontainer environment — skipping host bootstrap steps."
  echo "[bootstrap] Reopen the repository in the devcontainer (VS Code Remote - Containers) to provision the workspace."
  exit 0
fi

print() { echo -e "[bootstrap] $*"; }

# CLI flags
VERIFY=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --verify)
      VERIFY=true
      shift
      ;;
    *) shift ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Helper to run commands with sudo when necessary
run_priv() {
  if [ "$(id -u)" -eq 0 ]; then
    eval "$*"
  else
    sudo bash -c "$*"
  fi
}

run_as_user() {
  local cmd="$*"
  if [ -n "${SUDO_USER:-}" ] && [ "${SUDO_USER:-}" != "root" ]; then
    sudo -u "$SUDO_USER" bash -lc "$cmd"
  else
    bash -lc "$cmd"
  fi
}

# Ensure we have a package manager (Homebrew)
if ! command -v brew >/dev/null 2>&1; then
  print "Homebrew not found — installing Homebrew"
  /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
  # On Apple Silicon, add brew to PATH for this session
  if [ -d /opt/homebrew/bin ]; then
    eval "$(/opt/homebrew/bin/brew shellenv)"
  elif [ -d /usr/local/bin ]; then
    eval "$(/usr/local/bin/brew shellenv)" || true
  fi
else
  print "Homebrew found: $(brew --version | head -n1)"
fi

print "Updating Homebrew"
brew update || true

# Install Node.js via Homebrew (default major is 20). For per-user pinning prefer nvm as documented in LOCAL_DEVELOPMENT.md
# Default NODE_VERSION is 20.
if ! command -v node >/dev/null 2>&1 || [[ "$(node -v)" != v${NODE_VERSION}* ]]; then
  print "Installing Node.js ${NODE_VERSION} via Homebrew"
  run_priv "brew install node@${NODE_VERSION}"
  # Link into PATH
  run_priv "brew link --force --overwrite node@${NODE_VERSION} || true"
else
  print "Node.js present: $(node -v)"
fi

# Git
if ! command -v git >/dev/null 2>&1; then
  print "Installing Git via Homebrew"
  run_priv "brew install git"
else
  print "git present: $(git --version)"
fi

# Python3 and ruamel.yaml (CRITICAL for Docker Compose YAML generation)
# ruamel.yaml is required by compose-generator.sh for proper YAML handling
if ! command -v python3 >/dev/null 2>&1; then
  print "Installing Python3 via Homebrew"
  run_priv "brew install python3"
else
  print "Python3 present: $(python3 --version)"
fi

# Install ruamel.yaml Python module (CRITICAL DEPENDENCY)
if ! python3 -c "from ruamel.yaml import YAML" 2>/dev/null; then
  print "Installing Python module ruamel.yaml (CRITICAL for Docker deployment)..."
  python3 -m pip install --user ruamel.yaml || run_priv "pip3 install ruamel.yaml"
else
  print "ruamel.yaml already installed"
fi

# Try Homebrew dotnet first (may not have exact pinned versions). If not available
# or if you prefer a pinned version, use the repo-local dotnet-install script.
if ! command -v dotnet >/dev/null 2>&1; then
  print "Attempting to install .NET SDK ${REQ_DOTNET_VERSION} using dotnet-install script"
  if [ -f "${REPO_ROOT:-$(pwd)}/dotnet-install.sh" ]; then
    bash "${REPO_ROOT:-$(pwd)}/dotnet-install.sh" --version ${REQ_DOTNET_VERSION}
    export PATH="$HOME/.dotnet:$PATH"
    print ".NET installed via dotnet-install.sh"
  else
    print "dotnet-install.sh not found in repo root. Trying Homebrew (may install latest dotnet)."
  run_priv "brew install --cask dotnet-sdk || brew install dotnet-sdk || true"
    if command -v dotnet >/dev/null 2>&1; then
      print ".NET installed: $(dotnet --info | head -n1)"
    else
      print "Failed to install dotnet via Homebrew. Please install manually from https://dotnet.microsoft.com/download"
    fi
  fi
else
  print ".NET present: $(dotnet --info 2>/dev/null | head -n1 || true)"
fi

print "Bootstrap complete. Please run the following as your normal user (if PATH was modified by dotnet-install.sh):"
cat <<'EOF'
export PATH="$HOME/.dotnet:$PATH"

# Verify
dotnet --info
node --version
npm --version
git --version

# Build repository
cd src
dotnet restore ./farm-web.sln
dotnet build ./farm-web.sln -c Debug
EOF

print "Done."

if [ "$VERIFY" = true ]; then
  print "Running verification (--verify) checks as non-root user"
  run_as_user "export PATH=\"$HOME/.dotnet:$PATH\"; dotnet --info || true; node --version || true; npm --version || true; git --version || true"
  if [ -f "$REPO_ROOT/src/api/Farm.Web.Api.csproj" ]; then
    run_as_user "cd '$REPO_ROOT/src' && dotnet restore ./farm-web.sln && dotnet build ./api/Farm.Web.Api.csproj -c Debug --no-restore"
  else
    print "API project not found for smoke test; skipping build"
  fi
  print "Verification complete"
fi