#!/bin/bash

# Focused coverage for scripts/printfarmer-host-update.sh (issue #2980): the wrapper must expose
# only the fixed status/recover operations, refuse unsafe input before the CLI runs (exit 2), and
# pass accepted arguments through verbatim while preserving the CLI's exit code.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
WRAPPER="$REPO_ROOT/scripts/printfarmer-host-update.sh"
TEST_ROOT="$(mktemp -d -t "printfarmer-host-update-wrapper-XXXXXX")"
trap 'rm -rf -- "$TEST_ROOT"' EXIT

CLI_DIR="$TEST_ROOT/cli"
CONFIG="$TEST_ROOT/host-update.json"
FAKE_DOTNET="$TEST_ROOT/fake-dotnet"
ARGS_LOG="$TEST_ROOT/args.log"
mkdir -p "$CLI_DIR"
: > "$CLI_DIR/Farm.HostUpdate.Cli.dll"
printf '{}\n' > "$CONFIG"
cat > "$FAKE_DOTNET" <<EOF
#!/bin/bash
printf '%s\n' "\$@" > "$ARGS_LOG"
exit "\${FAKE_EXIT:-0}"
EOF
chmod +x "$FAKE_DOTNET"

failures=0

pass() {
    printf '[PASS] %s\n' "$1"
}

fail() {
    printf '[FAIL] %s\n' "$1" >&2
    failures=$((failures + 1))
}

run_wrapper() {
    rm -f "$ARGS_LOG"
    set +e
    PRINTFARMER_HOST_UPDATE_CLI_DIR="$CLI_DIR" PRINTFARMER_DOTNET="$FAKE_DOTNET" \
        bash "$WRAPPER" "$@" > "$TEST_ROOT/stdout.log" 2> "$TEST_ROOT/stderr.log"
    local code=$?
    set -e
    return "$code"
}

expect_usage() {
    local name="$1"
    shift
    local code=0
    run_wrapper "$@" || code=$?
    if [[ "$code" -eq 2 && ! -f "$ARGS_LOG" && ! -s "$TEST_ROOT/stdout.log" ]]; then
        pass "$name"
    else
        fail "$name (exit $code, cli invoked: $([[ -f "$ARGS_LOG" ]] && echo yes || echo no))"
    fi
}

expect_passthrough() {
    local name="$1"
    local expected="$2"
    shift 2
    local code=0
    run_wrapper "$@" || code=$?
    if [[ "$code" -eq 0 && -f "$ARGS_LOG" && "$(cat "$ARGS_LOG")" == "$expected" ]]; then
        pass "$name"
    else
        fail "$name (exit $code, args: $(tr '\n' ' ' < "$ARGS_LOG" 2>/dev/null || true))"
    fi
}

dll="$CLI_DIR/Farm.HostUpdate.Cli.dll"

expect_passthrough "status passes through" \
    "$(printf '%s\n' "$dll" --config "$CONFIG" status --json)" \
    --config "$CONFIG" status --json
expect_passthrough "status detail passes through" \
    "$(printf '%s\n' "$dll" --config "$CONFIG" status --release stable:1.2.3)" \
    --config "$CONFIG" status --release stable:1.2.3
expect_passthrough "recover preview passes through" \
    "$(printf '%s\n' "$dll" --config "$CONFIG" recover --release insider:1.2.3-rc.1 --request-id req-1 --preview)" \
    --config "$CONFIG" recover --release insider:1.2.3-rc.1 --request-id req-1 --preview
expect_passthrough "recover confirm passes through" \
    "$(printf '%s\n' "$dll" --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 --json)" \
    --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 --json
expect_passthrough "recover confirm with drift reapproval passes through" \
    "$(printf '%s\n' "$dll" --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 --reapprove-drift drift-0123456789abcdef0123456789abcdef)" \
    --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 --reapprove-drift drift-0123456789abcdef0123456789abcdef

expect_usage "malformed drift token refused" --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 --reapprove-drift 'drift-;rm'
expect_usage "missing drift token refused" --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 --reapprove-drift
expect_usage "duplicate drift token refused" --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 \
    --reapprove-drift drift-0123456789abcdef0123456789abcdef --reapprove-drift drift-0123456789abcdef0123456789abcdef
expect_passthrough "recover confirm with physical reconciliation passes through" \
    "$(printf '%s\n' "$dll" --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 --printers-reconciled physical-0123456789abcdef0123456789abcdef)" \
    --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 --printers-reconciled physical-0123456789abcdef0123456789abcdef
