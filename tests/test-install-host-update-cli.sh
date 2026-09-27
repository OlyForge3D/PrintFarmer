#!/bin/bash

# Host-update CLI installer tests (issue #3045). Builds fixture release assets whose launcher is a
# stub script, puts a recording cosign stub on PATH, and proves install verifies the signature
# identity, checksum, members and manifest, proves the CLI runs, and places it side by side
# without leaving a partial version behind. Also proves write-config emits owner-only JSON from
# only the host-update keys of a deployment .env, and refuses ambiguous input.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
INSTALLER="$REPO_ROOT/scripts/install-host-update-cli.sh"
TEST_ROOT="$(mktemp -d -t "printfarmer-install-host-update-cli-XXXXXX")"
trap 'rm -rf -- "$TEST_ROOT"' EXIT

failures=0
pass() { printf '[PASS] %s\n' "$1"; }
fail() { printf '[FAIL] %s\n' "$1" >&2; failures=$((failures + 1)); }
check() { if eval "$2"; then pass "$1"; else fail "$1"; fi; }

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) POSIX_MODES=false ;;
    *) POSIX_MODES=true ;;
esac
RID=linux-x64
STABLE=1.2.3
INSIDER=1.2.4-insider.5

sha() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'; else shasum -a 256 "$1" | awk '{print $1}'; fi; }
mode() { stat -c '%a' "$1" 2>/dev/null || stat -f '%Lp' "$1"; }

# Stub cosign: records its arguments; COSIGN_FAIL=1 makes verification fail.
BIN="$TEST_ROOT/bin"
mkdir -p "$BIN"
cat >"$BIN/cosign" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >>"$COSIGN_LOG"
[ "${COSIGN_FAIL:-0}" = "1" ] && exit 1
exit 0
STUB
chmod +x "$BIN/cosign"
export COSIGN_LOG="$TEST_ROOT/cosign.log"
export PATH="$BIN:$PATH"

# make_release <version> <dir> [variant]: writes the archive, checksum list and bundle.
make_release() {
    local version="$1" dir="$2" variant="${3:-good}" stage prefix manifest_version="$1"
    prefix="printfarmer-host-update-cli-v$version"
    stage="$(mktemp -d "$TEST_ROOT/stage.XXXXXX")"
    mkdir -p "$stage/cli" "$dir"
    cat >"$stage/cli/Farm.HostUpdate.Cli" <<'CLI'
#!/bin/sh
[ "$1" = "help" ] || exit 2
[ -n "${FAKE_CLI_BROKEN:-}" ] && exit 1
echo "Usage:"
echo "  printfarmer-host-update status [--release <releaseId>] [--json]"
CLI
    chmod 0755 "$stage/cli/Farm.HostUpdate.Cli"
    cp "$REPO_ROOT/scripts/printfarmer-host-update.sh" "$REPO_ROOT/scripts/common-utils.sh" "$stage/"
    [[ "$variant" != manifest ]] || manifest_version=9.9.9
    cat >"$stage/host-update-cli-package.json" <<JSON
{
  "schema": 1,
  "package": "printfarmer-host-update-cli",
  "version": "$manifest_version",
  "runtime": "$RID",
  "selfContained": true,
  "rolloutAuthorization": false
}
JSON
    case "$variant" in
        traversal) mkdir -p "$stage/x"; printf 'evil' >"$stage/evil" ;;
        symlink) ln -s /etc/passwd "$stage/cli/link" ;;
    esac
    if [[ "$variant" == traversal ]]; then
        # -P keeps the ../ prefix that both GNU tar and bsdtar strip by default.
        (cd "$stage/x" && tar -czPf "$dir/$prefix-$RID.tar.gz" ../evil)
    else
        tar -czf "$dir/$prefix-$RID.tar.gz" -C "$stage" .
    fi
    local hash
    hash="$(sha "$dir/$prefix-$RID.tar.gz")"
    case "$variant" in
        mismatch) hash="$(printf '0%.0s' $(seq 1 64))" ;;
    esac
    if [[ "$variant" == missing-entry ]]; then
        printf '%s  %s\n' "$hash" "$prefix-linux-arm64.tar.gz" >"$dir/$prefix-SHA256SUMS"
    else
        printf '%s  %s\n' "$hash" "$prefix-$RID.tar.gz" >"$dir/$prefix-SHA256SUMS"
    fi
    printf '{}\n' >"$dir/$prefix-SHA256SUMS.sigstore.json"
    rm -rf "$stage"
}

