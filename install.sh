#!/usr/bin/env bash
# ============================================================================
# PrintFarmer Installer
# One-command setup for your 3D printer management dashboard.
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/OlyForge3D/PrintFarmer/main/install.sh | bash
#   curl -fsSL ... | bash -s -- --non-interactive
#   ./install.sh --help
#
# Options:
#   --non-interactive     Skip all prompts, use sensible defaults
#   --profile PROFILE     Deployment profile: lite, standard, or full
#   --version TAG         Container image tag (default: latest)
#   --port PORT           HTTP port (default: 8080)
#   --dir DIR             Install directory (default: ./printfarmer)
#   --db sqlite|postgres  Database engine (default: sqlite)
#   --with-spoolman URL   Enable Spoolman filament tracking
#   --dry-run             Generate files only, don't start containers
#   --upgrade             Upgrade an existing installation in-place
#   --uninstall           Remove containers and images (preserves data)
#   --status              Show status of a running installation
#   --help, -h            Show this help
#
# Environment variables:
#   PRINTFARMER_PORT      Same as --port
#   PRINTFARMER_DIR       Same as --dir
#   PRINTFARMER_VERSION   Same as --version
#   PRINTFARMER_DB        Same as --db
#   PRINTFARMER_PROFILE   Same as --profile
# ============================================================================

set -euo pipefail

# ─── Version ────────────────────────────────────────────────────────────────
INSTALLER_VERSION="1.0.0"

# ─── Defaults ───────────────────────────────────────────────────────────────
REGISTRY_HOST="ghcr.io/olyforge3d"
IMAGE_TAG="${PRINTFARMER_VERSION:-latest}"
HTTP_PORT="${PRINTFARMER_PORT:-8080}"
INSTALL_DIR="${PRINTFARMER_DIR:-./printfarmer}"
DB_ENGINE="${PRINTFARMER_DB:-sqlite}"
DB_EXPLICIT=false
DEPLOY_PROFILE="${PRINTFARMER_PROFILE:-}"
NON_INTERACTIVE=false
SPOOLMAN_URL=""
DRY_RUN=false
DO_UPGRADE=false
DO_UNINSTALL=false
DO_STATUS=false
SHOW_HELP=false

# ─── Terminal capabilities ──────────────────────────────────────────────────
USE_COLOR=true
if [[ ! -t 1 ]] || [[ "${NO_COLOR:-}" == "1" ]] || [[ "${TERM:-}" == "dumb" ]]; then
    USE_COLOR=false
fi

if [[ "$USE_COLOR" == "true" ]]; then
    RED='\033[0;31m'
    GREEN='\033[0;32m'
    YELLOW='\033[1;33m'
    BLUE='\033[0;34m'
    MAGENTA='\033[0;35m'
    CYAN='\033[0;36m'
    WHITE='\033[1;37m'
    DIM='\033[2m'
    BOLD='\033[1m'
    NC='\033[0m'
    CHECK='✔'
    CROSS='✖'
    ARROW='▸'
    SPINNER_CHARS='⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏'
else
    RED='' GREEN='' YELLOW='' BLUE='' MAGENTA='' CYAN='' WHITE='' DIM='' BOLD='' NC=''
    CHECK='[OK]' CROSS='[FAIL]' ARROW='>' SPINNER_CHARS='|/-\'
fi

# ─── Portability helpers (macOS ships bash 3.2, no ${var,,}) ────────────────
lc() { echo "$1" | tr '[:upper:]' '[:lower:]'; }
yn_yes() { local v; v="$(lc "${1:-}")"; [[ "$v" == "y" || "$v" == "yes" ]]; }
yn_no()  { local v; v="$(lc "${1:-}")"; [[ "$v" == "n" || "$v" == "no" ]]; }

# ─── Logging ────────────────────────────────────────────────────────────────
info()    { printf "  ${BLUE}${ARROW}${NC} %s\n" "$*"; }
ok()      { printf "  ${GREEN}${CHECK}${NC} %s\n" "$*"; }
warn()    { printf "  ${YELLOW}!${NC} %s\n" "$*"; }
fail()    { printf "  ${RED}${CROSS}${NC} %s\n" "$*" >&2; }
die()     { fail "$@"; exit 1; }
step()    { printf "\n${BOLD}  %s${NC}\n" "$*"; }
dimtext() { printf "  ${DIM}%s${NC}\n" "$*"; }

# Spinner for long-running operations
spinner() {
    local pid=$1 msg="${2:-Working...}"
    local i=0 len=${#SPINNER_CHARS}
    if [[ ! -t 1 ]]; then
        # Non-interactive: just wait
        wait "$pid" 2>/dev/null
        return $?
    fi
    while kill -0 "$pid" 2>/dev/null; do
        local char="${SPINNER_CHARS:$((i % len)):1}"
        printf "\r  ${CYAN}%s${NC} %s" "$char" "$msg"
        sleep 0.08
        i=$((i + 1))
    done
    wait "$pid" 2>/dev/null
    local rc=$?
    printf "\r\033[K"
    return $rc
}

# Run command with spinner
run_with_spinner() {
    local msg="$1"; shift
    "$@" >/dev/null 2>&1 &
    local pid=$!
    if spinner "$pid" "$msg"; then
        ok "$msg"
        return 0
    else
        fail "$msg"
        return 1
    fi
}

# ─── Parse arguments ───────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
    case "$1" in
        --non-interactive)  NON_INTERACTIVE=true; shift ;;
        --version)          IMAGE_TAG="${2:?--version requires a tag}"; shift 2 ;;
        --version=*)        IMAGE_TAG="${1#*=}"; shift ;;
        --port)             HTTP_PORT="${2:?--port requires a number}"; shift 2 ;;
        --port=*)           HTTP_PORT="${1#*=}"; shift ;;
        --dir)              INSTALL_DIR="${2:?--dir requires a path}"; shift 2 ;;
        --dir=*)            INSTALL_DIR="${1#*=}"; shift ;;
        --db)               DB_ENGINE="${2:?--db requires sqlite or postgres}"; DB_EXPLICIT=true; shift 2 ;;
        --db=*)             DB_ENGINE="${1#*=}"; DB_EXPLICIT=true; shift ;;
        --profile)          DEPLOY_PROFILE="${2:?--profile requires lite, standard, or full}"; shift 2 ;;
        --profile=*)        DEPLOY_PROFILE="${1#*=}"; shift ;;
        --with-spoolman)    SPOOLMAN_URL="${2:?--with-spoolman requires a URL}"; shift 2 ;;
        --with-spoolman=*)  SPOOLMAN_URL="${1#*=}"; shift ;;
        --dry-run)          DRY_RUN=true; shift ;;
        --upgrade)          DO_UPGRADE=true; shift ;;
        --uninstall)        DO_UNINSTALL=true; shift ;;
        --status)           DO_STATUS=true; shift ;;
        --help|-h)          SHOW_HELP=true; shift ;;
        *) warn "Unknown option: $1 (ignored)"; shift ;;
    esac
