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
if [[ -n "${SIMCTL_CALL_MARKER:-}" ]]; then
  touch "$SIMCTL_CALL_MARKER"
fi
case "$*" in
  "simctl list devices available -j") cat "$SIMCTL_FIXTURE" ;;
  "simctl list runtimes -j") cat "$SIMCTL_RUNTIME_FIXTURE" ;;
  *) echo "Unexpected xcrun arguments: $*" >&2; exit 64 ;;
esac
MOCK
chmod +x "$MOCK_BIN/xcrun"

cat > "$MOCK_BIN/xcodebuild" <<'MOCK'
#!/bin/bash
set -euo pipefail
printf 'Xcode %s\nBuild version %s\n' \
  "${XCODE_VERSION_FIXTURE:-26.6}" \
  "${XCODE_BUILD_FIXTURE:-17F113}"
MOCK
chmod +x "$MOCK_BIN/xcodebuild"

cat > "$MOCK_BIN/sw_vers" <<'MOCK'
#!/bin/bash
set -euo pipefail
[[ "$*" == "-productVersion" ]] || { echo "Unexpected sw_vers arguments: $*" >&2; exit 64; }
if [[ "${MACOS_VERSION_FAIL:-0}" == "1" ]]; then
  echo "sw_vers failed" >&2
  exit 1
fi
printf '%s\n' "${MACOS_VERSION_FIXTURE-26.6.2}"
MOCK
chmod +x "$MOCK_BIN/sw_vers"

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

test_xcode_version_policy() {
  local output
  if output="$(
    env \
      PATH="$MOCK_BIN:$PATH" \
      XCODE_VERSION_FIXTURE="27.0" \
      XCODE_BUILD_FIXTURE="27A266a" \
      SIMCTL_FIXTURE="$FIXTURE_MIXED" \
      "$RESOLVER" 2>&1
  )"; then
    fail "Expected an unsupported Xcode version to fail"
  fi
  assert_contains "$output" "Xcode 26.6 build 17F113 is required to resolve iOS simulator destinations"
  assert_contains "$output" "found 'Xcode 27.0"
}

test_xcode_build_policy() {
  local output
  if output="$(
    env \
      PATH="$MOCK_BIN:$PATH" \
      XCODE_VERSION_FIXTURE="26.6" \
      XCODE_BUILD_FIXTURE="17F114" \
      SIMCTL_FIXTURE="$FIXTURE_MIXED" \
      "$RESOLVER" 2>&1
  )"; then
    fail "Expected an unsupported Xcode build to fail"
  fi
  assert_contains "$output" "Xcode 26.6 build 17F113 is required"
  assert_contains "$output" "Build version 17F114"
}

test_macos_version_policy() {
  local version mode output github_env marker log_file
  for version in 26.2 26.6.2; do
    output="$(
      env -u GITHUB_ENV -u IOS_SIMULATOR_DEVICE_FAMILY -u IOS_SIMULATOR_DEVICE_PREFIX \
        -u IOS_SIMULATOR_DEVICE_PREFERENCE -u IOS_SIMULATOR_RUNTIME_PREFERENCE \
        PATH="$MOCK_BIN:$PATH" MACOS_VERSION_FIXTURE="$version" \
        SIMCTL_FIXTURE="$FIXTURE_MIXED" "$RESOLVER" --udid 2>"$TEMP_DIR/macos-supported.log"
    )"
    [[ "$output" == PHONE-15-UDID ]] || fail "Supported macOS $version selected '$output'"
  done

  for version in 25.7 26.0 26.1 27.0 27.2 28.0; do
    for mode in local ci; do
      github_env="$TEMP_DIR/macos-$version-$mode.env"
      marker="$TEMP_DIR/macos-$version-$mode.simctl"
      log_file="$TEMP_DIR/macos-$version-$mode.log"
      printf '%s\n' "EXISTING=value" > "$github_env"
      if [[ "$mode" == local ]]; then
        if output="$(
          env PATH="$MOCK_BIN:$PATH" MACOS_VERSION_FIXTURE="$version" \
            SIMCTL_FIXTURE="$FIXTURE_MIXED" SIMCTL_CALL_MARKER="$marker" \
            GITHUB_ENV="$github_env" "$RESOLVER" --udid 2>"$log_file"
        )"; then
          fail "Expected macOS $version to fail in local mode"
        fi
      else
        if output="$(
          env PATH="$MOCK_BIN:$PATH" MACOS_VERSION_FIXTURE="$version" \
            SIMCTL_FIXTURE="$FIXTURE_MIXED" SIMCTL_CALL_MARKER="$marker" \
            GITHUB_ENV="$github_env" "$RESOLVER" 2>"$log_file"
        )"; then
          fail "Expected macOS $version to fail in CI mode"
        fi
      fi
      [[ -z "$output" ]] || fail "Rejected macOS emitted stdout: $output"
      [[ ! -e "$marker" ]] || fail "Rejected macOS called xcrun"
      [[ "$(cat "$github_env")" == "EXISTING=value" ]] || fail "Rejected macOS modified CI environment"
      assert_contains "$(cat "$log_file")" "not supported on macOS $version"
      assert_contains "$(cat "$log_file")" "macOS 26.2-26.x"
    done
  done

  for version in malformed empty failed; do
    marker="$TEMP_DIR/macos-$version.simctl"
    github_env="$TEMP_DIR/macos-$version.env"
    printf '%s\n' "EXISTING=value" > "$github_env"
    case "$version" in
      malformed) export MACOS_VERSION_FIXTURE="27beta" MACOS_VERSION_FAIL=0 ;;
      empty) export MACOS_VERSION_FIXTURE="" MACOS_VERSION_FAIL=0 ;;
      failed) export MACOS_VERSION_FIXTURE="26.6" MACOS_VERSION_FAIL=1 ;;
    esac
    if output="$(
      env PATH="$MOCK_BIN:$PATH" SIMCTL_FIXTURE="$FIXTURE_MIXED" \
        SIMCTL_CALL_MARKER="$marker" GITHUB_ENV="$github_env" \
        "$RESOLVER" --udid 2>"$TEMP_DIR/macos-$version.log"
    )"; then
      fail "Expected $version macOS detection to fail"
    fi
    [[ -z "$output" ]] || fail "Invalid macOS detection emitted stdout: $output"
    [[ ! -e "$marker" ]] || fail "Invalid macOS detection called xcrun"
    [[ "$(cat "$github_env")" == "EXISTING=value" ]] || fail "Invalid macOS detection modified CI environment"
    assert_contains "$(cat "$TEMP_DIR/macos-$version.log")" "cannot check Xcode 26.6 compatibility"
  done
  unset MACOS_VERSION_FIXTURE MACOS_VERSION_FAIL
}