expect_usage "malformed physical token refused" --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 --printers-reconciled 'physical-;rm'
expect_usage "duplicate physical token refused" --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 \
    --printers-reconciled physical-0123456789abcdef0123456789abcdef --printers-reconciled physical-0123456789abcdef0123456789abcdef
expect_usage "missing --config refused" status
expect_usage "relative --config refused" --config host-update.json status
expect_passthrough "missing config file is left to the CLI (exit 3)" \
    "$(printf '%s\n' "$dll" --config "$TEST_ROOT/missing.json" status)" \
    --config "$TEST_ROOT/missing.json" status
expect_usage "unknown command refused" --config "$CONFIG" apply
expect_usage "unknown option refused" --config "$CONFIG" status --compose-file /tmp/x.yml
expect_usage "path-like release refused" --config "$CONFIG" status --release 'stable:../../etc'
expect_usage "unchannelled release refused" --config "$CONFIG" status --release 1.2.3
expect_usage "shell metacharacters refused" --config "$CONFIG" recover --release 'stable:1;rm' --preview
expect_usage "invalid request id refused" --config "$CONFIG" recover --release stable:1.2.3 --request-id 'a b' --preview
expect_usage "missing option value refused" --config "$CONFIG" recover --release

ACT_STAGING="$TEST_ROOT/activation-staging"
ACT_ROOT="$TEST_ROOT/activation-trusted_root.json"
ACT_COSIGN="$TEST_ROOT/activation-cosign"
: > "$ACT_ROOT"
cp "$FAKE_DOTNET" "$ACT_COSIGN"
chmod +x "$ACT_COSIGN"
expect_passthrough "activate passes through to offline-activate" \
    "$(printf '%s\n' "$dll" --config "$CONFIG" offline-activate --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT" --json)" \
    activate --config "$CONFIG" --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT" --json
expect_passthrough "activate forwards cosign path" \
    "$(printf '%s\n' "$dll" --config "$CONFIG" offline-activate --staging "$ACT_STAGING" --channel insider --trusted-root "$ACT_ROOT" --cosign "$ACT_COSIGN")" \
    activate --config "$CONFIG" --staging "$ACT_STAGING" --channel insider --trusted-root "$ACT_ROOT" --cosign "$ACT_COSIGN"
expect_usage "activate requires config" activate --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT"
expect_usage "activate refuses relative staging" activate --config "$CONFIG" --staging staging --channel stable --trusted-root "$ACT_ROOT"
expect_usage "activate refuses invalid channel" activate --config "$CONFIG" --staging "$ACT_STAGING" --channel Stable --trusted-root "$ACT_ROOT"
code=0
FAKE_EXIT=6 run_wrapper activate --config "$CONFIG" --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT" || code=$?
[[ "$code" -eq 6 ]] && pass "activate preserves CLI refused exit code" || fail "activate preserves CLI refused exit code (exit $code)"

REC_BACKUP="$TEST_ROOT/protected-backup.json"
REC_DRIFT="drift-0123456789abcdef0123456789abcdef"
expect_passthrough "recover-offline passes through to offline-recover in canonical order" \
    "$(printf '%s\n' "$dll" --config "$CONFIG" offline-recover --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT" --protected-backup "$REC_BACKUP" --release stable:1.2.3 --preview --json)" \
    recover-offline --json --preview --release stable:1.2.3 --protected-backup "$REC_BACKUP" --config "$CONFIG" --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT"
expect_passthrough "recover-offline forwards confirm, request id, drift token and cosign" \
    "$(printf '%s\n' "$dll" --config "$CONFIG" offline-recover --staging "$ACT_STAGING" --channel insider --trusted-root "$ACT_ROOT" --cosign "$ACT_COSIGN" --protected-backup "$REC_BACKUP" --release insider:1.2.3 --request-id req-1 --confirm insider:1.2.3 --reapprove-drift "$REC_DRIFT")" \
    recover-offline --config "$CONFIG" --staging "$ACT_STAGING" --channel insider --trusted-root "$ACT_ROOT" --cosign "$ACT_COSIGN" --protected-backup "$REC_BACKUP" --release insider:1.2.3 --request-id req-1 --confirm insider:1.2.3 --reapprove-drift "$REC_DRIFT"
