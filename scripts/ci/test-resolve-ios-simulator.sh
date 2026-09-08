#!/bin/bash
# Deterministic tests for resolve-ios-simulator.sh family and preference rules.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
readonly REPO_ROOT
# shellcheck source=scripts/common-utils.sh
source "$REPO_ROOT/scripts/common-utils.sh"

readonly RESOLVER="$SCRIPT_DIR/resolve-ios-simulator.sh"
TEMP_DIR="$(mktemp -d)"
readonly TEMP_DIR
readonly MOCK_BIN="$TEMP_DIR/bin"
readonly FIXTURE_MIXED="$TEMP_DIR/mixed.json"
readonly FIXTURE_IPHONE_ONLY="$TEMP_DIR/iphone-only.json"

cleanup() {
  rm -rf "$TEMP_DIR"
}
trap cleanup EXIT

fail() {
  log_error "$1" >&2
  exit 1
}

assert_env_line() {
  local env_file="$1"
  local expected="$2"
  grep -Fqx "$expected" "$env_file" \
    || fail "Expected '$expected' in $env_file"
}

assert_contains() {
  local value="$1"
  local expected="$2"
  [[ "$value" == *"$expected"* ]] \
    || fail "Expected output to contain '$expected'; got: $value"
}

mkdir -p "$MOCK_BIN"
cat > "$MOCK_BIN/xcrun" <<'MOCK'
#!/bin/bash
set -euo pipefail
case "$*" in
  "simctl list devices available -j") cat "$SIMCTL_FIXTURE" ;;
  "simctl list runtimes -j") cat "$SIMCTL_RUNTIME_FIXTURE" ;;
  *) echo "Unexpected xcrun arguments: $*" >&2; exit 64 ;;
esac
MOCK
chmod +x "$MOCK_BIN/xcrun"

export SIMCTL_RUNTIME_FIXTURE="$TEMP_DIR/runtimes.json"
cat > "$SIMCTL_RUNTIME_FIXTURE" <<'JSON'
{
  "runtimes": [
    {"identifier": "com.apple.CoreSimulator.SimRuntime.iOS-26-5", "name": "iOS 26.5", "version": "26.5", "buildversion": "23F77", "isAvailable": true},
    {"identifier": "com.apple.CoreSimulator.SimRuntime.iOS-27-0", "name": "iOS 27.0", "version": "27.0", "buildversion": "24A5423a", "isAvailable": true},
    {"identifier": "com.apple.CoreSimulator.SimRuntime.iOS-26-4", "name": "iOS 26.4", "version": "26.4", "buildversion": "23E244", "isAvailable": true}
  ]
}
JSON

cat > "$FIXTURE_MIXED" <<'JSON'
{
  "devices": {
    "com.apple.CoreSimulator.SimRuntime.iOS-26-5": [
      {
        "name": "iPhone 15",
        "udid": "PHONE-15-UDID",
        "isAvailable": true
      },
      {
        "name": "iPhone 17 Pro",
        "udid": "PHONE-17-PRO-UDID",
        "isAvailable": true
      },
      {
        "name": "iPad Pro 13-inch (M5)",
        "udid": "IPAD-PRO-13-UDID",
        "isAvailable": true
      },
      {
        "name": "iPad mini (A17 Pro)",
        "udid": "IPAD-MINI-UDID",
        "isAvailable": true
      }
    ]
  }
}
JSON

cat > "$FIXTURE_IPHONE_ONLY" <<'JSON'
{
  "devices": {
    "com.apple.CoreSimulator.SimRuntime.iOS-26-5": [
      {
        "name": "iPhone 17",
        "udid": "PHONE-ONLY-UDID",
        "isAvailable": true
      }
    ]
  }
}
JSON

test_default_iphone() {
  local github_env="$TEMP_DIR/default.env"
  local output
  output="$(
    env \
      -u IOS_SIMULATOR_DEVICE_FAMILY \
      -u IOS_SIMULATOR_DEVICE_PREFIX \
      -u IOS_SIMULATOR_DEVICE_PREFERENCE \
      -u IOS_SIMULATOR_RUNTIME_PREFERENCE \
      PATH="$MOCK_BIN:$PATH" \
      GITHUB_ENV="$github_env" \
      SIMCTL_FIXTURE="$FIXTURE_MIXED" \
      "$RESOLVER" 2>&1
  )"

  assert_env_line "$github_env" "SIMULATOR_UDID=PHONE-15-UDID"
  assert_env_line "$github_env" "SIMULATOR_NAME=iPhone 15"
  assert_env_line "$github_env" "SIMULATOR_FAMILY=iPhone"
  assert_env_line "$github_env" "SIMULATOR_RUNTIME=iOS 26.5"
  [[ "$(wc -l < "$github_env" | tr -d ' ')" == 4 ]] \
    || fail "CI contract must contain exactly four environment lines"
  assert_contains "$output" "Using iOS simulator: iPhone 15"
}

