#!/usr/bin/env bash
# Reinstall Docker Engine and Compose plugin on Ubuntu 24.04
# Safe interactive script that backs up /var/lib/docker before destructive ops

set -euo pipefail

SCRIPT_NAME=$(basename "$0")

usage() {
  cat <<EOF
Usage: $SCRIPT_NAME [--yes] [--backup-dir DIR] [--wipe]

Options:
  --yes            Run non-interactively and accept destructive actions (use with care)
  --backup-dir DIR Place backups under DIR (default: /var/backups/docker)
  --wipe           After backup, wipe existing docker state (/var/lib/docker /var/lib/containerd)
  -h, --help       Show this help

This script will:
  1. Stop docker and containerd
  2. Backup /var/lib/docker and /etc/docker to the backup directory
  3. Purge Docker packages
  4. Optionally wipe docker data
  5. Reinstall Docker Engine, containerd and docker compose plugin from the official repo
  6. Start Docker and run basic verification

You said this is a clean machine; proceed only when you are ready.
EOF
}

DRY_RUN=false
ASSUME_YES=false
BACKUP_DIR="/var/backups/docker"
WIPE=false

while [ "$#" -gt 0 ]; do
  case "$1" in
    --yes) ASSUME_YES=true; shift ;;
    --backup-dir) BACKUP_DIR="$2"; shift 2 ;;
    --wipe) WIPE=true; shift ;;
    -h|--help) usage; exit 0 ;;
    --dry-run) DRY_RUN=true; shift ;;
    *) echo "Unknown arg: $1"; usage; exit 2 ;;
  esac
done

confirm() {
  local msg="$1"
  if [ "$ASSUME_YES" = true ]; then
    echo "[assume-yes] $msg -> yes"
    return 0
  fi
  read -r -p "$msg [y/N]: " answer || true
  case "$answer" in
    [Yy]|[Yy][Ee][Ss]) return 0 ;;
    *) return 1 ;;
  esac
}

print() { echo "[+] $*"; }
print_warn() { echo "[!] $*" >&2; }
print_err() { echo "[ERROR] $*" >&2; }

if [ "$EUID" -ne 0 ]; then
  print_err "This script must be run as root. Use sudo.";
  exit 1
fi

print "Starting Docker reinstall helper"
print "Backup dir: $BACKUP_DIR"
if [ "$WIPE" = true ]; then
  print_warn "WIPE option set: docker state will be removed after backup"
fi

if [ "$DRY_RUN" = true ]; then
  print_warn "Running in dry-run mode. No destructive commands will be executed."
fi

# Step 1: gather diagnostics
print "Gathering diagnostics"
docker version >/tmp/docker-version.out 2>&1 || true
docker info >/tmp/docker-info.out 2>&1 || true
docker ps -a --no-trunc > /tmp/docker-ps-all.out 2>&1 || true
docker volume ls > /tmp/docker-volumes.out 2>&1 || true
docker images --no-trunc > /tmp/docker-images.out 2>&1 || true
journalctl -u docker -n 200 --no-pager > /tmp/docker-journal.out 2>&1 || true
journalctl -u containerd -n 200 --no-pager > /tmp/containerd-journal.out 2>&1 || true

print "Diagnostics collected in /tmp (docker-* files)"

# Step 2: stop services
if systemctl is-active --quiet docker || systemctl is-active --quiet containerd; then
  print "Stopping docker and containerd services"
  if [ "$DRY_RUN" = false ]; then
    systemctl stop docker || true
    systemctl stop containerd || true
  fi
else
  print "Docker/containerd not running"
fi

# Step 3: backup data
TS=$(date -u +%Y%m%dT%H%M%SZ)
mkdir -p "$BACKUP_DIR"
BACKUP_SUB="$BACKUP_DIR/docker-backup-$TS"

