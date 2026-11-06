#!/bin/bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ENTRYPOINT="$REPO_ROOT/scripts/docker/slicer-host-entrypoint.sh"
FIXTURE_DIR="$(mktemp -d "$SCRIPT_DIR/.slicer-host-entrypoint-test.XXXXXX")"
MOCK_BIN="$FIXTURE_DIR/bin"
TEST_LOG="$FIXTURE_DIR/entrypoint.log"
FIND_LOG="$FIXTURE_DIR/find.log"
REAL_FIND="$(command -v find)"
REAL_MKDIR="$(command -v mkdir)"
REAL_STAT="$(command -v stat)"
REAL_UID="$(id -u)"
REAL_GID="$(id -g)"

fail() {
    printf 'FAIL: %s\n' "$1" >&2
    exit 1
}

pass() {
    printf 'PASS: %s\n' "$1"
}

cleanup() {
    rm -rf -- "$FIXTURE_DIR"
}
trap cleanup EXIT

mkdir -p "$MOCK_BIN"

cat > "$MOCK_BIN/id" <<'EOF'
#!/bin/bash
case "$*" in
    "-u") printf '%s\n' "${SLICER_TEST_EFFECTIVE_UID:-0}" ;;
    "-u appuser") printf '%s\n' "${SLICER_TEST_APP_UID:-2345}" ;;
    "-g appuser") printf '%s\n' "${SLICER_TEST_APP_GID:-3456}" ;;
    *) exit 2 ;;
esac
EOF

cat > "$MOCK_BIN/chown" <<'EOF'
#!/bin/bash
printf 'CALL\n' >> "$SLICER_TEST_LOG"
printf '<%s>\n' "$@" >> "$SLICER_TEST_LOG"
[[ "${SLICER_TEST_CHOWN_FAIL:-0}" != "1" ]]
EOF

cat > "$MOCK_BIN/gosu" <<'EOF'
#!/bin/bash
printf '%s\n' "$*" >> "$SLICER_TEST_GOSU_LOG"
if [[ "${2:-}" == "test" && "${SLICER_TEST_ACCESS_FAIL:-0}" == "1" ]]; then
    exit 1
fi
EOF

cat > "$MOCK_BIN/find" <<'EOF'
#!/bin/bash
printf 'find-target=<%s>; args=<%s>\n' "${2:-}" "$*" >> "$SLICER_TEST_FIND_LOG"
if [[ -n "${SLICER_TEST_FAKE_SYMLINK:-}" && " $* " == *" -type l "* ]]; then
    printf '%s\n' "$SLICER_TEST_FAKE_SYMLINK"
    exit 0
fi
if [[ "${SLICER_TEST_FOREIGN_DEVICE:-0}" == "1" && " $* " == *" -printf "* ]]; then
    printf '100\n101\n'
    exit 0