run_install() {
    local status=0
    "$INSTALLER" install --runtime "$RID" "$@" >"$TEST_ROOT/out.log" 2>&1 || status=$?
    echo "$status"
}

ROOT="$TEST_ROOT/opt/host-update-cli"
ASSETS="$TEST_ROOT/assets"
make_release "$STABLE" "$ASSETS"

: >"$COSIGN_LOG"
check "stable install succeeds" "[[ \$(run_install --version $STABLE --asset-dir '$ASSETS' --install-root '$ROOT') == 0 ]]"
check "stable identity is the main-branch release workflow" \
    "grep -q -- '--certificate-identity https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/main ' '$COSIGN_LOG'"
check "cosign pins the GitHub OIDC issuer" \
    "grep -q -- '--certificate-oidc-issuer https://token.actions.githubusercontent.com ' '$COSIGN_LOG'"
check "cosign verifies the checksum list, not a copy in the asset directory" \
    "! grep -q -- '$ASSETS' '$COSIGN_LOG'"
check "placed CLI launches through the packaged wrapper location" \
    "[[ -x '$ROOT/$STABLE/cli/Farm.HostUpdate.Cli' && -f '$ROOT/$STABLE/printfarmer-host-update.sh' ]]"
check "install prints the wrapper path" "grep -qx '$ROOT/$STABLE/printfarmer-host-update.sh' '$TEST_ROOT/out.log'"
if $POSIX_MODES; then
    check "placement is not group- or world-writable" \
        "[[ -z \$(find '$ROOT/$STABLE' -perm -020 -o -perm -002) && \$(mode '$ROOT/$STABLE') == 755 ]]"
fi

INSIDER_ASSETS="$TEST_ROOT/insider"
make_release "$INSIDER" "$INSIDER_ASSETS"
: >"$COSIGN_LOG"
check "insider install succeeds side by side" \
    "[[ \$(run_install --version $INSIDER --asset-dir '$INSIDER_ASSETS' --install-root '$ROOT') == 0 && -d '$ROOT/$STABLE' && -d '$ROOT/$INSIDER' ]]"
check "insider identity is the development-branch release workflow" \
    "grep -q -- 'consolidated-release.yml@refs/heads/development ' '$COSIGN_LOG'"

check "same-version reinstall of an identical placement succeeds" \
    "[[ \$(run_install --version $STABLE --asset-dir '$ASSETS' --install-root '$ROOT') == 0 && -d '$ROOT/$STABLE/cli' ]]"
printf 'tampered\n' >>"$ROOT/$STABLE/LICENSE"
cp "$ROOT/$STABLE/LICENSE" "$TEST_ROOT/tampered-license"
check "a differing same-version placement is refused and left untouched" \
    "[[ \$(run_install --version $STABLE --asset-dir '$ASSETS' --install-root '$ROOT') == 1 ]] && cmp -s '$ROOT/$STABLE/LICENSE' '$TEST_ROOT/tampered-license' && grep -q 'differs from the verified release' '$TEST_ROOT/out.log'"
rm -rf -- "${ROOT:?}/$STABLE"
check "a removed placement can be reinstalled" \
    "[[ \$(run_install --version $STABLE --asset-dir '$ASSETS' --install-root '$ROOT') == 0 && -d '$ROOT/$STABLE/cli' ]]"
chmod a-x "$ROOT/$STABLE/cli/Farm.HostUpdate.Cli"
check "a same-version placement with a non-executable launcher is refused and left untouched" \
    "[[ \$(run_install --version $STABLE --asset-dir '$ASSETS' --install-root '$ROOT') == 1 && ! -x '$ROOT/$STABLE/cli/Farm.HostUpdate.Cli' ]] && grep -q 'differs from the verified release' '$TEST_ROOT/out.log'"
