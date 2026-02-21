#!/usr/bin/env bash
# Bootstrap script for Ubuntu (20.04/22.04/24.04)
# Installs prerequisites to build and run PrintFarmer (dotnet 10.0.x, Node.js >=24.13, npm, git, build-essential)
# Designed to be idempotent and safe to run multiple times.

set -euo pipefail

# If running inside a VS Code devcontainer, skip host-level package installation.
# Devcontainers already provision the container image with dotnet/node and run post-create steps.
# NOTE: Check for DEVCONTAINER env var OR /.devcontainer at container root (NOT .devcontainer in repo!)
if [ -n "${DEVCONTAINER:-}" ] || [ -f "/.devcontainer" ]; then
  echo "[bootstrap] Detected devcontainer environment — skipping host bootstrap steps."
  echo "[bootstrap] Use the devcontainer postCreateCommand or reopen in container to provision the workspace."
  exit 0
fi

# CLI flags
VERIFY=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --verify)
      VERIFY=true
      shift
      ;;
    *)
      shift
      ;;
  esac
done

# Repository root for smoke-tests
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

REQ_DOTNET_VERSION=${DOTNET_VERSION:-10.0.x}
# Default to Node 24.x LTS to match frontend toolchain requirements
NODE_VERSION=${NODE_VERSION:-24}

print() { echo -e "[bootstrap] $*"; }
print() { echo -e "[bootstrap] $*"; }

# Helper: run a command as root when needed
run_priv() {
  if [ "$(id -u)" -eq 0 ]; then
    eval "$*"
  else
    sudo bash -c "$*"
  fi
}

# Run a command as the unprivileged invoking user (if the script was called via sudo)
run_as_user() {
  local cmd="$*"
  if [ -n "${SUDO_USER:-}" ] && [ "${SUDO_USER:-}" != "root" ]; then
    sudo -u "$SUDO_USER" bash -lc "$cmd"
  else
    bash -lc "$cmd"
  fi
}

print "Updating apt repositories..."
run_priv "apt-get update -y"

print "Installing core packages..."
run_priv "apt-get install -y --no-install-recommends \
  apt-transport-https \
  ca-certificates \
  curl \
  gnupg \
  lsb-release \
  software-properties-common \
  build-essential \
  git \
  wget \
  unzip \
  locales" || true

# Ensure locale to avoid warning in some dotnet installers
if ! locale -a | grep -q "en_US.utf8"; then
  run_priv "locale-gen en_US.UTF-8" || true
fi
export LANG=en_US.UTF-8

# Install Node.js (NodeSource) or skip if present. Default NODE_VERSION is 20.
if ! command -v node >/dev/null 2>&1 || [[ "$(node -v)" != v${NODE_VERSION}* ]]; then
  print "Installing Node.js ${NODE_VERSION} (NodeSource)"
  # This installs the matching Node major (e.g. 20.x). For precise pinning (20.19.0)
  # prefer using nvm as documented in LOCAL_DEVELOPMENT.md. NodeSource provides
  # system-wide Node packages which are suitable for CI/VMs.
  curl -fsSL https://deb.nodesource.com/setup_${NODE_VERSION}.x | sudo bash -
  run_priv "apt-get install -y nodejs"
else
  print "Node.js already installed: $(node -v)"
fi

# Install npm (comes with nodejs package normally)
if ! command -v npm >/dev/null 2>&1; then
  run_priv "apt-get install -y npm"
else
  print "npm already installed: $(npm -v)"
fi

# Install Git if missing
if ! command -v git >/dev/null 2>&1; then
  run_priv "apt-get install -y git"
else
  print "git already installed: $(git --version)"
fi

# Install Python3 and ruamel.yaml (CRITICAL for Docker Compose YAML generation)
# ruamel.yaml is required by compose-generator.sh for proper YAML handling
if ! command -v python3 >/dev/null 2>&1; then
  print "Installing Python3..."
  run_priv "apt-get install -y python3 python3-pip"
else
  print "Python3 already installed: $(python3 --version)"
fi

# Install ruamel.yaml Python module (CRITICAL DEPENDENCY)
if ! python3 -c "from ruamel.yaml import YAML" 2>/dev/null; then
  print "Installing Python module ruamel.yaml (CRITICAL for Docker deployment)..."
  run_priv "apt-get install -y python3-ruamel.yaml || pip3 install ruamel.yaml"
