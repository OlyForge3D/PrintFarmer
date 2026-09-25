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
check "deploy hook warns and continues when the root is not configured" \
    "[[ \$(HOOK_WRITE_RC=3 run_hook false $STABLE) == 0 ]] && grep -q '^WARN' '$TEST_ROOT/hook.out'"
check "deploy hook fails the deployment when install fails" \
    "[[ \$(HOOK_INSTALL_RC=1 run_hook false $STABLE) == 1 ]] && [[ \$(wc -l <'$HOOK_LOG') -eq 1 ]]"
check "deploy hook fails the deployment when write-config fails" "[[ \$(HOOK_WRITE_RC=1 run_hook false $STABLE) == 1 ]]"

if [[ $failures -gt 0 ]]; then
    printf '%d host-update CLI installer test(s) failed\n' "$failures" >&2
    exit 1
fi
printf 'All host-update CLI installer tests passed\n'