done

# ─── Help ───────────────────────────────────────────────────────────────────
if [[ "$SHOW_HELP" == "true" ]]; then
    cat <<'HELPEOF'

  PrintFarmer Installer

  USAGE
    ./install.sh [OPTIONS]
    curl -fsSL https://raw.githubusercontent.com/OlyForge3D/PrintFarmer/main/install.sh | bash
    curl ... | bash -s -- --non-interactive --port 9090

  OPTIONS
    --non-interactive     Skip prompts, use defaults (good for automation)
    --profile PROFILE     Deployment profile (lite, standard, full)
    --version TAG         Container image tag to pull (default: latest)
    --port PORT           HTTP port to expose (default: 8080)
    --dir DIR             Where to install (default: ./printfarmer)
    --db sqlite|postgres  Database engine (default: sqlite — zero config)
    --with-spoolman URL   Connect to Spoolman for filament tracking
    --dry-run             Generate config files without starting containers
    --upgrade             Pull latest images and restart an existing install
    --uninstall           Stop and remove containers (data volumes preserved)
    --status              Show running container status
    --help, -h            Show this help

  DEPLOYMENT PROFILES
    lite        Single container, SQLite, ideal for Raspberry Pi (4GB+ RAM)
    standard    API + Frontend + Nginx proxy, SQLite or PostgreSQL (8GB+ RAM)
    full        Standard + Monitoring + Discovery, PostgreSQL (16GB+ RAM)

  ENVIRONMENT VARIABLES
    PRINTFARMER_PORT      Equivalent to --port
    PRINTFARMER_DIR       Equivalent to --dir
    PRINTFARMER_VERSION   Equivalent to --version
    PRINTFARMER_DB        Equivalent to --db
    PRINTFARMER_PROFILE   Equivalent to --profile
    NO_COLOR=1            Disable colored output

  EXAMPLES
    # Simplest install — everything defaults, just works
    ./install.sh

    # Automated install on port 9090 with PostgreSQL
    ./install.sh --non-interactive --port 9090 --db postgres

    # Lightweight install for Raspberry Pi
    ./install.sh --profile lite

    # Full install with monitoring and discovery
    ./install.sh --non-interactive --profile full

    # Upgrade to a specific version
    ./install.sh --upgrade --version v2.1.0

    # Generate files for review before starting
    ./install.sh --dry-run

  ARM/Raspberry Pi Support:
    On ARM64 platforms, 3D model file support (STL, OBJ, STEP, 3MF) and slicing
    features are automatically disabled. G-code upload and all printer management
    features work normally.

    To force-enable (if you've compiled native libs yourself):
      PFARM__Platform__ModelFilesEnabled=true PFARM__Slicer__Enabled=true ./install.sh

  AFTER INSTALL
    Open http://localhost:8080 (or your chosen port) in a browser.
    You'll be guided through creating your admin account.

HELPEOF
    exit 0
fi

# ─── Banner ─────────────────────────────────────────────────────────────────
banner() {
    echo ""
    printf "${GREEN}"
    cat <<'ART'
    ____       _       __  ______
   / __ \_____(_)___  / /_/ ____/___ __________ ___  ___  _____
  / /_/ / ___/ / __ \/ __/ /_  / __ `/ ___/ __ `__ \/ _ \/ ___/
 / ____/ /  / / / / / /_/ __/ / /_/ / /  / / / / / /  __/ /
/_/   /_/  /_/_/ /_/\__/_/    \__,_/_/  /_/ /_/ /_/\___/_/
ART
    printf "${NC}"
    printf "    ${DIM}3D Printer Management Dashboard${NC}  ${CYAN}v${INSTALLER_VERSION}${NC}\n"
    echo ""
}

banner

# ═══════════════════════════════════════════════════════════════════════════
# PLATFORM DETECTION
# ═══════════════════════════════════════════════════════════════════════════
detect_platform() {
    local kernel arch
    kernel="$(uname -s 2>/dev/null || echo 'unknown')"
    arch="$(uname -m 2>/dev/null || echo 'unknown')"

    case "$kernel" in
        Linux)   OS="linux" ;;
        Darwin)  OS="macos" ;;
        MINGW*|MSYS*|CYGWIN*) OS="windows" ;;
        *)       OS="unknown" ;;
    esac

    case "$arch" in
        x86_64|amd64) ARCH="amd64" ;;
        aarch64|arm64) ARCH="arm64" ;;
        armv7*) ARCH="armv7" ;;
        *) ARCH="$arch" ;;
    esac

    # Detect Linux distro
    DISTRO="unknown"
    DISTRO_FAMILY="unknown"
    if [[ "$OS" == "linux" ]] && [[ -f /etc/os-release ]]; then
        # shellcheck disable=SC1091
        . /etc/os-release
        DISTRO="${ID:-unknown}"
        case "$DISTRO" in
            ubuntu|debian|raspbian|linuxmint|pop) DISTRO_FAMILY="debian" ;;
            fedora|rhel|centos|rocky|alma|ol) DISTRO_FAMILY="rhel" ;;
            arch|manjaro) DISTRO_FAMILY="arch" ;;
            opensuse*|sles) DISTRO_FAMILY="suse" ;;
            *) DISTRO_FAMILY="unknown" ;;
        esac
    elif [[ "$OS" == "macos" ]]; then
        DISTRO="macos"
        DISTRO_FAMILY="macos"
    fi

    # Detect WSL
    IS_WSL=false
    if [[ "$OS" == "linux" ]] && grep -qi microsoft /proc/version 2>/dev/null; then
        IS_WSL=true
    fi
}

detect_platform
dimtext "Platform: ${OS}/${ARCH} (${DISTRO})$( [[ "$IS_WSL" == "true" ]] && echo ' [WSL]' )"

# ─── ARM platform capability detection ──────────────────────────────────────
IS_ARM=false
case "$ARCH" in
    arm64|armv7)
        IS_ARM=true
        echo "⚠️  ARM platform detected ($ARCH) — 3D model and slicing features will be disabled"
        ;;
esac

# ─── Deployment profile selection ───────────────────────────────────────────
# Validate --profile if provided on CLI
if [[ -n "$DEPLOY_PROFILE" ]]; then
    DEPLOY_PROFILE="$(lc "$DEPLOY_PROFILE")"
    case "$DEPLOY_PROFILE" in
        lite|standard|full) ;;
        *) die "Invalid profile '$DEPLOY_PROFILE'. Choose: lite, standard, full" ;;
    esac
fi

