#!/usr/bin/env bash
# =============================================================================
# generate-codeql-slnf.sh — emits a solution filter (.slnf) covering every
# project in src/farm-web.sln EXCEPT the test projects under src/tests/, so the
# CodeQL C# leg in .github/workflows/codeql.yml compiles production code only.
#
# Why a build filter rather than `paths-ignore`: the `paths`/`paths-ignore`
# keys in .github/codeql/codeql-config.yml only take effect for interpreted
# languages and for compiled languages analysed with `build-mode: none`. Our
# C# leg uses `build-mode: manual`, so CodeQL analyses exactly what the build
# compiles and path filters have no effect on it. The only supported way to
# keep test code out of the C# database is therefore to not compile it.
#
# This does not reduce production coverage: test projects are leaf nodes — no
# production project references one — so every project they would have pulled
# in is already built on its own.
#
# The filter is GENERATED rather than committed so projects added to the
# solution later are picked up automatically and cannot silently drop out of
# CodeQL coverage by being forgotten in a hand-maintained list.
#
# Inputs (env vars):
#   SOLUTION       Path to the .sln. Default: <repo>/src/farm-web.sln
#   OUT_FILE       Path to write the .slnf to. Must sit in the solution's own
#                  directory, because a solution filter resolves its
#                  `solution.path` relative to the filter file's location.
#                  Default: <repo>/src/farm-web.codeql.slnf
#   EXCLUDE_REGEX  ERE matched against each project path after normalising
#                  backslashes to '/'. Default: (^|/)tests/
#
# Exits non-zero when the solution cannot be parsed, when the parse looks
# incomplete, when a listed project is missing from disk, or when the exclusion
# pattern would leave nothing to build — all cases where silently producing a
# filter would quietly shrink security coverage.
# =============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

SOLUTION="${SOLUTION:-$REPO_ROOT/src/farm-web.sln}"
OUT_FILE="${OUT_FILE:-$REPO_ROOT/src/farm-web.codeql.slnf}"
EXCLUDE_REGEX="${EXCLUDE_REGEX:-(^|/)tests/}"

if [[ ! -r "$SOLUTION" ]]; then
  echo "ERROR: solution not readable: $SOLUTION" >&2
  exit 1
fi

solution_dir="$(cd "$(dirname "$SOLUTION")" && pwd)"
solution_name="$(basename "$SOLUTION")"

out_parent="$(dirname "$OUT_FILE")"
if [[ ! -d "$out_parent" ]]; then
  echo "ERROR: OUT_FILE directory does not exist: $out_parent" >&2
  exit 1
fi
out_dir="$(cd "$out_parent" && pwd)"
if [[ "$out_dir" != "$solution_dir" ]]; then
  echo "ERROR: OUT_FILE must live beside the solution ($solution_dir), got: $out_dir" >&2
  exit 1
fi

# Solution folders share the `Project(...)` line shape with real projects but
# carry the folder name in place of a path, so match on the .csproj suffix.
mapfile -t all_projects < <(
  sed -nE 's/^Project\("\{[^}]*\}"\) *= *"[^"]*", *"([^"]+\.[Cc][Ss][Pp][Rr][Oo][Jj])".*/\1/p' "$SOLUTION" | sort -u
)

if (( ${#all_projects[@]} == 0 )); then
  echo "ERROR: no .csproj entries parsed from $solution_name — solution format may have changed" >&2
  exit 1
fi

# Cross-check the parse with an independent extraction. The guard above only
# catches a TOTAL parse failure; a partial miss (say a row the sed pattern no
# longer matches after a solution-format change) would drop that project from
# the filter, and therefore from the CodeQL database, while still exiting zero.
# This pattern is deliberately looser than the one above — it tolerates leading
# whitespace and any parenthesised type field — so it still sees rows the main
# parse misses. It reads the path field specifically, not any quoted token, so
# a project whose DISPLAY NAME ends in .csproj cannot inflate the count.
declared_count="$(
  sed -nE 's/^[[:space:]]*Project\([^)]*\)[[:space:]]*=[[:space:]]*"[^"]*",[[:space:]]*"([^"]+)".*/\1/p' "$SOLUTION" |
    { grep -iE '\.csproj$' || true; } | sort -u | wc -l | tr -d '[:space:]'
)"
if (( declared_count != ${#all_projects[@]} )); then
  echo "ERROR: parsed ${#all_projects[@]} project path(s) from $solution_name but the file declares $declared_count distinct .csproj path(s); refusing to emit a filter that may omit projects from analysis" >&2
  exit 1
fi

kept=()
excluded=()
for raw in "${all_projects[@]}"; do
  normalized="${raw//\\//}"
  if [[ "$normalized" =~ $EXCLUDE_REGEX ]]; then
    excluded+=("$normalized")
    continue
  fi
  if [[ ! -f "$solution_dir/$normalized" ]]; then
    echo "ERROR: project listed in $solution_name is missing from disk: $normalized" >&2
    exit 1
  fi
  kept+=("$raw")
done

if (( ${#kept[@]} == 0 )); then
  echo "ERROR: EXCLUDE_REGEX '$EXCLUDE_REGEX' excluded every project" >&2
  exit 1
fi

if (( ${#excluded[@]} == 0 )); then
  echo "WARNING: EXCLUDE_REGEX '$EXCLUDE_REGEX' matched no projects; test code will be analysed" >&2
fi

# Project paths keep the solution's own backslash separators (MSBuild
# normalises them per platform); backslashes must be escaped for JSON.
{
  printf '{\n  "solution": {\n    "path": "%s",\n    "projects": [\n' "$solution_name"
  for i in "${!kept[@]}"; do
    escaped="${kept[$i]//\\/\\\\}"
    if (( i + 1 < ${#kept[@]} )); then
      printf '      "%s",\n' "$escaped"
    else
      printf '      "%s"\n' "$escaped"
    fi
  done
  printf '    ]\n  }\n}\n'
} > "$OUT_FILE"

echo "Wrote $OUT_FILE (${#kept[@]} project(s) kept, ${#excluded[@]} excluded)"
if (( ${#excluded[@]} > 0 )); then
  for project in "${excluded[@]}"; do
    echo "  excluded: $project"
  done
fi
