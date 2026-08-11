#!/usr/bin/env bash
# =============================================================================
# test-generate-codeql-slnf.sh — regression suite for the CodeQL solution
# filter generator (scripts/ci/generate-codeql-slnf.sh, used by
# .github/workflows/codeql.yml).
#
# Covers the synthetic-solution cases (exclusion, JSON shape, fail-closed
# guards) plus one case pinned to the real src/farm-web.sln, so a rename of
# the test tree or a solution-format change is caught here rather than by a
# silent change in CodeQL coverage.
#
# Emits a compact PASS/FAIL line per case plus a summary, and exits non-zero
# if any case fails.
# =============================================================================

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
GENERATOR="$REPO_ROOT/scripts/ci/generate-codeql-slnf.sh"

if [[ ! -r "$GENERATOR" ]]; then
  echo "FATAL: generator not found at $GENERATOR" >&2
  exit 1
fi

PASSED=0
FAILED=0
FAILED_NAMES=()

# Paths to remove on exit. case_real_solution must write beside the real
# solution (a .slnf resolves solution.path relative to its own directory), so
# an interrupted run would otherwise leave a generated file in the work tree.
CLEANUP_PATHS=()
cleanup() {
  local path
  for path in "${CLEANUP_PATHS[@]:-}"; do
    [[ -n "$path" ]] && rm -rf "$path"
  done
}
# Cleanup runs once, on EXIT. The signal handlers exit with the conventional
# 128+signal status rather than cleaning up inline, so a cancelled run stops
# instead of resuming after the handler and recreating what was just removed.
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# ---------------------------------------------------------------------------
# Helpers.
# ---------------------------------------------------------------------------

# assert_eq <label> <actual> <expected>
assert_eq() {
  local label="$1" actual="$2" expected="$3"
  if [[ "$actual" != "$expected" ]]; then
    printf '  MISMATCH %s\n    expected: %q\n    actual:   %q\n' "$label" "$expected" "$actual" >&2
    return 1
  fi
  return 0
}

# assert_contains <label> <haystack> <needle>
assert_contains() {
  local label="$1" haystack="$2" needle="$3"
  if [[ "$haystack" != *"$needle"* ]]; then
    printf '  MISSING %s\n    expected to contain: %q\n' "$label" "$needle" >&2
    return 1
  fi
  return 0
}

# assert_not_contains <label> <haystack> <needle>
assert_not_contains() {
  local label="$1" haystack="$2" needle="$3"
  if [[ "$haystack" == *"$needle"* ]]; then
    printf '  UNEXPECTED %s\n    expected NOT to contain: %q\n' "$label" "$needle" >&2
    return 1
  fi
  return 0
}

# assert_gt <label> <actual> <minimum_exclusive>
assert_gt() {
  local label="$1" actual="$2" minimum="$3"
  if [[ ! "$actual" =~ ^[0-9]+$ ]] || (( actual <= minimum )); then
    printf '  MISMATCH %s\n    expected: > %s\n    actual:   %q\n' "$label" "$minimum" "$actual" >&2
    return 1
  fi
  return 0
}

record() {
  local name="$1" ok="$2"
  if (( ok == 0 )); then
    printf 'PASS  %s\n' "$name"
    PASSED=$((PASSED + 1))
  else
    printf 'FAIL  %s\n' "$name"
    FAILED=$((FAILED + 1))
    FAILED_NAMES+=("$name")
  fi
}

# write_project <solution_dir> <relative_path_with_backslashes>
write_project() {
  local dir="$1" rel="${2//\\//}"
  mkdir -p "$dir/$(dirname "$rel")"
  printf '<Project Sdk="Microsoft.NET.Sdk" />\n' > "$dir/$rel"
}

# make_solution <solution_dir> <project relative paths...> — writes a scratch
# .sln with one solution folder entry (which must be ignored) plus the given
# projects, and creates the project files on disk.
make_solution() {
  local dir="$1"; shift
  local sln="$dir/scratch.sln"
  mkdir -p "$dir"
  {
    printf 'Microsoft Visual Studio Solution File, Format Version 12.00\n'
    printf 'Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "tests", "tests", "{0AB3BF05-4346-4AA6-1389-037BE0695223}"\nEndProject\n'
    local i=0
    for rel in "$@"; do
      i=$((i + 1))
      printf 'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "P%d", "%s", "{00000000-0000-0000-0000-00000000000%d}"\nEndProject\n' \
        "$i" "$rel" "$i"
      write_project "$dir" "$rel"
    done
  } > "$sln"
  echo "$sln"
}