# Resolve profile: CLI flag > interactive prompt > ARM auto-default > fallback
if [[ -z "$DEPLOY_PROFILE" ]]; then
    if [[ "$NON_INTERACTIVE" == "true" ]]; then
        # Non-interactive: ARM defaults to lite, otherwise standard (backward compat)
        if [[ "$IS_ARM" == "true" ]]; then
            DEPLOY_PROFILE="lite"
            info "ARM detected — defaulting to 'lite' profile (single container, SQLite)"
        else
            DEPLOY_PROFILE="standard"
        fi
    elif [[ -t 0 ]]; then
        # Interactive terminal: show profile menu
        if [[ "$IS_ARM" == "true" ]]; then
            echo ""
            info "ARM platform detected — 'lite' profile is recommended for this hardware."
        fi
        echo ""
        printf "  ${BOLD}Select deployment profile:${NC}\n"
        printf "    ${CYAN}1${NC}) ${BOLD}Lite${NC}      — Single container, SQLite, ideal for Raspberry Pi ${DIM}(4GB+ RAM)${NC}\n"
        printf "    ${CYAN}2${NC}) ${BOLD}Standard${NC}  — API + Frontend + Proxy, SQLite or PostgreSQL ${DIM}(8GB+ RAM)${NC}\n"
        printf "    ${CYAN}3${NC}) ${BOLD}Full${NC}      — Standard + Monitoring + Discovery, PostgreSQL ${DIM}(16GB+ RAM)${NC}\n"
        echo ""

        local_default="2"
        if [[ "$IS_ARM" == "true" ]]; then
            local_default="1"
        fi
        read -rp "$(printf "  ${BLUE}?${NC} Profile ${DIM}[%s]${NC}: " "$local_default")" profile_choice
        profile_choice="${profile_choice:-$local_default}"
        case "$profile_choice" in
            1|lite)     DEPLOY_PROFILE="lite" ;;
            2|standard) DEPLOY_PROFILE="standard" ;;
            3|full)     DEPLOY_PROFILE="full" ;;
            *)          warn "Invalid choice '$profile_choice', using standard"
                        DEPLOY_PROFILE="standard" ;;
        esac
    else
        # Piped/non-terminal input without --non-interactive: ARM→lite, else standard
        if [[ "$IS_ARM" == "true" ]]; then
            DEPLOY_PROFILE="lite"
            info "ARM detected — defaulting to 'lite' profile"
        else
            DEPLOY_PROFILE="standard"
        fi
    fi
fi

# Apply profile defaults
case "$DEPLOY_PROFILE" in
    lite)
        # Lite forces SQLite — no database container
        if [[ "$DB_ENGINE" != "sqlite" ]]; then
            warn "Lite profile uses SQLite — overriding --db $DB_ENGINE"
            DB_ENGINE="sqlite"
        fi
        ;;
    full)
        # Full defaults to PostgreSQL unless user explicitly chose sqlite
        if [[ "$DB_EXPLICIT" != "true" ]] && [[ -z "${PRINTFARMER_DB:-}" ]]; then
            DB_ENGINE="postgres"
        fi
        ;;
esac

ok "Profile: $DEPLOY_PROFILE"

# ═══════════════════════════════════════════════════════════════════════════
# STATUS / UNINSTALL / UPGRADE (early exits)
# ═══════════════════════════════════════════════════════════════════════════

resolve_install_dir() {
    if command -v realpath >/dev/null 2>&1; then
        INSTALL_DIR="$(realpath -m "$INSTALL_DIR" 2>/dev/null || echo "$INSTALL_DIR")"
    elif command -v readlink >/dev/null 2>&1; then
        # macOS: readlink -f may not exist, use pwd trick
        mkdir -p "$INSTALL_DIR" 2>/dev/null || true
        INSTALL_DIR="$(cd "$INSTALL_DIR" 2>/dev/null && pwd || echo "$INSTALL_DIR")"
    fi
}

detect_compose_cmd() {
    if docker compose version &>/dev/null 2>&1; then
        COMPOSE_CMD="docker compose"
    elif command -v docker-compose &>/dev/null 2>&1; then
        COMPOSE_CMD="docker-compose"
    else
        COMPOSE_CMD=""
    fi
}

detect_compose_cmd

# --status
if [[ "$DO_STATUS" == "true" ]]; then
    resolve_install_dir
    if [[ ! -f "$INSTALL_DIR/docker-compose.yml" ]]; then
        die "No installation found at $INSTALL_DIR"
    fi
    step "PrintFarmer Status"
    cd "$INSTALL_DIR"
    $COMPOSE_CMD ps 2>/dev/null || die "Could not read container status"
    echo ""
    exit 0
fi

# --uninstall
if [[ "$DO_UNINSTALL" == "true" ]]; then
    resolve_install_dir
    if [[ ! -f "$INSTALL_DIR/docker-compose.yml" ]]; then
        die "No installation found at $INSTALL_DIR"
    fi
    step "Uninstalling PrintFarmer"
    warn "This will stop and remove containers. Data volumes are preserved."
    if [[ "$NON_INTERACTIVE" != "true" ]]; then
        read -rp "  Continue? [y/N]: " confirm
        yn_yes "$confirm" || { info "Cancelled."; exit 0; }
    fi
    cd "$INSTALL_DIR"
    $COMPOSE_CMD down --remove-orphans 2>/dev/null || true
    ok "Containers removed"
    dimtext "Data volumes preserved. To delete everything: $COMPOSE_CMD down -v"
    dimtext "Config files remain in: $INSTALL_DIR"
    echo ""
    exit 0
fi

# --upgrade
if [[ "$DO_UPGRADE" == "true" ]]; then
    resolve_install_dir
    if [[ ! -f "$INSTALL_DIR/docker-compose.yml" ]]; then
        die "No installation found at $INSTALL_DIR. Run without --upgrade first."
    fi
    step "Upgrading PrintFarmer"
    cd "$INSTALL_DIR"
    if [[ "$IMAGE_TAG" != "latest" ]]; then
        # Update .env with new image tag
        if [[ -f .env ]]; then
            if grep -q '^IMAGE_TAG=' .env; then
                sed -i.bak "s/^IMAGE_TAG=.*/IMAGE_TAG=${IMAGE_TAG}/" .env && rm -f .env.bak
            fi
        fi
        info "Image tag → ${IMAGE_TAG}"
    fi
    run_with_spinner "Pulling latest images" $COMPOSE_CMD pull || die "Pull failed"
    info "Restarting containers..."
    $COMPOSE_CMD up -d --remove-orphans 2>/dev/null
    ok "Upgrade complete"
    echo ""
    exit 0
fi

# ═══════════════════════════════════════════════════════════════════════════
# PREREQUISITES
# ═══════════════════════════════════════════════════════════════════════════
step "Checking prerequisites"