test_explicit_ipad_with_quoted_names() {
  local github_env="$TEMP_DIR/ipad.env"
  local output
  output="$(
    env \
      PATH="$MOCK_BIN:$PATH" \
      GITHUB_ENV="$github_env" \
      SIMCTL_FIXTURE="$FIXTURE_MIXED" \
      IOS_SIMULATOR_DEVICE_FAMILY="iPad" \
      IOS_SIMULATOR_DEVICE_PREFIX="iPad Pro " \
      IOS_SIMULATOR_DEVICE_PREFERENCE="iPad Pro 13-inch (M5),iPad Pro 11-inch (M5)" \
      IOS_SIMULATOR_RUNTIME_PREFERENCE="iOS 26.5" \
      "$RESOLVER" 2>&1
  )"

  assert_env_line "$github_env" "SIMULATOR_UDID=IPAD-PRO-13-UDID"
  assert_env_line "$github_env" "SIMULATOR_NAME=iPad Pro 13-inch (M5)"
  assert_env_line "$github_env" "SIMULATOR_FAMILY=iPad"
  assert_contains "$output" "Using iOS simulator: iPad Pro 13-inch (M5)"
}

test_no_matching_family() {
  local github_env="$TEMP_DIR/no-match.env"
  local output
  if output="$(
    env \
      PATH="$MOCK_BIN:$PATH" \
      GITHUB_ENV="$github_env" \
      SIMCTL_FIXTURE="$FIXTURE_IPHONE_ONLY" \
      IOS_SIMULATOR_DEVICE_FAMILY="iPad" \
      IOS_SIMULATOR_RUNTIME_PREFERENCE="iOS 26.5" \
      "$RESOLVER" 2>&1
  )"; then
    fail "Expected iPad resolution to reject an iPhone-only fixture"
  fi
  assert_contains "$output" "No available iPad simulator found on an approved runtime."
}

test_invalid_family_and_prefix() {
  local github_env="$TEMP_DIR/invalid.env"
  local output
  if output="$(
    env \
      PATH="$MOCK_BIN:$PATH" \
      GITHUB_ENV="$github_env" \
      SIMCTL_FIXTURE="$FIXTURE_MIXED" \
      IOS_SIMULATOR_DEVICE_FAMILY="AppleTV" \
      "$RESOLVER" 2>&1
  )"; then
    fail "Expected invalid simulator family to fail"
  fi
  assert_contains "$output" "must be iPhone or iPad"

  if output="$(
    env \
      PATH="$MOCK_BIN:$PATH" \
      GITHUB_ENV="$github_env" \
      SIMCTL_FIXTURE="$FIXTURE_MIXED" \
      IOS_SIMULATOR_DEVICE_FAMILY="iPad" \
      IOS_SIMULATOR_DEVICE_PREFIX="iPhone " \
      "$RESOLVER" 2>&1
  )"; then
    fail "Expected wrong-family device prefix to fail"
  fi
  assert_contains "$output" "does not match family 'iPad'"
}

