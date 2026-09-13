#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common-utils.sh"

log_error "Release publication is owned by consolidated-release.yml."
log_info "Review VERSION on main (stable) or development (insider), then dispatch that workflow."
log_info "This retired helper does not merge, tag, force-push, or rewrite release history."
exit 2
