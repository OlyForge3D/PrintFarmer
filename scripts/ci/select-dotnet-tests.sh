#!/usr/bin/env bash
# =============================================================================
# select-dotnet-tests.sh — path-aware selector for CI .NET matrix
#
# Emits deterministic GITHUB_OUTPUT lines that downstream CI jobs consume to
# decide (a) whether the frontend build/test job runs, (b) whether the full
# .NET solution is compiled, (c) which .NET test projects run in a matrix,
# (d) which EF Core migration-drift context/provider pairs run, and (e) a
# human-readable reason used in job summaries.
#
# Inputs (env):
#   CHANGED_FILES_FROM_Z  path to a file containing NUL-terminated changed paths
#                         (produced by `git diff -z --no-renames --name-only`).
#                         Preferred over CHANGED_FILES.
#   CHANGED_FILES         newline-separated changed paths (fallback). Any path
#                         containing a control character, backslash, or quote
#                         forces full-safe (Git may have quoted the path).
#   EVENT_NAME            GitHub event name: pull_request | push |
#                         workflow_dispatch | (empty). Trusted-branch pushes
#                         and workflow_dispatch always force full-safe.
#   BASE_REF              base branch name for push events (e.g. main).
#   FORCE_FULL_SAFE       when non-empty, forces the full safe matrix and
#                         records the value as the reason. Used by the calling
#                         workflow when diff discovery itself failed.
#   GITHUB_OUTPUT         path to the step outputs file (required). Failure to
#                         write to it exits with rc=3 rather than silently
#                         producing no outputs.
#   TEST_MANIFEST_PATH    override path to the checked test-project manifest
#                         (default: dotnet-test-manifest.json next to this
#                         script). Used by test-select-dotnet-tests.sh and
#                         test-dotnet-test-manifest.sh to point at fixtures.
#
# Outputs (GITHUB_OUTPUT):
#   want_frontend, want_dotnet_build, want_dotnet_test, want_mig_drift
#         — string booleans "true" | "false".
#   full_matrix
#         — "true" when the full safe fallback was chosen.
#   matrix
#         — JSON object with `include` list of
#           {name, project, label, run_integration, filter} for the .NET test
#           matrix. Sharded projects contribute one entry per shard. Always
#           contains at least one element (or want_dotnet_test=false).
#   mig_matrix
#         — JSON object with `include` list of {name, project, context,
#           provider} for the migration-drift matrix. May be empty when
#           want_mig_drift=false.
#   reason
#         — sanitized human-readable string describing the decision. ASCII
#           allowlist [a-zA-Z0-9._/:[:space:]-] only; shell metacharacters are
#           stripped so downstream `run:` blocks that reference `$REASON` via
#           step env cannot be command-injected.
# =============================================================================

set -uo pipefail

SCRIPT_VERSION="1.5.0"

# ---------------------------------------------------------------------------
# Required CI test projects, loaded from the checked manifest
# scripts/ci/dotnet-test-manifest.json (issue #2031) rather than a hardcoded
# array, so the set of registered test projects has exactly one source of
# truth that scripts/ci/tests/test-dotnet-test-manifest.sh can validate for
# completeness (every `*.Tests.csproj` on disk registered exactly once).
#
# Each loaded record has five fields: selection name, unique matrix-leg name,
# project, run_integration, and filter. A project without shards contributes
# one record whose selection and leg names are identical. A sharded project
# contributes one record per shard while retaining the project name as its
# selection key, so classify_path()/main() continue selecting projects exactly
# as before. Farm.Web.IntegrationTests intentionally lives outside
# farm-web.sln and must be invoked directly with RunIntegrationTests=true
# because its csproj disables test discovery otherwise.
#
# The manifest's `shards` are expanded here into matrix legs. `pathPrefixes`
# and `dependsOnProjects` remain declarative documentation validated by
# test-dotnet-test-manifest.sh; they have no effect on classify_path()/main(),
# which continue to encode the bucket→test-selection mapping (see docs/CI.md).
# ---------------------------------------------------------------------------
readonly DEFAULT_TEST_FILTER='Category!=DbHeavy&Category!=Docker'
# Shard filters contain literal `|` operators, so the legacy pipe-delimited
# record format would corrupt them. ASCII Unit Separator is rejected inside
# every manifest field by the reader before Bash parses any record.
readonly MANIFEST_FIELD_SEPARATOR=$'\x1f'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly TEST_MANIFEST="${TEST_MANIFEST_PATH:-$SCRIPT_DIR/dotnet-test-manifest.json}"

if [[ ! -r "$TEST_MANIFEST" ]]; then
  printf 'select-dotnet-tests: manifest not readable at %s\n' "$TEST_MANIFEST" >&2
  exit 3
fi

# Prefer python3 (what ci.yml's TRX-parsing steps already require on
# ubuntu-latest runners); fall back to `python` for local dev shells. A plain
# `command -v python3` is not sufficient: on Windows, `python3` can resolve to
# a Microsoft Store execution-alias stub that exists on PATH but exits
# non-zero with no real interpreter behind it, so each candidate is probed
# with `--version` before being accepted.
PYTHON_BIN=""
for candidate in python3 python; do
  candidate_path="$(command -v "$candidate" 2>/dev/null || true)"
  if [[ -n "$candidate_path" ]] && "$candidate_path" --version >/dev/null 2>&1; then
    PYTHON_BIN="$candidate_path"
    break
  fi
done
if [[ -z "$PYTHON_BIN" ]]; then
  printf 'select-dotnet-tests: no working python3/python interpreter found to read manifest\n' >&2
  exit 3
fi

# Read the manifest via a plain command substitution (not a `< <(...)`
# process substitution): bash propagates the exit status of a `var="$(cmd)"`
# assignment to `$?`, which lets us fail closed below if the interpreter
# crashes partway through (e.g. a malformed entry raises a KeyError after
# already printing a few valid lines). A process-substitution pipeline does
# not expose that exit status at all, so a partial crash would silently
# produce a partial ALL_TEST_PROJECTS array instead of failing closed.
manifest_output="$("$PYTHON_BIN" - "$TEST_MANIFEST" <<'PYEOF'
import json
import re
import sys

with open(sys.argv[1], encoding="utf-8") as f:
    data = json.load(f)

separator = "\x1f"
safe_leg_name = re.compile(r"^[A-Za-z0-9._-]+$")
seen_leg_names = set()

def emit_record(fields):
    for field in fields:
        if not isinstance(field, str):
            raise TypeError("manifest record fields must be strings")
        if separator in field or "\n" in field or "\r" in field:
            raise ValueError("manifest record field contains a reserved separator or newline")
    print(separator.join(fields))