chmod a+x "$ROOT/$STABLE/cli/Farm.HostUpdate.Cli"
chmod 0777 "$ROOT/$STABLE"
check "a world-writable same-version directory is refused and left untouched" \
    "[[ \$(run_install --version $STABLE --asset-dir '$ASSETS' --install-root '$ROOT') == 1 && \$(stat -c %a '$ROOT/$STABLE') == 777 ]] && grep -q 'differs from the verified release' '$TEST_ROOT/out.log'"
chmod 0755 "$ROOT/$STABLE"
if [[ "$(id -u)" == "0" ]] && id nobody >/dev/null 2>&1; then
    mkdir -p "$TEST_ROOT/foreign-root" && chown nobody "$TEST_ROOT/foreign-root" && chmod 0755 "$TEST_ROOT/foreign-root"
    check "an install root owned by another account is refused" \
        "[[ \$(run_install --version $STABLE --asset-dir '$ASSETS' --install-root '$TEST_ROOT/foreign-root') == 1 && -z \$(ls -A '$TEST_ROOT/foreign-root') ]] && grep -q 'owned by another account' '$TEST_ROOT/out.log'"
fi

make_release 2.0.0 "$TEST_ROOT/v2"
check "signature failure exits 1 and places nothing" \
    "[[ \$(COSIGN_FAIL=1 run_install --version 2.0.0 --asset-dir '$TEST_ROOT/v2' --install-root '$ROOT') == 1 && ! -e '$ROOT/2.0.0' ]] && grep -q 'not signed by the main release workflow' '$TEST_ROOT/out.log'"
check "missing cosign fails closed" \
    "[[ \$(PATH=/usr/bin:/bin run_install --version 2.0.0 --asset-dir '$TEST_ROOT/v2' --install-root '$ROOT') == 1 && ! -e '$ROOT/2.0.0' ]]"

for case in 'mismatch:SHA-256 mismatch' 'missing-entry:does not name' 'manifest:manifest does not match' \
    'symlink:link or special file' 'traversal:unsafe member name'; do
    variant="${case%%:*}"
    make_release 3.0.0 "$TEST_ROOT/$variant" "$variant"
    check "$variant archive is refused for the right reason and places nothing" \
        "[[ \$(run_install --version 3.0.0 --asset-dir '$TEST_ROOT/$variant' --install-root '$ROOT') == 1 && ! -e '$ROOT/3.0.0' ]] && grep -q '${case#*:}' '$TEST_ROOT/out.log'"
done

check "a CLI that cannot run on this host keeps the previous placement" \
    "[[ \$(FAKE_CLI_BROKEN=1 run_install --version $STABLE --asset-dir '$ASSETS' --install-root '$ROOT') == 1 && -x '$ROOT/$STABLE/cli/Farm.HostUpdate.Cli' ]]"
check "no staging or previous directories are left behind" \
    "[[ -z \$(find '$ROOT' -maxdepth 1 -name '.*' ! -name . -print) ]]"

check "invalid version is a usage error" \
    "[[ \$(run_install --version 1.2 --asset-dir '$ASSETS' --install-root '$ROOT') == 2 ]]"
check "relative install root is a usage error" \
    "[[ \$(run_install --version $STABLE --asset-dir '$ASSETS' --install-root rel) == 2 ]]"
check "unsupported runtime is a usage error" \
    "[[ \$(\"$INSTALLER\" install --version $STABLE --runtime win-x64 --asset-dir '$ASSETS' >/dev/null 2>&1; echo \$?) == 2 ]]"
if $POSIX_MODES; then
    OPEN_ROOT="$TEST_ROOT/open-root"
    mkdir -p "$OPEN_ROOT" && chmod 0777 "$OPEN_ROOT"
    check "a world-writable install root is refused" \
        "[[ \$(run_install --version $STABLE --asset-dir '$ASSETS' --install-root '$OPEN_ROOT') == 1 && ! -e '$OPEN_ROOT/$STABLE' ]]"
fi

