#!/usr/bin/env bash
# =============================================================================
# test-pre-push.sh — deterministic regression suite for .githooks/pre-push
#
# Each case builds an isolated Git repo in a temp directory, seeds it with a
# fake dotnet on PATH, runs the hook with a synthesized push-list on stdin,
# and asserts the exit code + side effects (cache markers, etc.).
#
# The suite is portable to macOS and Linux (no gnu-awk deps, no getopt).
# =============================================================================

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
HOOK="$REPO_ROOT/.githooks/pre-push"

if [[ ! -r "$HOOK" ]]; then
  echo "FATAL: hook not readable at $HOOK" >&2
  exit 1
fi

PASSED=0
FAILED=0
FAILED_NAMES=()

# ---------------------------------------------------------------------------
# Build a hermetic PATH that includes standard executables but excludes any
# `dotnet` binary. Used to prove the "missing dotnet" cases fail closed.
# `command -v` is a builtin, so `env -i PATH=... command -v dotnet` doesn't
# actually spawn `command` — we invoke bash-in-env to run it as a builtin
# inside a subshell that only sees $dst on PATH.
# ---------------------------------------------------------------------------
HERMETIC_NO_DOTNET_BIN=""

build_hermetic_no_dotnet_bin() {
  local dst
  dst="$(mktemp -d)"
  local src
  for src in /usr/local/bin /opt/homebrew/bin /usr/bin /bin /usr/sbin /sbin; do
    [[ -d "$src" ]] || continue
    local f
    for f in "$src"/*; do
      [[ -x "$f" && ! -d "$f" ]] || continue
      local base ; base="$(basename "$f")"
      case "$base" in
        dotnet|dotnet-*) continue ;;
      esac
      if [[ ! -e "$dst/$base" ]]; then
        ln -s "$f" "$dst/$base" 2>/dev/null || true
      fi
    done
  done
  # Prove hermetic-ness: dotnet must NOT be reachable, but git MUST be.
  local check
  check="$(env -i PATH="$dst" "$dst/bash" --noprofile --norc -c 'command -v dotnet' 2>/dev/null || true)"
  if [[ -n "$check" ]]; then
    echo "FATAL: hermetic PATH still resolves dotnet ($check)" >&2
    return 1
  fi
  check="$(env -i PATH="$dst" "$dst/bash" --noprofile --norc -c 'command -v git' 2>/dev/null || true)"
  if [[ -z "$check" ]]; then
    echo "FATAL: hermetic PATH cannot find git (positive control failed)" >&2
    return 1
  fi
  HERMETIC_NO_DOTNET_BIN="$dst"
}

# ---------------------------------------------------------------------------
# Build a fake `dotnet` that:
#   * `dotnet --version`         → prints $FAKE_DOTNET_VERSION (default 10.0.0)
#   * `dotnet format ... --verify-no-changes` → exits $FAKE_FORMAT_RC (default 0)
#   * `dotnet format --version`  → prints $FAKE_FORMAT_VERSION (default "10.0.0-format")
#   * `dotnet <anything else>`   → exits 0
#
# The invocation trace is written to $FAKE_LOG so tests can assert exact
# command-line arguments.
# ---------------------------------------------------------------------------
install_fake_dotnet() {
  local bin_dir="$1"
  mkdir -p "$bin_dir"
  cat > "$bin_dir/dotnet" <<'EOF'
#!/usr/bin/env bash
# The `${var-default}` (no colon) form preserves an explicitly-empty value,
# which the tests use to exercise the "empty version rejected" path.
: "${FAKE_LOG=/tmp/fake-dotnet.log}"
: "${FAKE_DOTNET_VERSION=10.0.0}"
: "${FAKE_FORMAT_VERSION=10.0.0-format}"
: "${FAKE_FORMAT_RC=0}"
printf '%s\n' "$*" >> "$FAKE_LOG"
if [[ "$1" == "--version" ]]; then
  printf '%s\n' "$FAKE_DOTNET_VERSION"
  exit 0
fi
if [[ "$1" == "format" && "$2" == "--version" ]]; then
  printf '%s\n' "$FAKE_FORMAT_VERSION"
  exit 0
fi
if [[ "$1" == "format" ]]; then
  # `dotnet format <sln> --verify-no-changes`
  exit "$FAKE_FORMAT_RC"
fi
exit 0
EOF
  chmod +x "$bin_dir/dotnet"
}

# ---------------------------------------------------------------------------
# Set up an isolated git repo with:
#   - src/farm-web.sln (minimal placeholder)
#   - src/api/Program.cs (initial content)
# Returns via globals REPO / PATH_BIN / FAKE_LOG.
# ---------------------------------------------------------------------------
REPO=""
PATH_BIN=""
FAKE_LOG=""

setup_repo() {
  REPO="$(mktemp -d)"
  PATH_BIN="$(mktemp -d)"
  FAKE_LOG="$(mktemp)"
  : > "$FAKE_LOG"

  install_fake_dotnet "$PATH_BIN"

  ( cd "$REPO"
    git init -q -b main
    git config user.email "test@example.com"
    git config user.name  "Test"
    git config commit.gpgsign false
    mkdir -p src/api
    printf 'sln\n' > src/farm-web.sln
    printf 'class P {}\n' > src/api/Program.cs
    printf 'docs\n' > README.md
    # Copy the hook in.
    mkdir -p .githooks
    cp "$HOOK" .githooks/pre-push
    chmod +x .githooks/pre-push
    git config core.hooksPath .githooks
    git add -A
    git commit -q -m "initial"
  )
}

teardown_repo() {
  rm -rf -- "$REPO" "$PATH_BIN"
  rm -f -- "$FAKE_LOG"
  REPO="" ; PATH_BIN="" ; FAKE_LOG=""
}

# run_hook <with_dotnet:yes|no> <stdin push list>
# Executes the hook with a controlled PATH.
#
# For with_dotnet=yes we prepend $PATH_BIN so the fake dotnet shadows any real
# one, but keep the surrounding PATH so git-for-Windows (which lives in
# /mingw64/bin, /cmd, etc.) is still reachable. `env -i` is only used for the
# "missing dotnet" cases, which are gated behind $HERMETIC_NO_DOTNET_BIN and
# therefore skipped on hosts where hermeticity can't be established.
run_hook() {
  local with_dotnet="$1"
  local stdin_list="$2"
  local path
  if [[ "$with_dotnet" == "yes" ]]; then
    path="$PATH_BIN:$PATH"
    ( cd "$REPO"
      printf '%s' "$stdin_list" \
        | PATH="$path" \
            FAKE_LOG="$FAKE_LOG" \
            FAKE_DOTNET_VERSION="${FAKE_DOTNET_VERSION-10.0.0}" \
            FAKE_FORMAT_VERSION="${FAKE_FORMAT_VERSION-10.0.0-format}" \
            FAKE_FORMAT_RC="${FAKE_FORMAT_RC-0}" \
            PRE_PUSH_DEBUG="${PRE_PUSH_DEBUG:-}" \
            bash .githooks/pre-push
    )
    return $?
  fi

  # with_dotnet=no path: require the hermetic bin.
  if [[ -z "$HERMETIC_NO_DOTNET_BIN" ]]; then
    echo "FATAL: hermetic bin not set up" >&2
    return 99
  fi
  path="$HERMETIC_NO_DOTNET_BIN"
  local ck1 ck2
  ck1="$(env -i PATH="$path" "$path/bash" --noprofile --norc -c 'command -v git' 2>/dev/null || true)"
  ck2="$(env -i PATH="$path" "$path/bash" --noprofile --norc -c 'command -v dotnet' 2>/dev/null || true)"
  if [[ -z "$ck1" || -n "$ck2" ]]; then
    echo "FATAL per-run: git=$ck1 dotnet=$ck2" >&2
    return 99
  fi
  ( cd "$REPO"
    printf '%s' "$stdin_list" \
      | env -i \
          PATH="$path" \
          HOME="$HOME" \
          FAKE_LOG="$FAKE_LOG" \
          bash .githooks/pre-push
  )
}

# assert_rc <label> <actual> <expected>
assert_rc() {
  local label="$1" actual="$2" expected="$3"
  if [[ "$actual" != "$expected" ]]; then
    printf '  BAD rc for %s: expected %s got %s\n' "$label" "$expected" "$actual" >&2
    return 1
  fi
  return 0
}

# make_commit <path> <content>
make_commit() {
  local path="$1" content="$2" msg="$3"
  ( cd "$REPO"
    mkdir -p "$(dirname "$path")"
    printf '%s' "$content" > "$path"
    git add -A
    git commit -q -m "$msg"
    git rev-parse HEAD
  )
}

# push_line <local_sha> <remote_sha>
push_line() {
  printf 'refs/heads/main %s refs/heads/main %s\n' "$1" "$2"
}

SKIPPED=0
SKIPPED_NAMES=()

# run_case <name> <fn>
run_case() {
  local name="$1" fn="$2"
  # Fresh repo per case.
  setup_repo
  local rc=0
  # Function can print SKIP:<reason> to stderr and return 77 to indicate skip.
  local out
  out="$("$fn" 2>&1)"
  rc=$?
  if (( rc == 77 )); then
    printf 'SKIP  %s\n' "$name"
    SKIPPED=$((SKIPPED+1))
    SKIPPED_NAMES+=("$name")
  elif (( rc == 0 )); then
    printf 'PASS  %s\n' "$name"
    PASSED=$((PASSED+1))
  else
    printf '%s\n' "$out" >&2
    printf 'FAIL  %s\n' "$name"
    FAILED=$((FAILED+1))
    FAILED_NAMES+=("$name")
  fi
  teardown_repo
  return 0
}

# =============================================================================
# Cases
# =============================================================================

case_pass_when_formatted() {
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class P { }' "reformat api")"
  local rc=0
  FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "pass_when_formatted" "$rc" "0"
}

case_fail_when_unformatted() {
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class P{}' "unformatted")"
  local rc=0
  FAKE_FORMAT_RC=1 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "fail_when_unformatted" "$rc" "1"
}

case_missing_dotnet_fails_closed() {
  if [[ -z "$HERMETIC_NO_DOTNET_BIN" ]]; then
    echo "SKIP: no hermetic no-dotnet PATH available on this host" >&2
    return 77
  fi
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class Q { }' "any change")"
  local rc=0
  run_hook no "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "missing_dotnet" "$rc" "1"
}

case_missing_dotnet_but_no_dotnet_changes_passes() {
  if [[ -z "$HERMETIC_NO_DOTNET_BIN" ]]; then
    echo "SKIP: no hermetic no-dotnet PATH available on this host" >&2
    return 77
  fi
  # Only README changed → hook should short-circuit before probing dotnet.
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit README.md 'new docs' "docs")"
  local rc=0
  run_hook no "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "missing_dotnet_docs_only" "$rc" "0"
}

case_cache_hit_skips_format() {
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class P { }' "changed")"
  # First push — cache miss, format runs, stamp written.
  local rc=0
  FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "first push" "$rc" "0" || return 1
  # Count format invocations after first push.
  local first_count
  first_count="$(grep -c '^format ' "$FAKE_LOG" || true)"
  # Second push of the same tree — should hit cache. Simulate by re-running.
  # We flip FAKE_FORMAT_RC to 1 so a re-verify would fail; cache hit must
  # bypass it.
  local rc2=0
  FAKE_FORMAT_RC=1 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc2=$?
  assert_rc "second push cache hit" "$rc2" "0" || return 1
  local second_count
  second_count="$(grep -c '^format ' "$FAKE_LOG" || true)"
  # Second push should NOT have added a new `format ...` line — only version
  # probes.
  # The first_count includes exactly one --verify-no-changes invocation.
  # After the second push, the count of `format ./farm-web.sln --verify-no-changes`
  # should remain unchanged.
  local verify_count
  verify_count="$(grep -Ec '^format .*--verify-no-changes' "$FAKE_LOG" || true)"
  if [[ "$verify_count" != "1" ]]; then
    printf '  expected 1 verify invocation, got %s (log below):\n' "$verify_count" >&2
    cat "$FAKE_LOG" >&2
    return 1
  fi
  return 0
}

case_changed_tree_invalidates_cache() {
  local sha0 sha1 sha2
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class P { }' "first change")"
  FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1
  # Now change the file → new tree → cache miss → format must run again.
  sha2="$(make_commit src/api/Program.cs 'class P { /* v2 */ }' "second change")"
  FAKE_FORMAT_RC=1 run_hook yes "$(push_line "$sha2" "$sha1")" >/dev/null 2>&1
  local rc=$?
  assert_rc "changed tree fails when unformatted" "$rc" "1"
}

