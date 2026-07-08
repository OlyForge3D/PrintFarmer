#!/bin/bash
# Boot and wait for an iOS simulator, retrying once for transient CoreSimulator failures.

set -euo pipefail

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../common-utils.sh"

if [[ $# -ne 1 || -z "${1:-}" ]]; then
  log_error "Usage: $0 <simulator-udid>" >&2
  exit 2
fi

readonly SIMULATOR_UDID="$1"
readonly MAX_ATTEMPTS=2
export SIMULATOR_UDID

simulator_state() {
  local devices_json
  devices_json="$(xcrun simctl list devices -j)"
  SIMCTL_DEVICES_JSON="$devices_json" python3 <<'PY'
import json
import os
import sys

udid = os.environ['SIMULATOR_UDID']
data = json.loads(os.environ['SIMCTL_DEVICES_JSON'])
for devices in data.get('devices', {}).values():
    for device in devices:
        if device.get('udid') == udid:
            print(device.get('state', 'Unknown'))
            sys.exit(0)
print('Unknown')
PY
}

for attempt in $(seq 1 "$MAX_ATTEMPTS"); do
  state="$(simulator_state)"
  if [[ "$state" != "Booted" ]]; then
    log_info "Booting simulator $SIMULATOR_UDID (attempt $attempt/$MAX_ATTEMPTS)"
    if ! xcrun simctl boot "$SIMULATOR_UDID"; then
      state="$(simulator_state)"
      if [[ "$state" != "Booted" ]]; then
        log_warn "Simulator boot command failed; current state is $state" >&2
      fi
    fi
  fi

  if xcrun simctl bootstatus "$SIMULATOR_UDID" -b; then
    log_success "Simulator $SIMULATOR_UDID is booted and ready"
    exit 0
  fi

  if [[ "$attempt" -lt "$MAX_ATTEMPTS" ]]; then
    log_warn "Bootstatus failed; retrying simulator boot" >&2
    xcrun simctl shutdown "$SIMULATOR_UDID" || true
    sleep 5
  fi
done

log_error "Simulator $SIMULATOR_UDID failed to boot after $MAX_ATTEMPTS attempts" >&2
exit 1