test_app_store_caller_reports_unsupported_host() {
  local output
  local marker="$TEMP_DIR/app-store.simctl"
  if output="$(
    env -u APP_STORE_IPHONE_SIMULATOR -u APP_STORE_IPAD_SIMULATOR \
      PATH="$MOCK_BIN:$PATH" MACOS_VERSION_FIXTURE="27.2" \
      SIMCTL_FIXTURE="$FIXTURE_MIXED" SIMCTL_CALL_MARKER="$marker" \
      python3 -B "$REPO_ROOT/mobile/scripts/app-store-screenshots.py" \
      2>"$TEMP_DIR/app-store.log"
  )"; then
    fail "Expected App Store capture to reject the unsupported host"
  fi
  [[ -z "$output" ]] || fail "App Store rejection emitted stdout: $output"
  [[ ! -e "$marker" ]] || fail "App Store capture called xcrun on the unsupported host"
  assert_contains "$(cat "$TEMP_DIR/app-store.log")" "not supported on macOS 27.2"
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

test_local_exploratory_policy() {
  local output marker log_file indicator value version build host
  log_file="$TEMP_DIR/exploratory.log"
  output="$(
    env -u CI -u GITHUB_ACTIONS -u GITHUB_ENV -u IOS_SIMULATOR_DEVICE_FAMILY \
      -u IOS_SIMULATOR_DEVICE_PREFIX -u IOS_SIMULATOR_DEVICE_PREFERENCE \
      -u IOS_SIMULATOR_RUNTIME_PREFERENCE \
      PATH="$MOCK_BIN:$PATH" XCODE_VERSION_FIXTURE="27.2" \
      XCODE_BUILD_FIXTURE="27B52" MACOS_VERSION_FIXTURE="27.2" \
      SIMCTL_FIXTURE="$FIXTURE_MIXED" "$RESOLVER" --local-exploratory-udid \
      2>"$log_file"
  )"
  [[ "$output" == PHONE-15-UDID ]] || fail "Exploratory destination polluted: $output"
  assert_contains "$(cat "$log_file")" "LOCAL EXPLORATORY ONLY"
  assert_contains "$(cat "$log_file")" "not pinned CI, release, or snapshot-acceptance evidence"
  assert_contains "$(cat "$log_file")" "iOS 26.5, 23F77"

  for indicator in CI GITHUB_ACTIONS GITHUB_ENV; do
    marker="$TEMP_DIR/exploratory-$indicator.simctl"
    value=true
    if [[ "$indicator" == GITHUB_ENV ]]; then
      value="$TEMP_DIR/exploratory-github.env"
      printf '%s\n' "EXISTING=value" > "$value"
    fi
    if output="$(
      env -u CI -u GITHUB_ACTIONS -u GITHUB_ENV \
        PATH="$MOCK_BIN:$PATH" XCODE_VERSION_FIXTURE="27.2" \
        XCODE_BUILD_FIXTURE="27B52" MACOS_VERSION_FIXTURE="27.2" \
        SIMCTL_FIXTURE="$FIXTURE_MIXED" SIMCTL_CALL_MARKER="$marker" \
        "$indicator=$value" "$RESOLVER" --local-exploratory-udid 2>"$log_file"
    )"; then
      fail "Exploratory mode accepted $indicator"
    fi
    [[ -z "$output" && ! -e "$marker" ]] || fail "CI rejection selected a simulator"
    assert_contains "$(cat "$log_file")" "cannot be used in CI or with GITHUB_ENV"
    if [[ "$indicator" == GITHUB_ENV ]]; then
      [[ "$(cat "$value")" == "EXISTING=value" ]] || fail "Exploratory mode modified GITHUB_ENV"
    fi
  done

  while read -r version build host; do
    marker="$TEMP_DIR/exploratory-$version-$build-$host.simctl"
    if output="$(
      env -u CI -u GITHUB_ACTIONS -u GITHUB_ENV \
        PATH="$MOCK_BIN:$PATH" XCODE_VERSION_FIXTURE="$version" \
        XCODE_BUILD_FIXTURE="$build" MACOS_VERSION_FIXTURE="$host" \
        SIMCTL_FIXTURE="$FIXTURE_MIXED" SIMCTL_CALL_MARKER="$marker" \
        "$RESOLVER" --local-exploratory-udid 2>"$log_file"
    )"; then
      fail "Exploratory mode accepted Xcode $version ($build) on macOS $host"
    fi
    [[ -z "$output" && ! -e "$marker" ]] || fail "Unapproved host selected a simulator"
    if [[ "$version" != 27.2 || "$build" != 27B52 ]]; then
      assert_contains "$(cat "$log_file")" "Xcode 27.2 build 27B52 is required"
    else
      assert_contains "$(cat "$log_file")" "requires macOS 27.x"
    fi
  done <<'CASES'