fi
if [[ "${2:-}" == /data/* ]]; then
    set -- "$1" "$SLICER_TEST_DATA_ROOT${2#/data}" "${@:3}"
fi
exec "$SLICER_TEST_REAL_FIND" "$@"
EOF

cat > "$MOCK_BIN/mkdir" <<'EOF'
#!/bin/bash
for arg in "$@"; do
    if [[ "$arg" == /data/* ]]; then
        exec "$SLICER_TEST_REAL_MKDIR" -p "$SLICER_TEST_DATA_ROOT${arg#/data}"
    fi
done
exec "$SLICER_TEST_REAL_MKDIR" "$@"
EOF

cat > "$MOCK_BIN/stat" <<'EOF'
#!/bin/bash
path="${@: -1}"
if [[ "${2:-}" == "%u %a" ]]; then
    if [[ "$path" == "/" ]]; then
        printf '0 755\n'
    elif [[ "$path" == /data || "$path" == /data/* ]]; then
        mapped="$SLICER_TEST_DATA_ROOT${path#/data}"
        [[ -d "$mapped" ]] || exit 1
        printf '%s %s\n' "${SLICER_TEST_ANCESTOR_UID:-0}" "${SLICER_TEST_ANCESTOR_MODE:-755}"
    else
        exec "$SLICER_TEST_REAL_STAT" "$@"
    fi
elif [[ "${2:-}" == "%d" && "$path" == /data/* ]]; then
    if [[ -n "${SLICER_TEST_DEVICE_ID:-}" ]]; then
        printf '%s\n' "$SLICER_TEST_DEVICE_ID"
    else
        exec "$SLICER_TEST_REAL_STAT" -c '%d' -- "$SLICER_TEST_DATA_ROOT${path#/data}"
    fi
else
    exec "$SLICER_TEST_REAL_STAT" "$@"
fi
EOF

chmod +x "$MOCK_BIN/id" "$MOCK_BIN/chown" "$MOCK_BIN/gosu" "$MOCK_BIN/find" "$MOCK_BIN/mkdir" "$MOCK_BIN/stat"
export PATH="$MOCK_BIN:$PATH"
export SLICER_TEST_LOG="$TEST_LOG"
export SLICER_TEST_FIND_LOG="$FIND_LOG"
export SLICER_TEST_GOSU_LOG="$FIXTURE_DIR/gosu.log"
export SLICER_TEST_REAL_FIND="$REAL_FIND"
export SLICER_TEST_REAL_MKDIR="$REAL_MKDIR"
export SLICER_TEST_REAL_STAT="$REAL_STAT"
export SLICER_TEST_DATA_ROOT="$FIXTURE_DIR/data"
export SLICER_TEST_APP_UID=2345
export SLICER_TEST_APP_GID=3456

assert_contains() {
    grep -Fq -- "$2" "$1" || fail "expected '$2' in $1"
}

assert_not_contains() {
    ! grep -Fq -- "$2" "$1" || fail "did not expect '$2' in $1"
}

assert_chown_calls() {
    local expected="$1"
    local actual
    actual="$(grep -c '^CALL$' "$TEST_LOG" || true)"
    [[ "$actual" == "$expected" ]] || fail "expected $expected chown calls, got $actual"
}

custom_root="$FIXTURE_DIR/data/alternate-volume/slicer/artifact-store"
mkdir -p "$custom_root/nested"
printf 'retained artifact\n' > "$custom_root/nested/existing.bin"
chmod 0750 "$custom_root" "$custom_root/nested"
chmod 0640 "$custom_root/nested/existing.bin"
root_mode_before="$(stat -c '%a' "$custom_root")"
nested_mode_before="$(stat -c '%a' "$custom_root/nested")"
file_mode_before="$(stat -c '%a' "$custom_root/nested/existing.bin")"
export ArtifactStorage__RootPath="/data/alternate-volume/slicer/artifact-store"

"$ENTRYPOINT" dotnet Farm.Slicer.Host.dll
"$ENTRYPOINT" dotnet Farm.Slicer.Host.dll

[[ "$(cat "$custom_root/nested/existing.bin")" == "retained artifact" ]] ||
    fail "startup changed existing artifact contents"
[[ "$(stat -c '%a' "$custom_root")" == "$root_mode_before" ]] || fail "startup changed artifact root mode"
[[ "$(stat -c '%a' "$custom_root/nested")" == "$nested_mode_before" ]] ||
    fail "startup changed nested directory mode"
[[ "$(stat -c '%a' "$custom_root/nested/existing.bin")" == "$file_mode_before" ]] ||
    fail "startup changed existing artifact file mode"
assert_chown_calls 6
assert_contains "$TEST_LOG" '<2345:3456>'
assert_contains "$TEST_LOG" "existing.bin"
assert_not_contains "$TEST_LOG" "$FIXTURE_DIR/alternate-volume"
assert_contains "$FIND_LOG" 'find-target=</data/alternate-volume/slicer/artifact-store>'
assert_not_contains "$FIND_LOG" 'find-target=</data>'
assert_contains "$FIND_LOG" '-xdev'
assert_contains "$FIND_LOG" '-execdir'
assert_contains "$FIND_LOG" 'chown -h'
[[ "$(grep -c 'appuser dotnet' "$SLICER_TEST_GOSU_LOG")" == "2" ]] || fail "repeated starts did not drop to appuser each time"
assert_contains "$SLICER_TEST_GOSU_LOG" "appuser dotnet Farm.Slicer.Host.dll"
assert_contains "$SLICER_TEST_GOSU_LOG" "appuser test -w /data/alternate-volume/slicer/artifact-store"
assert_contains "$SLICER_TEST_GOSU_LOG" "appuser test -x /data/alternate-volume/slicer/artifact-store"
pass "existing custom-path artifacts survive repeated startup and use the actual non-1001 appuser UID/GID"

: > "$TEST_LOG"
: > "$SLICER_TEST_GOSU_LOG"
export ArtifactStorage__RootPath="/data/unprefixed-artifact-root"
export PFARM__ArtifactStorage__RootPath="/data/pfarm-prefixed-artifact-root"
"$ENTRYPOINT" dotnet Farm.Slicer.Host.dll
assert_contains "$TEST_LOG" "pfarm-prefixed-artifact-root"
assert_not_contains "$TEST_LOG" "unprefixed-artifact-root"
unset PFARM__ArtifactStorage__RootPath
pass "PFARM-prefixed artifact root follows application configuration precedence"

: > "$TEST_LOG"
: > "$SLICER_TEST_GOSU_LOG"
export ArtifactStorage__RootPath="/data/unsafe-parent/artifacts"
export SLICER_TEST_ANCESTOR_UID=2345
if "$ENTRYPOINT" dotnet Farm.Slicer.Host.dll >/dev/null 2>&1; then
    fail "unprivileged-owned ancestor unexpectedly passed"
fi
unset SLICER_TEST_ANCESTOR_UID
export SLICER_TEST_ANCESTOR_MODE=0775
if "$ENTRYPOINT" dotnet Farm.Slicer.Host.dll >/dev/null 2>&1; then
    fail "group-writable ancestor unexpectedly passed"
fi
unset SLICER_TEST_ANCESTOR_MODE
[[ ! -s "$TEST_LOG" && ! -s "$SLICER_TEST_GOSU_LOG" ]] ||
    fail "unsafe ancestor performed ownership repair or launched the application"
pass "unprivileged-owned or group-writable ancestors block privileged repair"

: > "$TEST_LOG"
: > "$SLICER_TEST_GOSU_LOG"
export ArtifactStorage__RootPath="/data/non-rooted"
export SLICER_TEST_EFFECTIVE_UID=1001
if "$ENTRYPOINT" dotnet Farm.Slicer.Host.dll >/dev/null 2>&1; then
    fail "non-root startup unexpectedly passed"
fi
[[ ! -s "$TEST_LOG" && ! -s "$SLICER_TEST_GOSU_LOG" ]] ||
    fail "non-root startup performed ownership repair or launched the application"
unset SLICER_TEST_EFFECTIVE_UID
pass "non-root startup fails closed before touching storage"

: > "$TEST_LOG"
: > "$SLICER_TEST_GOSU_LOG"
export ArtifactStorage__RootPath="/data/root-uid-appuser"
export SLICER_TEST_APP_UID=0
if "$ENTRYPOINT" dotnet Farm.Slicer.Host.dll >/dev/null 2>&1; then
    fail "UID 0 appuser unexpectedly passed"
fi
[[ ! -s "$TEST_LOG" && ! -s "$SLICER_TEST_GOSU_LOG" ]] ||
    fail "UID 0 appuser triggered ownership changes or application startup"
export SLICER_TEST_APP_UID=2345
pass "UID 0 appuser is rejected"

: > "$TEST_LOG"
: > "$SLICER_TEST_GOSU_LOG"
export ArtifactStorage__RootPath="/data/chown-failure"
export SLICER_TEST_CHOWN_FAIL=1
if "$ENTRYPOINT" dotnet Farm.Slicer.Host.dll >/dev/null 2>&1; then
    fail "ownership failure unexpectedly passed"
fi
[[ ! -s "$SLICER_TEST_GOSU_LOG" ]] || fail "application launched after ownership repair failed"
unset SLICER_TEST_CHOWN_FAIL
pass "ownership repair failure blocks application startup"

: > "$SLICER_TEST_GOSU_LOG"
export SLICER_TEST_ACCESS_FAIL=1
if "$ENTRYPOINT" dotnet Farm.Slicer.Host.dll >/dev/null 2>&1; then
    fail "unwritable artifact root unexpectedly passed"
fi
assert_not_contains "$SLICER_TEST_GOSU_LOG" "appuser dotnet"
unset SLICER_TEST_ACCESS_FAIL
pass "insufficient non-root storage permissions block application startup without chmod"

: > "$TEST_LOG"
: > "$SLICER_TEST_GOSU_LOG"
export SLICER_TEST_DEVICE_ID=100
export SLICER_TEST_FOREIGN_DEVICE=1
if "$ENTRYPOINT" dotnet Farm.Slicer.Host.dll >/dev/null 2>&1; then
    fail "nested filesystem mount unexpectedly passed"
fi
[[ ! -s "$TEST_LOG" && ! -s "$SLICER_TEST_GOSU_LOG" ]] ||
    fail "nested filesystem mount triggered ownership changes or application startup"
unset SLICER_TEST_DEVICE_ID SLICER_TEST_FOREIGN_DEVICE
pass "different-device nested mounts block ownership changes, including the mount root"

for invalid_root in "relative/path" "/" "/data" "/data/" "/data//" "/data/../outside" "/app/artifacts"; do
    : > "$TEST_LOG"
    export ArtifactStorage__RootPath="$invalid_root"
    if "$ENTRYPOINT" dotnet Farm.Slicer.Host.dll >/dev/null 2>&1; then
        fail "invalid artifact path unexpectedly passed: $invalid_root"
    fi
    [[ ! -s "$TEST_LOG" ]] || fail "invalid artifact path triggered ownership changes: $invalid_root"
done
pass "relative, mount-root, outside-mount, and traversal paths are rejected"

symlink_root="/data/nested-symlink-check"
mkdir -p "$symlink_root"
: > "$TEST_LOG"
export ArtifactStorage__RootPath="$symlink_root"
export SLICER_TEST_FAKE_SYMLINK="$symlink_root/existing-link"
if "$ENTRYPOINT" dotnet Farm.Slicer.Host.dll >/dev/null 2>&1; then
    fail "symlink inside artifact storage unexpectedly passed"
fi
[[ ! -s "$TEST_LOG" ]] || fail "symlink inside artifact storage triggered ownership changes"
pass "symlinks inside artifact storage are rejected"

if [[ "$REAL_UID" != "0" ]] && command -v chown >/dev/null 2>&1; then
    mv "$MOCK_BIN/chown" "$MOCK_BIN/chown.mock"
    actual_root="$SLICER_TEST_DATA_ROOT/actual-chown"
    mkdir -p "$actual_root/nested"
    printf 'real chown artifact\n' > "$actual_root/nested/existing.bin"
    chmod 0750 "$actual_root" "$actual_root/nested"
    chmod 0640 "$actual_root/nested/existing.bin"
    root_mode_before="$(stat -c '%a' "$actual_root")"
    nested_mode_before="$(stat -c '%a' "$actual_root/nested")"
    file_mode_before="$(stat -c '%a' "$actual_root/nested/existing.bin")"
    export SLICER_TEST_APP_UID="$REAL_UID"
    export SLICER_TEST_APP_GID="$REAL_GID"
    unset SLICER_TEST_FAKE_SYMLINK
    export ArtifactStorage__RootPath="/data/actual-chown"
    "$ENTRYPOINT" dotnet Farm.Slicer.Host.dll
    [[ "$(cat "$actual_root/nested/existing.bin")" == "real chown artifact" ]] ||
        fail "real chown changed artifact contents"
    [[ "$(stat -c '%a' "$actual_root")" == "$root_mode_before" ]] ||
        fail "real chown changed artifact root mode"
    [[ "$(stat -c '%a' "$actual_root/nested")" == "$nested_mode_before" ]] ||
        fail "real chown changed nested directory mode"
    [[ "$(stat -c '%a' "$actual_root/nested/existing.bin")" == "$file_mode_before" ]] ||
        fail "real chown changed artifact file mode"
    pass "real chown preserves artifact contents and ordinary modes"
else
    printf 'SKIP: real chown mode check (requires non-root host user and chown)\n'
fi
