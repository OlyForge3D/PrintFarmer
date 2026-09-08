#!/bin/bash
# Resolve an approved simulator for CI or local use (--udid prints only its UDID).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/common-utils.sh
source "$SCRIPT_DIR/../common-utils.sh"

OUTPUT_MODE="${1:-}"
if [[ $# -gt 1 || ( -n "$OUTPUT_MODE" && "$OUTPUT_MODE" != "--udid" ) ]]; then
  log_error "Usage: $0 [--udid]" >&2
  exit 2
fi
readonly OUTPUT_MODE

readonly DEVICE_FAMILY="${IOS_SIMULATOR_DEVICE_FAMILY:-iPhone}"
case "$DEVICE_FAMILY" in
  iPhone)
    DEFAULT_DEVICE_PREFERENCE="iPhone 15,iPhone 16,iPhone 17,iPhone 17 Pro,iPhone 17e"
    ;;
  iPad)
    DEFAULT_DEVICE_PREFERENCE="iPad Pro 13-inch (M5),iPad Pro 11-inch (M5),iPad Air 13-inch (M4),iPad Air 11-inch (M4),iPad (A16),iPad mini (A17 Pro)"
    ;;
  *)
    log_error "IOS_SIMULATOR_DEVICE_FAMILY must be iPhone or iPad; got '$DEVICE_FAMILY'." >&2
    exit 2
    ;;
esac
readonly DEFAULT_DEVICE_PREFERENCE

readonly DEFAULT_DEVICE_PREFIX="${DEVICE_FAMILY} "
readonly DEVICE_PREFIX="${IOS_SIMULATOR_DEVICE_PREFIX:-$DEFAULT_DEVICE_PREFIX}"
if [[ "$DEVICE_PREFIX" != "$DEVICE_FAMILY"* ]]; then
  log_error "IOS_SIMULATOR_DEVICE_PREFIX '$DEVICE_PREFIX' does not match family '$DEVICE_FAMILY'." >&2
  exit 2
fi

readonly DEVICE_PREFERENCE="${IOS_SIMULATOR_DEVICE_PREFERENCE:-$DEFAULT_DEVICE_PREFERENCE}"
readonly RUNTIME_PREFERENCE="${IOS_SIMULATOR_RUNTIME_PREFERENCE:-iOS 26.5}"

SIMCTL_DEVICES_JSON="$(xcrun simctl list devices available -j)"
readonly SIMCTL_DEVICES_JSON
SIMCTL_RUNTIMES_JSON="$(xcrun simctl list runtimes -j)"
readonly SIMCTL_RUNTIMES_JSON
export SIMCTL_DEVICES_JSON SIMCTL_RUNTIMES_JSON DEVICE_FAMILY DEVICE_PREFIX DEVICE_PREFERENCE RUNTIME_PREFERENCE

RESOLVED_SIMULATOR="$(
  python3 <<'PY'
import json
import os
import re
import sys


# A preference is ordering, not approval. Pin the build too: a beta can share
# the final runtime version/identifier. Extend only with unchanged-image evidence.
APPROVED_RUNTIMES = {
    ('com.apple.CoreSimulator.SimRuntime.iOS-26-5', '26.5', '23F77'),
}


def version_key(name: str) -> tuple[int, ...]:
    return tuple(int(part) for part in re.findall(r'\d+', name))


def preference_values(name: str) -> list[str]:
    return [value.strip() for value in os.environ[name].split(',') if value.strip()]


data = json.loads(os.environ['SIMCTL_DEVICES_JSON'])
runtimes = json.loads(os.environ['SIMCTL_RUNTIMES_JSON'])['runtimes']
approved = {
    runtime['identifier']: runtime
    for runtime in runtimes
    if runtime.get('isAvailable') is True
    and (runtime.get('identifier'), runtime.get('version'), runtime.get('buildversion'))
    in APPROVED_RUNTIMES
}
device_preference = preference_values('DEVICE_PREFERENCE')
runtime_preference = preference_values('RUNTIME_PREFERENCE')
device_family = os.environ['DEVICE_FAMILY']
device_prefix = os.environ['DEVICE_PREFIX']