# --- Docker ---
install_docker_hint() {
    echo ""
    case "$DISTRO_FAMILY" in
        debian)
            info "Install Docker with:"
            printf "    ${BOLD}curl -fsSL https://get.docker.com | sh${NC}\n"
            printf "    ${BOLD}sudo usermod -aG docker \$USER${NC}\n"
            dimtext "Then log out and back in, and re-run this installer."
            ;;
        rhel)
            info "Install Docker with:"
            printf "    ${BOLD}sudo dnf install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin${NC}\n"
            printf "    ${BOLD}sudo systemctl enable --now docker${NC}\n"
            printf "    ${BOLD}sudo usermod -aG docker \$USER${NC}\n"
            ;;
        macos)
            info "Install Docker Desktop:"
            printf "    ${BOLD}https://www.docker.com/products/docker-desktop/${NC}\n"
            dimtext "Or with Homebrew: brew install --cask docker"
            ;;
        *)
            info "Install Docker: https://docs.docker.com/get-docker/"
            ;;
    esac
    echo ""
}

# Check Docker binary
if ! command -v docker &>/dev/null; then
    fail "Docker is not installed"
    install_docker_hint

    # Offer auto-install on supported Linux distros
    if [[ "$OS" == "linux" && "$DISTRO_FAMILY" =~ ^(debian|rhel)$ && "$NON_INTERACTIVE" != "true" ]]; then
        read -rp "  Install Docker automatically? [y/N]: " auto_install
        if yn_yes "$auto_install"; then
            step "Installing Docker"
            if curl -fsSL https://get.docker.com | sh 2>&1 | tail -5; then
                sudo usermod -aG docker "$USER" 2>/dev/null || true
                ok "Docker installed"
                warn "You may need to log out and back in for group changes."
                warn "Trying to continue with sudo..."
                # Try newgrp or sudo for this session
                if ! docker info &>/dev/null; then
                    die "Docker installed but not accessible yet. Log out, log back in, and re-run."
                fi
            else
                die "Docker installation failed. Install manually and re-run."
            fi
        else
            exit 1
        fi
    else
        exit 1
    fi
fi

# Check Docker daemon is running
if ! docker info &>/dev/null 2>&1; then
    fail "Docker daemon is not running"
    case "$OS" in
        linux)
            info "Start it with: ${BOLD}sudo systemctl start docker${NC}"
            if [[ "$NON_INTERACTIVE" != "true" ]]; then
                read -rp "  Start Docker now? [y/N]: " start_docker
                if yn_yes "$start_docker"; then
                    sudo systemctl start docker 2>/dev/null || die "Could not start Docker"
                    sleep 2
                    docker info &>/dev/null || die "Docker still not responding after start"
                    ok "Docker started"
                else
                    exit 1
                fi
            else
                exit 1
            fi
            ;;
        macos)
            info "Open Docker Desktop from your Applications folder"
            die "Start Docker Desktop and re-run this installer"
            ;;
        *)
            die "Start the Docker daemon and re-run this installer"
            ;;
    esac
fi

# Get Docker version (portable — no PCRE)
DOCKER_VER="$(docker --version 2>/dev/null | sed 's/[^0-9.]//g; s/\.$//' | head -c 20)"
ok "Docker ${DOCKER_VER:-unknown}"

# Check Docker Compose
if [[ -z "$COMPOSE_CMD" ]]; then
    fail "Docker Compose not found"
    case "$OS" in
        linux)
            info "Install the compose plugin:"
            printf "    ${BOLD}sudo apt-get install docker-compose-plugin${NC}  (Debian/Ubuntu)\n"
            printf "    ${BOLD}sudo dnf install docker-compose-plugin${NC}      (Fedora/RHEL)\n"
            ;;
        macos)
            info "Docker Desktop includes Compose. Make sure it's up to date."
            ;;
    esac
    die "Install Docker Compose and re-run."
fi

COMPOSE_VER="$($COMPOSE_CMD version 2>/dev/null | sed 's/[^0-9.]//g; s/\.$//' | head -c 20)"
ok "Docker Compose ${COMPOSE_VER:-unknown}"

# Check available disk space (warn if <2GB)
check_disk_space() {
    local target_dir="$1"
    local available_kb
    if command -v df >/dev/null 2>&1; then
        available_kb=$(df -k "$(dirname "$target_dir")" 2>/dev/null | tail -1 | awk '{print $4}')
        if [[ -n "$available_kb" ]] && [[ "$available_kb" =~ ^[0-9]+$ ]]; then
            local available_gb=$((available_kb / 1048576))
            if [[ $available_kb -lt 2097152 ]]; then
                warn "Low disk space: ~${available_gb}GB available (recommend 2GB+)"
            fi
        fi
    fi
}

# Check port availability
check_port() {
    local port="$1"
    if command -v lsof >/dev/null 2>&1; then
        if lsof -Pi ":$port" -sTCP:LISTEN -t >/dev/null 2>&1; then
            return 1
        fi
    elif command -v ss >/dev/null 2>&1; then
        if ss -tlnp 2>/dev/null | grep -q ":$port "; then
            return 1
        fi
    fi
    return 0
}

if ! check_port "$HTTP_PORT"; then
    warn "Port $HTTP_PORT is already in use"
    if [[ "$NON_INTERACTIVE" != "true" ]]; then
        read -rp "  Use a different port? [8081]: " alt_port
        HTTP_PORT="${alt_port:-8081}"
        if ! check_port "$HTTP_PORT"; then
            die "Port $HTTP_PORT is also in use. Free the port and try again."
        fi
        ok "Using port $HTTP_PORT"
    else
        die "Port $HTTP_PORT is in use. Use --port to specify another."
    fi
fi

# ═══════════════════════════════════════════════════════════════════════════
# INTERACTIVE CONFIGURATION
# ═══════════════════════════════════════════════════════════════════════════
ask() {
    local prompt="$1" default="$2" var="$3"
    if [[ "$NON_INTERACTIVE" == "true" ]]; then
        eval "$var=\"\${!var:-$default}\""
        return
    fi
    local current="${!var:-$default}"
    read -rp "$(printf "  ${BLUE}?${NC} %-30s ${DIM}[%s]${NC}: " "$prompt" "$current")" input
    eval "$var=\"${input:-$current}\""
}

if [[ "$NON_INTERACTIVE" != "true" ]]; then
    step "Configuration"
    dimtext "Press Enter to accept defaults."
    echo ""
fi

ask "Install directory"           "$INSTALL_DIR"  INSTALL_DIR
ask "HTTP port"                   "$HTTP_PORT"    HTTP_PORT
ask "Image version"               "$IMAGE_TAG"    IMAGE_TAG

# Only prompt for database if profile allows choice
if [[ "$DEPLOY_PROFILE" == "lite" ]]; then
    dimtext "Database: sqlite (lite profile)"
elif [[ "$DEPLOY_PROFILE" == "full" && "$DB_EXPLICIT" != "true" ]]; then
    dimtext "Database: postgres (full profile default)"
else
    ask "Database (sqlite/postgres)"  "$DB_ENGINE"    DB_ENGINE
fi

if [[ "$NON_INTERACTIVE" != "true" && -z "$SPOOLMAN_URL" ]]; then
    ask "Spoolman URL (blank=skip)" "" SPOOLMAN_URL
fi