case_delete_ref_skipped() {
  # Delete: local sha is all zeroes → hook must skip cleanly.
  local sha0
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  local ZERO="0000000000000000000000000000000000000000"
  local line="refs/heads/main $ZERO refs/heads/main $sha0"
  local rc=0
  # A delete-only push has no .NET-relevant changes, so we use the with-dotnet
  # PATH (dotnet won't actually be invoked). The hermetic no-dotnet PATH is
  # only used for tests that specifically exercise the missing-dotnet path.
  run_hook yes "$line" >/dev/null 2>&1 || rc=$?
  assert_rc "delete_ref" "$rc" "0"
}

case_new_branch_verifies() {
  # New branch: remote sha is all zeroes. We synthesize a base against the
  # existing commit (which is on main) using a fresh commit.
  local sha1
  sha1="$(make_commit src/api/Program.cs 'class P { }' "feature")"
  local ZERO="0000000000000000000000000000000000000000"
  local line="refs/heads/feature $sha1 refs/heads/feature $ZERO"
  local rc=0
  FAKE_FORMAT_RC=0 run_hook yes "$line" >/dev/null 2>&1 || rc=$?
  assert_rc "new_branch" "$rc" "0"
}

case_multi_ref_dedup() {
  # Two refs pointing at the same tip should format once.
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class P { }' "shared")"
  local lines
  lines="$(push_line "$sha1" "$sha0")"$'\n'"refs/heads/other $sha1 refs/heads/other $sha0"$'\n'
  local rc=0
  FAKE_FORMAT_RC=0 run_hook yes "$lines" >/dev/null 2>&1 || rc=$?
  assert_rc "multi_ref rc" "$rc" "0" || return 1
  local verify_count
  verify_count="$(grep -Ec '^format .*--verify-no-changes' "$FAKE_LOG" || true)"
  if [[ "$verify_count" != "1" ]]; then
    printf '  expected 1 verify invocation for dedup, got %s\n' "$verify_count" >&2
    return 1
  fi
}

