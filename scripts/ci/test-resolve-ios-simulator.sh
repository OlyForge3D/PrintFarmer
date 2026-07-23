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
if [[ "$*" != "simctl list devices available -j" ]]; then
  echo "Unexpected xcrun arguments: $*" >&2
  exit 64
fi
cat "$SIMCTL_FIXTURE"
MOCK
chmod +x "$MOCK_BIN/xcrun"

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
      PATH="$MOCK_BIN:$PATH" \
      GITHUB_ENV="$github_env" \
      SIMCTL_FIXTURE="$FIXTURE_MIXED" \
      IOS_SIMULATOR_RUNTIME_PREFERENCE="iOS 26.5" \
      "$RESOLVER" 2>&1
  )"

  assert_env_line "$github_env" "SIMULATOR_UDID=PHONE-15-UDID"
  assert_env_line "$github_env" "SIMULATOR_NAME=iPhone 15"
  assert_env_line "$github_env" "SIMULATOR_FAMILY=iPhone"
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
  assert_contains "$output" "No available iPad simulator found."
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

test_default_iphone
test_explicit_ipad_with_quoted_names
test_no_matching_family
test_invalid_family_and_prefix

log_success "resolve-ios-simulator.sh tests passed"
