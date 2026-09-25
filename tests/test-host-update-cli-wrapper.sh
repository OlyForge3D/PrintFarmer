#!/bin/bash

# Focused coverage for scripts/printfarmer-host-update.sh (issues #2980, #2997): the wrapper must
# expose only the fixed status/recover operations, refuse unsafe input before the CLI runs (exit 2),
# pass accepted arguments through in canonical order while preserving the CLI's exit code, and
# match the grammar of scripts/printfarmer-host-update.ps1 (see test-host-update-cli-wrapper.ps1).
# It also proves the release-package layout: a self-contained apphost in cli/ beside the wrapper.

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

expect_passthrough "options may precede --config and the command" \
    "$(printf '%s\n' "$dll" --config "$CONFIG" status --json)" \
    --json status --config "$CONFIG"
expect_passthrough "arguments are passed in canonical order" \
    "$(printf '%s\n' "$dll" --config "$CONFIG" recover --release stable:1.2.3 --request-id req-1 --preview --json)" \
    --config "$CONFIG" recover --json --preview --request-id req-1 --release stable:1.2.3

expect_usage "no arguments refused"
expect_usage "missing --config refused" status
expect_usage "missing command refused" --config "$CONFIG"
expect_usage "duplicate --config refused" --config "$CONFIG" --config "$CONFIG" status
expect_usage "duplicate command refused" --config "$CONFIG" status status
expect_usage "second command refused" --config "$CONFIG" status recover
expect_usage "miscased command refused" --config "$CONFIG" Status
expect_usage "duplicate flag refused" --config "$CONFIG" status --json --json
expect_usage "duplicate --release refused" --config "$CONFIG" status --release stable:1.2.3 --release stable:1.2.3
expect_usage "PowerShell-style option refused" --config "$CONFIG" status -Json
expect_usage "miscased release refused" --config "$CONFIG" status --release Stable:1.2.3
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

# A self-contained CLI directory is launched through its apphost; no dotnet host is involved.
APPHOST_DIR="$TEST_ROOT/apphost-cli"
mkdir -p "$APPHOST_DIR"
: > "$APPHOST_DIR/Farm.HostUpdate.Cli.dll"
cat > "$APPHOST_DIR/Farm.HostUpdate.Cli" <<EOF
#!/bin/bash
printf '%s\n' "\$@" > "$ARGS_LOG"
exit "\${FAKE_EXIT:-0}"
EOF
chmod +x "$APPHOST_DIR/Farm.HostUpdate.Cli"

rm -f "$ARGS_LOG"
code=0
PRINTFARMER_HOST_UPDATE_CLI_DIR="$APPHOST_DIR" bash "$WRAPPER" --config "$CONFIG" status --json > /dev/null 2>&1 || code=$?
if [[ "$code" -eq 0 && -f "$ARGS_LOG" && "$(cat "$ARGS_LOG")" == "$(printf '%s\n' --config "$CONFIG" status --json)" ]]; then
    pass "self-contained apphost preferred over dotnet"
else
    fail "self-contained apphost preferred over dotnet (exit $code)"
fi

rm -f "$ARGS_LOG"
code=0
PRINTFARMER_HOST_UPDATE_CLI_DIR="$APPHOST_DIR" PRINTFARMER_DOTNET="$FAKE_DOTNET" \
    bash "$WRAPPER" --config "$CONFIG" status > /dev/null 2>&1 || code=$?
if [[ "$code" -eq 2 && ! -f "$ARGS_LOG" ]]; then
    pass "PRINTFARMER_DOTNET refused for a self-contained CLI"
else
    fail "PRINTFARMER_DOTNET refused for a self-contained CLI (exit $code)"
fi

# Release-package layout: wrapper, common-utils.sh and cli/ side by side, no environment needed.
PACKAGE_DIR="$TEST_ROOT/package"
mkdir -p "$PACKAGE_DIR"
cp "$WRAPPER" "$REPO_ROOT/scripts/common-utils.sh" "$PACKAGE_DIR/"
cp -R "$APPHOST_DIR" "$PACKAGE_DIR/cli"
rm -f "$ARGS_LOG"
code=0
env -u PRINTFARMER_HOST_UPDATE_CLI_DIR -u PRINTFARMER_DOTNET FAKE_EXIT=5 \
    bash "$PACKAGE_DIR/printfarmer-host-update.sh" --config "$CONFIG" recover --release stable:1.2.3 --preview \
    > /dev/null 2>&1 || code=$?
if [[ "$code" -eq 5 && "$(cat "$ARGS_LOG" 2>/dev/null)" == "$(printf '%s\n' --config "$CONFIG" recover --release stable:1.2.3 --preview)" ]]; then
    pass "package layout runs the bundled CLI without environment"
else
    fail "package layout runs the bundled CLI without environment (exit $code)"
fi

rm -f "$ARGS_LOG"
code=0
PRINTFARMER_HOST_UPDATE_CLI_DIR="$CLI_DIR" PRINTFARMER_DOTNET="$FAKE_DOTNET" \
    bash "$PACKAGE_DIR/printfarmer-host-update.sh" --config "$CONFIG" status > /dev/null 2>&1 || code=$?
if [[ "$code" -eq 0 && "$(head -n 1 "$ARGS_LOG" 2>/dev/null)" == "$dll" ]]; then
    pass "explicit CLI dir overrides the package default"
else
    fail "explicit CLI dir overrides the package default (exit $code)"
fi

code=0
FAKE_EXIT=11 run_wrapper --config "$CONFIG" recover --release stable:1.2.3 --confirm stable:1.2.3 || code=$?
[[ "$code" -eq 11 ]] && pass "CLI exit code preserved" || fail "CLI exit code preserved (exit $code)"

if [[ "$failures" -gt 0 ]]; then
    printf '%d wrapper test(s) failed\n' "$failures" >&2
    exit 1
fi

printf 'All host-update wrapper tests passed\n'