case_non_dotnet_change_skipped() {
  # Only README changed → hook should skip format entirely.
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit README.md 'new docs' "docs")"
  local rc=0
  FAKE_FORMAT_RC=1 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  # Even though FORMAT_RC=1 (would fail), skip means no invocation → success.
  assert_rc "docs_only" "$rc" "0" || return 1
  local verify_count
  verify_count="$(grep -Ec '^format .*--verify-no-changes' "$FAKE_LOG" || true)"
  if [[ "$verify_count" != "0" ]]; then
    printf '  expected 0 verify invocations for docs, got %s\n' "$verify_count" >&2
    return 1
  fi
}

case_missing_sln_fails_closed() {
  # Delete the sln → .cs still changed → hook must reject.
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  ( cd "$REPO"
    rm -f src/farm-web.sln
    printf 'class P { }\n' > src/api/Program.cs
    git add -A
    git commit -q -m "delete sln"
  )
  sha1="$(cd "$REPO" && git rev-parse HEAD)"
  local rc=0
  FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "missing_sln" "$rc" "1"
}

case_empty_dotnet_version_rejected() {
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class P { }' "change")"
  local rc=0
  FAKE_DOTNET_VERSION="" FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "empty_dotnet_version" "$rc" "1"
}

case_empty_format_version_rejected() {
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class P { }' "change")"
  local rc=0
  FAKE_FORMAT_VERSION="" FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "empty_format_version" "$rc" "1"
}