# Validate DB engine
DB_ENGINE="$(lc "$DB_ENGINE")"
if [[ "$DB_ENGINE" != "sqlite" && "$DB_ENGINE" != "postgres" ]]; then
    warn "Unknown database '$DB_ENGINE', defaulting to sqlite"
    DB_ENGINE="sqlite"
fi

echo ""

# ═══════════════════════════════════════════════════════════════════════════
# INSTALLATION
# ═══════════════════════════════════════════════════════════════════════════
step "Installing PrintFarmer"

# Resolve install dir
resolve_install_dir
check_disk_space "$INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
info "Directory: $INSTALL_DIR"

# ─── Generate secrets ───────────────────────────────────────────────────────
generate_secret() {
    local length="${1:-48}"
    local raw
    if command -v openssl >/dev/null 2>&1; then
        raw="$(openssl rand -base64 256 2>/dev/null)"
    else
        raw="$(dd if=/dev/urandom bs=256 count=1 2>/dev/null | base64 2>/dev/null || echo "fallback$(date +%s)$$")"
    fi
    raw="${raw//[\/+=[:space:]]/}"
    printf '%s' "${raw:0:$length}"
}

JWT_KEY="$(generate_secret 64)"

# ─── Detect LAN IP ──────────────────────────────────────────────────────────
detect_lan_ip() {
    local ip=""
    if command -v hostname >/dev/null 2>&1; then
        ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
    fi
    if [[ -z "$ip" ]] && command -v ifconfig >/dev/null 2>&1; then
        ip="$(ifconfig 2>/dev/null | grep 'inet ' | grep -v '127.0.0.1' | head -1 | awk '{print $2}')"
    fi
    if [[ -z "$ip" ]] && command -v ip >/dev/null 2>&1; then
        ip="$(ip -4 route get 1.1.1.1 2>/dev/null | head -1 | sed 's/.*src \([0-9.]*\).*/\1/')"
    fi
    echo "${ip:-localhost}"
}

LAN_IP="$(detect_lan_ip)"

# ─── Generate .env ──────────────────────────────────────────────────────────
info "Writing configuration..."

if [[ "$DB_ENGINE" == "postgres" ]]; then
    DB_PASSWORD="$(generate_secret 32)"
    cat > "$INSTALL_DIR/.env" <<ENVEOF
# PrintFarmer — generated $(date '+%Y-%m-%d %H:%M:%S')
REGISTRY_HOST=${REGISTRY_HOST}
IMAGE_TAG=${IMAGE_TAG}
HTTP_PORT=${HTTP_PORT}
DEPLOY_PROFILE=${DEPLOY_PROFILE}

# Database: PostgreSQL
DB_PROVIDER=Postgres
POSTGRES_DB=printfarmer
POSTGRES_USER=printfarmer
POSTGRES_PASSWORD=${DB_PASSWORD}
ConnectionStrings__Default=Host=database;Port=5432;Database=printfarmer;Username=printfarmer;Password=${DB_PASSWORD}

# Auth
Jwt__Key=${JWT_KEY}
Jwt__Issuer=PrintFarmer
Jwt__Audience=PrintFarmer

# Runtime
ASPNETCORE_ENVIRONMENT=Production
DEVMODE_BYPASS_AUTH=false
ALLOW_LOCAL_NETWORK=true
ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8,172.16.0.0/12
PFARM__NetworkDiscovery__EnableDiscovery=true
PFARM__Spoolman__BaseUrl=${SPOOLMAN_URL}
ENVEOF
else
    # SQLite — no database container needed
    cat > "$INSTALL_DIR/.env" <<ENVEOF
# PrintFarmer — generated $(date '+%Y-%m-%d %H:%M:%S')
REGISTRY_HOST=${REGISTRY_HOST}
IMAGE_TAG=${IMAGE_TAG}
HTTP_PORT=${HTTP_PORT}
DEPLOY_PROFILE=${DEPLOY_PROFILE}

# Database: SQLite (zero config)
DB_PROVIDER=Sqlite
ConnectionStrings__Default=Data Source=/data/printfarmer.db

# Auth
Jwt__Key=${JWT_KEY}
Jwt__Issuer=PrintFarmer
Jwt__Audience=PrintFarmer

# Runtime
ASPNETCORE_ENVIRONMENT=Production
DEVMODE_BYPASS_AUTH=false
ALLOW_LOCAL_NETWORK=true
ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8,172.16.0.0/12
PFARM__NetworkDiscovery__EnableDiscovery=true
PFARM__Spoolman__BaseUrl=${SPOOLMAN_URL}
ENVEOF
fi

# Append ARM platform overrides if running on ARM
if [[ "$IS_ARM" == "true" ]]; then
    cat >> "$INSTALL_DIR/.env" <<ARMEOF

# ARM Platform — 3D model and slicing features disabled
PFARM__Slicer__Enabled=false
PFARM__Platform__ModelFilesEnabled=false
PFARM__Platform__ThumbnailGenerationEnabled=false
PFARM__Platform__Architecture=${ARCH}
ARMEOF

    # For bare-metal (non-Docker) .NET deployments, write appsettings.Platform.json
    cat > "$INSTALL_DIR/appsettings.Platform.json" <<'PLATFORMEOF'
{
  "Slicer": {
    "Enabled": false
  },
  "Platform": {
    "ModelFilesEnabled": false,
    "ThumbnailGenerationEnabled": false,
    "Architecture": "arm64"
  }
}
PLATFORMEOF
    dimtext "Created appsettings.Platform.json for bare-metal .NET deployments"
fi

ok "Environment config"

# ─── Generate nginx config (standard/full only — lite serves directly) ──────
if [[ "$DEPLOY_PROFILE" != "lite" ]]; then
mkdir -p "$INSTALL_DIR/nginx"
cat > "$INSTALL_DIR/nginx/nginx-proxy.conf" <<'NGINXEOF'
user nginx;
worker_processes auto;
pid /var/cache/nginx/nginx.pid;
error_log /var/log/nginx/error.log warn;

events { worker_connections 1024; }