# write-config
write_config() {
    local status=0
    "$INSTALLER" write-config "$@" >"$TEST_ROOT/out.log" 2>&1 || status=$?
    echo "$status"
}
ENV="$TEST_ROOT/deploy.env"
CONFIG="$TEST_ROOT/etc/host-update.json"
printf 'DB_PROVIDER=postgres\nPOSTGRES_PASSWORD=unrelated\n' >"$ENV"
check "write-config exits 3 and writes nothing when the root is not configured" \
    "[[ \$(write_config --env-file '$ENV' --output '$CONFIG') == 3 && ! -e '$CONFIG' ]]"

STATE_ROOT="$TEST_ROOT/state"
mkdir -p "$STATE_ROOT"
cat >"$ENV" <<ENVFILE
# deployment settings
DB_PROVIDER=sqlite
POSTGRES_PASSWORD=unrelated
ConnectionStrings__Default=Host=db;Password=p"w\\d
HostUpdateExecution__RootDirectory=$STATE_ROOT
HostUpdateExecution__ComposeFiles__0=/srv/pf/docker-compose.yml
HostUpdateExecution__ActiveServiceIds__0=api
HostUpdateExecution__ActiveServiceIds__0=api-last
HostUpdates__HostState__Namespace=farm-a
ENVFILE
printf 'DB_PROVIDER=postgres\r\n' >>"$ENV"
check "write-config succeeds" "[[ \$(write_config --env-file '$ENV' --output '$CONFIG') == 0 ]]"
if command -v python3 >/dev/null 2>&1; then
    expected="{'ConnectionStrings': {'Default': 'Host=db;Password=p\"w\\\\d'}, 'DB_PROVIDER': 'postgres', 'HostUpdateExecution': {'ActiveServiceIds': {'0': 'api-last'}, 'ComposeFiles': {'0': '/srv/pf/docker-compose.yml'}, 'RootDirectory': '$STATE_ROOT'}, 'HostUpdates': {'HostState': {'Namespace': 'farm-a'}}}"
    actual="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])))' "$CONFIG")"
    [[ "$actual" == "$expected" ]] && pass "config holds exactly the host-update keys, last value wins" \
        || fail "config holds exactly the host-update keys, last value wins: $actual"
fi
check "unrelated secrets are not copied" "! grep -q unrelated '$CONFIG'"
if $POSIX_MODES; then
    check "config is mode 0600 and owned by the state root's owner" \
        "[[ \$(mode '$CONFIG') == 600 && \$(ls -ld '$CONFIG' | awk '{print \$3}') == \$(ls -ld '$STATE_ROOT' | awk '{print \$3}') ]]"
fi
check "no temporary config files are left behind" "[[ -z \$(find '$TEST_ROOT/etc' -name '.host-update.json.*') ]]"

reject() {
    local name="$1" line="$2"
    printf 'HostUpdateExecution__RootDirectory=%s\n%s\n' "$STATE_ROOT" "$line" >"$ENV"
    cp "$CONFIG" "$TEST_ROOT/before.json"
    check "$name is refused and the existing config is kept" \
        "[[ \$(write_config --env-file '$ENV' --output '$CONFIG') == 1 ]] && cmp -s '$CONFIG' '$TEST_ROOT/before.json'"
    check "$name error does not print the value" "! grep -q 'SECRET' '$TEST_ROOT/out.log'"
}
reject 'a value with $' 'ConnectionStrings__Default=Password=SECRET$x'
reject 'a case-only duplicate key' 'HostUpdateExecution__rootdirectory=/SECRET'
reject 'a key that is both value and section' "HostUpdateExecution__RootDirectory__Child=SECRET"
reject 'a malformed key segment' 'HostUpdateExecution__Bad___Key=SECRET'

ln -s "$TEST_ROOT/elsewhere.json" "$TEST_ROOT/link.json"
printf 'HostUpdateExecution__RootDirectory=%s\n' "$STATE_ROOT" >"$ENV"
check "a symlinked output is refused" \
    "[[ \$(write_config --env-file '$ENV' --output '$TEST_ROOT/link.json') == 1 && ! -e '$TEST_ROOT/elsewhere.json' ]]"