for entry in data["testProjects"]:
    name = entry["name"]
    base_leg = entry.get("leg") or name
    project = entry["testProject"]
    run_integration = "true" if entry.get("runIntegration") else "false"
    default_filter = entry.get("defaultFilter") or ""
    shards = entry.get("shards") or []

    if shards:
        for shard in shards:
            leg_name = f"{base_leg}-{shard['name']}"
            shard_filter = shard["filter"]
            test_filter = (
                f"({shard_filter})&({default_filter})"
                if default_filter
                else shard_filter
            )
            if not safe_leg_name.fullmatch(leg_name):
                raise ValueError(f"matrix leg name is not filesystem-safe: {leg_name!r}")
            if leg_name in seen_leg_names:
                raise ValueError(f"duplicate matrix leg name: {leg_name}")
            seen_leg_names.add(leg_name)
            emit_record((name, leg_name, project, run_integration, test_filter))
    else:
        if not safe_leg_name.fullmatch(base_leg):
            raise ValueError(f"matrix leg name is not filesystem-safe: {base_leg!r}")
        if base_leg in seen_leg_names:
            raise ValueError(f"duplicate matrix leg name: {base_leg}")
        seen_leg_names.add(base_leg)
        emit_record((name, base_leg, project, run_integration, default_filter))
PYEOF
)"
manifest_reader_rc=$?
if [[ $manifest_reader_rc -ne 0 ]]; then
  printf 'select-dotnet-tests: manifest reader failed (rc=%d) for %s -- treating as fail-closed\n' \
    "$manifest_reader_rc" "$TEST_MANIFEST" >&2
  exit 3
fi

ALL_TEST_PROJECTS=()
while IFS= read -r manifest_line; do
  # Strip a trailing CR: some local Windows Python interpreters translate
  # stdout newlines to CRLF even when the script only ever prints "\n".
  # `command -v python3` may legitimately return one of these on a dev
  # machine, so tolerate CRLF here rather than assuming LF-only output.
  manifest_line="${manifest_line%$'\r'}"
  [[ -z "$manifest_line" ]] && continue
  ALL_TEST_PROJECTS+=("$manifest_line")