else
  print "ruamel.yaml already installed"
fi

# Install dotnet 9 using the Microsoft package repo and preferred fixed version.
# If the bundled `dotnet-install.sh` script is present in the repo, fall back to it as a last resort.
if ! command -v dotnet >/dev/null 2>&1; then
  print "Installing .NET SDK ${REQ_DOTNET_VERSION} (from Microsoft packages)"

  # Add Microsoft package signing key and feed
  wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb || true
  if [ -f /tmp/packages-microsoft-prod.deb ]; then
    run_priv "dpkg -i /tmp/packages-microsoft-prod.deb || true"
    run_priv "rm -f /tmp/packages-microsoft-prod.deb"
  else
    print "Warning: failed to download Microsoft packages file; falling back to dotnet-install script"
  fi

  run_priv "apt-get update -y"
  # Install the SDK (this will pick a matching 10.x package). We try to request the SDK meta-package.
  if run_priv "apt-get install -y dotnet-sdk-10"; then
    print ".NET SDK installed from apt (dotnet --info follows)"
    dotnet --info || true
  else
    print "apt install of dotnet-sdk-10 failed; trying manual dotnet-install.sh script"
    # Fallback to repo-local dotnet-install.sh if present
    if [ -f "${REPO_ROOT:-/root}/dotnet-install.sh" ]; then
      print "Using repo-local dotnet-install.sh to install ${REQ_DOTNET_VERSION}"
      bash "${REPO_ROOT:-/root}/dotnet-install.sh" --version ${REQ_DOTNET_VERSION}
      export PATH="$HOME/.dotnet:$PATH"
      dotnet --info || true
    else
      print "No dotnet-install.sh available; please install dotnet SDK ${REQ_DOTNET_VERSION} manually or put dotnet-install.sh in repository root."
      exit 2
    fi
  fi
else
  print ".NET already present: $(dotnet --info 2>/dev/null | head -n1 || true)"
fi

# Restore project-level dependencies (dotnet + npm)
print "Restoring .NET dependencies..."
if [ -f "$REPO_ROOT/src/farm-web.sln" ]; then
  run_as_user "cd '$REPO_ROOT/src' && dotnet restore ./farm-web.sln"
else
  print "Warning: farm-web.sln not found at $REPO_ROOT/src — skipping dotnet restore"
fi

print "Installing React/frontend dependencies (npm install)..."
if [ -f "$REPO_ROOT/src/Web/ReactApp/package.json" ]; then
  run_as_user "cd '$REPO_ROOT/src/Web/ReactApp' && npm install"
else
  print "Warning: package.json not found at $REPO_ROOT/src/Web/ReactApp — skipping npm install"
fi

print "Bootstrap complete. Verify with:"
cat <<'EOF'
# Ensure dotnet is in PATH if installed via dotnet-install.sh
export PATH="$HOME/.dotnet:$PATH"

dotnet --info
node --version
npm --version

# Build
cd src
dotnet build ./farm-web.sln -c Debug

# Run tests
dotnet test ./farm-web.sln -c Debug
cd Web/ReactApp && npm run test:run
EOF

print "Done."

# If requested, run verification/smoke tests as the non-root user
if [ "$VERIFY" = true ]; then
  print "Running verification (--verify) checks as non-root user"
  run_as_user "export PATH=\"$HOME/.dotnet:$PATH\"; dotnet --info || true; node --version || true; npm --version || true; git --version || true"
  # Small smoke test: build the API project and check vitest is available
  if [ -f "$REPO_ROOT/src/api/Farm.Web.Api.csproj" ]; then
    print "Running dotnet build smoke test (API project)"
    run_as_user "cd '$REPO_ROOT/src' && dotnet build ./api/Farm.Web.Api.csproj -c Debug --no-restore"
  else
    print "API project not found for smoke test; skipping build"
  fi
  if [ -x "$REPO_ROOT/src/Web/ReactApp/node_modules/.bin/vitest" ]; then
    print "vitest binary found — React test runner available"
  else
    print "Warning: vitest binary not found — run 'npm install' in src/Web/ReactApp/"
  fi
  print "Verification complete"
fi