check "a relative env file is a usage error" "[[ \$(write_config --env-file deploy.env --output '$CONFIG') == 2 ]]"
ln -s "$STATE_ROOT" "$TEST_ROOT/state-link"
printf 'HostUpdateExecution__RootDirectory=%s\n' "$TEST_ROOT/state-link" >"$ENV"
check "a symlinked state root is not trusted as the config owner" \
    "[[ \$(write_config --env-file '$ENV' --output '$CONFIG') == 0 && \$(ls -ld '$CONFIG' | awk '{print \$3}') == \$(id -un) ]] && grep -q 'non-link directory' '$TEST_ROOT/out.log'"
printf 'HostUpdateExecution__RootDirectory=%s\n' "$TEST_ROOT/not-created-yet" >"$ENV"
check "a missing state root warns that the current user owns the config" \
    "[[ \$(write_config --env-file '$ENV' --output '$CONFIG') == 0 ]] && grep -q 'non-link directory' '$TEST_ROOT/out.log'"
printf 'HostUpdateExecution__RootDirectory=relative/state\n' >"$ENV"
check "a relative state root warns that the current user owns the config" \
    "[[ \$(write_config --env-file '$ENV' --output '$CONFIG') == 0 ]] && grep -q 'non-link directory' '$TEST_ROOT/out.log'"

# deploy-docker.sh opt-in hook: run the real function against a recording stub installer.
HOOK_DIR="$TEST_ROOT/hook"
mkdir -p "$HOOK_DIR"
cat >"$HOOK_DIR/install-host-update-cli.sh" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >>"$HOOK_LOG"
[ "$1" = "write-config" ] && exit "${HOOK_WRITE_RC:-0}"
exit "${HOOK_INSTALL_RC:-0}"
STUB
chmod +x "$HOOK_DIR/install-host-update-cli.sh"
sed -n '/^install_host_update_cli_if_requested() {$/,/^}$/p' "$REPO_ROOT/scripts/deploy-docker.sh" >"$HOOK_DIR/hook.sh"
run_hook() {
    # run_hook <dry-run> <version> [assets]: echoes the exit status; log in $HOOK_LOG.
    : >"$HOOK_LOG"
    (
        cd "$HOOK_DIR"
        print_info() { echo "$*"; }; print_success() { echo "$*"; }
        print_warning() { echo "WARN $*"; }; print_error() { echo "ERR $*"; }
        id() { echo 0; }
        # shellcheck disable=SC1091
        source ./hook.sh
        SCRIPT_DIR="$HOOK_DIR" ENV_FILE=.env DRY_RUN="$1" HOST_UPDATE_CLI_VERSION="$2" HOST_UPDATE_CLI_ASSETS="${3:-}"
        install_host_update_cli_if_requested
    ) >"$TEST_ROOT/hook.out" 2>&1 && echo 0 || echo $?
}
export HOOK_LOG="$TEST_ROOT/hook.log"
check "deploy hook is a no-op without a version" "[[ \$(run_hook false '') == 0 && ! -s '$HOOK_LOG' ]]"
check "deploy hook dry run installs nothing" \
    "[[ \$(run_hook true $STABLE) == 0 && ! -s '$HOOK_LOG' ]] && grep -q 'DRY RUN' '$TEST_ROOT/hook.out'"
check "deploy hook installs then writes config from the absolute env file" \
    "[[ \$(run_hook false $STABLE /media/assets) == 0 ]] && diff -q <(printf 'install --version $STABLE --asset-dir /media/assets\nwrite-config --env-file $HOOK_DIR/.env\n') '$HOOK_LOG' >/dev/null"
check "deploy hook makes a relative asset directory absolute" \
    "[[ \$(run_hook false $STABLE offline) == 0 ]] && head -n 1 '$HOOK_LOG' | grep -qx -- 'install --version $STABLE --asset-dir $HOOK_DIR/offline'"
check "deploy hook warns and continues when the root is not configured" \
    "[[ \$(HOOK_WRITE_RC=3 run_hook false $STABLE) == 0 ]] && grep -q '^WARN' '$TEST_ROOT/hook.out'"
