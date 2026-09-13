#!/usr/bin/env bash
# The standalone proxy repair is retired; use the generated deployment.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common-utils.sh"
log_error "Standalone nginx repair is unsupported and made no changes."
log_info "From the repository root, rerun ./scripts/deploy-docker.sh with your existing saved configuration and desired published HTTP port."
log_info "This regenerates the shared bridge deployment; the proxy reaches http://api:5245 using service DNS."
log_info "Then recreate nginx-proxy using the generated Compose and matching environment file: docker compose --env-file .env -f docker-compose.yml up -d --force-recreate nginx-proxy"
exit 1