http {
    include       /etc/nginx/mime.types;
    default_type  application/octet-stream;
    sendfile on;
    keepalive_timeout 65;
    client_max_body_size 500M;

    gzip on;
    gzip_vary on;
    gzip_min_length 1024;
    gzip_types text/plain text/css text/xml text/javascript application/javascript application/json;

    resolver 127.0.0.11:53 valid=10s;
    resolver_timeout 5s;

    upstream api_backend      { server api:5245; }
    upstream frontend_backend { server frontend:80; }

    server {
        listen 80;
        server_name _;

        # Health endpoints
        location = /healthz { proxy_pass http://api_backend/healthz; proxy_http_version 1.1; proxy_set_header Host $host; }
        location = /health  { proxy_pass http://api_backend/health;  proxy_http_version 1.1; proxy_set_header Host $host; }

        # API — long timeout for gcode dispatch
        location ~ ^/api/job-queue/[^/]+/dispatch$ {
            proxy_pass http://api_backend;
            proxy_http_version 1.1;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection 'upgrade';
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
            proxy_read_timeout 3600s;
            proxy_send_timeout 3600s;
            proxy_buffering off;
        }

        location /api/ {
            proxy_pass http://api_backend;
            proxy_http_version 1.1;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection 'upgrade';
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
            proxy_read_timeout 300s;
            proxy_send_timeout 300s;
            proxy_buffering off;
        }

        # SignalR WebSockets
        location /hubs/ {
            proxy_pass http://api_backend;
            proxy_http_version 1.1;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection "upgrade";
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
            proxy_read_timeout 3600s;
            proxy_send_timeout 3600s;
            proxy_buffering off;
        }

        # Frontend SPA
        location / {
            proxy_pass http://frontend_backend;
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
        }

        # Security headers
        add_header X-Frame-Options "SAMEORIGIN" always;
        add_header X-Content-Type-Options "nosniff" always;
        add_header X-XSS-Protection "1; mode=block" always;
        add_header Referrer-Policy "strict-origin-when-cross-origin" always;
    }
}
NGINXEOF
ok "Nginx config"
fi

# ─── Generate docker-compose.yml ────────────────────────────────────────────
info "Generating docker-compose.yml..."

if [[ "$DEPLOY_PROFILE" == "lite" ]]; then
    # ── LITE: single monolith container ──────────────────────────────────────
    cat > "$INSTALL_DIR/docker-compose.yml" <<COMPOSEEOF
# PrintFarmer — generated $(date '+%Y-%m-%d %H:%M:%S')
# Profile: lite | Database: sqlite | Single container
# Docs: https://github.com/OlyForge3D/PrintFarmer

name: printfarmer

services:
  # PrintFarmer Monolith (API + Frontend in one container)
  printfarmer:
    image: \${REGISTRY_HOST}/printfarmer-monolith:\${IMAGE_TAG}
    container_name: printfarmer-monolith
    restart: unless-stopped
    ports:
      - "\${HTTP_PORT:-8080}:5000"
    environment:
      - DEPLOYMENT_MODE=monolith
      - ASPNETCORE_ENVIRONMENT=\${ASPNETCORE_ENVIRONMENT:-Production}
      - ASPNETCORE_URLS=http://+:5000
      - DB_PROVIDER=Sqlite
      - DB_STARTUP_TIMEOUT=180
      - ConnectionStrings__Default=\${ConnectionStrings__Default}
      - Jwt__Key=\${Jwt__Key}
      - Jwt__Issuer=\${Jwt__Issuer:-PrintFarmer}
      - Jwt__Audience=\${Jwt__Audience:-PrintFarmer}
      - Security__DevModeBypassAuth=\${DEVMODE_BYPASS_AUTH:-false}
      - GCODE_STORAGE_PATH=/app/gcode
      - MODEL_UPLOAD_PATH=/app/models
      - DATAPROTECTION_KEYS_PATH=/app/data-protection-keys
      - PFARM__Spoolman__BaseUrl=\${PFARM__Spoolman__BaseUrl:-}
      - PFARM__NetworkDiscovery__EnableDiscovery=\${PFARM__NetworkDiscovery__EnableDiscovery:-true}
      - Logging__LogLevel__Default=Information
      - Logging__LogLevel__Microsoft.AspNetCore=Warning
    volumes:
      - printfarmer-app-data:/data
      - printfarmer-model-storage:/app/models
      - printfarmer-gcode-storage:/app/gcode
      - printfarmer-slicer-profiles:/app/profiles
      - printfarmer-uploads:/app/uploads
      - printfarmer-dataprotection-keys:/app/data-protection-keys
    healthcheck:
      test: ["CMD", "sh", "-c", "curl -sf http://localhost:5000/healthz || exit 1"]
      interval: 30s
      timeout: 15s
      retries: 8
      start_period: 180s
    networks:
      - printfarmer-network

volumes:
  printfarmer-app-data:
  printfarmer-model-storage:
  printfarmer-gcode-storage:
  printfarmer-slicer-profiles:
  printfarmer-uploads:
  printfarmer-dataprotection-keys:

networks:
  printfarmer-network:
    name: printfarmer-network
    driver: bridge
COMPOSEEOF

else
    # ── STANDARD / FULL: microservices (api + frontend + nginx-proxy) ────────
    compose_api_depends=""
    compose_database_service=""
    compose_database_volume=""

    if [[ "$DB_ENGINE" == "postgres" ]]; then
        compose_api_depends='    depends_on:
      database:
        condition: service_healthy'
        compose_database_service='
  # PostgreSQL database
  database:
    image: postgres:16-alpine
    container_name: printfarmer-database
    restart: unless-stopped
    environment:
      POSTGRES_DB: ${POSTGRES_DB:-printfarmer}
      POSTGRES_USER: ${POSTGRES_USER:-printfarmer}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - printfarmer-database:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER:-printfarmer} -d ${POSTGRES_DB:-printfarmer}"]
      interval: 10s
      timeout: 5s
      retries: 5
      start_period: 15s
    networks:
      - printfarmer-network'
        compose_database_volume='  printfarmer-database:'
    fi

    # Build monitoring + discovery services for full profile
    compose_extra_services=""
    compose_extra_volumes=""
    if [[ "$DEPLOY_PROFILE" == "full" ]]; then
        compose_extra_services='
  # Printer Discovery Service
  printer-discovery:
    image: ${REGISTRY_HOST}/printfarmer-api:${IMAGE_TAG}
    container_name: printfarmer-printer-discovery
    restart: unless-stopped
    entrypoint: ["dotnet", "Farm.PrinterDiscovery.dll"]
    environment:
      - ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT:-Production}
      - ASPNETCORE_URLS=http://+:5247
      - Discovery__ApiBaseUrl=http://api:5245
      - EnablePeriodicDiscovery=${ENABLE_PERIODIC_DISCOVERY:-true}
      - ScanIntervalSeconds=${SCAN_INTERVAL_SECONDS:-300}
      - Subnets=${DISCOVERY_SUBNETS:-192.168.0.0/24}
      - ProbeTimeoutMs=${PROBE_TIMEOUT_MS:-1000}
      - MaxConcurrentProbes=${MAX_CONCURRENT_PROBES:-50}
      - PFARM__NetworkDiscovery__EnableDiscovery=${PFARM__NetworkDiscovery__EnableDiscovery:-true}
      - Logging__LogLevel__Default=Information
    depends_on:
      api:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:5247/api/discovery/health"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 30s
    networks:
      - printfarmer-network
    deploy:
      resources:
        limits:
          cpus: "0.5"
          memory: 256M

  # Prometheus — metrics collection
  prometheus:
    image: prom/prometheus:latest
    container_name: printfarmer-prometheus
    restart: unless-stopped
    command:
      - "--config.file=/etc/prometheus/prometheus.yml"
      - "--storage.tsdb.path=/prometheus"
      - "--storage.tsdb.retention.time=200h"
      - "--web.enable-lifecycle"
    volumes:
      - ./monitoring/prometheus/prometheus.yml:/etc/prometheus/prometheus.yml:ro
      - printfarmer-prometheus-data:/prometheus
    expose:
      - "9090"
    healthcheck:
      test: ["CMD", "wget", "--no-verbose", "--tries=1", "--spider", "http://localhost:9090/-/healthy"]
      interval: 30s
      timeout: 10s
      retries: 3
    networks:
      - printfarmer-network

  # Grafana — dashboards
  grafana:
    image: grafana/grafana:latest
    container_name: printfarmer-grafana
    restart: unless-stopped
    environment:
      GF_SECURITY_ADMIN_PASSWORD: ${GRAFANA_ADMIN_PASSWORD:-admin}
      GF_SECURITY_ADMIN_USER: ${GRAFANA_ADMIN_USER:-admin}
      GF_AUTH_PROXY_ENABLED: "true"
      GF_AUTH_PROXY_HEADER_NAME: X-WEBAUTH-USER
      GF_AUTH_PROXY_HEADER_PROPERTY: username
      GF_AUTH_PROXY_AUTO_SIGN_UP: "true"
      GF_SERVER_ROOT_URL: "%(protocol)s://%(domain)s/grafana/"
      GF_SERVER_SERVE_FROM_SUB_PATH: "true"
      GF_SECURITY_ALLOW_EMBEDDING: "true"
    volumes:
      - printfarmer-grafana-data:/var/lib/grafana
    expose:
      - "3000"
    networks:
      - printfarmer-network'
        compose_extra_volumes='  printfarmer-prometheus-data:
  printfarmer-grafana-data:'
    fi

    cat > "$INSTALL_DIR/docker-compose.yml" <<COMPOSEEOF