done <<< "$manifest_output"
if [[ ${#ALL_TEST_PROJECTS[@]} -eq 0 ]]; then
  printf 'select-dotnet-tests: manifest at %s produced zero test projects\n' "$TEST_MANIFEST" >&2
  exit 3
fi
readonly ALL_TEST_PROJECTS

# All migration context/provider pairs (matches the ci.yml legacy drift block).
readonly ALL_MIG_ENTRIES=(
  "AppPg|AppDbContext PostgreSQL|migrations/Farm.Migrations.PostgreSQL|AppDbContext|postgres"
  "AppSqlServer|AppDbContext SQL Server|migrations/Farm.Migrations.SqlServer|AppDbContext|sqlserver"
  "SlicerPg|SlicerDbContext PostgreSQL|migrations/Farm.Slicer.Migrations.PostgreSQL|SlicerDbContext|postgres"
  "SlicerSqlServer|SlicerDbContext SQL Server|migrations/Farm.Slicer.Migrations.SqlServer|SlicerDbContext|sqlserver"
)

# ---------------------------------------------------------------------------
# Sanitize a free-form reason string: keep ASCII alphanumerics, dot, slash,
# underscore, colon, brackets, whitespace, and hyphen. Everything else — dollar,
# backtick, backslash, quotes, semicolon, ampersand, pipe, control chars — is
# stripped. Truncated to 300 chars to keep step summaries bounded.
# ---------------------------------------------------------------------------
sanitize_reason() {
  local raw="${1-}"
  local clean
  clean="$(printf '%s' "$raw" | LC_ALL=C tr -c 'A-Za-z0-9._/:[:space:]\-' ' ' | tr -s ' ')"
  clean="${clean## }"
  clean="${clean%% }"
  if (( ${#clean} > 300 )); then
    clean="${clean:0:297}..."
  fi
  printf '%s' "$clean"
}

# ---------------------------------------------------------------------------
# Write a single key=value line to $GITHUB_OUTPUT. Multi-line values use the
# heredoc form (`KEY<<DELIM ... DELIM`). Exits rc=3 if writes fail — the
# workflow depends on every output being present.
# ---------------------------------------------------------------------------
emit() {
  local key="$1"
  local value="$2"
  if [[ -z "${GITHUB_OUTPUT:-}" ]]; then
    printf 'select-dotnet-tests: GITHUB_OUTPUT is unset — cannot emit %s\n' "$key" >&2
    exit 3
  fi
  if [[ ! -w "$GITHUB_OUTPUT" ]]; then
    printf 'select-dotnet-tests: GITHUB_OUTPUT (%s) is not writable\n' "$GITHUB_OUTPUT" >&2
    exit 3
  fi
  if [[ "$value" == *$'\n'* ]]; then
    local delim
    delim="EOF_$(printf '%s%s' "$key" "$RANDOM" | tr -cd 'A-Za-z0-9')"
    {
      printf '%s<<%s\n' "$key" "$delim"
      printf '%s\n' "$value"
      printf '%s\n' "$delim"
    } >> "$GITHUB_OUTPUT" || exit 3
  else
    printf '%s=%s\n' "$key" "$value" >> "$GITHUB_OUTPUT" || exit 3
  fi
}

# ---------------------------------------------------------------------------
# Load changed files from CHANGED_FILES_FROM_Z (NUL-terminated) first, falling
# back to CHANGED_FILES (newline-separated). Returns nonzero on I/O failure.
# Sets globals CHANGED_LIST (bash array) and CHANGED_COUNT.
# ---------------------------------------------------------------------------
CHANGED_LIST=()
CHANGED_COUNT=0

check_nul_terminated() {
  local f="$1"
  # Empty file is valid (no changes).
  if [[ ! -s "$f" ]]; then
    return 0
  fi
  local last_byte
  # Pipefail is inherited. Reject if the pipeline itself failed OR if the
  # last byte is not 00 (Git NUL-terminates every record including the last).
  last_byte="$(tail -c1 "$f" | od -An -tx1 | tr -d ' \n')" || return 1
  if [[ -z "$last_byte" ]]; then
    return 1
  fi
  if [[ "$last_byte" != "00" ]]; then
    return 1
  fi
  return 0
}

load_changed_files() {
  CHANGED_LIST=()
  CHANGED_COUNT=0

  local z_file="${CHANGED_FILES_FROM_Z:-}"
  if [[ -n "$z_file" ]]; then
    if [[ ! -r "$z_file" ]]; then
      printf 'select-dotnet-tests: CHANGED_FILES_FROM_Z=%s is not readable\n' "$z_file" >&2
      return 1
    fi
    if ! check_nul_terminated "$z_file"; then
      printf 'select-dotnet-tests: CHANGED_FILES_FROM_Z=%s is not properly NUL-terminated\n' "$z_file" >&2
      return 1
    fi
    # BSD awk on macOS cannot use NUL as RS. Use bash read with -d ''.
    local entry
    while IFS= read -r -d '' entry; do
      # Empty entries can appear if the input starts with NUL; skip them.
      [[ -z "$entry" ]] && continue
      CHANGED_LIST+=("$entry")
    done < "$z_file"
    CHANGED_COUNT=${#CHANGED_LIST[@]}
    return 0
  fi

  local nl_input="${CHANGED_FILES:-}"
  if [[ -z "$nl_input" ]]; then
    CHANGED_COUNT=0
    return 0
  fi
  # Newline-separated input cannot represent paths containing newlines. Detect
  # Git-quoted paths (leading double quote) and control characters, and force
  # full-safe by returning nonzero — the caller records this as fail-safe.
  local line
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    # Reject Git-quoted paths ("path\nwith\tspecial") — caller must set
    # core.quotePath=false and switch to CHANGED_FILES_FROM_Z.
    if [[ "$line" == \"* ]]; then
      printf 'select-dotnet-tests: CHANGED_FILES contains git-quoted path; refusing (set CHANGED_FILES_FROM_Z)\n' >&2
      return 1
    fi
    # Reject embedded control characters.
    if printf '%s' "$line" | LC_ALL=C grep -q $'[\x01-\x08\x0b-\x1f\x7f]'; then
      printf 'select-dotnet-tests: CHANGED_FILES contains control character; refusing\n' >&2
      return 1
    fi
    CHANGED_LIST+=("$line")
  done <<< "$nl_input"
  CHANGED_COUNT=${#CHANGED_LIST[@]}
  return 0
}

# ---------------------------------------------------------------------------
# Classification of one path. Sets integer bitfield flags on stdout as a
# space-separated list of category tokens. We keep this out-of-band from
# the affected-tests table so callers can print human-readable reasons.
#
# Tokens (order matters only for readability):
#   shared_config   — global.json, *.sln, Directory.Build.*,
#                     Directory.Packages.props, NuGet.Config
#   ci_selector     — the narrow set of files that actually govern .NET
#                     test/build selection or repo-wide hook enforcement:
#                     .github/workflows/ci.yml, scripts/ci/select-dotnet-tests.sh,
#                     scripts/ci/compute-change-set.sh, .githooks/**. Editing
#                     any of these can silently change what gets tested for
#                     every PR, so it remains full-safe.
#   ci_other        — every other .github/workflows/**, scripts/ci/**, and
#                     .devcontainer/** path (e.g. an unrelated workflow, a
#                     script's own test file, a Dockerfile-build workflow).
#                     Inert like docs/mobile — recorded in the reason string
#                     but never forces want_dotnet_build/want_dotnet_test/
#                     want_mig_drift/want_frontend or full-safe.
#   docs            — docs/**, *.md, LICENSE, .editorconfig outside src/
#   frontend        — src/Web/**
#   api             — src/api/**
#   infra           — src/infra/** (conservatively includes App model drift)
#   backend_core    — src/backends/Farm.Backend.Plugin.Core/**
#                     (referenced by Farm.Slicer.Module in addition to the
#                     concrete plugins, so both test projects are affected).
#   backend_plugin  — every other src/backends/** path (concrete plugin
#                     projects: Moonraker, PrusaLink, OctoPrint, Sdcp,
#                     FlashForge, TestEmulator). Farm.Web.Api.Tests and
#                     Farm.Web.IntegrationTests both exercise the assembled
#                     Farm.Web.Api graph that references these.
#   slicer          — src/slicer/**, src/Slicers/**, src/worker-shared/**
#   orca_worker     — src/orcaslicer-worker/**
#   discovery       — src/discovery/**, src/printer-discovery/**
#   settings        — src/settings/**
#   modules         — src/modules/** (Farm.Modules.Abstractions — the
#                     IApiModule host-seam contract, issue #2035. Foundational
#                     like discovery/settings: Farm.Web.Api references it
#                     directly and every future Farm.Modules.* vertical slice
#                     will too, so treat any change as full-safe rather than
#                     attempting to enumerate dependents.
#   smartplug       — src/modules/Farm.Modules.SmartPlug/** (issue #2036,
#                     Phase 8: the pilot vertical-slice module carved out of
#                     Farm.Web.Api). Unlike the foundational `modules` bucket
#                     above, this is a concrete leaf module with a single
#                     dependent test project, so it gets its own narrow
#                     path-selection bucket instead of full-safe -- this is
#                     the pattern later Farm.Modules.* phases (9-18) should
#                     copy. Matched before the generic `src/modules/*` case.
#   maintenance     — src/modules/Farm.Modules.Maintenance/** (issue #2037,
#                     Phase 9: the Maintenance vertical-slice module, including
#                     the first hub -- MaintenanceHub -- to move out of the
#                     host). Same narrow-bucket treatment as smartplug above.
#                     Matched before the generic `src/modules/*` case.
#   calibration     — src/modules/Farm.Modules.Calibration/** (issue #2038,
#                     Phase 10: the calibration vertical-slice module carved
#                     out of Farm.Web.Api, following the smartplug pattern
#                     above). Matched before the generic `src/modules/*` case.
#   gcode           — src/modules/Farm.Modules.Gcode/** (issue #2039, Phase
#                     11: the gcode/file-management vertical-slice module
#                     carved out of Farm.Web.Api, following the smartplug/
#                     calibration pattern above). Matched before the generic
#                     `src/modules/*` case.
#   migrations_app  — src/migrations/Farm.Migrations.*/**
#   migrations_slcr — src/migrations/Farm.Slicer.Migrations.*/**
#   tests_api       — src/tests/Farm.Web.Api.Tests/**
#   tests_slicer    — src/tests/Farm.Slicer.Module.Tests/**
#   tests_orca      — src/tests/Farm.OrcaSlicer.Worker.Tests/**
#   tests_integration — src/tests/Farm.Web.IntegrationTests/**
#   tests_modules   — src/tests/Farm.Modules.Abstractions.Tests/**
#   tests_shared    — src/tests/Farm.Testing.Shared/** (issue #2032: shared
#                     host-independent test fixtures/HostFixture base
#                     referenced by Farm.Web.Api.Tests, Farm.Slicer.Module.Tests
#                     and Farm.Web.IntegrationTests. Foundational like
#                     Farm.Modules.Abstractions, so treated as full-safe below
#                     rather than attempting to enumerate every consumer.)
#   tests_smartplug — src/tests/Farm.Modules.SmartPlug.Tests/** (issue #2036).
#                     Matched before the generic `src/tests/*` case.
#   tests_maintenance — src/tests/Farm.Modules.Maintenance.Tests/** (issue
#                     #2037). Matched before the generic `src/tests/*` case.
#   tests_calibration — src/tests/Farm.Modules.Calibration.Tests/** (issue
#                     #2038). Matched before the generic `src/tests/*` case.
#   tests_gcode     — src/tests/Farm.Modules.Gcode.Tests/** (issue #2039).
#                     Matched before the generic `src/tests/*` case.
#   tests_other     — any other src/tests/**
#   tools           — src/tools/**
#   dotnet_config   — src/*.props, src/*.targets, src/.editorconfig
#   mobile          — mobile/** (does not force any .NET action)
#   unknown_src     — any other src/**
#   unclassified    — anything else outside the buckets above
# ---------------------------------------------------------------------------
classify_path() {
  local p="$1"
  # Reject anything that could be shell-metacharacter-injected before we act.
  # The reason sanitizer will scrub the final string; classifier itself is
  # data-only.
  case "$p" in
    # Shared configuration that affects every project.
    global.json|NuGet.Config|NuGet.config|nuget.config|Directory.Build.props|Directory.Build.targets|Directory.Packages.props)
      printf 'shared_config' ; return ;;
    */Directory.Build.props|*/Directory.Build.targets|*/Directory.Packages.props)
      printf 'shared_config' ; return ;;
    src/farm-web.sln|src/.editorconfig)
      printf 'shared_config' ; return ;;
    *.sln)
      printf 'shared_config' ; return ;;

    # CI selector proper: only files that actually govern .NET test/build
    # selection, plus repo-wide git hooks. These MUST be matched before the
    # general .github/workflows/* and scripts/ci/* patterns below, since
    # classify_path returns on first match.
    .github/workflows/ci.yml)
      printf 'ci_selector' ; return ;;
    scripts/ci/select-dotnet-tests.sh|scripts/ci/compute-change-set.sh)
      printf 'ci_selector' ; return ;;
    .githooks/*)
      printf 'ci_selector' ; return ;;

    # Every other workflow, CI script, or devcontainer path. Unrelated to
    # .NET test selection — inert like docs/mobile.
    .github/workflows/*)
      printf 'ci_other' ; return ;;
    scripts/ci/*)
      printf 'ci_other' ; return ;;
    .devcontainer/*)
      printf 'ci_other' ; return ;;

    # iOS/macOS surface — does not trigger .NET work.
    mobile/*)
      printf 'mobile' ; return ;;

    # Documentation and markdown outside src/. `LICENSE.md` is intentionally
    # not listed separately because `*.md` already covers it (ShellCheck
    # SC2221 flagged the redundancy in an earlier iteration).
    docs/*|*.md|LICENSE|.gitignore|.gitattributes|.editorconfig)
      printf 'docs' ; return ;;

    # Frontend.
    src/Web/*)
      printf 'frontend' ; return ;;
  esac

  case "$p" in
    src/api/*)               printf 'api' ; return ;;
    src/infra/*)             printf 'infra' ; return ;;
    # Farm.Backend.Plugin.Core is the shared plugin abstraction referenced by
    # BOTH `Farm.Slicer.Module` (via ../../backends/Farm.Backend.Plugin.Core)
    # and every concrete backend plugin. Because Farm.Slicer.Module.Tests
    # transitively depends on it through Farm.Slicer.Module, any Core edit
    # affects the slicer test project too. Match this bucket BEFORE the more
    # general `src/backends/*` case so concrete plugins keep their narrower
    # API-tests-only classification. See docs/CI.md for the mapping table.
    src/backends/Farm.Backend.Plugin.Core/*) printf 'backend_core' ; return ;;
    src/backends/*)          printf 'backend_plugin' ; return ;;
    src/slicer/*)            printf 'slicer' ; return ;;
    src/Slicers/*)           printf 'slicer' ; return ;;
    src/worker-shared/*)     printf 'slicer' ; return ;;
    src/orcaslicer-worker/*) printf 'orca_worker' ; return ;;
    src/discovery/*)         printf 'discovery' ; return ;;
    src/printer-discovery/*) printf 'discovery' ; return ;;
    src/settings/*)          printf 'settings' ; return ;;
    # Farm.Modules.SmartPlug is a concrete vertical-slice module (issue
    # #2036, Phase 8) with a single dependent test project, unlike the
    # foundational Farm.Modules.Abstractions host seam below -- match it
    # first so it gets its own narrow bucket instead of falling into the
    # full-safe `modules` bucket. Future Farm.Modules.* phases (9-18) should
    # add their own case here, above the generic `src/modules/*` line.
    src/modules/Farm.Modules.SmartPlug/*) printf 'smartplug' ; return ;;
    # Farm.Modules.Maintenance is a concrete vertical-slice module (issue
    # #2037, Phase 9), same rationale as Farm.Modules.SmartPlug above --
    # match it before the generic `src/modules/*` case.
    src/modules/Farm.Modules.Maintenance/*) printf 'maintenance' ; return ;;
    # Farm.Modules.Calibration is a concrete vertical-slice module (issue
    # #2038, Phase 10) following the same pattern as smartplug above --
    # matched first so it gets its own narrow bucket instead of falling into
    # the full-safe `modules` bucket.
    src/modules/Farm.Modules.Calibration/*) printf 'calibration' ; return ;;
    # Farm.Modules.Gcode is a concrete vertical-slice module (issue #2039,
    # Phase 11) following the same pattern as smartplug/calibration above --
    # matched first so it gets its own narrow bucket instead of falling into
    # the full-safe `modules` bucket.
    src/modules/Farm.Modules.Gcode/*) printf 'gcode' ; return ;;
    src/modules/*)           printf 'modules' ; return ;;
    src/migrations/Farm.Migrations.*)         printf 'migrations_app' ; return ;;
    src/migrations/Farm.Slicer.Migrations.*)  printf 'migrations_slcr' ; return ;;
    src/tests/Farm.Web.Api.Tests/*)             printf 'tests_api' ; return ;;
    src/tests/Farm.Slicer.Module.Tests/*)       printf 'tests_slicer' ; return ;;
    src/tests/Farm.OrcaSlicer.Worker.Tests/*)   printf 'tests_orca' ; return ;;
    src/tests/Farm.Web.IntegrationTests/*)      printf 'tests_integration' ; return ;;
    src/tests/Farm.Modules.Abstractions.Tests/*) printf 'tests_modules' ; return ;;
    src/tests/Farm.Testing.Shared/*)          printf 'tests_shared' ; return ;;
    src/tests/Farm.Modules.SmartPlug.Tests/*)   printf 'tests_smartplug' ; return ;;
    src/tests/Farm.Modules.Maintenance.Tests/*) printf 'tests_maintenance' ; return ;;
    src/tests/Farm.Modules.Calibration.Tests/*) printf 'tests_calibration' ; return ;;
    src/tests/Farm.Modules.Gcode.Tests/*)       printf 'tests_gcode' ; return ;;
    src/tests/*)             printf 'tests_other' ; return ;;
    src/tools/*)             printf 'tools' ; return ;;
  esac

  case "$p" in
    src/*)   printf 'unknown_src' ; return ;;
    *)       printf 'unclassified' ; return ;;
  esac
}

# ---------------------------------------------------------------------------
# Emit the final matrix and outputs, then exit 0.
# ---------------------------------------------------------------------------
finish() {
  local want_frontend="$1" want_dotnet_build="$2" want_dotnet_test="$3"
  local want_mig_drift="$4" full_matrix="$5" reason_raw="$6"
  shift 6
  # Remaining args: test project names, then a "---" separator, then mig entry names.
  # `"$@"` is safe with 0 args; we defensively guard subsequent array
  # expansions with `${arr[@]+"${arr[@]}"}` for Bash 3.2 + `set -u`
  # (macOS default), which errors on `"${empty_arr[@]}"`.
  local test_selected=() mig_selected=() sawsep=0
  local a
  for a in "$@"; do
    if [[ "$a" == "---" ]]; then sawsep=1; continue; fi
    if (( sawsep == 0 )); then
      test_selected+=("$a")
    else
      mig_selected+=("$a")
    fi
  done

  # Build test matrix JSON.
  local matrix_json='{"include":[]}'
  if (( ${#test_selected[@]} > 0 )); then
    local items="" first=1 entry selected_name
    for selected_name in "${test_selected[@]}"; do
      # A selected project may match one manifest record or several shard
      # records. Every matching record becomes its own matrix leg.
      for entry in "${ALL_TEST_PROJECTS[@]}"; do
        local entry_name leg_name project run_integration test_filter
        IFS="$MANIFEST_FIELD_SEPARATOR" read -r \
          entry_name leg_name project run_integration test_filter <<< "$entry"
        if [[ "$entry_name" != "$selected_name" ]]; then
          continue
        fi
        if [[ -z "$test_filter" ]]; then
          test_filter="$DEFAULT_TEST_FILTER"
        fi
        if (( first == 0 )); then items+=","; fi
        first=0
        items+='{"name":"'"$leg_name"'","project":"'"$project"'","label":"'"$leg_name"'","run_integration":"'"$run_integration"'","filter":"'"$test_filter"'"}'
      done
    done
    matrix_json='{"include":['"$items"']}'
  fi

  # Build mig matrix JSON.
  local mig_json='{"include":[]}'
  if (( ${#mig_selected[@]} > 0 )); then
    local items="" first=1 entry name
    for name in "${mig_selected[@]}"; do
      for entry in "${ALL_MIG_ENTRIES[@]}"; do
        IFS='|' read -r ename elabel eproject econtext eprovider <<< "$entry"
        if [[ "$ename" == "$name" ]]; then
          if (( first == 0 )); then items+=","; fi
          first=0
          items+='{"name":"'"$ename"'","label":"'"$elabel"'","project":"'"$eproject"'","context":"'"$econtext"'","provider":"'"$eprovider"'"}'
          break
        fi
      done
    done
    mig_json='{"include":['"$items"']}'
  fi

  # If want_dotnet_test=true but selection empty, that's a bug — coerce to full-safe.
  if [[ "$want_dotnet_test" == "true" && "$matrix_json" == '{"include":[]}' ]]; then
    reason_raw="internal: empty test selection with want_dotnet_test=true — coercing full safe"
    full_matrix="true"
    local items="" first=1 entry entry_name name project run_integration test_filter
    for entry in "${ALL_TEST_PROJECTS[@]}"; do
      IFS="$MANIFEST_FIELD_SEPARATOR" read -r \
        entry_name name project run_integration test_filter <<< "$entry"
      if [[ -z "$test_filter" ]]; then
        test_filter="$DEFAULT_TEST_FILTER"
      fi
      if (( first == 0 )); then items+=","; fi
      first=0
      items+='{"name":"'"$name"'","project":"'"$project"'","label":"'"$name"'","run_integration":"'"$run_integration"'","filter":"'"$test_filter"'"}'
    done
    matrix_json='{"include":['"$items"']}'
  fi

  local reason
  reason="$(sanitize_reason "$reason_raw")"

  emit "want_frontend"     "$want_frontend"
  emit "want_dotnet_build" "$want_dotnet_build"
  emit "want_dotnet_test"  "$want_dotnet_test"
  emit "want_mig_drift"    "$want_mig_drift"
  emit "full_matrix"       "$full_matrix"
  emit "matrix"            "$matrix_json"
  emit "mig_matrix"        "$mig_json"
  emit "reason"            "$reason"

  # Human-readable summary to stderr for the CI log.
  {
    printf '=== select-dotnet-tests v%s ===\n' "$SCRIPT_VERSION"
    printf 'reason:            %s\n' "$reason"
    printf 'want_frontend:     %s\n' "$want_frontend"
    printf 'want_dotnet_build: %s\n' "$want_dotnet_build"
    printf 'want_dotnet_test:  %s\n' "$want_dotnet_test"
    printf 'want_mig_drift:    %s\n' "$want_mig_drift"
    printf 'full_matrix:       %s\n' "$full_matrix"
    printf 'matrix:            %s\n' "$matrix_json"
    printf 'mig_matrix:        %s\n' "$mig_json"
  } >&2

  exit 0
}

# ---------------------------------------------------------------------------
# Produce every test project + every mig entry (used by full-safe fallback).
# `ALL_TEST_PROJECTS`/`ALL_MIG_ENTRIES` are non-empty constants but we still
# guard the `finish` invocation with `${arr[@]+…}` so a future refactor that
# empties them cannot regress into the Bash 3.2 empty-array crash path.
# ---------------------------------------------------------------------------
emit_full_safe() {
  local reason="$1"
  local all_tests=() all_migs=() entry selection_name existing duplicate
  for entry in "${ALL_TEST_PROJECTS[@]}"; do
    IFS="$MANIFEST_FIELD_SEPARATOR" read -r selection_name _ <<< "$entry"
    duplicate=0
    for existing in ${all_tests[@]+"${all_tests[@]}"}; do
      if [[ "$existing" == "$selection_name" ]]; then
        duplicate=1
        break
      fi
    done
    if (( duplicate == 0 )); then
      all_tests+=("$selection_name")
    fi
  done
  for entry in "${ALL_MIG_ENTRIES[@]}"; do all_migs+=("${entry%%|*}"); done
  finish "true" "true" "true" "true" "true" "$reason" \
    ${all_tests[@]+"${all_tests[@]}"} "---" ${all_migs[@]+"${all_migs[@]}"}
}

# ---------------------------------------------------------------------------
# Main decision logic.
# ---------------------------------------------------------------------------
main() {
  # Explicit force from caller — used when the workflow's own diff step failed.
  if [[ -n "${FORCE_FULL_SAFE:-}" ]]; then
    emit_full_safe "full-safe: caller forced (${FORCE_FULL_SAFE})"
  fi

  local event="${EVENT_NAME:-}"
  local base="${BASE_REF:-}"

  case "$event" in
    workflow_dispatch)
      emit_full_safe "full-safe: workflow_dispatch"
      ;;
    push)
      # Trusted branches always run the full safe matrix so nothing merges
      # to main/development untested.
      if [[ "$base" == "main" || "$base" == "development" ]]; then
        emit_full_safe "full-safe: trusted push to $base"
      fi
      ;;
  esac

  # Load changed files. Any I/O failure — including hostile paths — falls back
  # to full-safe rather than emitting an empty matrix.
  if ! load_changed_files; then
    emit_full_safe "full-safe: selector input load failed"
  fi

  if (( CHANGED_COUNT == 0 )); then
    # No changes detected. Safest interpretation: run nothing beyond
    # frontend=false, dotnet=false. This can happen on doc-only base-changes
    # already merged, or on synchronize events with no fresh commits.
    finish "false" "false" "false" "false" "false" \
      "no changed files detected" "---"
  fi

  # Bucket flags.
  local has_shared_config=0 has_ci_selector=0 has_frontend=0
  local has_api=0 has_infra=0 has_backend=0 has_backend_core=0 has_slicer=0
  local has_orca=0 has_discovery=0 has_settings=0 has_modules=0 has_smartplug=0
  local has_maintenance=0 has_calibration=0 has_gcode=0
  local has_mig_app=0 has_mig_slcr=0
  local has_tests_api=0 has_tests_slicer=0 has_tests_orca=0
  local has_tests_integration=0 has_tests_modules=0 has_tests_shared=0 has_tests_smartplug=0 has_tests_other=0
  local has_tests_maintenance=0 has_tests_calibration=0 has_tests_gcode=0
  local has_tools=0 has_unknown_src=0 has_docs=0 has_mobile=0 has_ci_other=0 has_other=0

  local p category
  for p in "${CHANGED_LIST[@]}"; do
    category="$(classify_path "$p")"
    case "$category" in
      shared_config)   has_shared_config=1 ;;
      ci_selector)     has_ci_selector=1 ;;
      frontend)        has_frontend=1 ;;
      api)             has_api=1 ;;
      infra)           has_infra=1 ;;
      backend_plugin)  has_backend=1 ;;
      backend_core)    has_backend_core=1 ;;
      slicer)          has_slicer=1 ;;
      orca_worker)     has_orca=1 ;;
      discovery)       has_discovery=1 ;;
      settings)        has_settings=1 ;;
      modules)         has_modules=1 ;;
      smartplug)       has_smartplug=1 ;;
      maintenance)     has_maintenance=1 ;;
      calibration)     has_calibration=1 ;;
      gcode)           has_gcode=1 ;;
      migrations_app)  has_mig_app=1 ;;
      migrations_slcr) has_mig_slcr=1 ;;
      tests_api)       has_tests_api=1 ;;
      tests_slicer)    has_tests_slicer=1 ;;
      tests_orca)      has_tests_orca=1 ;;
      tests_integration) has_tests_integration=1 ;;
      tests_modules)   has_tests_modules=1 ;;
      tests_shared)    has_tests_shared=1 ;;
      tests_smartplug) has_tests_smartplug=1 ;;
      tests_maintenance) has_tests_maintenance=1 ;;
      tests_calibration) has_tests_calibration=1 ;;
      tests_gcode)     has_tests_gcode=1 ;;
      tests_other)     has_tests_other=1 ;;
      tools)           has_tools=1 ;;
      unknown_src)     has_unknown_src=1 ;;
      docs)            has_docs=1 ;;
      mobile)          has_mobile=1 ;;
      ci_other)        has_ci_other=1 ;;
      *)               has_other=1 ;;
    esac
  done

  # Full-safe conditions (highest priority) — any of these routes to the
  # full matrix, ignoring the more granular buckets.
  if (( has_shared_config )); then
    emit_full_safe "full-safe: shared build/package/solution config changed"
  fi
  if (( has_ci_selector )); then
    emit_full_safe "full-safe: CI selector or hook changed"
  fi
  if (( has_unknown_src )); then
    emit_full_safe "full-safe: unknown src/ path (unmapped)"
  fi
  # Discovery/settings are foundational — nearly every other project transitively
  # depends on them via the plugin chain. Treat as full-safe rather than
  # attempting to enumerate.
  if (( has_discovery )); then
    emit_full_safe "full-safe: discovery framework changed"
  fi
  if (( has_settings )); then
    emit_full_safe "full-safe: settings abstractions changed"
  fi
  # Farm.Modules.Abstractions is the IApiModule host-seam contract (issue
  # #2035) — foundational like discovery/settings, so treat any change as
  # full-safe rather than attempting to enumerate dependents.
  if (( has_modules )); then
    emit_full_safe "full-safe: module host seam (Farm.Modules.Abstractions) changed"
  fi
  # tests_other = a future unmapped test project. Do not silently ignore.
  if (( has_tests_other )); then
    emit_full_safe "full-safe: unmapped test project changed"
  fi
  # tests_modules: Farm.Modules.Abstractions.Tests is not yet wired into a
  # narrower path-selection bucket (mirrors Farm.Slicer.ProfileParsing.Tests
  # and Farm.Moonraker.Emulator.Tests above) — full-safe until it is.
  if (( has_tests_modules )); then
    emit_full_safe "full-safe: Farm.Modules.Abstractions.Tests changed"
  fi
  # tests_shared: Farm.Testing.Shared (issue #2032) is the shared HostFixture
  # base + fixture library referenced by Farm.Web.Api.Tests,
  # Farm.Slicer.Module.Tests and Farm.Web.IntegrationTests. Foundational like
  # Farm.Modules.Abstractions — treat any change as full-safe rather than
  # attempting to enumerate every consuming test project.
  if (( has_tests_shared )); then
    emit_full_safe "full-safe: Farm.Testing.Shared changed"
  fi

  # From here, we're in scoped-selection territory.
  local test_names=() mig_names=()
  local want_frontend="false" want_dotnet_build="false"
  local want_dotnet_test="false" want_mig_drift="false"

  if (( has_frontend )); then
    want_frontend="true"
  fi

  # Any .NET-relevant bucket forces a full solution build to preserve compile
  # coverage across the whole graph. This is load-bearing: dotnet-test and
  # migration-drift both depend on dotnet-build and consume its artifacts, so
  # every bucket that can request either consumer must also request the build.
  if (( has_api || has_infra || has_backend || has_backend_core || has_slicer ||
        has_orca || has_smartplug || has_maintenance || has_calibration || has_gcode ||
        has_mig_app || has_mig_slcr ||
        has_tests_api || has_tests_slicer || has_tests_orca ||
        has_tests_integration || has_tests_smartplug || has_tests_maintenance || has_tests_calibration || has_tests_gcode || has_tools )); then
    want_dotnet_build="true"
  fi

  # tools alone → build only, no tests.
  local net_test_bucket_hit=0
  if (( has_api || has_infra )); then
    # api / infra sit under both tests. Both are affected.
    test_names+=("Farm.Web.Api.Tests" "Farm.Slicer.Module.Tests" "Farm.Web.IntegrationTests")
    net_test_bucket_hit=1
  fi
  if (( has_infra )); then
    # Farm.OrcaSlicer.Worker.Tests references infra through the worker graph.
    # Farm.Modules.SmartPlug (issue #2036), Farm.Modules.Maintenance (issue
    # #2037), Farm.Modules.Calibration (issue #2038), and Farm.Modules.Gcode
    # (issue #2039) also reference Farm.Infrastructure directly, so an infra
    # change must re-run all four test projects too.
    test_names+=("Farm.OrcaSlicer.Worker.Tests" "Farm.Modules.SmartPlug.Tests" "Farm.Modules.Maintenance.Tests" "Farm.Modules.Calibration.Tests" "Farm.Modules.Gcode.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_backend )); then
    # Concrete backend plugins (Moonraker/PrusaLink/OctoPrint/Sdcp/FlashForge/
    # TestEmulator) are referenced by Farm.Web.Api. IntegrationTests targets the
    # assembled API, so run it alongside Api.Tests; they are NOT referenced by
    # Farm.Slicer.Module or Farm.Slicer.Module.Tests.
    test_names+=("Farm.Web.Api.Tests" "Farm.Web.IntegrationTests")
    net_test_bucket_hit=1
  fi
  if (( has_backend_core )); then
    # Farm.Backend.Plugin.Core is referenced directly by Farm.Web.Api.Tests
    # AND transitively by Farm.Slicer.Module.Tests through Farm.Slicer.Module
    # (src/slicer/Farm.Slicer.Module/Farm.Slicer.Module.csproj declares
    # ../../backends/Farm.Backend.Plugin.Core/Farm.Backend.Plugin.Core.csproj).
    # A Core edit must therefore run both test suites.
    test_names+=("Farm.Web.Api.Tests" "Farm.Slicer.Module.Tests" "Farm.OrcaSlicer.Worker.Tests" "Farm.Web.IntegrationTests")
    net_test_bucket_hit=1
  fi
  if (( has_slicer )); then
    # slicer projects are referenced by both test suites. Farm.Modules.Calibration
    # (issue #2038) references Farm.Slicer.Module directly (slicer-host
    # calibration profile resolution), and Farm.Modules.Gcode (issue #2039)
    # references Farm.Slicer.Module/Farm.Slicer.Module.Api directly too
    # (AddSlicerModule is on for this module), so a slicer change must
    # re-run both their test projects.
    test_names+=("Farm.Web.Api.Tests" "Farm.Slicer.Module.Tests" "Farm.OrcaSlicer.Worker.Tests" "Farm.Web.IntegrationTests" "Farm.Modules.Calibration.Tests" "Farm.Modules.Gcode.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_orca )); then
    test_names+=("Farm.OrcaSlicer.Worker.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_smartplug )); then
    # Farm.Modules.SmartPlug owns AdminPowerMonitorsController, but the tests
    # that actually cover it (RouteTableSnapshotTests and
    # AdminPowerMonitorsControllerTests, a CustomWebApplicationFactory
    # integration test) intentionally stayed behind in Farm.Web.Api.Tests --
    # see docs/MODULE_MIGRATION_PATTERN.md. A controller-owning module must
    # therefore also select Farm.Web.Api.Tests, unlike a pure-service module
    # such as Farm.OrcaSlicer.Worker.
    test_names+=("Farm.Modules.SmartPlug.Tests" "Farm.Web.Api.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_maintenance )); then
    # Farm.Modules.Maintenance owns the 5 Maintenance*Controller endpoints and
    # MaintenanceHub, but several tests that cover maintenance surfaces that
    # did NOT move (MaintenanceHubAuthorizationIntegrationTests,
    # MaintenanceScheduleDeploymentToolheadScopeTests, RouteTableSnapshotTests,
    # and other CustomWebApplicationFactory-based tests) intentionally stayed
    # behind in Farm.Web.Api.Tests -- see docs/MODULE_MIGRATION_PATTERN.md. A
    # controller/hub-owning module must therefore also select
    # Farm.Web.Api.Tests, unlike a pure-service module such as
    # Farm.OrcaSlicer.Worker.
    test_names+=("Farm.Modules.Maintenance.Tests" "Farm.Web.Api.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_calibration )); then
    # Farm.Modules.Calibration (issue #2038) owns the calibration controllers,
    # but the tests that actually cover the retained contract-negotiation,
    # health-check, and route-table surface (RouteTableSnapshotTests,
    # CalibrationProfileResolutionContractTests, and friends) intentionally
    # stayed behind in Farm.Web.Api.Tests -- see docs/MODULE_MIGRATION_PATTERN.md.
    # A controller-owning module must therefore also select Farm.Web.Api.Tests,
    # unlike a pure-service module such as Farm.OrcaSlicer.Worker.
    test_names+=("Farm.Modules.Calibration.Tests" "Farm.Web.Api.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_gcode )); then
    # Farm.Modules.Gcode (issue #2039) owns the gcode/harvest/promotion
    # controllers, but RouteTableSnapshotTests -- the retained coverage of
    # its route-table surface -- intentionally stayed behind in
    # Farm.Web.Api.Tests -- see docs/MODULE_MIGRATION_PATTERN.md. A
    # controller-owning module must therefore also select Farm.Web.Api.Tests,
    # unlike a pure-service module such as Farm.OrcaSlicer.Worker.
    test_names+=("Farm.Modules.Gcode.Tests" "Farm.Web.Api.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_mig_app )); then
    # Api.Tests and IntegrationTests cover the assembled API graph that includes
    # the App migration projects.
    test_names+=("Farm.Web.Api.Tests" "Farm.Web.IntegrationTests")
    mig_names+=("AppPg" "AppSqlServer")
    want_mig_drift="true"
    net_test_bucket_hit=1
  fi
  if (( has_mig_slcr )); then
    # Farm.Web.Api references slicer migrations directly, IntegrationTests
    # targets the assembled API, and Slicer.Module.Tests covers the slicer graph.
    test_names+=("Farm.Web.Api.Tests" "Farm.Slicer.Module.Tests" "Farm.Web.IntegrationTests")
    mig_names+=("SlicerPg" "SlicerSqlServer")
    want_mig_drift="true"
    net_test_bucket_hit=1
  fi
  # Api changes also imply App migration drift (Api owns AppDbContext model).
  if (( has_api )); then
    mig_names+=("AppPg" "AppSqlServer")
    want_mig_drift="true"
  fi
  # AppDbContext, domain entities, and IEntityTypeConfiguration classes all
  # live under src/infra. Conservatively run both App providers for any infra
  # change rather than risk missing model drift when those files move.
  if (( has_infra )); then
    mig_names+=("AppPg" "AppSqlServer")
    want_mig_drift="true"
  fi
  # Slicer changes also imply Slicer migration drift (slicer owns SlicerDbContext).
  if (( has_slicer )); then
    mig_names+=("SlicerPg" "SlicerSqlServer")
    want_mig_drift="true"
  fi
  # Test-project-only edits run just that test project, but still require the
  # central build whose compiled artifact the dotnet-test job downloads.
  if (( has_tests_api )); then
    test_names+=("Farm.Web.Api.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_tests_slicer )); then
    test_names+=("Farm.Slicer.Module.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_tests_orca )); then
    test_names+=("Farm.OrcaSlicer.Worker.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_tests_integration )); then
    test_names+=("Farm.Web.IntegrationTests")
    net_test_bucket_hit=1
  fi
  if (( has_tests_smartplug )); then
    test_names+=("Farm.Modules.SmartPlug.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_tests_maintenance )); then
    test_names+=("Farm.Modules.Maintenance.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_tests_calibration )); then
    test_names+=("Farm.Modules.Calibration.Tests")
    net_test_bucket_hit=1
  fi
  if (( has_tests_gcode )); then
    test_names+=("Farm.Modules.Gcode.Tests")
    net_test_bucket_hit=1
  fi
  if (( net_test_bucket_hit )); then
    want_dotnet_test="true"
  fi

  # Reason string composition.
  local reason=""
  if (( has_frontend )); then reason+="frontend "; fi
  if (( has_api )); then reason+="api "; fi
  if (( has_infra )); then reason+="infra "; fi
  if (( has_backend )); then reason+="backend-plugin "; fi
  if (( has_backend_core )); then reason+="backend-core "; fi
  if (( has_slicer )); then reason+="slicer "; fi
  if (( has_orca )); then reason+="orcaslicer-worker "; fi
  if (( has_smartplug )); then reason+="smartplug "; fi
  if (( has_maintenance )); then reason+="maintenance "; fi
  if (( has_calibration )); then reason+="calibration "; fi
  if (( has_gcode )); then reason+="gcode "; fi
  if (( has_mig_app )); then reason+="mig-app "; fi
  if (( has_mig_slcr )); then reason+="mig-slicer "; fi
  if (( has_tests_api )); then reason+="tests-api "; fi
  if (( has_tests_slicer )); then reason+="tests-slicer "; fi
  if (( has_tests_orca )); then reason+="tests-orca "; fi
  if (( has_tests_integration )); then reason+="tests-integration "; fi
  if (( has_tests_smartplug )); then reason+="tests-smartplug "; fi
  if (( has_tests_maintenance )); then reason+="tests-maintenance "; fi
  if (( has_tests_calibration )); then reason+="tests-calibration "; fi
  if (( has_tests_gcode )); then reason+="tests-gcode "; fi
  if (( has_tools )); then reason+="tools "; fi
  if (( has_docs )); then reason+="docs "; fi
  if (( has_mobile )); then reason+="mobile "; fi
  if (( has_ci_other )); then reason+="ci-other "; fi
  if (( has_other )); then reason+="other "; fi
  reason="${reason%% }"
  if [[ -z "$reason" ]]; then
    reason="no relevant buckets"
  fi
  if [[ "$want_dotnet_test" == "false" && "$want_dotnet_build" == "true" ]]; then
    reason="scoped: $reason (build-only)"
  else
    reason="scoped: $reason"
  fi

  # Dedup test/mig names with indexed arrays so the selector remains runnable
  # under macOS's Bash 3.2 as well as CI's newer Bash. Bash 3.2 + `set -u`
  # errors on `"${empty_arr[@]}"`; the `${arr[@]+"${arr[@]}"}` form is safe
  # on the first-item iteration when `out`/`out2` are still empty.
  local -a out=()
  local nm existing duplicate
  for nm in ${test_names[@]+"${test_names[@]}"}; do
    duplicate=0
    for existing in ${out[@]+"${out[@]}"}; do
      if [[ "$existing" == "$nm" ]]; then
        duplicate=1
        break
      fi
    done
    if (( duplicate == 0 )); then
      out+=("$nm")
    fi
  done
  test_names=(${out[@]+"${out[@]}"})

  local -a out2=()
  local nm2 existing2
  for nm2 in ${mig_names[@]+"${mig_names[@]}"}; do
    duplicate=0
    for existing2 in ${out2[@]+"${out2[@]}"}; do
      if [[ "$existing2" == "$nm2" ]]; then
        duplicate=1
        break
      fi
    done
    if (( duplicate == 0 )); then
      out2+=("$nm2")
    fi
  done
  mig_names=(${out2[@]+"${out2[@]}"})

  # When nothing at all is wanted, still return a well-formed set of outputs.
  # `test_names`/`mig_names` may be empty here (e.g. pure-frontend scoped run);
  # guard both expansions so the final `finish` call is safe on Bash 3.2.
  finish "$want_frontend" "$want_dotnet_build" "$want_dotnet_test" \
         "$want_mig_drift" "false" "$reason" \
         ${test_names[@]+"${test_names[@]}"} "---" ${mig_names[@]+"${mig_names[@]}"}
}

main "$@"
