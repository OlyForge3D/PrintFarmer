#!/bin/bash

# Focused regression coverage for ensure_tls_certificates temporary cleanup.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEPLOY_SCRIPT="$REPO_ROOT/scripts/deploy-docker.sh"
TEST_ROOT="$(mktemp -d -t "printfarmer-tls-cleanup-XXXXXX")"
trap 'rm -rf -- "$TEST_ROOT"' EXIT

pass() {
    printf '[PASS] %s\n' "$1"
}

fail() {
    printf '[FAIL] %s\n' "$1" >&2
    exit 1
}

extract_ensure_tls_certificates() {
    sed -n '/^ensure_tls_certificates[[:space:]]*()/,/^}/p' "$DEPLOY_SCRIPT"
}

run_cleanup_case() {
    local case_name="$1"
    local openssl_failure="$2"
    local case_root="$TEST_ROOT/$case_name"
    local harness="$case_root/harness.sh"
    local output="$case_root/output.log"
    local cleanup_log="$case_root/cleanup.log"
    local tls_temp="$case_root/tls-temp"

    mkdir -p "$case_root"
    {
        cat <<EOF
#!/bin/bash
set -euo pipefail

CASE_ROOT='$case_root'
CLEANUP_LOG='$cleanup_log'
TLS_TEMP='$tls_temp'
OPENSSL_FAILURE='$openssl_failure'
HTTPS_PORT=8443

print_info() { :; }
print_warning() { :; }
print_success() { :; }
tls_certificate_is_valid() { return 1; }

mktemp() {
    mkdir -p "\$TLS_TEMP"
    printf '%s\n' "\$TLS_TEMP"
}

rm() {
    if [[ " \$* " == *" \$TLS_TEMP "* ]]; then
        printf 'cleanup\n' >> "\$CLEANUP_LOG"
    fi
    command rm "\$@"
}

hostname() {
    if [[ "\${1:-}" == "-I" ]]; then
        printf '127.0.0.1\n'
        return 0
    fi
    command hostname "\$@"
}

openssl() {
    local argument
    local output_path=""
    local previous=""

    if [[ "\$OPENSSL_FAILURE" == "yes" && "\${1:-}" == "req" && "\${2:-}" == "-x509" ]]; then
        return 23
    fi

    for argument in "\$@"; do
        if [[ "\$previous" == "-out" ]]; then
            output_path="\$argument"
            break
        fi
        previous="\$argument"
    done

    if [[ -n "\$output_path" ]]; then
        mkdir -p "\$(dirname "\$output_path")"
        : > "\$output_path"
    fi
}
EOF
        extract_ensure_tls_certificates
        cat <<'EOF'

outer_caller() {
    local status
    if ensure_tls_certificates; then
        printf 'outer-returned\n'
    else
        status=$?
        return "$status"
    fi
}

outer_caller
EOF
    } > "$harness"

    chmod +x "$harness"

    local status
    set +e
    (
        cd "$case_root"
        bash "$harness"
    ) > "$output" 2>&1
    status=$?
    set -e

    if grep -q 'unbound variable' "$output"; then
        cat "$output" >&2
        fail "$case_name emitted unbound-variable output"
    fi

    if [[ -d "$tls_temp" ]]; then
        fail "$case_name leaked its temporary directory"
    fi

    local cleanup_count=0
    if [[ -f "$cleanup_log" ]]; then
        cleanup_count="$(wc -l < "$cleanup_log" | tr -d ' ')"
    fi
    if [[ "$cleanup_count" -ne 1 ]]; then
        fail "$case_name cleaned its temporary directory $cleanup_count times"
    fi

    if [[ "$openssl_failure" == "yes" ]]; then
        if [[ "$status" -ne 23 ]]; then
            cat "$output" >&2
            fail "$case_name returned $status instead of the OpenSSL failure status"
        fi
        if grep -q 'outer-returned' "$output"; then
            fail "$case_name continued its enclosing caller after failure"
        fi
    else
        if [[ "$status" -ne 0 ]]; then
            cat "$output" >&2
            fail "$case_name returned $status"
        fi
        if ! grep -q 'outer-returned' "$output"; then
            fail "$case_name did not return through the enclosing function"
        fi
    fi

    pass "$case_name cleanup"
}