# PrintFarmer — generated $(date '+%Y-%m-%d %H:%M:%S')
# Profile: ${DEPLOY_PROFILE} | Database: ${DB_ENGINE} | Images: \${REGISTRY_HOST}/*:\${IMAGE_TAG}
# Docs: https://github.com/OlyForge3D/PrintFarmer

name: printfarmer

services:
${compose_database_service}
  # PrintFarmer API
  api:
    image: \${REGISTRY_HOST}/printfarmer-api:\${IMAGE_TAG}
    container_name: printfarmer-api
    restart: unless-stopped
${compose_api_depends}
    environment:
      - ASPNETCORE_ENVIRONMENT=\${ASPNETCORE_ENVIRONMENT:-Production}
      - ASPNETCORE_URLS=http://+:5245
      - DB_PROVIDER=\${DB_PROVIDER:-Sqlite}
      - ConnectionStrings__Default=\${ConnectionStrings__Default}
      - CORS__AllowedOrigins=http://localhost:3000,http://localhost:\${HTTP_PORT:-8080}
      - DEPLOYMENT_MODE=microservices
      - ALLOW_LOCAL_NETWORK=\${ALLOW_LOCAL_NETWORK:-true}
      - ALLOWED_NETWORK_RANGES=\${ALLOWED_NETWORK_RANGES:-192.168.0.0/16,10.0.0.0/8}
      - Jwt__Key=\${Jwt__Key}
      - Jwt__Issuer=\${Jwt__Issuer:-PrintFarmer}
      - Jwt__Audience=\${Jwt__Audience:-PrintFarmer}
      - Security__DevModeBypassAuth=\${DEVMODE_BYPASS_AUTH:-false}
      - GCODE_STORAGE_PATH=/app/gcode
      - MODEL_UPLOAD_PATH=/app/models
      - DATAPROTECTION_KEYS_PATH=/app/data-protection-keys
      - PFARM__Spoolman__BaseUrl=\${PFARM__Spoolman__BaseUrl:-}
      - PFARM__NetworkDiscovery__EnableDiscovery=\${PFARM__NetworkDiscovery__EnableDiscovery:-true}
      - Logging__LogLevel__Default=Information
      - Logging__LogLevel__Microsoft.AspNetCore=Warning
    volumes:
      - printfarmer-app-data:/data
      - printfarmer-model-storage:/app/models
      - printfarmer-gcode-storage:/app/gcode
      - printfarmer-slicer-profiles:/app/profiles
      - printfarmer-dataprotection-keys:/app/data-protection-keys
    healthcheck:
      test: ["CMD", "sh", "-c", "curl -sf http://localhost:5245/healthz || exit 1"]
      interval: 30s
      timeout: 15s
      retries: 5
      start_period: 120s
    networks:
      - printfarmer-network

  # React Frontend (Nginx)
  frontend:
    image: \${REGISTRY_HOST}/printfarmer-frontend:\${IMAGE_TAG}
    container_name: printfarmer-frontend
    restart: unless-stopped
    depends_on:
      api:
        condition: service_healthy
    environment:
      - NGINX_PROXY_API=http://api:5245
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:80/health"]
      interval: 30s
      timeout: 10s
      retries: 3
    networks:
      - printfarmer-network

  # Nginx reverse proxy — single entry point
  nginx-proxy:
    image: nginx:alpine
    container_name: printfarmer-nginx-proxy
    restart: unless-stopped
    ports:
      - "\${HTTP_PORT:-8080}:80"
    volumes:
      - ./nginx/nginx-proxy.conf:/etc/nginx/nginx.conf:ro
    depends_on:
      - frontend
      - api
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:80/healthz"]
      interval: 30s
      timeout: 10s
      retries: 3
    networks:
      - printfarmer-network
${compose_extra_services}

volumes:
${compose_database_volume}
  printfarmer-app-data:
  printfarmer-model-storage:
  printfarmer-gcode-storage:
  printfarmer-slicer-profiles:
  printfarmer-dataprotection-keys:
${compose_extra_volumes}

networks:
  printfarmer-network:
    name: printfarmer-network
    driver: bridge
COMPOSEEOF

fi

ok "docker-compose.yml"

# ─── Generate monitoring config for full profile ────────────────────────────
if [[ "$DEPLOY_PROFILE" == "full" ]]; then
    mkdir -p "$INSTALL_DIR/monitoring/prometheus"
    cat > "$INSTALL_DIR/monitoring/prometheus/prometheus.yml" <<'PROMEOF'
global:
  scrape_interval: 15s
  evaluation_interval: 15s

scrape_configs:
  - job_name: 'printfarmer-api'
    metrics_path: /metrics
    static_configs:
      - targets: ['api:5245']
PROMEOF
    ok "Monitoring config (prometheus.yml)"
fi

