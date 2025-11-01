#!/usr/bin/env bash
# Bootstrap script for Ubuntu (20.04/22.04/24.04)
# Installs prerequisites to build and run PrintFarmer (dotnet 9.0.302, Node.js 18+, npm, git, build-essential)
# Designed to be idempotent and safe to run multiple times.

set -euo pipefail

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

REQ_DOTNET_VERSION=${DOTNET_VERSION:-9.0.302}
NODE_VERSION=${NODE_VERSION:-18}

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
  locales || true

# Ensure locale to avoid warning in some dotnet installers
if ! locale -a | grep -q "en_US.utf8"; then
  run_priv "locale-gen en_US.UTF-8" || true
fi
export LANG=en_US.UTF-8

# Install Node.js 18.x (NodeSource)
if ! command -v node >/dev/null 2>&1 || [[ "$(node -v)" != v${NODE_VERSION}* ]]; then
  print "Installing Node.js ${NODE_VERSION} (NodeSource)"
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
  # Install the SDK (this will pick a matching 9.x package). We try to request the SDK meta-package.
  if run_priv "apt-get install -y dotnet-sdk-9"; then
    print ".NET SDK installed from apt (dotnet --info follows)"
    dotnet --info || true
  else
    print "apt install of dotnet-sdk-9 failed; trying manual dotnet-install.sh script"
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

print "Bootstrap complete. Run the following (non-root) commands to verify and continue:"
cat <<'EOF'
# As your normal user (not root):
# Ensure dotnet is in PATH if installed via dotnet-install.sh
export PATH="$HOME/.dotnet:$PATH"

# Verify basic tooling
dotnet --info
node --version
npm --version
git --version

# Build the repo (from the 'src' directory)
cd src
dotnet restore ./farm-web.sln
dotnet build ./farm-web.sln -c Debug
EOF

print "Done."

# If requested, run verification/smoke tests as the non-root user
if [ "$VERIFY" = true ]; then
  print "Running verification (--verify) checks as non-root user"
  run_as_user "export PATH=\"$HOME/.dotnet:$PATH\"; dotnet --info || true; node --version || true; npm --version || true; git --version || true"
  # Small smoke test: build the API project only
  if [ -f "$REPO_ROOT/src/api/Farm.Web.Api.csproj" ]; then
    print "Running small dotnet build smoke test (API project)"
    run_as_user "cd '$REPO_ROOT/src' && dotnet restore ./farm-web.sln && dotnet build ./api/Farm.Web.Api.csproj -c Debug --no-restore"
  else
    print "API project not found for smoke test; skipping build"
  fi
  print "Verification complete"
fi