check "deploy hook fails the deployment when install fails" \
    "[[ \$(HOOK_INSTALL_RC=1 run_hook false $STABLE) == 1 ]] && [[ \$(wc -l <'$HOOK_LOG') -eq 1 ]]"
check "deploy hook fails the deployment when write-config fails" "[[ \$(HOOK_WRITE_RC=1 run_hook false $STABLE) == 1 ]]"

# Issue #3118: the daemon unit is opt-in and installed only after the config is written.
CLI_DIR="/opt/printfarmer/host-update-cli/$STABLE"
check "deploy hook does not install the daemon unit unless asked" \
    "[[ \$(run_hook false $STABLE) == 0 ]] && ! grep -q install-service '$HOOK_LOG'"
check "deploy hook installs the daemon unit after write-config when asked" \
    "[[ \$(HOST_UPDATE_DAEMON_SERVICE=true run_hook false $STABLE) == 0 ]] && tail -n 1 '$HOOK_LOG' | grep -qx -- 'install-service --cli-dir $CLI_DIR'"
check "deploy hook passes the daemon account" \
    "[[ \$(HOST_UPDATE_DAEMON_SERVICE=true HOST_UPDATE_DAEMON_USER=pfhost run_hook false $STABLE) == 0 ]] && tail -n 1 '$HOOK_LOG' | grep -qx -- 'install-service --cli-dir $CLI_DIR --service-user pfhost'"
check "deploy hook never enables the daemon unit" \
    "[[ \$(HOST_UPDATE_DAEMON_SERVICE=true run_hook false $STABLE) == 0 ]] && ! grep -q -- '--enable' '$HOOK_LOG'"
check "deploy hook dry run describes the daemon unit without installing it" \
    "[[ \$(HOST_UPDATE_DAEMON_SERVICE=true run_hook true $STABLE) == 0 && ! -s '$HOOK_LOG' ]] && grep -q 'daemon unit' '$TEST_ROOT/hook.out'"
check "deploy hook refuses the daemon unit without a CLI version" \
    "[[ \$(HOST_UPDATE_DAEMON_SERVICE=true run_hook false '') == 1 && ! -s '$HOOK_LOG' ]]"
check "deploy hook fails when the daemon unit needs a config that was not written" \
    "[[ \$(HOST_UPDATE_DAEMON_SERVICE=true HOOK_WRITE_RC=3 run_hook false $STABLE) == 1 ]] && ! grep -q install-service '$HOOK_LOG'"
check "deploy hook fails the deployment when the daemon unit cannot be installed" \
    "[[ \$(HOST_UPDATE_DAEMON_SERVICE=true HOOK_INSTALL_RC=1 run_hook false $STABLE) == 1 ]]"

# install-service / uninstall-service against a recording systemctl stub and a scratch unit dir.
service() {
    local status=0
    "$INSTALLER" "$@" >"$TEST_ROOT/out.log" 2>&1 || status=$?
    echo "$status"
}
check "install-service without --cli-dir is a usage error" "[[ \$(service install-service) == 2 ]]"
check "install-service with a relative --cli-dir is a usage error" "[[ \$(service install-service --cli-dir cli) == 2 ]]"
if [[ "$(uname -s)" == "Linux" ]]; then
    cat >"$BIN/systemctl" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >>"$SYSTEMCTL_LOG"
