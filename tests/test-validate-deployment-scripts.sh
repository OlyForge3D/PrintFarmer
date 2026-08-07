#!/bin/bash

# Focused regression coverage for validate-deployment-scripts.sh result handling.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VALIDATOR="$SCRIPT_DIR/validate-deployment-scripts.sh"

RED=$'\033[0;31m'
GREEN=$'\033[0;32m'
NC=$'\033[0m'

pass() { printf '%s[PASS]%s %s\n' "$GREEN" "$NC" "$*"; }

fail() {
    printf '%s[FAIL]%s %s\n' "$RED" "$NC" "$*"
    exit 1
}

if [[ ! -f "$VALIDATOR" ]]; then
    fail "Validator not found: $VALIDATOR"
fi

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

mock_repo="$TMP_ROOT/repo"
mkdir -p \
    "$mock_repo/tests" \
    "$mock_repo/scripts/docker/compose-templates" \
    "$mock_repo/bin"
cp "$VALIDATOR" "$mock_repo/tests/validate-deployment-scripts.sh"
chmod +x "$mock_repo/tests/validate-deployment-scripts.sh"

cat > "$mock_repo/scripts/deploy-docker.sh" <<'EOF'
#!/bin/bash
set -euo pipefail

# Supported backend: PrusaLink
if [[ "${1:-}" == "--help" ]]; then
    cat <<'HELP'
PrintFarmer Docker Deployment Script
USAGE:
    ./scripts/deploy-docker.sh [OPTIONS]
PrintFarmer uses a containerized microservices architecture.
HELP
    exit 0
fi

if grep -q '^DB_PROVIDER=sqlserver$' .deploy-config; then
    printf '%s\n' \
        'DB_PROVIDER=sqlserver' \
        'ConnectionStrings__Default=Server=sqlserver;Database=printfarmer;User Id=sa;******;TrustServerCertificate=True;' > .env
else
    printf 'DB_PROVIDER=postgres\n' > .env
fi
EOF
chmod +x "$mock_repo/scripts/deploy-docker.sh"

cat > "$mock_repo/scripts/docker/compose-generator.sh" <<'EOF'
#!/bin/bash
set -euo pipefail

output_dir=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --output-dir)
            output_dir="$2"
            shift 2
            ;;
        *)
            shift
            ;;
    esac
done

mkdir -p "$output_dir"
cat > "$output_dir/docker-compose.yml" <<'COMPOSE'
services:
  api:
    dockerfile: Dockerfile.multistage
  printfarmer-prometheus:
    image: prom/prometheus
  printfarmer-jaeger:
    image: jaegertracing/all-in-one
networks:
  default:
COMPOSE
: > "$output_dir/Dockerfile.multistage"
EOF
chmod +x "$mock_repo/scripts/docker/compose-generator.sh"

cat > "$mock_repo/bin/docker" <<'EOF'
#!/bin/bash
exit 0
EOF
chmod +x "$mock_repo/bin/docker"

: > "$mock_repo/scripts/docker/compose-templates/base.yml"

run_validator() {
    local output_file="$1"
    set +e
    PATH="$mock_repo/bin:$PATH" bash "$mock_repo/tests/validate-deployment-scripts.sh" >"$output_file" 2>&1
    local status=$?
    set -e
    return "$status"
}

strip_ansi() {
    sed -r $'s/\x1b\\[[0-9;]*[A-Za-z]//g' "$1"
}

clean_output="$TMP_ROOT/clean.out"
if ! run_validator "$clean_output"; then
    cat "$clean_output"
    fail "Clean validator control should exit 0."
fi
clean_plain=$(strip_ansi "$clean_output")
if echo "$clean_plain" | grep -q "FAIL"; then
    cat "$clean_output"
    fail "Clean validator control emitted a FAIL line."
fi
if ! echo "$clean_plain" | grep -q "Validation completed!"; then
    cat "$clean_output"
    fail "Clean validator control did not emit its success banner."
fi
pass "Clean control exits 0 with zero FAIL lines"

# Negative control: introduce exactly one prohibited PrusaSlicer reference.
printf '\n# PrusaSlicer negative control\n' >> "$mock_repo/scripts/deploy-docker.sh"
single_failure_output="$TMP_ROOT/single-failure.out"
if run_validator "$single_failure_output"; then
    cat "$single_failure_output"
    fail "A deliberately broken assertion must make the validator exit non-zero."
fi
single_failure_plain=$(strip_ansi "$single_failure_output")
single_failure_count=$(echo "$single_failure_plain" | grep -c "FAIL" || true)
if [[ "$single_failure_count" -ne 1 ]]; then
    cat "$single_failure_output"
    fail "Single negative control should produce exactly one FAIL line, got $single_failure_count."
fi
if echo "$single_failure_plain" | grep -qE "Validation completed!|Key improvements verified:"; then
    cat "$single_failure_output"
    fail "Failure output must suppress the success banner and verified summary."
fi
pass "Single broken assertion exits non-zero and suppresses success output"

# Restore the first control, then break two independent assertions to prove
# failures accumulate instead of stopping after or forgetting the first.
sed -i '/PrusaSlicer negative control/d' "$mock_repo/scripts/deploy-docker.sh"
printf 'redis:\n' > "$mock_repo/scripts/docker/compose-templates/base.yml"
printf '\n# PrusaSlicer accumulation control\n' >> "$mock_repo/scripts/deploy-docker.sh"

multiple_failure_output="$TMP_ROOT/multiple-failure.out"
if run_validator "$multiple_failure_output"; then
    cat "$multiple_failure_output"
    fail "Multiple broken assertions must make the validator exit non-zero."
fi
multiple_failure_plain=$(strip_ansi "$multiple_failure_output")
multiple_failure_count=$(echo "$multiple_failure_plain" | grep -c "FAIL" || true)
if [[ "$multiple_failure_count" -ne 2 ]]; then
    cat "$multiple_failure_output"
    fail "Two negative controls should produce exactly two FAIL lines, got $multiple_failure_count."
fi
if ! echo "$multiple_failure_plain" | grep -q "failed with 2 failed assertion(s)"; then
    cat "$multiple_failure_output"
    fail "Failure summary did not preserve the accumulated assertion count."
fi
pass "Multiple broken assertions accumulate into the final non-zero result"

printf '%sAll validate-deployment-scripts regressions passed%s\n' "$GREEN" "$NC"