expect_usage "recover-offline requires protected backup" recover-offline --config "$CONFIG" --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT" --release stable:1.2.3 --preview
expect_usage "recover-offline refuses relative protected backup" recover-offline --config "$CONFIG" --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT" --protected-backup backup.json --release stable:1.2.3 --preview
expect_usage "recover-offline refuses invalid release" recover-offline --config "$CONFIG" --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT" --protected-backup "$REC_BACKUP" --release 'stable:1;rm' --preview
expect_usage "recover-offline refuses invalid drift token" recover-offline --config "$CONFIG" --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT" --protected-backup "$REC_BACKUP" --release stable:1.2.3 --confirm stable:1.2.3 --reapprove-drift drift-x
expect_usage "recover-offline refuses repeated preview" recover-offline --config "$CONFIG" --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT" --protected-backup "$REC_BACKUP" --release stable:1.2.3 --preview --preview
expect_usage "recover-offline refuses unsupported argument" recover-offline --config "$CONFIG" --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT" --protected-backup "$REC_BACKUP" --release stable:1.2.3 --preview --bundle "$ACT_ROOT"
code=0
FAKE_EXIT=6 run_wrapper recover-offline --config "$CONFIG" --staging "$ACT_STAGING" --channel stable --trusted-root "$ACT_ROOT" --protected-backup "$REC_BACKUP" --release stable:1.2.3 --preview || code=$?
[[ "$code" -eq 6 ]] && pass "recover-offline preserves CLI refused exit code" || fail "recover-offline preserves CLI refused exit code (exit $code)"

code=0
PRINTFARMER_DOTNET="$FAKE_DOTNET" bash "$WRAPPER" --config "$CONFIG" status > /dev/null 2>&1 || code=$?
[[ "$code" -eq 2 ]] && pass "missing CLI dir refused" || fail "missing CLI dir refused (exit $code)"

code=0
PRINTFARMER_HOST_UPDATE_CLI_DIR="$CLI_DIR" PRINTFARMER_DOTNET="fake-dotnet" \
    bash "$WRAPPER" --config "$CONFIG" status > /dev/null 2>&1 || code=$?
[[ "$code" -eq 2 ]] && pass "relative PRINTFARMER_DOTNET refused" || fail "relative PRINTFARMER_DOTNET refused (exit $code)"

code=0
bash "$WRAPPER" --help > /dev/null 2>&1 || code=$?
[[ "$code" -eq 0 ]] && pass "--help works without --config or CLI dir" || fail "--help works without --config or CLI dir (exit $code)"

code=0
FAKE_EXIT=11 run_wrapper --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 || code=$?
[[ "$code" -eq 11 ]] && pass "CLI exit code preserved" || fail "CLI exit code preserved (exit $code)"

# Issue #3041: a self-contained package launcher runs directly, without a dotnet host.
APPHOST_DIR="$TEST_ROOT/apphost-cli"
mkdir -p "$APPHOST_DIR"
cat > "$APPHOST_DIR/Farm.HostUpdate.Cli" <<EOF
#!/bin/bash
printf '%s\n' "\$@" > "$ARGS_LOG"
exit "\${FAKE_EXIT:-0}"
EOF
chmod +x "$APPHOST_DIR/Farm.HostUpdate.Cli"

run_apphost() {
    rm -f "$ARGS_LOG"
    local code=0
    env -u PRINTFARMER_DOTNET PRINTFARMER_HOST_UPDATE_CLI_DIR="$APPHOST_DIR" \
        bash "$WRAPPER" "$@" > /dev/null 2>&1 || code=$?
    return "$code"
}

code=0
FAKE_EXIT=10 run_apphost --config "$CONFIG" status --json || code=$?
if [[ "$code" -eq 10 && -f "$ARGS_LOG" && "$(cat "$ARGS_LOG")" == "$(printf '%s\n' --config "$CONFIG" status --json)" ]]; then
    pass "self-contained launcher runs directly and preserves its exit code"
else
    fail "self-contained launcher runs directly (exit $code, args: $(tr '\n' ' ' < "$ARGS_LOG" 2>/dev/null || true))"
fi

rm -f "$ARGS_LOG"
code=0
PRINTFARMER_HOST_UPDATE_CLI_DIR="$APPHOST_DIR" PRINTFARMER_DOTNET="$FAKE_DOTNET" \
    bash "$WRAPPER" --config "$CONFIG" status > /dev/null 2>&1 || code=$?