27.0 27A266a 27.2
27.2 27B53 27.2
27.2 27B52 26.6
27.2 27B52 28.0
CASES

  if output="$(
    env -u CI -u GITHUB_ACTIONS -u GITHUB_ENV \
      PATH="$MOCK_BIN:$PATH" XCODE_VERSION_FIXTURE="27.2" \
      XCODE_BUILD_FIXTURE="27B52" MACOS_VERSION_FIXTURE="27.2" \
      SIMCTL_FIXTURE="$TEMP_DIR/same-version-beta-devices.json" \
      SIMCTL_RUNTIME_FIXTURE="$TEMP_DIR/same-version-beta-runtimes.json" \
      "$RESOLVER" --local-exploratory-udid 2>"$log_file"
  )"; then
    fail "Exploratory mode accepted an unapproved runtime build"
  fi
  [[ -z "$output" ]] || fail "Unapproved exploratory runtime emitted a destination"
  assert_contains "$(cat "$log_file")" "Approved runtime: iOS 26.5 (23F77)"
}

test_shared_workflow_policy() {
  python3 - "$REPO_ROOT" <<'PY'
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
for workflow in (root / '.github/workflows').glob('*.y*ml'):
    assert '--local-exploratory-udid' not in workflow.read_text(), workflow.name

for name, count in [('ios-pr-ci', 3), ('ios-live-sensor-e2e', 1), ('ci', 1), ('testflight-beta', 1)]:
    text = (root / f'.github/workflows/{name}.yml').read_text()
    assert text.count('runs-on: macos-26') == count, name
    assert text.count('XCODE_PATH="/Applications/Xcode_26.6.app"') == count, name
    assert text.count('!= "Xcode 26.6"') == count, name
    assert text.count('!= "17F113"') == count, name
    assert 'xcode-27' not in text and '27A266a' not in text, name

text = (root / '.github/workflows/ios-pr-ci.yml').read_text()
unit = text.split('  test:\n', 1)[1].split('  xcui-shards:\n', 1)[0]
assert 'name: Unit Tests (iOS)' in unit
assert 'family: iPhone' in unit and 'selector: PrintFarmerTests\n' in unit
assert 'artifact: ios-test-results\n' in unit
assert 'name: Filament accessibility (iPad)' in unit
assert 'family: iPad' in unit
assert 'selector: PrintFarmerTests/PrinterFilamentSectionTests\n' in unit
assert 'artifact: ios-filament-ipad-results\n' in unit
assert 'IOS_SIMULATOR_DEVICE_FAMILY: ${{ matrix.family }}' in unit
assert 'IOS_SIMULATOR_DEVICE_PREFERENCE: ${{ matrix.device_preference }}' in unit
assert '"-only-testing:$UNIT_TEST_SELECTOR"' in unit
assert "if: matrix.family == 'iPad'" in unit
assert "--test-id 'PrinterFilamentSectionTests/testFilamentCardKeepsWholeWordsAndControlsAtEveryAccessibilitySize()'" in unit
assert "wc -l | tr -d ' ')\" = 10" in unit
assert 'mobile/build/FilamentAttachments/' in unit
assert 'name: ${{ matrix.artifact }}' in unit

PY
}

test_default_iphone
test_xcode_version_policy
test_xcode_build_policy
test_macos_version_policy
test_app_store_caller_reports_unsupported_host
test_explicit_ipad_with_quoted_names
test_no_matching_family
test_invalid_family_and_prefix
test_runtime_policy
test_local_exploratory_policy
test_shared_workflow_policy

log_success "resolve-ios-simulator.sh tests passed"