# ─── Write management helper script ─────────────────────────────────────────
cat > "$INSTALL_DIR/printfarmer.sh" <<MGMTEOF
#!/usr/bin/env bash
# PrintFarmer management helper
# Usage: ./printfarmer.sh [logs|status|stop|start|restart|update|backup]
set -euo pipefail
cd "\$(dirname "\$0")"
CMD="${COMPOSE_CMD}"
case "\${1:-help}" in
    logs)    \$CMD logs -f --tail=100 ;;
    status)  \$CMD ps ;;
    stop)    \$CMD down ;;
    start)   \$CMD up -d ;;
    restart) \$CMD restart ;;
    update)  \$CMD pull && \$CMD up -d --remove-orphans ;;
    backup)
        echo "Backing up data volumes..."
        ts=\$(date +%Y%m%d_%H%M%S)
        docker run --rm -v printfarmer-app-data:/data -v "\$(pwd)":/backup alpine tar czf "/backup/printfarmer-backup-\$ts.tar.gz" -C /data .
        echo "Backup saved: printfarmer-backup-\$ts.tar.gz"
        ;;
    *)
        echo "Usage: ./printfarmer.sh [logs|status|stop|start|restart|update|backup]"
        ;;
esac
MGMTEOF
chmod +x "$INSTALL_DIR/printfarmer.sh"
ok "Management script (printfarmer.sh)"

# ═══════════════════════════════════════════════════════════════════════════
# SUMMARY
# ═══════════════════════════════════════════════════════════════════════════
echo ""
printf "  ${BOLD}${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}\n"
printf "  ${BOLD}            Installation Summary${NC}\n"
printf "  ${BOLD}${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}\n"
echo ""
printf "  ${BOLD}Profile${NC}      %s\n" "$DEPLOY_PROFILE"
printf "  ${BOLD}Directory${NC}    %s\n" "$INSTALL_DIR"
printf "  ${BOLD}Database${NC}     %s\n" "$DB_ENGINE"
printf "  ${BOLD}Port${NC}         %s\n" "$HTTP_PORT"
printf "  ${BOLD}Version${NC}      %s\n" "$IMAGE_TAG"
if [[ -n "$SPOOLMAN_URL" ]]; then
    printf "  ${BOLD}Spoolman${NC}     %s\n" "$SPOOLMAN_URL"
fi
echo ""
printf "  ${DIM}Files generated:${NC}\n"
printf "    ${DIM}%s/docker-compose.yml${NC}\n" "$INSTALL_DIR"
printf "    ${DIM}%s/.env${NC}\n" "$INSTALL_DIR"
if [[ "$DEPLOY_PROFILE" != "lite" ]]; then
    printf "    ${DIM}%s/nginx/nginx-proxy.conf${NC}\n" "$INSTALL_DIR"
fi
if [[ "$DEPLOY_PROFILE" == "full" ]]; then
    printf "    ${DIM}%s/monitoring/prometheus/prometheus.yml${NC}\n" "$INSTALL_DIR"
fi
printf "    ${DIM}%s/printfarmer.sh${NC}\n" "$INSTALL_DIR"
echo ""

# ═══════════════════════════════════════════════════════════════════════════
# LAUNCH
# ═══════════════════════════════════════════════════════════════════════════
if [[ "$DRY_RUN" == "true" ]]; then
    info "Dry run complete — files generated but containers not started."
    echo ""
    printf "  To start: ${BOLD}cd %s && %s up -d${NC}\n" "$INSTALL_DIR" "$COMPOSE_CMD"
    echo ""
    exit 0
fi

START=true
if [[ "$NON_INTERACTIVE" != "true" ]]; then
    read -rp "$(printf "  ${BLUE}?${NC} Start PrintFarmer now? ${DIM}[Y/n]${NC}: ")" yn
    case "$(lc "$yn")" in
        n|no) START=false ;;
    esac
fi

if [[ "$START" == "true" ]]; then
    cd "$INSTALL_DIR"

    run_with_spinner "Pulling container images (first run takes 2-5 min)" $COMPOSE_CMD pull \
        || die "Failed to pull images. Check your internet connection and try again."

    info "Starting containers..."
    $COMPOSE_CMD up -d --remove-orphans 2>/dev/null

    # Wait for health
    echo ""
    step "Waiting for services"

    MAX_WAIT=240
    WAITED=0
    INTERVAL=5
    API_READY=false

    # Container name depends on profile
    if [[ "$DEPLOY_PROFILE" == "lite" ]]; then
        HEALTH_CONTAINER="printfarmer-monolith"
        HEALTH_LABEL="Monolith"
        LOGS_SERVICE="printfarmer"
    else
        HEALTH_CONTAINER="printfarmer-api"
        HEALTH_LABEL="API"
        LOGS_SERVICE="api"
    fi

    while [[ $WAITED -lt $MAX_WAIT ]]; do
        if docker inspect --format='{{.State.Health.Status}}' "$HEALTH_CONTAINER" 2>/dev/null | grep -q healthy; then
            API_READY=true
            break
        fi
        local_status="$(docker inspect --format='{{.State.Health.Status}}' "$HEALTH_CONTAINER" 2>/dev/null || echo 'starting')"
        printf "\r  ${CYAN}⠼${NC} ${HEALTH_LABEL}: %-12s (${WAITED}s / ${MAX_WAIT}s)" "$local_status"
        sleep $INTERVAL
        WAITED=$((WAITED + INTERVAL))
    done
    printf "\r\033[K"

    if [[ "$API_READY" == "true" ]]; then
        ok "${HEALTH_LABEL} healthy"
    else
        warn "${HEALTH_LABEL} still starting — it may need another minute. Check: $COMPOSE_CMD logs $LOGS_SERVICE"
    fi

    # Final output
    echo ""
    printf "  ${BOLD}${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}\n"
    printf "  ${BOLD}  ${GREEN}${CHECK}${NC}${BOLD}  PrintFarmer is running!${NC}\n"
    printf "  ${BOLD}${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}\n"
    echo ""
    printf "  ${BOLD}Open in your browser:${NC}\n"
    printf "    Local:   ${BOLD}${CYAN}http://localhost:${HTTP_PORT}${NC}\n"
    if [[ "$LAN_IP" != "localhost" ]]; then
        printf "    Network: ${BOLD}${CYAN}http://${LAN_IP}:${HTTP_PORT}${NC}\n"
    fi
    echo ""
    printf "  ${BOLD}First time?${NC} You'll create your admin account in the browser.\n"
    echo ""
    printf "  ${BOLD}Manage your install:${NC}\n"
    printf "    ${DIM}cd %s${NC}\n" "$INSTALL_DIR"
    printf "    ${BOLD}./printfarmer.sh logs${NC}      Watch live logs\n"
    printf "    ${BOLD}./printfarmer.sh status${NC}    Container status\n"
    printf "    ${BOLD}./printfarmer.sh stop${NC}      Stop everything\n"
    printf "    ${BOLD}./printfarmer.sh update${NC}    Pull latest & restart\n"
    printf "    ${BOLD}./printfarmer.sh backup${NC}    Backup your data\n"
    echo ""
else
    printf "  To start later:\n"
    printf "    ${BOLD}cd %s && %s up -d${NC}\n" "$INSTALL_DIR" "$COMPOSE_CMD"
    echo ""
fi