[[ "$code" -eq 2 && ! -f "$ARGS_LOG" ]] && pass "PRINTFARMER_DOTNET refused for a self-contained launcher" \
    || fail "PRINTFARMER_DOTNET refused for a self-contained launcher (exit $code)"

chmod -x "$APPHOST_DIR/Farm.HostUpdate.Cli"
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
        # Git Bash reports any #! file as executable, so the mode check cannot be exercised here.
        pass "non-executable launcher refused (skipped: no POSIX modes on Windows)"
        ;;
    *)
        code=0
        run_apphost --config "$CONFIG" status || code=$?
        [[ "$code" -eq 2 && ! -f "$ARGS_LOG" ]] && pass "non-executable launcher refused" \
            || fail "non-executable launcher refused (exit $code)"
        ;;
esac

# An installed package resolves its CLI from cli/ beside the wrapper when no directory is given.
PACKAGE_DIR="$TEST_ROOT/package"
mkdir -p "$PACKAGE_DIR/cli"
cp "$WRAPPER" "$REPO_ROOT/scripts/common-utils.sh" "$PACKAGE_DIR/"
printf '{}\n' > "$PACKAGE_DIR/host-update-cli-package.json"
: > "$PACKAGE_DIR/cli/Farm.HostUpdate.Cli.dll"
rm -f "$ARGS_LOG"
code=0
env -u PRINTFARMER_HOST_UPDATE_CLI_DIR PRINTFARMER_DOTNET="$FAKE_DOTNET" \
    bash "$PACKAGE_DIR/printfarmer-host-update.sh" --config "$CONFIG" status > /dev/null 2>&1 || code=$?
if [[ "$code" -eq 0 && "$(cat "$ARGS_LOG" 2>/dev/null)" == "$(printf '%s\n' "$PACKAGE_DIR/cli/Farm.HostUpdate.Cli.dll" --config "$CONFIG" status)" ]]; then
    pass "installed package resolves cli/ beside the wrapper"
else
    fail "installed package resolves cli/ beside the wrapper (exit $code)"
fi

rm -f "$PACKAGE_DIR/host-update-cli-package.json" "$ARGS_LOG"
code=0
env -u PRINTFARMER_HOST_UPDATE_CLI_DIR PRINTFARMER_DOTNET="$FAKE_DOTNET" \
    bash "$PACKAGE_DIR/printfarmer-host-update.sh" --config "$CONFIG" status > /dev/null 2>&1 || code=$?
[[ "$code" -eq 2 && ! -f "$ARGS_LOG" ]] && pass "no package marker means no default CLI dir" \
    || fail "no package marker means no default CLI dir (exit $code)"

# Issue #3063/#3064: host-local offline bundle import. It runs the offline bundle tool on node with a
# fixed, pre-validated argument vector that names the resolved host-update CLI (for replay admission)
# and its --config. The PowerShell test asserts the identical vector, which is the Bash/PowerShell
# parity contract.
FAKE_NODE="$TEST_ROOT/fake-node"
FAKE_TOOL="$TEST_ROOT/offline-update-bundle.mjs"
FAKE_COSIGN="$TEST_ROOT/fake-cosign"
: > "$FAKE_TOOL"
cp "$FAKE_DOTNET" "$FAKE_NODE"
cp "$FAKE_DOTNET" "$FAKE_COSIGN"
chmod +x "$FAKE_NODE" "$FAKE_COSIGN"
BUNDLE="$TEST_ROOT/printfarmer-offline-update.tar"
ROOT_JSON="$TEST_ROOT/trusted_root.json"
APPROVAL="$TEST_ROOT/trusted-root-approval.json"
STAGING="$TEST_ROOT/staging"
RECORDS="$TEST_ROOT/records"
import_base=(import --config "$CONFIG" --bundle "$BUNDLE" --channel stable --version 1.2.3 --trusted-root "$ROOT_JSON"
    --trusted-root-approval "$APPROVAL" --staging "$STAGING" --records "$RECORDS" --operator ops.alice@site-1)
import_fixed=(--trusted-root-approval "$APPROVAL" --staging "$STAGING" --records "$RECORDS")

run_import() {
    rm -f "$ARGS_LOG"
    local code=0
    env -u PRINTFARMER_DOTNET -u PRINTFARMER_COSIGN -u PRINTFARMER_DOCKER PRINTFARMER_HOST_UPDATE_CLI_DIR="$CLI_DIR" \
        PRINTFARMER_NODE="$FAKE_NODE" PRINTFARMER_OFFLINE_BUNDLE_TOOL="$FAKE_TOOL" "$@" \
        > "$TEST_ROOT/stdout.log" 2> "$TEST_ROOT/stderr.log" || code=$?
    return "$code"
}

