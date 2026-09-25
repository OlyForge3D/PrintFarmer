#!/bin/bash

# Packaged host-update CLI smoke test (issue #3041). Builds the self-contained archive for this
# Linux host's runtime with the release packaging code, verifies it against its checksum list,
# extracts it the way the runbook installs it, and runs the real CLI through the packaged wrapper
# with any dotnet on PATH poisoned, proving the package needs no source checkout, API or runtime.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_ROOT="$(mktemp -d -t "printfarmer-host-update-package-XXXXXX")"
trap 'rm -rf -- "$TEST_ROOT"' EXIT

[[ "$(uname -s)" == Linux ]] || { printf 'Packaged CLI smoke test supports Linux hosts only\n' >&2; exit 1; }
case "$(uname -m)" in
    x86_64) RID=linux-x64 ;;
    aarch64|arm64) RID=linux-arm64 ;;
    *) printf 'Unsupported architecture: %s\n' "$(uname -m)" >&2; exit 1 ;;
esac

VERSION="0.0.0-package-test"
OUT="$TEST_ROOT/out"
ARCHIVE="printfarmer-host-update-cli-v$VERSION-$RID.tar.gz"
SUMS="printfarmer-host-update-cli-v$VERSION-SHA256SUMS"

failures=0
pass() { printf '[PASS] %s\n' "$1"; }
fail() { printf '[FAIL] %s\n' "$1" >&2; failures=$((failures + 1)); }

node "$REPO_ROOT/scripts/ci/host-update-cli-package.mjs" \
    --version "$VERSION" --runtime "$RID" --output "$OUT" --source "$REPO_ROOT"

(cd "$OUT" && sha256sum --check --strict "$SUMS") && pass "archive matches its checksum list" \
    || fail "archive matches its checksum list"
[[ "$(wc -l < "$OUT/$SUMS")" -eq 1 ]] && pass "checksum list names only the built archive" \
    || fail "checksum list names only the built archive"

if tar --numeric-owner -tvzf "$OUT/$ARCHIVE" | awk '{ print $2 }' | grep -qv '^0/0$'; then
    fail "every archive member is owned by 0/0"
else
    pass "every archive member is owned by 0/0"
fi

INSTALL="$TEST_ROOT/opt/printfarmer/host-update-cli/$VERSION"
mkdir -p "$INSTALL"
tar -xzf "$OUT/$ARCHIVE" -C "$INSTALL" --no-same-owner

mode() { stat -c '%a' "$1"; }
[[ "$(mode "$INSTALL/printfarmer-host-update.sh")" == 755 && "$(mode "$INSTALL/cli/Farm.HostUpdate.Cli")" == 755 \
    && "$(mode "$INSTALL/cli/Farm.HostUpdate.Cli.dll")" == 644 && "$(mode "$INSTALL/cli")" == 755 ]] \
    && pass "launchers are 0755, libraries 0644, directories 0755" \
    || fail "normalized modes (wrapper $(mode "$INSTALL/printfarmer-host-update.sh"), launcher $(mode "$INSTALL/cli/Farm.HostUpdate.Cli"))"

manifest="$INSTALL/host-update-cli-package.json"
if grep -q "\"runtime\": \"$RID\"" "$manifest" && grep -q '"selfContained": true' "$manifest" \
    && grep -q '"rolloutAuthorization": false' "$manifest" && grep -q "\"version\": \"$VERSION\"" "$manifest"; then
    pass "package manifest records version, runtime and no rollout authorization"
else
    fail "package manifest records version, runtime and no rollout authorization"
fi

# Any dotnet the wrapper might reach is poisoned: the package must run on its own runtime.
POISON="$TEST_ROOT/poison"
mkdir -p "$POISON"
printf '#!/bin/sh\nexit 99\n' > "$POISON/dotnet"
chmod +x "$POISON/dotnet"
CONFIG="$TEST_ROOT/host-update.json"
printf '{}\n' > "$CONFIG"

run_package() {
    local code=0
    env -u PRINTFARMER_HOST_UPDATE_CLI_DIR -u PRINTFARMER_DOTNET -u DOTNET_ROOT PATH="$POISON:/usr/bin:/bin" \
        "$INSTALL/printfarmer-host-update.sh" "$@" > "$TEST_ROOT/stdout.log" 2> "$TEST_ROOT/stderr.log" || code=$?
    return "$code"
}

code=0
run_package help || code=$?
[[ "$code" -eq 0 ]] && pass "packaged wrapper help" || fail "packaged wrapper help (exit $code)"

code=0
run_package --config "$CONFIG" status --json || code=$?
if [[ "$code" -eq 3 ]] && grep -q '"exitCode": 3' "$TEST_ROOT/stdout.log" \
    && grep -q 'root_directory_not_visible' "$TEST_ROOT/stdout.log"; then
    pass "packaged CLI runs without dotnet and fails closed on an unconfigured root (exit 3)"
else
    fail "packaged CLI run (exit $code): $(cat "$TEST_ROOT/stdout.log" "$TEST_ROOT/stderr.log")"
fi

code=0
run_package --config relative.json status || code=$?
[[ "$code" -eq 2 ]] && pass "packaged wrapper still refuses a relative config" \
    || fail "packaged wrapper still refuses a relative config (exit $code)"

if [[ "$failures" -gt 0 ]]; then
    printf '%d packaged CLI test(s) failed\n' "$failures" >&2
    exit 1
fi

printf 'All packaged host-update CLI tests passed (%s)\n' "$RID"