print "Preparing backup at: $BACKUP_SUB"
if [ "$DRY_RUN" = false ]; then
  mkdir -p "$BACKUP_SUB"
  if command -v rsync >/dev/null 2>&1; then
    print "Backing up /var/lib/docker using rsync (preserves perms, progress hidden)"
    rsync -a /var/lib/docker/ "$BACKUP_SUB/var-lib-docker/" || true
  else
    print "rsync not available - moving /var/lib/docker to backup location (mv)"
    mv /var/lib/docker "$BACKUP_SUB/var-lib-docker" || true
  fi
  # backup /etc/docker if present
  if [ -d /etc/docker ]; then
    cp -a /etc/docker "$BACKUP_SUB/" || true
  fi
  # backup containerd state if present
  if [ -d /var/lib/containerd ]; then
    rsync -a /var/lib/containerd/ "$BACKUP_SUB/var-lib-containerd/" || true
  fi
  print "Backup complete: $BACKUP_SUB"
else
  print "(dry-run) would backup /var/lib/docker to $BACKUP_SUB"
fi

# Step 4: purge packages
if confirm "Purge Docker packages from the system? This removes docker packages (apt) but not backups."; then
  print "Purging docker packages"
  if [ "$DRY_RUN" = false ]; then
    apt-get remove -y docker docker-engine docker.io docker-ce docker-ce-cli containerd runc docker-compose-plugin || true
    apt-get purge -y docker-ce docker-ce-cli containerd || true
    apt-get autoremove -y
    apt-get autoclean -y
  else
    print "(dry-run) apt-get remove/purge commands skipped"
  fi
else
  print_warn "Skipping package purge as requested"
fi

# Step 5: optional wipe
if [ "$WIPE" = true ]; then
  if confirm "WIPE is enabled: remove /var/lib/docker and /var/lib/containerd now? This is destructive."; then
    print_warn "Deleting docker runtime data (this is irreversible unless you have backups)"
    if [ "$DRY_RUN" = false ]; then
      rm -rf /var/lib/docker || true
      rm -rf /var/lib/containerd || true
      rm -rf /etc/docker || true
      rm -f /var/run/docker.sock || true
    else
      print "(dry-run) would remove /var/lib/docker and related paths"
    fi
  else
    print_warn "WIPE requested but cancelled by user; keeping data in backup location"
  fi
fi

# Remove old apt lists and keys for docker
if [ "$DRY_RUN" = false ]; then
  rm -f /etc/apt/sources.list.d/docker.list || true
  rm -f /etc/apt/keyrings/docker.gpg || true
  rm -f /usr/share/keyrings/docker-archive-keyring.gpg || true
  apt-get update || true
fi

# Step 6: install prerequisites and Docker from official repo
if confirm "Install Docker Engine and Compose plugin from the official Docker repository?"; then
  print "Installing prerequisites and Docker repo"
  if [ "$DRY_RUN" = false ]; then
    apt-get update
    apt-get install -y ca-certificates curl gnupg lsb-release apt-transport-https
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
    chmod a+r /etc/apt/keyrings/docker.gpg || true
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(lsb_release -cs) stable" > /etc/apt/sources.list.d/docker.list
    apt-get update
    apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
  else
    print "(dry-run) would configure repo and install docker-ce, containerd, docker-compose-plugin"
  fi
else
  print_warn "Skipping Docker install as requested"
fi

# Step 7: enable and start docker
if [ "$DRY_RUN" = false ]; then
  systemctl enable --now docker || true
  sleep 1
  systemctl status docker --no-pager || true
fi

# Step 8: verification
print "Verification steps"
if [ "$DRY_RUN" = false ]; then
  docker version || true
  docker info || true
  print "Running hello-world (may pull an image)"
  docker run --rm hello-world || true
  print "Checking docker compose plugin"
  docker compose version || true
fi

print "Reinstall script completed. If you backed up data you may restore specific volumes or the entire /var/lib/docker from $BACKUP_SUB"
print "If Docker fails to start, inspect journal: sudo journalctl -u docker -n 200 --no-pager"

exit 0