[ "$1" != "${SYSTEMCTL_FAIL:-}" ] || exit 1
exit "${SYSTEMCTL_RC:-0}"
STUB
    chmod +x "$BIN/systemctl"
    export SYSTEMCTL_LOG="$TEST_ROOT/systemctl.log"
    SVC_CLI="$TEST_ROOT/svc/cli-root/$STABLE"
    mkdir -p "$SVC_CLI/cli" "$TEST_ROOT/svc/units" "$TEST_ROOT/svc/state"
    chmod 0755 "$TEST_ROOT/svc" "$TEST_ROOT/svc/cli-root" "$SVC_CLI" "$SVC_CLI/cli"
    printf '#!/bin/sh\nexit 0\n' >"$SVC_CLI/cli/Farm.HostUpdate.Cli"
    chmod 0755 "$SVC_CLI/cli/Farm.HostUpdate.Cli"
    printf '{\n  "package": "printfarmer-host-update-cli",\n  "rolloutAuthorization": false\n}\n' >"$SVC_CLI/host-update-cli-package.json"
    SVC_CONFIG="$TEST_ROOT/svc/host-update.json"
    printf '{"HostUpdateExecution":{"RootDirectory":"%s"}}\n' "$TEST_ROOT/svc/state" >"$SVC_CONFIG"
    chmod 0600 "$SVC_CONFIG"
    UNITS="$TEST_ROOT/svc/units"
    UNIT="$UNITS/printfarmer-host-update-daemon.service"
    ME="$(id -un)"
    svc_install() { : >"$SYSTEMCTL_LOG"; service install-service --cli-dir "$SVC_CLI" --config "$SVC_CONFIG" --unit-dir "$UNITS" "$@"; }

    if [[ "$(id -u)" == "0" ]]; then
        check "a root-owned config is refused unless root is named explicitly" \
            "[[ \$(svc_install) == 1 && ! -e '$UNIT' ]] && grep -q 'would run as root' '$TEST_ROOT/out.log'"
    else
        check "the unit runs as the config owner by default" \
            "[[ \$(svc_install) == 0 ]] && grep -qx 'User=$ME' '$UNIT'"
        rm -f "$UNIT"
    fi
    check "install-service writes the unit" "[[ \$(svc_install --service-user '$ME') == 0 && -f '$UNIT' ]]"
    check "the unit is not enabled or started by default" \
        "! grep -qE '^(enable|start)' '$SYSTEMCTL_LOG' && grep -qx 'daemon-reload' '$SYSTEMCTL_LOG'"
    check "the unit runs the daemon from the installed CLI with the config" \
        "grep -qx 'ExecStart=$SVC_CLI/cli/Farm.HostUpdate.Cli --config $SVC_CONFIG daemon' '$UNIT'"
    check "the unit runs from the CLI directory, not /, so the state root is not under its working directory" \
        "grep -qx 'WorkingDirectory=$SVC_CLI/cli' '$UNIT'"
    check "the unit carries no environment or credential" "! grep -qiE '^(Environment|EnvironmentFile|LoadCredential|SetCredential)' '$UNIT'"
    check "the unit is hardened" \
        "grep -qx 'NoNewPrivileges=yes' '$UNIT' && grep -qx 'CapabilityBoundingSet=' '$UNIT' && grep -qx 'RestartPreventExitStatus=2 3 7' '$UNIT'"
    check "the unit is mode 0644" "[[ \$(mode '$UNIT') == 644 ]]"
    cp "$UNIT" "$TEST_ROOT/unit.before"
    check "a rerun is idempotent and does not reload" \
        "[[ \$(svc_install --service-user '$ME') == 0 ]] && cmp -s '$UNIT' '$TEST_ROOT/unit.before' && ! grep -q . '$SYSTEMCTL_LOG'"
    if command -v systemd-analyze >/dev/null 2>&1; then
        check "systemd accepts the rendered unit" "systemd-analyze verify --man=no '$UNIT' >'$TEST_ROOT/verify.log' 2>&1"
    else
        echo "[SKIP] systemd-analyze is not available to verify the rendered unit"
    fi
    printf '# edited\n' >>"$UNIT"
    cp "$UNIT" "$TEST_ROOT/unit.edited"
    check "a failed daemon-reload restores the previous unit" \
        "[[ \$(SYSTEMCTL_FAIL=daemon-reload svc_install --service-user '$ME') == 1 ]] && cmp -s '$UNIT' '$TEST_ROOT/unit.edited' && [[ -z \$(find '$UNITS' -name '.*' -print) ]]"
    check "a failed try-restart restores the previous unit and reloads" \
        "[[ \$(SYSTEMCTL_FAIL=try-restart svc_install --service-user '$ME') == 1 ]] && cmp -s '$UNIT' '$TEST_ROOT/unit.edited' && [[ \$(grep -cx 'daemon-reload' '$SYSTEMCTL_LOG') == 2 ]]"
    check "a rerun after a failure installs the unit" \
        "[[ \$(svc_install --service-user '$ME') == 0 ]] && cmp -s '$UNIT' '$TEST_ROOT/unit.before'"
    rm -f "$UNIT"
    check "a failed first install leaves no unit behind" \
        "[[ \$(SYSTEMCTL_FAIL=daemon-reload svc_install --service-user '$ME') == 1 && ! -e '$UNIT' ]]"
    check "install-service reinstalls the unit" "[[ \$(svc_install --service-user '$ME') == 0 && -f '$UNIT' ]]"
    if [[ "$(id -u)" == "0" ]] && id nobody >/dev/null 2>&1; then
        NOBODY_CONFIG="$TEST_ROOT/svc/nobody.json"
        cp "$SVC_CONFIG" "$NOBODY_CONFIG" && chown nobody "$NOBODY_CONFIG" && chmod 0600 "$NOBODY_CONFIG"
        chmod 0700 "$SVC_CLI/cli"
        check "a CLI the service account cannot execute is refused" \
            "[[ \$(service install-service --cli-dir '$SVC_CLI' --config '$NOBODY_CONFIG' --unit-dir '$UNITS' --service-user nobody) == 1 ]] && grep -q 'cannot read and execute' '$TEST_ROOT/out.log'"
        chmod 0755 "$SVC_CLI/cli"
    fi
    check "--enable enables and starts the unit" \
        "[[ \$(svc_install --service-user '$ME' --enable) == 0 ]] && grep -qx 'enable --now printfarmer-host-update-daemon.service' '$SYSTEMCTL_LOG'"
    check "an unknown service account is refused" "[[ \$(svc_install --service-user pf-no-such-user) == 1 ]]"
    check "a service account that does not own the config is refused" \
        "[[ \$(svc_install --service-user nobody) == 1 ]] && grep -q 'must be owned by the service account' '$TEST_ROOT/out.log'"
    chmod 0640 "$SVC_CONFIG"
    check "a group-readable config is refused" "[[ \$(svc_install --service-user '$ME') == 1 ]]"
    chmod 0600 "$SVC_CONFIG"
    chmod 0775 "$SVC_CLI/cli"
    check "a group-writable CLI is refused" "[[ \$(svc_install --service-user '$ME') == 1 ]]"
    chmod 0755 "$SVC_CLI/cli"
    check "a CLI directory without the package manifest is refused" \
        "[[ \$(service install-service --cli-dir '$TEST_ROOT/svc/cli-root' --config '$SVC_CONFIG' --unit-dir '$UNITS' --service-user '$ME') == 1 ]]"

    : >"$SYSTEMCTL_LOG"
    check "uninstall-service stops, disables and removes the unit and keeps the config" \
        "[[ \$(service uninstall-service --unit-dir '$UNITS') == 0 && ! -e '$UNIT' && -f '$SVC_CONFIG' && -d '$TEST_ROOT/svc/state' ]] && grep -qx 'disable --now printfarmer-host-update-daemon.service' '$SYSTEMCTL_LOG'"
    check "uninstall-service is a no-op when the unit is absent" "[[ \$(service uninstall-service --unit-dir '$UNITS') == 0 ]]"
    printf '[Unit]\nDescription=someone else\n' >"$UNIT"
    check "install-service refuses to replace a unit it did not write" \
        "[[ \$(svc_install --service-user '$ME') == 1 ]] && grep -qx 'Description=someone else' '$UNIT'"
    check "uninstall-service refuses to remove a unit it did not write" \
        "[[ \$(service uninstall-service --unit-dir '$UNITS') == 1 && -f '$UNIT' ]]"
    rm -f "$BIN/systemctl"
else
    check "install-service is refused off Linux" \
        "[[ \$(service install-service --cli-dir /opt/x --config /etc/x.json --unit-dir '$TEST_ROOT') == 1 ]]"
fi

if [[ $failures -gt 0 ]]; then
    printf '%d host-update CLI installer test(s) failed\n' "$failures" >&2
    exit 1
fi
printf 'All host-update CLI installer tests passed\n'