expect_import() {
    local name="$1" expected="$2"
    shift 2
    expect_import_command "$name" "$expected" bash "$WRAPPER" "$@"
}

expect_import_command() {
    local name="$1" expected="$2"
    shift 2
    local code=0
    run_import "$@" || code=$?
    if [[ "$code" -eq 0 && -f "$ARGS_LOG" && "$(cat "$ARGS_LOG")" == "$expected" ]]; then
        pass "$name"
    else
        fail "$name (exit $code, args: $(tr '\n' ' ' < "$ARGS_LOG" 2>/dev/null || true))"
    fi
}

expect_import_usage() {
    local name="$1"
    shift
    local code=0
    run_import "$@" || code=$?
    if [[ "$code" -eq 2 && ! -f "$ARGS_LOG" && ! -s "$TEST_ROOT/stdout.log" ]]; then
        pass "$name"
    else
        fail "$name (exit $code, tool invoked: $([[ -f "$ARGS_LOG" ]] && echo yes || echo no))"
    fi
}

expect_import "import passes a fixed argument vector to the bundle tool" \
    "$(printf '%s\n' "$FAKE_TOOL" import --bundle "$BUNDLE" --channel stable --version 1.2.3 --trusted-root "$ROOT_JSON" \
        "${import_fixed[@]}" --operator ops.alice@site-1 --config "$CONFIG" --host-update-cli "$dll")" \
    "${import_base[@]}"
expect_import "import options are normalised to a fixed order" \
    "$(printf '%s\n' "$FAKE_TOOL" import --bundle "$BUNDLE" --channel insider --version 1.2.3-rc.1 --trusted-root "$ROOT_JSON" \
        "${import_fixed[@]}" --operator ops --config "$CONFIG" --host-update-cli "$dll" \
        --prior-recovery-set "$TEST_ROOT/prior" --protected-backup "$TEST_ROOT/backup.json")" \
    import --protected-backup "$TEST_ROOT/backup.json" --operator ops --records "$RECORDS" --prior-recovery-set "$TEST_ROOT/prior" \
    --staging "$STAGING" --trusted-root-approval "$APPROVAL" --trusted-root "$ROOT_JSON" --version 1.2.3-rc.1 \
    --channel insider --config "$CONFIG" --bundle "$BUNDLE"
expect_import_command "import forwards an absolute PRINTFARMER_DOTNET for a framework-dependent CLI" \
    "$(printf '%s\n' "$FAKE_TOOL" import --bundle "$BUNDLE" --channel stable --version 1.2.3 --trusted-root "$ROOT_JSON" \
        "${import_fixed[@]}" --operator ops.alice@site-1 --config "$CONFIG" --host-update-cli "$dll" --dotnet "$FAKE_DOTNET")" \
    env PRINTFARMER_DOTNET="$FAKE_DOTNET" bash "$WRAPPER" "${import_base[@]}"
chmod +x "$APPHOST_DIR/Farm.HostUpdate.Cli"
expect_import_command "import names a self-contained CLI launcher directly" \
    "$(printf '%s\n' "$FAKE_TOOL" import --bundle "$BUNDLE" --channel stable --version 1.2.3 --trusted-root "$ROOT_JSON" \
        "${import_fixed[@]}" --operator ops.alice@site-1 --config "$CONFIG" --host-update-cli "$APPHOST_DIR/Farm.HostUpdate.Cli")" \
    env PRINTFARMER_HOST_UPDATE_CLI_DIR="$APPHOST_DIR" bash "$WRAPPER" "${import_base[@]}"

rm -f "$ARGS_LOG"
code=0
env -u PRINTFARMER_DOTNET -u PRINTFARMER_DOCKER PRINTFARMER_HOST_UPDATE_CLI_DIR="$CLI_DIR" PRINTFARMER_NODE="$FAKE_NODE" \
    PRINTFARMER_OFFLINE_BUNDLE_TOOL="$FAKE_TOOL" PRINTFARMER_COSIGN="$FAKE_COSIGN" \
    bash "$WRAPPER" "${import_base[@]}" > /dev/null 2>&1 || code=$?