candidates = []
for runtime_identifier, devices in data.get('devices', {}).items():
    if runtime_identifier not in approved:
        continue
    metadata = approved[runtime_identifier]
    runtime = f"iOS {metadata['version']}"
    for device in devices:
        name = device.get('name', '')
        if device.get('isAvailable') is not True or not name.startswith(device_prefix):
            continue
        candidates.append({
            'runtime': runtime,
            'build': metadata['buildversion'],
            'runtimeVersion': version_key(runtime),
            'name': name,
            'model': version_key(name),
            'deviceRank': device_preference.index(name) if name in device_preference else None,
            'runtimeRank': runtime_preference.index(runtime) if runtime in runtime_preference else None,
            'udid': device['udid'],
        })

if not candidates:
    print(f'No available {device_family} simulator found on an approved runtime.', file=sys.stderr)
    print('Approved runtime: iOS 26.5 (23F77); beta/unapproved builds are excluded.', file=sys.stderr)
    print(
        'Install iOS 26.5 (23F77) in Xcode Settings > Components, select that Xcode '
        'with DEVELOPER_DIR or xcode-select, and create an available matching device '
        'in Window > Devices and Simulators. Inspect xcrun simctl list runtimes and '
        'xcrun simctl list devices available. Runtime preferences cannot approve a build.',
        file=sys.stderr,
    )
    print(f'Installed runtimes: {[(r.get("name"), r.get("buildversion"), r.get("isAvailable")) for r in runtimes]}', file=sys.stderr)
    print(f'Device prefix: {device_prefix!r}', file=sys.stderr)
    print(f'Device preference: {device_preference}', file=sys.stderr)
    print(f'Runtime preference: {runtime_preference}', file=sys.stderr)
    sys.exit(1)

preferred_runtime_and_device = [
    candidate for candidate in candidates
    if candidate['deviceRank'] is not None and candidate['runtimeRank'] is not None
]
preferred_device = [candidate for candidate in candidates if candidate['deviceRank'] is not None]

if preferred_runtime_and_device:
    selected = min(
        preferred_runtime_and_device,
        key=lambda candidate: (candidate['runtimeRank'], candidate['deviceRank']),
    )
elif preferred_device:
    selected = max(
        preferred_device,
        key=lambda candidate: (candidate['runtimeVersion'], -candidate['deviceRank'], candidate['name'], candidate['udid']),
    )
    print(
        '::warning::Preferred iOS simulator runtime not found; '
        f"using preferred device {selected['name']} on approved runtime {selected['runtime']}.",
        file=sys.stderr,
    )
else:
    selected = max(
        candidates,
        key=lambda candidate: (candidate['runtimeVersion'], candidate['model'], candidate['name'], candidate['udid']),
    )
    print(
        f'::warning::Preferred {device_family} simulator device/runtime not found; '
        f"falling back to {selected['name']} on approved runtime {selected['runtime']}.",
        file=sys.stderr,
    )

print(f"{selected['udid']}\t{selected['name']}\t{selected['runtime']}\t{selected['build']}")
PY
)"

IFS=$'\t' read -r SIMULATOR_UDID SIMULATOR_NAME SIMULATOR_RUNTIME SIMULATOR_RUNTIME_BUILD <<< "$RESOLVED_SIMULATOR"

if [[ -n "${GITHUB_ENV:-}" ]]; then
  {
    echo "SIMULATOR_UDID=$SIMULATOR_UDID"
    echo "SIMULATOR_NAME=$SIMULATOR_NAME"
    echo "SIMULATOR_RUNTIME=$SIMULATOR_RUNTIME"
    echo "SIMULATOR_FAMILY=$DEVICE_FAMILY"
  } >> "$GITHUB_ENV"
fi

log_info "Using iOS simulator: $SIMULATOR_NAME ($SIMULATOR_RUNTIME, $SIMULATOR_RUNTIME_BUILD; $DEVICE_FAMILY) [$SIMULATOR_UDID]" >&2
if [[ "$OUTPUT_MODE" == "--udid" ]]; then
  printf '%s\n' "$SIMULATOR_UDID"
fi
