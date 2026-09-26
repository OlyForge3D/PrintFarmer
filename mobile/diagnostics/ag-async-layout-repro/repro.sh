#!/bin/bash
# Runs the standalone AG async-layout repro on an iOS Simulator (#3067).
#
# usage: repro.sh [--udid UDID] [--seconds N] [--matrix | --label NAME [KEY=VALUE ...]]
#
#   --udid     simulator to use; default is the shared resolver's approved iPad
#              (IOS_SIMULATOR_DEVICE_FAMILY=iPad ../../../scripts/ci/resolve-ios-simulator.sh)
#   --seconds  how long each launch runs before it is terminated (default 16)
#   --matrix   run the canonical A/B: async default, AG_ASYNC_LAYOUTS=0,
#              NavigationStack shell, and no starvation
#   KEY=VALUE  launch environment for a single run (see ReproApp.swift header)
#
# The simulator is booted if needed and left booted; shut it down when done:
#   xcrun simctl shutdown <udid>
set -euo pipefail

readonly script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly bundle_id="com.example.agasyncrepro"
readonly app_dir="${script_dir}/build/AGAsyncRepro.app"
readonly out_dir="${script_dir}/build/runs"

udid=""
seconds=16
matrix=false
label="run"
run_env=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --udid) udid="$2"; shift 2 ;;
    --seconds) seconds="$2"; shift 2 ;;
    --matrix) matrix=true; shift ;;
    --label) label="$2"; shift 2 ;;
    *=*) run_env+=("$1"); shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "${udid}" ]]; then
  udid="$(IOS_SIMULATOR_DEVICE_FAMILY=iPad \
    "${script_dir}/../../../scripts/ci/resolve-ios-simulator.sh" --udid)"
fi

[[ -d "${app_dir}" ]] || "${script_dir}/build.sh"
mkdir -p "${out_dir}"

xcrun simctl boot "${udid}" 2>/dev/null || true
xcrun simctl bootstatus "${udid}" -b >/dev/null
xcrun simctl install "${udid}" "${app_dir}"

# run_once LABEL [KEY=VALUE ...] — launches the app with the given environment,
# waits, terminates it, and prints the probe lines.
run_once() {
  local name="$1"
  shift
  local log="${out_dir}/${name}.log"
  local child_env=()
  local pair
  for pair in "$@"; do
    child_env+=("SIMCTL_CHILD_${pair}")
  done

  xcrun simctl terminate "${udid}" "${bundle_id}" >/dev/null 2>&1 || true
  sleep 1
  rm -f "${log}"
  env ${child_env[@]+"${child_env[@]}"} xcrun simctl launch \
    --stdout="${log}" --stderr="${out_dir}/${name}.err" "${udid}" "${bundle_id}" >/dev/null
  sleep "${seconds}"
  xcrun simctl terminate "${udid}" "${bundle_id}" >/dev/null 2>&1 || true

  echo "== ${name} (${*:-default environment})"
  grep -E "REPRO .*(start|starving|mounting|toggling|first main|stall|RESULT)" "${log}" || echo "(no probe output; see ${log})"
}

if [[ "${matrix}" == true ]]; then
  run_once async-default REPRO_STARVE_SECONDS=8
  run_once async-off REPRO_STARVE_SECONDS=8 AG_ASYNC_LAYOUTS=0
  run_once stack-shell REPRO_STARVE_SECONDS=8 REPRO_SHELL=stack
  run_once no-starvation REPRO_STARVE_SECONDS=0
else
  run_once "${label}" ${run_env[@]+"${run_env[@]}"}
fi
echo "Logs: ${out_dir} (simulator ${udid} left booted)"