if [[ "$code" -eq 0 && "$(tail -n 2 "$ARGS_LOG" 2>/dev/null)" == "$(printf '%s\n' --cosign "$FAKE_COSIGN")" ]]; then
    pass "import forwards an absolute PRINTFARMER_COSIGN"
else
    fail "import forwards an absolute PRINTFARMER_COSIGN (exit $code)"
fi

code=0
FAKE_EXIT=1 run_import bash "$WRAPPER" "${import_base[@]}" || code=$?
[[ "$code" -eq 1 && -f "$ARGS_LOG" ]] && pass "import preserves the refused exit code" || fail "import preserves the refused exit code (exit $code)"

expect_import_usage "import refuses a duplicate --config" bash "$WRAPPER" "${import_base[@]}" --config "$CONFIG"
expect_import_usage "import refuses a missing required option" bash "$WRAPPER" import --bundle "$BUNDLE" --channel stable \
    --version 1.2.3 --trusted-root "$ROOT_JSON" --staging "$STAGING" --records "$RECORDS"
expect_import_usage "import requires --config" bash "$WRAPPER" import --bundle "$BUNDLE" --channel stable --version 1.2.3 \
    --trusted-root "$ROOT_JSON" --trusted-root-approval "$APPROVAL" --staging "$STAGING" --records "$RECORDS" --operator ops
expect_import_usage "import requires --trusted-root-approval" bash "$WRAPPER" import --config "$CONFIG" --bundle "$BUNDLE" \
    --channel stable --version 1.2.3 --trusted-root "$ROOT_JSON" --staging "$STAGING" --records "$RECORDS" --operator ops
expect_import_usage "import refuses a relative --config" bash "$WRAPPER" import --config host-update.json --bundle "$BUNDLE" \
    --channel stable --version 1.2.3 --trusted-root "$ROOT_JSON" --trusted-root-approval "$APPROVAL" --staging "$STAGING" \
    --records "$RECORDS" --operator ops
expect_import_usage "import refuses a missing CLI directory" env -u PRINTFARMER_HOST_UPDATE_CLI_DIR bash "$WRAPPER" "${import_base[@]}"
expect_import_usage "import refuses a duplicate option" bash "$WRAPPER" "${import_base[@]}" --channel stable
expect_import_usage "import refuses a relative bundle path" bash "$WRAPPER" import --bundle bundle.tar --channel stable \
    --version 1.2.3 --trusted-root "$ROOT_JSON" --staging "$STAGING" --records "$RECORDS" --operator ops
expect_import_usage "import refuses a relative records path" bash "$WRAPPER" import --bundle "$BUNDLE" --channel stable \
    --version 1.2.3 --trusted-root "$ROOT_JSON" --staging "$STAGING" --records records --operator ops
expect_import_usage "import refuses an unknown channel" bash "$WRAPPER" import --bundle "$BUNDLE" --channel Stable \
    --version 1.2.3 --trusted-root "$ROOT_JSON" --staging "$STAGING" --records "$RECORDS" --operator ops
expect_import_usage "import refuses shell metacharacters in the version" bash "$WRAPPER" import --bundle "$BUNDLE" --channel stable \
    --version '1;rm' --trusted-root "$ROOT_JSON" --staging "$STAGING" --records "$RECORDS" --operator ops
expect_import_usage "import refuses a malformed operator" bash "$WRAPPER" import --bundle "$BUNDLE" --channel stable \
    --version 1.2.3 --trusted-root "$ROOT_JSON" --staging "$STAGING" --records "$RECORDS" --operator '-ops'
expect_import_usage "import refuses a verification bypass option" bash "$WRAPPER" "${import_base[@]}" --skip-verification true
expect_import_usage "import refuses a missing option value" bash "$WRAPPER" "${import_base[@]}" --prior-recovery-set
expect_import_usage "import refuses a relative PRINTFARMER_NODE" env PRINTFARMER_NODE=node bash "$WRAPPER" "${import_base[@]}"
expect_import_usage "import refuses a relative PRINTFARMER_COSIGN" env PRINTFARMER_COSIGN=cosign bash "$WRAPPER" "${import_base[@]}"
expect_import_usage "import refuses a missing bundle tool" env PRINTFARMER_OFFLINE_BUNDLE_TOOL="$TEST_ROOT/missing.mjs" \
    bash "$WRAPPER" "${import_base[@]}"

if [[ "$failures" -gt 0 ]]; then
    printf '%d wrapper test(s) failed\n' "$failures" >&2
    exit 1
fi

printf 'All host-update wrapper tests passed\n'
