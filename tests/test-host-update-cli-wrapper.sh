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

if [[ "$failures" -gt 0 ]]; then
    printf '%d wrapper test(s) failed\n' "$failures" >&2
    exit 1
fi

printf 'All host-update wrapper tests passed\n'