# ---------------------------------------------------------------------------
# Case: projects under tests/ are excluded, everything else is kept.
# ---------------------------------------------------------------------------
case_excludes_tests() {
  local tmp sln out output ok=0
  tmp="$(mktemp -d)"
  sln="$(make_solution "$tmp" 'api\Farm.Web.Api.csproj' 'tests\Farm.Web.Api.Tests\Farm.Web.Api.Tests.csproj' 'infra\Farm.Infrastructure.csproj')"
  out="$tmp/scratch.codeql.slnf"

  SOLUTION="$sln" OUT_FILE="$out" bash "$GENERATOR" > /dev/null 2>&1 || ok=1
  output="$(cat "$out" 2>/dev/null)"

  assert_contains "keeps api project" "$output" 'api\\Farm.Web.Api.csproj' || ok=1
  assert_contains "keeps infra project" "$output" 'infra\\Farm.Infrastructure.csproj' || ok=1
  assert_not_contains "drops test project" "$output" 'Farm.Web.Api.Tests' || ok=1
  assert_contains "points at the solution" "$output" '"path": "scratch.sln"' || ok=1
  # Solution folder rows must not be mistaken for projects.
  assert_not_contains "ignores solution folders" "$output" '"tests"' || ok=1

  if command -v node > /dev/null 2>&1; then
    local parsed
    parsed="$(node -e 'const d=require("fs").readFileSync(process.argv[1],"utf8");const j=JSON.parse(d);console.log(j.solution.projects.length)' "$out" 2>/dev/null)"
    assert_eq "emits valid JSON with 2 projects" "$parsed" "2" || ok=1
  fi

  rm -rf "$tmp"
  record "excludes test projects, keeps the rest" "$ok"
}

# ---------------------------------------------------------------------------
# Case: a solution with no parsable projects fails closed.
# ---------------------------------------------------------------------------
case_unparsable_solution() {
  local tmp sln out status ok=0
  tmp="$(mktemp -d)"
  sln="$tmp/scratch.sln"
  printf 'Microsoft Visual Studio Solution File, Format Version 12.00\n' > "$sln"
  out="$tmp/scratch.codeql.slnf"

  SOLUTION="$sln" OUT_FILE="$out" bash "$GENERATOR" > /dev/null 2>&1
  status=$?

  assert_eq "exits non-zero" "$status" "1" || ok=1
  if [[ -f "$out" ]]; then
    printf '  UNEXPECTED wrote a filter for an unparsable solution\n' >&2
    ok=1
  fi

  rm -rf "$tmp"
  record "fails closed when no projects parse" "$ok"
}

# ---------------------------------------------------------------------------
# Case: an exclusion pattern that removes everything fails closed.
# ---------------------------------------------------------------------------
case_excludes_everything() {
  local tmp sln out status ok=0
  tmp="$(mktemp -d)"
  sln="$(make_solution "$tmp" 'api\Farm.Web.Api.csproj')"
  out="$tmp/scratch.codeql.slnf"

  SOLUTION="$sln" OUT_FILE="$out" EXCLUDE_REGEX='.' bash "$GENERATOR" > /dev/null 2>&1
  status=$?

  assert_eq "exits non-zero" "$status" "1" || ok=1

  rm -rf "$tmp"
  record "fails closed when everything is excluded" "$ok"
}

# ---------------------------------------------------------------------------
# Case: a project listed in the solution but absent from disk fails closed,
# rather than emitting a filter that silently drops it from analysis.
# ---------------------------------------------------------------------------
case_missing_project_file() {
  local tmp sln out status ok=0
  tmp="$(mktemp -d)"
  sln="$(make_solution "$tmp" 'api\Farm.Web.Api.csproj')"
  rm -f "$tmp/api/Farm.Web.Api.csproj"
  out="$tmp/scratch.codeql.slnf"

  SOLUTION="$sln" OUT_FILE="$out" bash "$GENERATOR" > /dev/null 2>&1
  status=$?

  assert_eq "exits non-zero" "$status" "1" || ok=1

  rm -rf "$tmp"
  record "fails closed when a listed project is missing" "$ok"
}

# ---------------------------------------------------------------------------
# Case: an OUT_FILE outside the solution directory is rejected — a solution
# filter resolves `solution.path` relative to its own location.
# ---------------------------------------------------------------------------
case_out_file_must_sit_beside_solution() {
  local tmp sln status ok=0
  tmp="$(mktemp -d)"
  sln="$(make_solution "$tmp/sln" 'api\Farm.Web.Api.csproj')"
  mkdir -p "$tmp/elsewhere"

  SOLUTION="$sln" OUT_FILE="$tmp/elsewhere/scratch.codeql.slnf" bash "$GENERATOR" > /dev/null 2>&1
  status=$?

  assert_eq "exits non-zero" "$status" "1" || ok=1

  rm -rf "$tmp"
  record "rejects an OUT_FILE outside the solution directory" "$ok"
}

