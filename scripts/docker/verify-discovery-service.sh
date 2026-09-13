#!/bin/bash
# Read-only diagnostics of actual discovery isolation and bridge HTTP paths.
# Usage: verify-discovery-service.sh [deployment-directory [env-file]]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../common-utils.sh"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
DEPLOYMENT_DIR="$(cd "${1:-$REPO_ROOT}" && pwd)"
ENV_FILE="${2:-$DEPLOYMENT_DIR/.env}"
cd "$REPO_ROOT"
COMPOSE=(docker compose --env-file "$ENV_FILE" -f "$DEPLOYMENT_DIR/docker-compose.yml")

fail() {
    log_error "$1"
    log_error "Check selected deployment, Docker DNS, routes and local service logs. Regenerate/recreate stale configuration; do not grant additional privileges. Redact credentials before sharing logs."
    exit 1
}

[[ -f "$DEPLOYMENT_DIR/docker-compose.yml" && -f "$ENV_FILE" ]] ||
    fail "Generated Compose and the deployment environment file are required."
discovery_id="$("${COMPOSE[@]}" ps -q printer-discovery)" || fail "Cannot query discovery."
api_id="$("${COMPOSE[@]}" ps -q api)" || fail "Cannot query API."
[[ -n "$discovery_id" && -n "$api_id" ]] || fail "API or discovery container is missing."
for id in "$discovery_id" "$api_id"; do
    [[ "$(docker inspect -f '{{.State.Running}}' "$id")" == true ]] ||
        fail "API or discovery container is stopped."
done

# Inspect only isolation fields, never the secret-bearing environment.
isolation="$(docker inspect -f '{{.HostConfig.Privileged}}|{{.HostConfig.ReadonlyRootfs}}|{{len .HostConfig.CapAdd}}|{{len .HostConfig.Devices}}|{{range .Mounts}}{{if ne .Type "tmpfs"}}host-mount{{end}}{{end}}|{{.HostConfig.PidMode}}|{{.HostConfig.NetworkMode}}|{{.Config.User}}|{{.HostConfig.CapDrop}}|{{.HostConfig.SecurityOpt}}' "$discovery_id")" ||
    fail "Cannot inspect discovery isolation."
IFS='|' read -r privileged readonly_root added devices mounts pid_mode network_mode user dropped security <<< "$isolation"
[[ "$privileged" == false && "$readonly_root" == true && "$added" == 0 &&
   "$devices" == 0 && -z "$mounts" && -z "$pid_mode" &&
   "$network_mode" != host && "$network_mode" != container:* &&
   -n "$user" && "$user" != root && "$user" != root:* && "$user" != 0 && "$user" != 0:* &&
   "$dropped" == "[ALL]" &&
   ( "$security" == "[no-new-privileges:true]" || "$security" == "[no-new-privileges]" ) ]] ||
    fail "Discovery isolation differs from the socket-free canonical deployment (privileged=$privileged, readonly=$readonly_root, added-capabilities=$added, devices=$devices, host-mounts=${mounts:-none}, pid=${pid_mode:-private}, network=$network_mode, user=$user, dropped=$dropped, security=$security)."

discovery_networks="$(docker inspect -f '{{range $name, $_ := .NetworkSettings.Networks}}{{println $name}}{{end}}' "$discovery_id")" ||
    fail "Cannot inspect discovery networks."
api_networks="$(docker inspect -f '{{range $name, $_ := .NetworkSettings.Networks}}{{println $name}}{{end}}' "$api_id")" ||
    fail "Cannot inspect API networks."
shared_bridge=false
while IFS= read -r network; do
    [[ -n "$network" ]] || continue
    if grep -Fxq "$network" <<< "$api_networks"; then
        driver="$(docker network inspect -f '{{.Driver}}' "$network")" || fail "Cannot inspect shared network."
        [[ "$driver" != bridge ]] || shared_bridge=true
    fi
done <<< "$discovery_networks"
[[ "$shared_bridge" == true ]] || fail "API and discovery do not share a bridge network."

probe() {
    local service="$1" url="$2"
    log_info "Checking $service -> $url"
    "${COMPOSE[@]}" exec -T "$service" curl --fail --silent --show-error \
        --connect-timeout 5 --max-time 15 --output /dev/null "$url" ||
        fail "$service cannot reach $url (DNS, connection or HTTP health failure)."
}
probe printer-discovery http://localhost:5247/api/discovery/health
probe printer-discovery http://api:5245/healthz
probe api http://printer-discovery:5247/api/discovery/health
log_success "Discovery isolation and bridge HTTP paths verified."
log_info "Next: sign in as a farm administrator, verify a recent heartbeat, then scan a known reachable printer. HTTP health alone does not prove either."
