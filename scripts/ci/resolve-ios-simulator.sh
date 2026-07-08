#!/bin/bash
# Resolve a deterministic iPhone simulator UDID for GitHub Actions iOS CI.

set -euo pipefail

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../common-utils.sh"

: "${GITHUB_ENV:?GITHUB_ENV must be set by GitHub Actions}"

readonly DEVICE_PREFERENCE="${IOS_SIMULATOR_DEVICE_PREFERENCE:-iPhone 15,iPhone 16}"
readonly RUNTIME_PREFERENCE="${IOS_SIMULATOR_RUNTIME_PREFERENCE:-iOS 26.4,iOS 26.5}"

SIMCTL_DEVICES_JSON="$(xcrun simctl list devices available -j)"
readonly SIMCTL_DEVICES_JSON
export SIMCTL_DEVICES_JSON DEVICE_PREFERENCE RUNTIME_PREFERENCE

RESOLVED_SIMULATOR="$(
  python3 <<'PY'
import json
import os
import re
import sys


def runtime_name(identifier: str) -> str:
    suffix = identifier.rsplit('.', 1)[-1]
    match = re.fullmatch(r'iOS-(\d+)-(\d+)(?:-(\d+))?', suffix)
    if not match:
        return suffix.replace('-', ' ')
    parts = [part for part in match.groups() if part is not None]
    return f"iOS {'.'.join(parts)}"


def version_key(name: str) -> tuple[int, ...]:
    return tuple(int(part) for part in re.findall(r'\d+', name))


def preference_values(name: str) -> list[str]:
    return [value.strip() for value in os.environ[name].split(',') if value.strip()]


data = json.loads(os.environ['SIMCTL_DEVICES_JSON'])
device_preference = preference_values('DEVICE_PREFERENCE')
runtime_preference = preference_values('RUNTIME_PREFERENCE')

candidates = []
for runtime_identifier, devices in data.get('devices', {}).items():
    runtime = runtime_name(runtime_identifier)
    if not runtime.startswith('iOS '):
        continue
    for device in devices:
        name = device.get('name', '')
        if device.get('isAvailable') is False or name not in device_preference:
            continue
        candidates.append({
            'runtime': runtime,
            'runtimeVersion': version_key(runtime),
            'name': name,
            'deviceRank': device_preference.index(name),
            'runtimeRank': runtime_preference.index(runtime) if runtime in runtime_preference else None,
            'udid': device['udid'],
        })

preferred = [candidate for candidate in candidates if candidate['runtimeRank'] is not None]
if preferred:
    selected = min(preferred, key=lambda candidate: (candidate['runtimeRank'], candidate['deviceRank']))
elif candidates:
    candidates.sort(key=lambda candidate: (candidate['runtimeVersion'], -candidate['deviceRank']))
    selected = candidates[-1]
    print(
        '::warning::Preferred iOS simulator runtime not found; '
        f"falling back to {selected['name']} on {selected['runtime']}.",
        file=sys.stderr,
    )
else:
    print('No available preferred iPhone simulator found.', file=sys.stderr)
    print(f'Device preference: {device_preference}', file=sys.stderr)
    print(f'Runtime preference: {runtime_preference}', file=sys.stderr)
    sys.exit(1)

print(f"{selected['udid']}\t{selected['name']}\t{selected['runtime']}")
PY
)"

IFS=$'\t' read -r SIMULATOR_UDID SIMULATOR_NAME SIMULATOR_RUNTIME <<< "$RESOLVED_SIMULATOR"

{
  echo "SIMULATOR_UDID=$SIMULATOR_UDID"
  echo "SIMULATOR_NAME=$SIMULATOR_NAME"
  echo "SIMULATOR_RUNTIME=$SIMULATOR_RUNTIME"
} >> "$GITHUB_ENV"

log_info "Using iOS simulator: $SIMULATOR_NAME ($SIMULATOR_RUNTIME) [$SIMULATOR_UDID]"