# ---------------------------------------------------------------------------
# Case: a project row the extraction pattern cannot parse fails closed, rather
# than emitting a filter that silently omits it from analysis. Guards the
# partial-miss case that the "no projects parsed" check cannot see.
# ---------------------------------------------------------------------------
case_partial_parse_miss() {
  local tmp sln out status ok=0
  tmp="$(mktemp -d)"
  sln="$(make_solution "$tmp" 'api\Farm.Web.Api.csproj')"
  # Indented, so the anchored `^Project(` pattern misses it while the path is
  # still plainly a project reference in the file.
  printf '  Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "P9", "tools\\Hidden.csproj", "{00000000-0000-0000-0000-000000000009}"\nEndProject\n' >> "$sln"
  write_project "$tmp" 'tools\Hidden.csproj'
  out="$tmp/scratch.codeql.slnf"

  SOLUTION="$sln" OUT_FILE="$out" bash "$GENERATOR" > /dev/null 2>&1
  status=$?

  assert_eq "exits non-zero" "$status" "1" || ok=1
  if [[ -f "$out" ]]; then
    printf '  UNEXPECTED wrote a filter despite an unparsed project row\n' >&2
    ok=1
  fi

  rm -rf "$tmp"
  record "fails closed when a project row does not parse" "$ok"
}

# ---------------------------------------------------------------------------
# Case: a project whose DISPLAY NAME ends in .csproj must not inflate the
# cross-check count and reject an otherwise valid solution.
# ---------------------------------------------------------------------------
case_display_name_looks_like_a_path() {
  local tmp sln out output status ok=0
  tmp="$(mktemp -d)"
  sln="$tmp/scratch.sln"
  {
    printf 'Microsoft Visual Studio Solution File, Format Version 12.00\n'
    printf 'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Farm.Web.Api.csproj", "api\\Farm.Web.Api.csproj", "{00000000-0000-0000-0000-000000000001}"\nEndProject\n'
    printf 'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "P2", "tests\\T.Tests\\T.Tests.csproj", "{00000000-0000-0000-0000-000000000002}"\nEndProject\n'
  } > "$sln"
  write_project "$tmp" 'api\Farm.Web.Api.csproj'
  write_project "$tmp" 'tests\T.Tests\T.Tests.csproj'
  out="$tmp/scratch.codeql.slnf"

  SOLUTION="$sln" OUT_FILE="$out" bash "$GENERATOR" > /dev/null 2>&1
  status=$?
  output="$(cat "$out" 2>/dev/null)"

  assert_eq "exits zero" "$status" "0" || ok=1
  assert_contains "keeps the api project" "$output" 'api\\Farm.Web.Api.csproj' || ok=1
  assert_not_contains "drops the test project" "$output" 'T.Tests' || ok=1

  rm -rf "$tmp"
  record "a display name ending in .csproj does not fail the cross-check" "$ok"
}

# ---------------------------------------------------------------------------
# Case: the real solution still has a tests/ tree to exclude, and production
# projects survive. Guards against a rename silently disabling the filter.
# ---------------------------------------------------------------------------
case_real_solution() {
  local tmp out output excluded_count ok=0
  tmp="$(mktemp -d)"
  # The generator requires OUT_FILE beside the solution, so this must be
  # written into src/. Use a per-PID name so concurrent runs cannot clobber
  # each other, and register it for cleanup on interrupt as well as on exit.
  out="$REPO_ROOT/src/farm-web.codeql.$$.slnf"
  CLEANUP_PATHS+=("$out" "$tmp")

  if ! SOLUTION="$REPO_ROOT/src/farm-web.sln" OUT_FILE="$out" bash "$GENERATOR" > "$tmp/log" 2>&1; then
    cat "$tmp/log" >&2
    ok=1
  fi
  output="$(cat "$out" 2>/dev/null)"

  assert_contains "keeps the API project" "$output" 'api\\Farm.Web.Api.csproj' || ok=1
  assert_not_contains "drops Farm.Web.Api.Tests" "$output" 'Farm.Web.Api.Tests' || ok=1
  assert_not_contains "drops Farm.Slicer.Module.Tests" "$output" 'Farm.Slicer.Module.Tests' || ok=1
  assert_not_contains "drops Farm.OrcaSlicer.Worker.Tests" "$output" 'Farm.OrcaSlicer.Worker.Tests' || ok=1
  # TestEmulator is a shipped backend plugin, not a test project.
  assert_contains "keeps Farm.Backend.Plugin.TestEmulator" "$output" 'Farm.Backend.Plugin.TestEmulator' || ok=1
  # Parse the count rather than substring-matching "0 excluded", which is also
  # a substring of "10 excluded".
  excluded_count="$(sed -n 's/^Wrote .*, \([0-9][0-9]*\) excluded)$/\1/p' "$tmp/log" | head -n 1)"
  assert_gt "excluded at least one project" "$excluded_count" 0 || ok=1

  rm -f "$out"
  rm -rf "$tmp"
  record "real farm-web.sln still has a tests tree to exclude" "$ok"
}

# ---------------------------------------------------------------------------
# Run.
# ---------------------------------------------------------------------------
case_excludes_tests
case_unparsable_solution
case_excludes_everything
case_missing_project_file
case_out_file_must_sit_beside_solution
case_partial_parse_miss
case_display_name_looks_like_a_path
case_real_solution

printf '\n%d passed, %d failed\n' "$PASSED" "$FAILED"
if (( FAILED > 0 )); then
  printf 'failed: %s\n' "${FAILED_NAMES[*]}"
  exit 1
fi