case_rename_dotnet_to_nondotnet_verified() {
  # Renaming a .cs file to a non-.cs path (or the reverse) still counts as
  # a .NET-relevant path change because --no-renames decomposes to add+delete.
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  ( cd "$REPO"
    git mv src/api/Program.cs docs/Program.cs.old
    git commit -q -m "move api to docs"
  )
  sha1="$(cd "$REPO" && git rev-parse HEAD)"
  local rc=0
  FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "rename_cs_to_docs" "$rc" "0"
}

case_rename_nondotnet_to_dotnet_verified() {
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  ( cd "$REPO"
    printf 'hello\n' > notes.txt
    git add notes.txt
    git commit -q -m "add notes"
  )
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  ( cd "$REPO"
    mkdir -p src/api
    git mv notes.txt src/api/Notes.cs
    git commit -q -m "promote notes to cs"
  )
  sha1="$(cd "$REPO" && git rev-parse HEAD)"
  local rc=0
  FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "rename_docs_to_cs" "$rc" "0"
}

case_hook_hash_invalidates_cache() {
  # Simulate the hook itself changing → cache key differs → format runs again.
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class P { }' "change")"
  FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1
  # Modify the hook by appending a whitespace-only comment.
  printf '\n# noop\n' >> "$REPO/.githooks/pre-push"
  local rc=0
  FAKE_FORMAT_RC=1 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  # New hook hash → cache miss → format fails → rc=1.
  assert_rc "hook_hash_invalidation" "$rc" "1"
}

case_dotnet_version_invalidates_cache() {
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class P { }' "change")"
  FAKE_DOTNET_VERSION="10.0.0" FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1
  # Bump the SDK version → different cache key → new run.
  local rc=0
  FAKE_DOTNET_VERSION="10.0.1" FAKE_FORMAT_RC=1 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "dotnet_version_invalidation" "$rc" "1"
}

