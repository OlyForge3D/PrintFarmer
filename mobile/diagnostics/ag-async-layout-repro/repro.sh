#!/bin/bash
# Runs the standalone AG async-layout repro on an iOS Simulator (#3067).
#
# usage: repro.sh [--udid UDID] [--timeout N] [--matrix | --label NAME [KEY=VALUE ...]]
#
#   --udid     simulator to use; default is the shared resolver's approved iPad
#              (IOS_SIMULATOR_DEVICE_FAMILY=iPad ../../../scripts/ci/resolve-ios-simulator.sh)
#   --timeout  seconds to wait for each run's RESULT line (default 40)
#   --matrix   run the canonical A/B: async default, AG_ASYNC_LAYOUTS=0,
#              NavigationStack shell, and no starvation
#   --label    run name, used for build/runs/<label>.log (letters, digits, . _ -)
#   KEY=VALUE  launch environment for a single run (see ReproApp.swift header)
#
# Every run must start from a stopped app and end with the app's RESULT line;
# otherwise the script exits nonzero. The simulator is booted if needed and
# left booted; shut it down when done:
#   xcrun simctl shutdown <udid>
set -euo pipefail

readonly script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly bundle_id="com.example.agasyncrepro"
readonly app_dir="${script_dir}/build/AGAsyncRepro.app"
readonly out_dir="${script_dir}/build/runs"

udid=""
timeout=40
matrix=false
label="run"
run_env=()
failures=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --udid) udid="$2"; shift 2 ;;
    --timeout) timeout="$2"; shift 2 ;;
    --matrix) matrix=true; shift ;;
    --label) label="$2"; shift 2 ;;
    *=*) run_env+=("$1"); shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [[ ! "${timeout}" =~ ^[1-9][0-9]*$ ]]; then
  echo "--timeout must be a positive integer: ${timeout}" >&2
  exit 2
fi
timeout=$((10#${timeout}))
if [[ ! "${label}" =~ ^[A-Za-z0-9._-]+$ || "${label}" == "." || "${label}" == ".." ]]; then
  echo "--label must use only letters, digits, '.', '_' and '-': ${label}" >&2
  exit 2
fi

if [[ -z "${udid}" ]]; then
  udid="$(IOS_SIMULATOR_DEVICE_FAMILY=iPad \
    "${script_dir}/../../../scripts/ci/resolve-ios-simulator.sh" --udid)"
fi

[[ -d "${app_dir}" ]] || "${script_dir}/build.sh"
mkdir -p "${out_dir}"

xcrun simctl boot "${udid}" 2>/dev/null || true
xcrun simctl bootstatus "${udid}" -b >/dev/null
xcrun simctl install "${udid}" "${app_dir}"

# app_state — prints "running" or "stopped" from launchd in the simulator.
# Fails when the launchctl query itself fails, so an unknown state is never
# treated as stopped.
app_state() {
  local listing
  listing="$(xcrun simctl spawn "${udid}" launchctl list)" || return 1
  printf '%s\n' "${listing}" |
    awk -v id="UIKitApplication:${bundle_id}[" '
      index($3, id) == 1 && $1 ~ /^[0-9]+$/ { found = 1 }
      END { print (found ? "running" : "stopped") }'
}

# stop_app — terminates the app and fails unless it is confirmed stopped.
# simctl exits 3 (ESRCH) when the app is not running; that is not an error.
stop_app() {
  local rc=0
  xcrun simctl terminate "${udid}" "${bundle_id}" >/dev/null 2>&1 || rc=$?
  if [[ ${rc} -ne 0 && ${rc} -ne 3 ]]; then
    echo "simctl terminate failed (exit ${rc})" >&2
    return 1
  fi
  local attempt state
  for attempt in 1 2 3 4 5 6 7 8 9 10; do
    if ! state="$(app_state)"; then
      echo "could not query launchd in simulator ${udid}" >&2
      return 1
    fi
    [[ "${state}" == "stopped" ]] && return 0
    sleep 0.5
  done
  echo "${bundle_id} is still running after terminate (${attempt} checks)" >&2
  return 1
}

trap 'stop_app >/dev/null 2>&1 || true' EXIT
trap 'exit 130' INT TERM

# run_once LABEL [KEY=VALUE ...] — launches a fresh app with the given
# environment, waits for its RESULT line, stops it, and prints the probe lines.
# Returns nonzero when the run is not isolated or does not complete.
run_once() {
  local name="$1"
  shift
  local log="${out_dir}/${name}.log"
  local child_env=()
  local pair
  for pair in "$@"; do
    child_env+=("SIMCTL_CHILD_${pair}")
  done

  stop_app || return 1
  rm -f "${log}" "${out_dir}/${name}.err"
  env ${child_env[@]+"${child_env[@]}"} xcrun simctl launch \
    --stdout="${log}" --stderr="${out_dir}/${name}.err" "${udid}" "${bundle_id}" >/dev/null

  local waited=0
  while ! grep -q "REPRO .*RESULT " "${log}" 2>/dev/null; do
    if [[ ${waited} -ge ${timeout} ]]; then
      break
    fi
    sleep 1
    waited=$((waited + 1))
  done
  stop_app || return 1

  echo "== ${name} (${*:-default environment})"
  grep -E "REPRO .*(start|starving|mounting|toggling|first main|stall|RESULT)" "${log}" || true
  if ! grep -q "REPRO .*RESULT " "${log}"; then
    echo "FAILED: no RESULT line within ${timeout}s; see ${log}" >&2
    return 1
  fi
}

if [[ "${matrix}" == true ]]; then
  run_once async-default REPRO_STARVE_SECONDS=8 || failures=$((failures + 1))
  run_once async-off REPRO_STARVE_SECONDS=8 AG_ASYNC_LAYOUTS=0 || failures=$((failures + 1))
  run_once stack-shell REPRO_STARVE_SECONDS=8 REPRO_SHELL=stack || failures=$((failures + 1))
  run_once no-starvation REPRO_STARVE_SECONDS=0 || failures=$((failures + 1))
else
  run_once "${label}" ${run_env[@]+"${run_env[@]}"} || failures=$((failures + 1))
fi
echo "Logs: ${out_dir} (simulator ${udid} left booted)"
if [[ ${failures} -gt 0 ]]; then
  echo "${failures} run(s) did not complete" >&2
  exit 1
fi