find_unsafe_return_traps() {
    grep -HnE \
        -e "trap[[:space:]]+'[^']*\\\$[A-Za-z_][A-Za-z0-9_]*[^']*'[[:space:]]+RETURN" \
        -e 'trap[[:space:]]+[A-Za-z_][A-Za-z0-9_]*[[:space:]]+RETURN' \
        -e "trap[[:space:]]+'[A-Za-z_][A-Za-z0-9_]*'[[:space:]]+RETURN" \
        -e 'trap[[:space:]]+"[A-Za-z_][A-Za-z0-9_]*"[[:space:]]+RETURN' \
        "$@" || true
    grep -HnE 'trap[[:space:]]+".*"[[:space:]]+RETURN' "$@" \
        | grep -F '\$' || true
}

trap_fixture="$TEST_ROOT/return-trap-fixture.sh"
cat > "$trap_fixture" <<'EOF'
safe_registration() {
    local temp_dir="/tmp/safe"
    trap "rm -rf -- '$temp_dir'" RETURN
}
unsafe_single_quotes() {
    local temp_dir="/tmp/unsafe-single"
    trap 'rm -rf -- "$temp_dir"' RETURN # unsafe-single
}
unsafe_escaped_double_quotes() {
    local temp_dir="/tmp/unsafe-double"
    trap "rm -rf -- \"\$temp_dir\"" RETURN # unsafe-double
}
unsafe_indirect_handler() {
    local temp_dir="/tmp/unsafe-indirect"
    cleanup_temp_dir() { rm -rf -- "$temp_dir"; }
    trap cleanup_temp_dir RETURN # unsafe-indirect
}
unsafe_single_quoted_handler() {
    local temp_dir="/tmp/unsafe-single-handler"
    cleanup_single_quoted() { rm -rf -- "$temp_dir"; }
    trap 'cleanup_single_quoted' RETURN # unsafe-single-handler
}
unsafe_double_quoted_handler() {
    local temp_dir="/tmp/unsafe-double-handler"
    cleanup_double_quoted() { rm -rf -- "$temp_dir"; }
    trap "cleanup_double_quoted" RETURN # unsafe-double-handler
}
EOF

fixture_findings="$(find_unsafe_return_traps "$trap_fixture")"
for expected_finding in \
    unsafe-single \
    unsafe-double \
    unsafe-indirect \
    unsafe-single-handler \
    unsafe-double-handler; do
    if ! grep -q "$expected_finding" <<<"$fixture_findings"; then
        fail "RETURN trap sweep missed $expected_finding fixture"
    fi
done
if grep -q 'safe_registration' <<<"$fixture_findings"; then
    fail "RETURN trap sweep rejected safely expanded registration"
fi
pass "RETURN trap sweep distinguishes deferred and registration-time expansion"

unsafe_return_traps="$(
    while IFS= read -r -d '' shell_file; do
        find_unsafe_return_traps "$shell_file"
    done < <(
        find "$REPO_ROOT" -type f -name '*.sh' \
            -not -path '*/.git/*' \
            -not -path '*/node_modules/*' \
            -not -path "$REPO_ROOT/tests/test-tls-certificate-cleanup.sh" \
            -print0
    )
)"
if [[ -n "$unsafe_return_traps" ]]; then
    printf '%s\n' "$unsafe_return_traps" >&2
    fail "deferred RETURN trap references a variable that may leave scope"
fi
pass "RETURN traps do not defer variable expansion past local scope"

run_cleanup_case "nested-success" "no"
run_cleanup_case "nested-failure" "yes"