case_format_version_invalidates_cache() {
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class P { }' "change")"
  FAKE_FORMAT_VERSION="10.0.0-format" FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1
  local rc=0
  FAKE_FORMAT_VERSION="10.0.1-format" FAKE_FORMAT_RC=1 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "format_version_invalidation" "$rc" "1"
}

case_csproj_change_verified() {
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  ( cd "$REPO"
    mkdir -p src/api
    printf '<Project />\n' > src/api/Farm.Web.Api.csproj
    git add -A
    git commit -q -m "add csproj"
  )
  sha1="$(cd "$REPO" && git rev-parse HEAD)"
  local rc=0
  FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "csproj_verified" "$rc" "0"
}

case_editorconfig_change_verified() {
  local sha0 sha1
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  ( cd "$REPO"
    printf 'root = true\n' > src/.editorconfig
    git add -A
    git commit -q -m "add editorconfig"
  )
  sha1="$(cd "$REPO" && git rev-parse HEAD)"
  local rc=0
  FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha1" "$sha0")" >/dev/null 2>&1 || rc=$?
  assert_rc "editorconfig_verified" "$rc" "0"
}

case_force_push_range() {
  # Force push: remote_sha is not an ancestor of local_sha. `git diff`
  # against remote_sha still yields a symmetric diff which we treat as
  # the outgoing set.
  local sha0 sha1 sha2
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class P { /* branch1 */ }' "branch1")"
  # Reset and create an alternate history.
  ( cd "$REPO"
    git reset -q --hard "$sha0"
  )
  sha2="$(make_commit src/api/Program.cs 'class Q { /* branch2 */ }' "branch2")"
  local rc=0
  FAKE_FORMAT_RC=0 run_hook yes "$(push_line "$sha2" "$sha1")" >/dev/null 2>&1 || rc=$?
  assert_rc "force_push" "$rc" "0"
}

case_empty_push_list_skipped() {
  local rc=0
  run_hook yes "" >/dev/null 2>&1 || rc=$?
  assert_rc "empty_push" "$rc" "0"
}

case_multi_ref_mixed_relevance() {
  # One ref has .NET changes; the other only has docs.
  local sha0 sha1 sha2
  sha0="$(cd "$REPO" && git rev-parse HEAD)"
  sha1="$(make_commit src/api/Program.cs 'class P { }' "api")"
  sha2="$(make_commit README.md 'more docs' "docs")"
  local lines
  lines="refs/heads/feat $sha1 refs/heads/feat $sha0"$'\n'"refs/heads/docs $sha2 refs/heads/docs $sha1"$'\n'
  local rc=0
  FAKE_FORMAT_RC=0 run_hook yes "$lines" >/dev/null 2>&1 || rc=$?
  assert_rc "multi_ref_mixed" "$rc" "0"
}

# =============================================================================
# Runner
# =============================================================================

TESTS=(
  case_pass_when_formatted
  case_fail_when_unformatted
  case_missing_dotnet_fails_closed
  case_missing_dotnet_but_no_dotnet_changes_passes
  case_cache_hit_skips_format
  case_changed_tree_invalidates_cache
  case_delete_ref_skipped
  case_new_branch_verifies
  case_multi_ref_dedup
  case_non_dotnet_change_skipped
  case_missing_sln_fails_closed
  case_empty_dotnet_version_rejected
  case_empty_format_version_rejected
  case_rename_dotnet_to_nondotnet_verified
  case_rename_nondotnet_to_dotnet_verified
  case_hook_hash_invalidates_cache
  case_dotnet_version_invalidates_cache
  case_format_version_invalidates_cache
  case_csproj_change_verified
  case_editorconfig_change_verified
  case_force_push_range
  case_empty_push_list_skipped
  case_multi_ref_mixed_relevance
)

printf '=== pre-push hook test suite ===\n'
if ! build_hermetic_no_dotnet_bin 2>/dev/null; then
  echo "NOTE: hermetic no-dotnet PATH unavailable — missing-dotnet cases will SKIP" >&2
fi

for t in "${TESTS[@]}"; do
  run_case "$t" "$t"
done

printf '\n=== summary ===\n'
printf 'passed:  %d\n' "$PASSED"
printf 'failed:  %d\n' "$FAILED"
printf 'skipped: %d\n' "$SKIPPED"
if (( SKIPPED > 0 )); then
  printf 'skipped cases:\n'
  for n in "${SKIPPED_NAMES[@]}"; do
    printf '  - %s\n' "$n"
  done
fi
if (( FAILED > 0 )); then
  printf 'failing cases:\n'
  for n in "${FAILED_NAMES[@]}"; do
    printf '  - %s\n' "$n"
  done
  exit 1
fi
exit 0