test_runtime_policy() {
  # Generate variants of the same device names: neither fallback may escape
  # approval, including a prerelease with the final version and identifier.
  python3 - "$TEMP_DIR" "$FIXTURE_MIXED" "$SIMCTL_RUNTIME_FIXTURE" <<'PY'
import copy
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
base = json.loads(pathlib.Path(sys.argv[2]).read_text())
runtimes = json.loads(pathlib.Path(sys.argv[3]).read_text())
stable = 'com.apple.CoreSimulator.SimRuntime.iOS-26-5'
beta = 'com.apple.CoreSimulator.SimRuntime.iOS-27-0'
old = 'com.apple.CoreSimulator.SimRuntime.iOS-26-4'

for case in ['mixed', 'beta-only', 'missing', 'unavailable', 'unknown-availability',
             'runtime-unavailable', 'runtime-missing', 'same-version-beta',
             'fallback', 'ipad-fallback']:
    data = copy.deepcopy(base)
    metadata = copy.deepcopy(runtimes)
    data['devices'][beta] = copy.deepcopy(data['devices'][stable])
    for device in data['devices'][beta]:
        device['udid'] = 'BETA-' + device['udid']
    if case == 'beta-only':
        del data['devices'][stable]
        metadata['runtimes'] = [metadata['runtimes'][1]]
    elif case == 'missing':
        data['devices'] = {old: data['devices'][stable]}
        metadata['runtimes'] = [metadata['runtimes'][2]]
    elif case in ['unavailable', 'unknown-availability']:
        for device in data['devices'][stable]:
            device['isAvailable'] = False if case == 'unavailable' else None
    elif case == 'runtime-unavailable':
        metadata['runtimes'][0]['isAvailable'] = False
    elif case == 'runtime-missing':
        metadata['runtimes'].pop(0)
    elif case == 'same-version-beta':
        metadata['runtimes'][0]['buildversion'] = '23F5043g'
    elif case in ['fallback', 'ipad-fallback']:
        family = 'iPad' if case == 'ipad-fallback' else 'iPhone'
        data['devices'][stable] = [
            {'name': f'{family} 13', 'udid': 'OLDER-MODEL', 'isAvailable': True},
            {'name': f'{family} 14', 'udid': 'FALLBACK-MODEL', 'isAvailable': True},
        ]
    (root / f'{case}-devices.json').write_text(json.dumps(data))
    (root / f'{case}-runtimes.json').write_text(json.dumps(metadata))
PY

  local case preference output github_env family
  for case in mixed fallback ipad-fallback; do
    family="iPhone"
    [[ "$case" != "ipad-fallback" ]] || family="iPad"
    # An unapproved preference exercises the preferred-device fallback too.
    for preference in "iOS 26.5" "iOS 27.0"; do
      github_env="$TEMP_DIR/$case-$preference.env"
      output="$(
        env -u IOS_SIMULATOR_DEVICE_PREFIX -u IOS_SIMULATOR_DEVICE_PREFERENCE \
          PATH="$MOCK_BIN:$PATH" GITHUB_ENV="$github_env" \
          SIMCTL_FIXTURE="$TEMP_DIR/$case-devices.json" \
          SIMCTL_RUNTIME_FIXTURE="$TEMP_DIR/$case-runtimes.json" \
          IOS_SIMULATOR_DEVICE_FAMILY="$family" \
          IOS_SIMULATOR_RUNTIME_PREFERENCE="$preference" \
          "$RESOLVER" --udid 2>"$TEMP_DIR/selection.log"
      )"
      if [[ "$case" == mixed ]]; then
        [[ "$output" == PHONE-15-UDID ]] || fail "Mixed runtimes selected '$output'"
      else
        [[ "$output" == FALLBACK-MODEL ]] || fail "Fallback selected '$output'"
      fi
      assert_env_line "$github_env" "SIMULATOR_RUNTIME=iOS 26.5"
      assert_env_line "$github_env" "SIMULATOR_FAMILY=$family"
    done
  done

  for case in beta-only missing unavailable unknown-availability runtime-unavailable runtime-missing same-version-beta; do
    for preference in "iOS 26.5" "iOS 27.0"; do
      github_env="$TEMP_DIR/$case-$preference.env"
      printf '%s\n' "EXISTING=value" > "$github_env"
      if output="$(
        env -u IOS_SIMULATOR_DEVICE_PREFIX -u IOS_SIMULATOR_DEVICE_PREFERENCE \
          PATH="$MOCK_BIN:$PATH" GITHUB_ENV="$github_env" \
          SIMCTL_FIXTURE="$TEMP_DIR/$case-devices.json" \
          SIMCTL_RUNTIME_FIXTURE="$TEMP_DIR/$case-runtimes.json" \
          IOS_SIMULATOR_DEVICE_FAMILY="iPhone" \
          IOS_SIMULATOR_RUNTIME_PREFERENCE="$preference" \
          RESOLVER="$RESOLVER" \
          bash -c 'udid="$("$RESOLVER" --udid)" || exit; echo "TESTS-LAUNCHED:$udid"' \
          2>"$TEMP_DIR/rejection.log"
      )"; then
        fail "Expected $case ($preference) to fail before tests"
      fi
      [[ -z "$output" ]] || fail "Failure emitted a destination or launched tests: $output"
      [[ "$(cat "$github_env")" == "EXISTING=value" ]] || fail "Failure modified CI environment"
      assert_contains "$(cat "$TEMP_DIR/rejection.log")" "Approved runtime: iOS 26.5 (23F77)"
      assert_contains "$(cat "$TEMP_DIR/rejection.log")" "Xcode Settings > Components"
    done
  done

  # No GitHub Actions state, stdout is one UDID; diagnostics stay on stderr.
  output="$(
    env -u GITHUB_ENV -u IOS_SIMULATOR_DEVICE_PREFIX -u IOS_SIMULATOR_DEVICE_PREFERENCE \
      -u IOS_SIMULATOR_RUNTIME_PREFERENCE -u IOS_SIMULATOR_DEVICE_FAMILY \
      PATH="$MOCK_BIN:$PATH" SIMCTL_FIXTURE="$TEMP_DIR/mixed-devices.json" \
      "$RESOLVER" --udid 2>"$TEMP_DIR/local.log"
  )"
  [[ "$output" == PHONE-15-UDID ]] || fail "Local destination polluted: $output"
  assert_contains "$(cat "$TEMP_DIR/local.log")" "23F77"
}

test_default_iphone
test_explicit_ipad_with_quoted_names
test_no_matching_family
test_invalid_family_and_prefix
test_runtime_policy

log_success "resolve-ios-simulator.sh tests passed"
