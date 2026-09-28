#!/bin/bash

# Packaged host-update CLI smoke test (issue #3041). Builds the self-contained archive for this
# Linux host's runtime with the release packaging code, verifies it against its checksum list,
# extracts it the way the runbook installs it, and runs the real CLI through the packaged wrapper
# with any dotnet on PATH poisoned, proving the package needs no source checkout, API or runtime.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_ROOT="$(mktemp -d -t "printfarmer-host-update-package-XXXXXX")"
LIFE_UNIT=printfarmer-host-update-daemon.service
LIFE_INSTALLED=0
LIFE_CLI_ROOT=""
LIFE_ROOT=""
cleanup() {
    if [[ "$LIFE_INSTALLED" == 1 ]]; then
        sudo -n bash "$REPO_ROOT/scripts/install-host-update-cli.sh" uninstall-service >/dev/null 2>&1 || true
    fi
    [[ -z "$LIFE_CLI_ROOT" ]] || sudo -n rm -rf -- "$LIFE_CLI_ROOT"
    [[ -z "$LIFE_ROOT" ]] || rm -rf -- "$LIFE_ROOT"
    rm -rf -- "$TEST_ROOT"
}
trap cleanup EXIT

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

# Issue #3118: the packaged CLI as a real systemd service. Needs a running systemd, passwordless
# sudo and no existing unit (GitHub-hosted Ubuntu runners have all three).
if [[ -d /run/systemd/system ]] && sudo -n true >/dev/null 2>&1 && [[ ! -e "/etc/systemd/system/$LIFE_UNIT" ]]; then
    check() { if eval "$2"; then pass "$1"; else fail "$1"; fi; }
    wait_for() {
        local deadline=$((SECONDS + $1))
        shift
        until "$@"; do
            ((SECONDS < deadline)) || return 1
            sleep 0.5
        done
    }
    unit_active() { systemctl is-active --quiet "$LIFE_UNIT"; }
    unit_inactive() { ! systemctl is-active --quiet "$LIFE_UNIT"; }
    unit_show() { systemctl show -p "$1" --value "$LIFE_UNIT"; }
    restarted_from() { unit_active && [[ "$(unit_show MainPID)" != "$1" && "$(unit_show MainPID)" != 0 ]]; }
    installer() {
        sudo -n bash "$REPO_ROOT/scripts/install-host-update-cli.sh" "$@" >"$TEST_ROOT/service.log" 2>&1 && return 0
        cat "$TEST_ROOT/service.log" >&2
        return 1
    }

    # The unit's CLI must be root-owned, as a real install is; the state root is owned by the
    # service account and lives outside the OS temp directory.
    LIFE_CLI_ROOT="/opt/printfarmer-lifecycle-$$"
    LIFE_CLI="$LIFE_CLI_ROOT/$VERSION"
    sudo -n mkdir -p "$LIFE_CLI_ROOT"
    sudo -n cp -a "$INSTALL" "$LIFE_CLI"
    sudo -n chown -R root:root "$LIFE_CLI_ROOT"
    sudo -n chmod -R go-w "$LIFE_CLI_ROOT"
    # As the install command does: the archive's root entry does not decide the directory mode.
    sudo -n chmod 0755 "$LIFE_CLI_ROOT" "$LIFE_CLI"
    LIFE_ROOT="$(mktemp -d "$HOME/pf-lifecycle-XXXXXX")"
    chmod 0755 "$LIFE_ROOT"
    mkdir -p "$LIFE_ROOT/root/state"
    printf 'HostUpdateExecution__RootDirectory=%s\n' "$LIFE_ROOT/root" >"$LIFE_ROOT/deploy.env"
    LIFE_CONFIG="$LIFE_ROOT/host-update.json"
    check "write-config for the service lifecycle succeeds" \
        "bash '$REPO_ROOT/scripts/install-host-update-cli.sh' write-config --env-file '$LIFE_ROOT/deploy.env' --output '$LIFE_CONFIG' >/dev/null 2>&1"

    LIFE_INSTALLED=1
    check "install-service installs the packaged daemon unit disabled and inactive" \
        "installer install-service --cli-dir '$LIFE_CLI' --config '$LIFE_CONFIG' && [[ \$(systemctl is-enabled $LIFE_UNIT || true) == disabled ]] && unit_inactive"
    sudo -n systemctl start "$LIFE_UNIT"
    check "systemd starts the daemon as the config owner" \
        "wait_for 30 unit_active && sleep 5 && unit_active && [[ \$(unit_show User) == '$(id -un)' ]]"
    check "the daemon reports its cycles to the journal" \
        "wait_for 30 sh -c \"sudo -n journalctl -u $LIFE_UNIT --no-pager -o cat | grep -q daemon_cycle_completed\""

    sudo -n systemctl stop "$LIFE_UNIT"
    check "an operator stop stops the daemon cleanly" "unit_inactive && [[ \$(unit_show Result) == success ]]"
    sleep 35
    check "an operator stop is not followed by a restart" "unit_inactive"

    sudo -n systemctl start "$LIFE_UNIT"
    wait_for 30 unit_active || true
    pid="$(unit_show MainPID)"
    sudo -n kill -KILL "$pid"
    check "systemd restarts the daemon after it dies" "wait_for 60 restarted_from '$pid'"

    # Exits 2, 3 and 7 (usage, invalid configuration, already running) are deliberately not
    # restarted: a missing state directory makes the daemon exit 3.
    sudo -n systemctl stop "$LIFE_UNIT"
    rm -rf -- "$LIFE_ROOT/root/state"
    sudo -n systemctl start "$LIFE_UNIT" || true
    check "the daemon stops itself with exit 3 when its state directory is missing" \
        "wait_for 30 unit_inactive && [[ \$(unit_show ExecMainStatus) == 3 ]]"
    sleep 35
    check "an exit-3 self-stop is not restarted" "unit_inactive"
    mkdir -p "$LIFE_ROOT/root/state"
    sudo -n systemctl reset-failed "$LIFE_UNIT" || true

    check "install-service --enable enables and starts the daemon" \
        "installer install-service --cli-dir '$LIFE_CLI' --config '$LIFE_CONFIG' --enable && [[ \$(systemctl is-enabled $LIFE_UNIT) == enabled ]] && wait_for 30 unit_active"
    check "uninstall-service disables, stops and removes the unit" \
        "installer uninstall-service && [[ ! -e /etc/systemd/system/$LIFE_UNIT ]] && unit_inactive"
    LIFE_INSTALLED=0
else
    printf '[SKIP] systemd service lifecycle needs a running systemd, passwordless sudo and no existing unit\n'
fi

if [[ "$failures" -gt 0 ]]; then
    printf '%d packaged CLI test(s) failed\n' "$failures" >&2
    exit 1
fi

printf 'All packaged host-update CLI tests passed (%s)\n' "$RID"
