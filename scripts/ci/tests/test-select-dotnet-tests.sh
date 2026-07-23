#!/usr/bin/env bash
# =============================================================================
# test-select-dotnet-tests.sh — deterministic regression suite for the CI
# .NET affected-test selector.
#
# Each case sets CHANGED_FILES (or CHANGED_FILES_FROM_Z), EVENT_NAME, BASE_REF,
# etc., runs scripts/ci/select-dotnet-tests.sh with GITHUB_OUTPUT pointed at a
# temp file, then asserts the outputs match expectations.
#
# Exit non-zero on the first failed case (with `set -e`). Emit a compact
# PASS/FAIL line per case and a summary at the end.
# =============================================================================

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
SELECTOR="$REPO_ROOT/scripts/ci/select-dotnet-tests.sh"

if [[ ! -x "$SELECTOR" && ! -r "$SELECTOR" ]]; then
  echo "FATAL: selector not found at $SELECTOR" >&2
  exit 1
fi

PASSED=0
FAILED=0
FAILED_NAMES=()

# ---------------------------------------------------------------------------
# Helpers.
# ---------------------------------------------------------------------------

# get_output <path> <key>
get_output() {
  local out="$1" key="$2"
  # Handle heredoc form: KEY<<DELIM\n...content...\nDELIM\n
  awk -v k="$key" '
    $0 ~ "^"k"<<" {
      delim = substr($0, length(k)+3)
      capture = 1
      buf = ""
      next
    }
    capture && $0 == delim {
      capture = 0
      print buf
      exit
    }
    capture {
      if (buf != "") buf = buf "\n"
      buf = buf $0
      next
    }
    $0 ~ "^"k"=" {
      print substr($0, length(k)+2)
      exit
    }
  ' "$out"
}

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
    printf '  MISSING %s\n    needle: %q\n    haystack: %q\n' "$label" "$needle" "$haystack" >&2
    return 1
  fi
  return 0
}

# assert_not_contains <label> <haystack> <needle>
assert_not_contains() {
  local label="$1" haystack="$2" needle="$3"
  if [[ "$haystack" == *"$needle"* ]]; then
    printf '  UNEXPECTED %s\n    needle: %q\n    haystack: %q\n' "$label" "$needle" "$haystack" >&2
    return 1
  fi
  return 0
}

# assert_app_migration_drift <output path>
assert_app_migration_drift() {
  local out="$1"
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "true" || return 1
  local mig
  mig="$(get_output "$out" mig_matrix)"
  assert_contains "mig app pg" "$mig" '"name":"AppPg"' || return 1
  assert_contains "mig app sql" "$mig" '"name":"AppSqlServer"' || return 1
  assert_not_contains "no slicer mig" "$mig" '"name":"SlicerPg"' || return 1
  assert_not_contains "no slicer sql mig" "$mig" '"name":"SlicerSqlServer"' || return 1
}

# assert_full_mig_matrix_shape <label> <mig_json>
#
# For a full-safe selection, `mig_matrix` MUST contain exactly the
# four canonical context/provider entries — AppPg, AppSqlServer,
# SlicerPg, SlicerSqlServer — each appearing once, with no extras.
# This helper counts every `"name":"<expected>"` substring
# occurrence in the JSON output and rejects any count other than 1
# per expected entry AND any total-count other than 4 (which would
# indicate a stray or duplicated leg).
#
# R14 added this after Hicks flagged that the trusted-push and
# workflow_dispatch cases only asserted `full_matrix=true` and
# nothing about the shape of `mig_matrix`. A regression that
# dropped one of the four entries (e.g. missing SlicerSqlServer)
# would still pass the `full_matrix=true` assertion while silently
# leaving one provider/context pair unchecked in every full-safe
# CI run — including trusted pushes to `main`/`development`, where
# nothing is supposed to merge untested.
#
# Uses awk with `index()` to count occurrences of each substring;
# no regex, no bash 4 associative arrays (Bash 3.2 compatible).
assert_full_mig_matrix_shape() {
  local label="$1" mig="$2"
  local expected n total
  for expected in AppPg AppSqlServer SlicerPg SlicerSqlServer; do
    n="$(printf '%s' "$mig" | awk -v needle="\"name\":\"${expected}\"" '
      BEGIN { c = 0 }
      {
        s = $0
        while ((p = index(s, needle)) > 0) {
          c++
          s = substr(s, p + length(needle))
        }
      }
      END { print c+0 }
    ')"
    if [[ "$n" != "1" ]]; then
      printf '  %s: expected exactly one "%s" entry in mig_matrix, found %s\n' \
        "$label" "$expected" "$n" >&2
      printf '    mig_matrix: %q\n' "$mig" >&2
      return 1
    fi
  done
  total="$(printf '%s' "$mig" | awk '
    BEGIN { c = 0 }
    {
      s = $0
      while ((p = index(s, "\"name\":")) > 0) {
        c++
        s = substr(s, p + 7)
      }
    }
    END { print c+0 }
  ')"
  if [[ "$total" != "4" ]]; then
    printf '  %s: expected exactly 4 name entries in mig_matrix, found %s\n' \
      "$label" "$total" >&2
    printf '    mig_matrix: %q\n' "$mig" >&2
    return 1
  fi
  return 0
}

# run_case <name> <function>
run_case() {
  local name="$1" fn="$2"
  local out
  out="$(mktemp)"
  local rc=0
  if ( export GITHUB_OUTPUT="$out"; "$fn" "$out" ); then
    printf 'PASS  %s\n' "$name"
    PASSED=$((PASSED+1))
  else
    printf 'FAIL  %s\n' "$name"
    FAILED=$((FAILED+1))
    FAILED_NAMES+=("$name")
    rc=1
  fi
  rm -f "$out"
  return 0
}

# select_run [z_file]
# Runs the selector with the current CHANGED_FILES* env against GITHUB_OUTPUT.
select_run() {
  bash "$SELECTOR"
}

# =============================================================================
# Cases
# =============================================================================

case_react_only() {
  local out="$1"
  CHANGED_FILES=$'src/Web/ReactApp/src/App.tsx\nsrc/Web/ReactApp/package.json'
  EVENT_NAME="pull_request" BASE_REF="development" \
    CHANGED_FILES_FROM_Z="" FORCE_FULL_SAFE="" \
    CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_frontend" "$(get_output "$out" want_frontend)" "true" || return 1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "false" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "false" || return 1
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "false" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  assert_eq "matrix" "$(get_output "$out" matrix)" '{"include":[]}' || return 1
}

case_docs_only() {
  local out="$1"
  CHANGED_FILES=$'README.md\ndocs/CI.md'
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_frontend" "$(get_output "$out" want_frontend)" "false" || return 1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "false" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "false" || return 1
}

case_api_change() {
  local out="$1"
  CHANGED_FILES="src/api/Controllers/PrintersController.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "true" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix api" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_contains "matrix slicer" "$matrix" "Farm.Slicer.Module.Tests" || return 1
}

case_infra_change() {
  local out="$1"
  CHANGED_FILES="src/infra/Data/AppDbContext.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix api" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_contains "matrix slicer" "$matrix" "Farm.Slicer.Module.Tests" || return 1
  assert_app_migration_drift "$out" || return 1
}

case_infra_entity_change_selects_app_drift() {
  local out="$1"
  CHANGED_FILES="src/infra/Domain/Printer.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_app_migration_drift "$out"
}

case_infra_configuration_change_selects_app_drift() {
  local out="$1"
  CHANGED_FILES="src/infra/Data/Configurations/PrinterConfiguration.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_app_migration_drift "$out"
}

case_backend_plugin_change() {
  local out="$1"
  CHANGED_FILES="src/backends/Farm.Backend.Plugin.Moonraker/MoonrakerClient.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix api" "$matrix" "Farm.Web.Api.Tests" || return 1
  # Backends do not appear as a direct ProjectReference of Slicer.Module.Tests.
  assert_not_contains "matrix slicer absent" "$matrix" "Farm.Slicer.Module.Tests" || return 1
  local reason ; reason="$(get_output "$out" reason)"
  # Ensure the concrete-plugin edit produces the plugin token, not the core one.
  assert_contains "reason backend-plugin" "$reason" "backend-plugin" || return 1
  assert_not_contains "reason not backend-core" "$reason" "backend-core" || return 1
}

# Second concrete plugin — proves the split classifier is not accidentally
# specialized to Moonraker. FlashForge sits at the same directory level and
# only Api.Tests should be selected.
case_backend_plugin_change_flashforge() {
  local out="$1"
  CHANGED_FILES="src/backends/Farm.Backend.Plugin.FlashForge/FlashForgeClient.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix api" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_not_contains "matrix slicer absent" "$matrix" "Farm.Slicer.Module.Tests" || return 1
}

# Farm.Backend.Plugin.Core is the shared plugin abstraction. It is a direct
# ProjectReference of Farm.Web.Api.Tests AND a transitive dependency of
# Farm.Slicer.Module.Tests via Farm.Slicer.Module → Farm.Backend.Plugin.Core.
# A Core edit must therefore select BOTH test projects, not just Api.Tests.
# This is the r4-blocker regression Hicks flagged.
case_backend_core_change_selects_both_tests() {
  local out="$1"
  CHANGED_FILES="src/backends/Farm.Backend.Plugin.Core/IBackendPlugin.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "true" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix api" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_contains "matrix slicer" "$matrix" "Farm.Slicer.Module.Tests" || return 1
  local reason ; reason="$(get_output "$out" reason)"
  assert_contains "reason backend-core" "$reason" "backend-core" || return 1
}

# Nested path under Farm.Backend.Plugin.Core must classify the same way as
# a top-level file. Guards against a future refactor that accidentally makes
# the classifier match only one directory level.
case_backend_core_nested_path_selects_both_tests() {
  local out="$1"
  CHANGED_FILES="src/backends/Farm.Backend.Plugin.Core/Contracts/PluginRegistration.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix api nested" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_contains "matrix slicer nested" "$matrix" "Farm.Slicer.Module.Tests" || return 1
}

# Mixed edit touching Core and a concrete plugin in the same PR must still
# select both test suites (Core drives the slicer selection). Also verifies
# dedup so Api.Tests appears exactly once in the matrix.
case_backend_core_and_plugin_mixed() {
  local out="$1"
  CHANGED_FILES=$'src/backends/Farm.Backend.Plugin.Core/IBackendPlugin.cs\nsrc/backends/Farm.Backend.Plugin.Moonraker/MoonrakerClient.cs'
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix api mixed" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_contains "matrix slicer mixed" "$matrix" "Farm.Slicer.Module.Tests" || return 1
  local api_count slicer_count
  api_count="$(grep -o '"name":"Farm\.Web\.Api\.Tests"' <<< "$matrix" | wc -l | tr -d ' ')"
  slicer_count="$(grep -o '"name":"Farm\.Slicer\.Module\.Tests"' <<< "$matrix" | wc -l | tr -d ' ')"
  if [[ "$api_count" != "1" ]]; then
    printf '  api appears %s times in mixed matrix: %s\n' "$api_count" "$matrix" >&2
    return 1
  fi
  if [[ "$slicer_count" != "1" ]]; then
    printf '  slicer appears %s times in mixed matrix: %s\n' "$slicer_count" "$matrix" >&2
    return 1
  fi
  local reason ; reason="$(get_output "$out" reason)"
  assert_contains "reason includes core" "$reason" "backend-core" || return 1
  assert_contains "reason includes plugin" "$reason" "backend-plugin" || return 1
}

# Static regression: verify the classifier orders backend_core BEFORE
# backend_plugin so `case` evaluation matches Core first. If a future edit
# reorders these two patterns, Core would fall through to backend_plugin and
# silently regress to Api.Tests-only selection.
case_selector_backend_core_pattern_precedes_plugin() {
  local core_line plugin_line
  core_line="$(grep -n 'src/backends/Farm.Backend.Plugin.Core/\*)' "$SELECTOR" | head -n1 | cut -d: -f1)"
  plugin_line="$(grep -n "^[[:space:]]*src/backends/\*)" "$SELECTOR" | head -n1 | cut -d: -f1)"
  if [[ -z "$core_line" ]]; then
    printf '  selector missing src/backends/Farm.Backend.Plugin.Core/* pattern\n' >&2
    return 1
  fi
  if [[ -z "$plugin_line" ]]; then
    printf '  selector missing src/backends/* pattern\n' >&2
    return 1
  fi
  if (( core_line >= plugin_line )); then
    printf '  backend_core pattern (line %s) must precede backend_plugin pattern (line %s)\n' \
      "$core_line" "$plugin_line" >&2
    return 1
  fi
}

case_slicer_change() {
  local out="$1"
  CHANGED_FILES="src/slicer/Farm.Slicer.Module/SliceOrchestrator.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "true" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix api" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_contains "matrix slicer" "$matrix" "Farm.Slicer.Module.Tests" || return 1
  local mig ; mig="$(get_output "$out" mig_matrix)"
  assert_contains "mig slicer pg" "$mig" "SlicerPg" || return 1
  assert_contains "mig slicer sql" "$mig" "SlicerSqlServer" || return 1
}

case_orca_worker_change() {
  local out="$1"
  # orcaslicer-worker is outside farm-web.sln → full-safe.
  CHANGED_FILES="src/orcaslicer-worker/Program.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
  local reason ; reason="$(get_output "$out" reason)"
  assert_contains "reason orca" "$reason" "orcaslicer-worker" || return 1
}

case_migration_app_change() {
  local out="$1"
  CHANGED_FILES="src/migrations/Farm.Migrations.PostgreSQL/Migrations/20260101_AddThing.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "true" || return 1
  local mig ; mig="$(get_output "$out" mig_matrix)"
  assert_contains "mig app pg" "$mig" "AppPg" || return 1
  assert_contains "mig app sql" "$mig" "AppSqlServer" || return 1
  assert_not_contains "no slicer mig" "$mig" "SlicerPg" || return 1
}

case_migration_slicer_change() {
  local out="$1"
  CHANGED_FILES="src/migrations/Farm.Slicer.Migrations.SqlServer/Migrations/20260101_Change.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "true" || return 1
  local mig ; mig="$(get_output "$out" mig_matrix)"
  assert_contains "mig slicer pg" "$mig" "SlicerPg" || return 1
  assert_contains "mig slicer sql" "$mig" "SlicerSqlServer" || return 1
  assert_not_contains "no app mig" "$mig" "AppPg" || return 1
}

case_test_only_api() {
  local out="$1"
  CHANGED_FILES="src/tests/Farm.Web.Api.Tests/PrintersControllerTests.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix api" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_not_contains "no slicer" "$matrix" "Farm.Slicer.Module.Tests" || return 1
}

case_test_only_slicer() {
  local out="$1"
  CHANGED_FILES="src/tests/Farm.Slicer.Module.Tests/SlicerHostTests.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix slicer" "$matrix" "Farm.Slicer.Module.Tests" || return 1
  assert_not_contains "no api" "$matrix" "Farm.Web.Api.Tests" || return 1
}

case_tests_other_full_safe() {
  local out="$1"
  # Farm.Web.IntegrationTests / Farm.OrcaSlicer.Worker.Tests are not in the
  # sln. Any change under those paths is treated as full-safe rather than
  # silently dropped.
  CHANGED_FILES="src/tests/Farm.Web.IntegrationTests/Foo.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
}

case_unknown_src_path() {
  local out="$1"
  CHANGED_FILES="src/brand-new-thing/Program.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
  local reason ; reason="$(get_output "$out" reason)"
  assert_contains "reason unknown" "$reason" "unknown" || return 1
}

case_shared_config_change() {
  local out="$1"
  CHANGED_FILES="global.json"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
  local reason ; reason="$(get_output "$out" reason)"
  assert_contains "reason shared" "$reason" "shared" || return 1
}

case_shared_package_config_change() {
  local out="$1"
  CHANGED_FILES="Directory.Packages.props"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
}

case_ci_workflow_change() {
  local out="$1"
  CHANGED_FILES=".github/workflows/ci.yml"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
  local reason ; reason="$(get_output "$out" reason)"
  assert_contains "reason ci" "$reason" "CI selector" || return 1
}

case_hook_file_change() {
  local out="$1"
  CHANGED_FILES=".githooks/pre-push"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
}

case_ci_script_change() {
  local out="$1"
  CHANGED_FILES="scripts/ci/select-dotnet-tests.sh"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
}

case_tools_only_build_no_tests() {
  local out="$1"
  CHANGED_FILES="src/tools/AdminCli/Program.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "true" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "false" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
}

case_mobile_change_no_dotnet() {
  local out="$1"
  CHANGED_FILES="mobile/PrintFarmer/View.swift"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_frontend" "$(get_output "$out" want_frontend)" "false" || return 1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "false" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "false" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
}

case_push_to_development_full_safe() {
  local out="$1"
  CHANGED_FILES="README.md"
  EVENT_NAME="push" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
  # R14: a trusted push to `development` runs the full safe matrix,
  # and the migration-drift matrix must cover ALL four canonical
  # context/provider pairs exactly once — no duplicates, no gaps.
  # Nothing merges to development untested, so a dropped SlicerPg or
  # duplicated AppPg would silently weaken the gate.
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "true" || return 1
  local mig
  mig="$(get_output "$out" mig_matrix)"
  assert_full_mig_matrix_shape "trusted push to development" "$mig" || return 1
}

case_push_to_main_full_safe() {
  local out="$1"
  CHANGED_FILES="README.md"
  EVENT_NAME="push" BASE_REF="main" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
}

case_workflow_trusted_pushes_unfiltered() {
  local workflow="$REPO_ROOT/.github/workflows/ci.yml"
  extract_event_block() {
    local event="$1"
    # NOTE: Strip any trailing CR before pattern matching so a Windows-checkout
    # (`core.autocrlf=true`) worktree, where `ci.yml` may have CRLF line
    # endings, is compared byte-for-byte the same as a Linux-style checkout.
    # Without this a BSD/POSIX awk on macOS or an awk that does not silently
    # trim `\r` in text mode would fail the exact-match on `$0 == marker`.
    awk -v marker="  ${event}:" '
      { sub(/\r$/, "") }
      $0 == marker { inside = 1; next }
      inside && (/^[^ ]/ || /^  [A-Za-z_][A-Za-z0-9_-]*:/) { exit }
      inside { print }
    ' "$workflow"
  }

  local push_block pull_block
  push_block="$(extract_event_block push)"
  pull_block="$(extract_event_block pull_request)"
  assert_contains "push branches" "$push_block" "branches: [main, development]" || return 1
  if printf '%s\n%s\n' "$push_block" "$pull_block" \
      | grep -Eq '^[[:space:]]+(paths|paths-ignore):'; then
    printf '  push/pull_request workflow events must not define path filters\n' >&2
    return 1
  fi
}

case_workflow_dispatch_full_safe() {
  local out="$1"
  CHANGED_FILES=""
  EVENT_NAME="workflow_dispatch" BASE_REF="" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
  # R14: manual workflow_dispatch runs the full safe matrix, and
  # the migration-drift matrix must contain ALL four canonical
  # context/provider pairs exactly once. A regression that dropped
  # or duplicated an entry from `emit_full_safe`'s enumeration of
  # `ALL_MIG_ENTRIES` would silently weaken the gate for every
  # manual re-run of the workflow (a common oncall / release path).
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "true" || return 1
  local mig
  mig="$(get_output "$out" mig_matrix)"
  assert_full_mig_matrix_shape "workflow_dispatch full-safe" "$mig" || return 1
}

case_force_full_safe_from_caller() {
  local out="$1"
  CHANGED_FILES="src/api/Foo.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="diff-failed" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
  local reason ; reason="$(get_output "$out" reason)"
  assert_contains "reason caller" "$reason" "caller forced" || return 1
}

case_empty_changes() {
  local out="$1"
  CHANGED_FILES=""
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="" \
    select_run >/dev/null 2>&1
  # No changes → nothing wanted, not full-safe.
  assert_eq "want_frontend" "$(get_output "$out" want_frontend)" "false" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "false" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
}

case_missing_github_output() {
  # Should exit rc=3 (with 'select-dotnet-tests: GITHUB_OUTPUT is unset').
  # Note: we intentionally do NOT set GITHUB_OUTPUT here; must clear the
  # helper's pre-set value.
  local rc=0
  CHANGED_FILES="src/api/Foo.cs" \
    EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" \
    env -u GITHUB_OUTPUT bash "$SELECTOR" >/dev/null 2>&1 || rc=$?
  if (( rc != 3 )); then
    printf '  expected rc=3, got %d\n' "$rc" >&2
    return 1
  fi
}

case_z_file_with_null_terminators() {
  local out="$1"
  local zfile ; zfile="$(mktemp)"
  printf 'src/api/Foo.cs\0src/backends/Farm.Backend.Plugin.Moonraker/Bar.cs\0' > "$zfile"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="$zfile" CHANGED_FILES="" \
    select_run >/dev/null 2>&1
  local rc=$?
  rm -f "$zfile"
  (( rc == 0 )) || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
}

case_z_file_not_terminated_forces_full_safe() {
  local out="$1"
  local zfile ; zfile="$(mktemp)"
  # No trailing NUL — must fail-safe.
  printf 'src/api/Foo.cs' > "$zfile"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="$zfile" CHANGED_FILES="" \
    select_run >/dev/null 2>&1
  local rc=$?
  rm -f "$zfile"
  (( rc == 0 )) || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
}

case_git_quoted_path_forces_full_safe() {
  local out="$1"
  # Newline-form input starting with double-quote → Git-quoted path.
  CHANGED_FILES=$'"src/api/weird\\nname.cs"'
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
}

case_hostile_metachar_in_reason_stripped() {
  local out="$1"
  # The reason string routes through sanitize_reason. If a path can inject
  # metacharacters into the reason, the sanitizer must strip them.
  # In practice, our reason is built from bucket names and never from raw
  # paths, so this case verifies the sanitizer itself works.
  CHANGED_FILES="src/api/Foo.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE='$(rm -rf /)`whoami`;evil|pipe&amp;bg' \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  local reason ; reason="$(get_output "$out" reason)"
  # None of these characters should appear in the sanitized reason.
  for bad in '$' '`' ';' '|' '&' '(' ')'; do
    if [[ "$reason" == *"$bad"* ]]; then
      printf '  reason contains %q: %q\n' "$bad" "$reason" >&2
      return 1
    fi
  done
}

case_multi_bucket_dedup() {
  local out="$1"
  # Both api and infra select the same tests — matrix must dedup.
  CHANGED_FILES=$'src/api/A.cs\nsrc/infra/B.cs\nsrc/slicer/Farm.Slicer.Module/C.cs'
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  local matrix ; matrix="$(get_output "$out" matrix)"
  local api_count slicer_count
  api_count="$(grep -o '"name":"Farm\.Web\.Api\.Tests"' <<< "$matrix" | wc -l | tr -d ' ')"
  slicer_count="$(grep -o '"name":"Farm\.Slicer\.Module\.Tests"' <<< "$matrix" | wc -l | tr -d ' ')"
  # Each name should appear exactly once as `"name":"..."` inside the JSON.
  if [[ "$api_count" != "1" ]]; then
    printf '  api appears %s times in matrix (expected 1): %s\n' "$api_count" "$matrix" >&2
    return 1
  fi
  if [[ "$slicer_count" != "1" ]]; then
    printf '  slicer appears %s times in matrix (expected 1): %s\n' "$slicer_count" "$matrix" >&2
    return 1
  fi
}

case_devcontainer_change_full_safe() {
  local out="$1"
  CHANGED_FILES=".devcontainer/devcontainer.json"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
}

case_discovery_full_safe() {
  local out="$1"
  CHANGED_FILES="src/discovery/DiscoveryHost.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
}

case_settings_full_safe() {
  local out="$1"
  CHANGED_FILES="src/settings/Farm.Settings.Abstractions/ISettings.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
}

case_mixed_react_and_dotnet() {
  local out="$1"
  CHANGED_FILES=$'src/Web/ReactApp/src/App.tsx\nsrc/api/Foo.cs'
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_frontend" "$(get_output "$out" want_frontend)" "true" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
}

case_selector_uses_bash32_compatible_dedup() {
  if grep -Eq '(^|[[:space:]])(local|declare)[[:space:]]+-A([[:space:]]|$)' "$SELECTOR"; then
    printf '  selector must not use Bash 4 associative arrays\n' >&2
    return 1
  fi
}

# Static regression (Vasquez blocker): the selector must never expand its
# dedup output arrays (`out`, `out2`) or its `finish`-args arrays (`test_names`,
# `mig_names`, `all_tests`, `all_migs`) with a bare `"${arr[@]}"` — Bash 3.2
# (macOS default) + `set -u` crashes on empty-array expansion. Every such
# expansion must use the `${arr[@]+"${arr[@]}"}` guard.
case_selector_dedup_safe_for_empty_arrays() {
  local names='out|out2|test_names|mig_names|all_tests|all_migs'
  # Find any "${name[@]}" occurrence and filter out the safe `+` form.
  # Use `[+]` (literal `+` inside a bracket expression) rather than `\+`
  # because POSIX ERE leaves `\+` undefined and BSD grep on macOS may treat
  # it as "one-or-more" or as an error rather than a literal plus.
  local unsafe
  unsafe="$(grep -nE "\"\\\$\\{($names)\\[@\\]\\}\"" "$SELECTOR" \
    | grep -vE "\\\$\\{($names)\\[@\\][+]" \
    || true)"
  if [[ -n "$unsafe" ]]; then
    printf '  selector has unguarded empty-array expansions:\n%s\n' "$unsafe" >&2
    return 1
  fi
  # Positive assertion — the guard idiom must actually appear, otherwise a
  # future refactor that removes the arrays entirely would silently pass.
  if ! grep -qE "\\\$\\{($names)\\[@\\][+]\"\\\$\\{($names)\\[@\\]\\}\"\\}" "$SELECTOR"; then
    printf '  selector is missing the Bash 3.2 empty-array guard idiom\n' >&2
    return 1
  fi
}

# Static regression: the `finish` function itself must tolerate being called
# with zero trailing test/mig arguments. `"$@"` is safe with 0 args, but the
# derived `test_selected` / `mig_selected` arrays must be built without a
# bare `"${empty_arr[@]}"` anywhere in the function body.
case_selector_finish_tolerates_empty_args() {
  local body
  # Strip any trailing CR from selector source lines defensively (the file is
  # gitattribute-pinned to LF today, but future changes must not break this
  # extractor if the working tree ever picks up CRLF).
  body="$(awk '{ sub(/\r$/, "") } /^finish\(\)[[:space:]]*\{/{f=1} f{print} f && /^\}[[:space:]]*$/{exit}' "$SELECTOR")"
  if [[ -z "$body" ]]; then
    printf '  could not locate finish() body\n' >&2
    return 1
  fi
  # test_selected / mig_selected must only be expanded through length checks
  # (`${#arr[@]}`) or through the `for x in "${arr[@]}"` loop, which Bash 3.2
  # tolerates when the array was declared with `arr=()`. The dangerous pattern
  # is a bare expansion in argument position — flag any such use.
  local unsafe
  unsafe="$(printf '%s\n' "$body" \
    | grep -nE '"\$\{(test_selected|mig_selected)\[@\]\}"' \
    | grep -vE 'for [a-z0-9_]+ in "\$\{(test_selected|mig_selected)\[@\]\}"' \
    || true)"
  if [[ -n "$unsafe" ]]; then
    printf '  finish() has unsafe empty-array expansion:\n%s\n' "$unsafe" >&2
    return 1
  fi
}

# =============================================================================
# Portability regressions specific to this Windows-worktree revision (#772).
# =============================================================================

# extract_event_block, when applied to a workflow file that carries CRLF line
# endings (a Windows worktree with `core.autocrlf=true` will produce this),
# must still exact-match the event marker line. This regression case builds a
# synthetic CRLF YAML fixture in a temp file, runs the same awk pattern used
# by case_workflow_trusted_pushes_unfiltered, and asserts the returned block
# contains the expected content and none of the sibling events.
case_extract_event_block_crlf_tolerant() {
  local fixture
  fixture="$(mktemp)"
  # NOTE: emit CRLF explicitly with `\r\n` inside printf. Do not rely on the
  # host runtime translating line endings for us.
  printf 'name: CI\r\non:\r\n  workflow_dispatch:\r\n  push:\r\n    branches: [main, development]\r\n  pull_request:\r\n    types: [opened, synchronize, reopened]\r\njobs:\r\n  select:\r\n    runs-on: ubuntu-latest\r\n' > "$fixture"

  local event="push"
  local block
  block="$(awk -v marker="  ${event}:" '
    { sub(/\r$/, "") }
    $0 == marker { inside = 1; next }
    inside && (/^[^ ]/ || /^  [A-Za-z_][A-Za-z0-9_-]*:/) { exit }
    inside { print }
  ' "$fixture")"
  rm -f -- "$fixture"

  assert_contains "crlf push branches" "$block" "branches: [main, development]" || return 1
  # The pull_request marker line and the top-level jobs: line must NOT appear
  # inside the extracted push: block. This proves the awk actually terminates
  # at the next sibling event under CRLF input.
  assert_not_contains "crlf push isolated" "$block" "pull_request:" || return 1
  assert_not_contains "crlf push isolated" "$block" "jobs:" || return 1
}

# Every `printf` in .github/workflows/ci.yml whose format string begins with a
# literal `-` (a Markdown list bullet) must be preceded by the POSIX `--`
# end-of-options marker. Otherwise Bash's `printf` builtin can, under `set -e`
# and certain builds, treat the format string as an option. This test looks at
# each `printf` line and asserts the leading-dash format is guarded.
case_workflow_publish_printf_option_safe() {
  local workflow="$REPO_ROOT/.github/workflows/ci.yml"
  # Collect any offending line: format string that starts with `-` (a bullet)
  # but is NOT the `-- ` option-terminator form. `printf -- '- ...` is safe;
  # `printf '- ...` is not.
  local offenders
  offenders="$(grep -nE "printf +'-[^-]" "$workflow" || true)"
  if [[ -n "$offenders" ]]; then
    printf '  workflow has unsafe leading-dash printf format strings:\n%s\n' "$offenders" >&2
    return 1
  fi
  # Positive assertion — the `printf -- '- ...` idiom must actually appear so
  # a future refactor that removes the bullet lines entirely does not silently
  # pass this test (belt-and-braces).
  if ! grep -qE "printf -- '- reason: " "$workflow"; then
    printf '  workflow is missing the printf -- option-terminator idiom\n' >&2
    return 1
  fi
}

# extract_drift_run_body <workflow>
#
# Emit (to stdout) the exact `run: |` script body for the step named
# `Check EF Core migration drift`, with each line dedented by the block's
# base indent and trailing CRs stripped.
#
# This helper is retained as a lower-level primitive after R12 promoted
# the primary shape gate to `extract_drift_step_block` (which captures
# the FULL step yaml, not just its shell body). The run-body extractor
# still backs targeted robustness tests
# (`case_drift_run_body_extractor_*`) that pin the awk state machine's
# bail behaviour on zero-indent and tab-indented input — regressions
# there would otherwise surface as opaque snapshot mismatches in the
# step-block gate rather than as a targeted extractor failure.
#
# Portability & shape:
#   * POSIX awk only — no gawk extensions, no PCRE, no arrays keyed by
#     regex. Runs under BusyBox awk, mawk, gawk, and BSD awk (macOS).
#   * Bash 3.2 safe — the caller uses only POSIX-`local` and string
#     comparison; no `mapfile`, no associative arrays.
#   * CRLF-tolerant — `sub(/\r$/, "")` on every input line, same idiom
#     proved correct by `case_extract_event_block_crlf_tolerant` and
#     exercised again by `case_drift_run_body_extractor_crlf_tolerant`
#     against a synthetic CRLF fixture.
#   * Base indent is derived from the first non-blank content line of
#     the `run: |` block, so a future reformat that changes the block's
#     indentation does not fail the assertion for the wrong reason.
#   * The block terminates at the first non-blank line whose indent is
#     less than the base indent — i.e. the next step's key, `env:`, or
#     the next `- name:` header.
#
# R12 bail-safety fix: POSIX awk sets `RLENGTH = -1` when `match()` finds
# no match. The pre-R12 body used `match($0, /^ +/)` (one-or-more spaces)
# on the base-indent probe. If the first non-blank line inside the
# `run: |` block had zero leading spaces (a malformed workflow, or a
# hostile mutation), `RLENGTH` became -1, `base_indent` became -1, the
# `base_indent == 0` guard was skipped, and the subsequent per-line
# `RLENGTH < base_indent` compare (`0 < -1` is false) also refused to
# bail — so the extractor happily consumed every remaining line in the
# file. R12 uses `/^ */` (zero-or-more, always matches, RLENGTH >= 0)
# and a `base_indent <= 0` bail guard. Robustness pinned by
# `case_drift_run_body_extractor_bails_on_zero_indent` and
# `case_drift_run_body_extractor_bails_on_tab_indent`.
extract_drift_run_body() {
  local workflow="$1"
  awk '
    { sub(/\r$/, "") }
    state == 0 && $0 == "      - name: Check EF Core migration drift" {
      state = 1
      next
    }
    # Bail if we hit the next step header before finding `run: |`.
    state == 1 && /^      - name:/ { exit }
    state == 1 && $0 == "        run: |" {
      state = 2
      base_indent = 0
      next
    }
    state == 2 {
      # Blank lines are part of the block; emit them verbatim.
      if ($0 ~ /^[[:space:]]*$/) { print ""; next }
      # First non-blank line sets the base indent for the whole block.
      # `/^ */` (zero-or-more) always matches on POSIX awk, so RLENGTH is
      # always >= 0; `/^ +/` (one-or-more) sets RLENGTH = -1 on no match
      # and would fail the `== 0` guard, letting the extractor run away.
      if (base_indent == 0) {
        match($0, /^ */)
        base_indent = RLENGTH
        # `<= 0` covers both "no leading space" (base_indent == 0) and
        # any theoretical negative RLENGTH from a non-conformant awk.
        if (base_indent <= 0) { exit }
      }
      # Current line indent (0 if the line starts with a non-space char).
      match($0, /^ */)
      if (RLENGTH < base_indent) { exit }
      print substr($0, base_indent + 1)
    }
  ' "$workflow"
}

# extract_drift_step_block <workflow>
#
# Emit (to stdout) the full yaml text of the step named
# `Check EF Core migration drift` — including its `working-directory`,
# `env:`, comment block, and `run: |` body — from the step's `- name:`
# header through the line immediately before the next sibling step, the
# next job header, or the next top-level key. Trailing blank lines that
# separate the drift step from the following section are elided so the
# snapshot is stable under whitespace-only edits below the step.
#
# Rationale (R12): the R11 gate compared only the shell body of the
# drift step against a canonical snapshot. That gate is silent about
# yaml-level control flow injected as sibling keys of `run:` — a mutant
# that adds `continue-on-error: true` or `if: false` to the step yaml
# would leave the shell body byte-identical and slip through. This
# extractor captures the entire step block so the snapshot gate can
# assert full yaml shape, not just shell shape.
#
# Termination rules (executed against CRLF-stripped input):
#   * `^      - ` at the start of a line marks the NEXT step at the
#     same 6-space step indent — bail before emitting.
#   * Any non-blank line whose leading-space count is less than 6 means
#     we have left the enclosing `steps:` list (`  # section comment`
#     at 2-space indent, next job header at 2-space indent, top-level
#     key at column 0) — bail.
#   * Blank lines are buffered and only flushed when a subsequent
#     in-block line is emitted, so trailing whitespace below the step
#     never leaks into the snapshot.
extract_drift_step_block() {
  local workflow="$1"
  awk '
    { sub(/\r$/, "") }
    state == 0 && $0 == "      - name: Check EF Core migration drift" {
      state = 1
      print
      next
    }
    state == 1 && /^[[:space:]]*$/ {
      pending_blanks++
      next
    }
    state == 1 && /^      - / { exit }
    state == 1 {
      match($0, /^ */)
      if (RLENGTH < 6) { exit }
      while (pending_blanks > 0) { print ""; pending_blanks-- }
      print
    }
  ' "$workflow"
}

# extract_job_block <workflow> <job>
#
# Emit (to stdout) the yaml body of the named job (everything under
# `  <job>:` at 2-space indent, up to the next sibling job or the next
# top-level key). Strips trailing CRs so a Windows-checkout worktree
# compares byte-for-byte with a Linux-style checkout. Extracted so both
# the migration-drift shape gate and the R12/R13/R14 adversarial mutation
# tests can share one job-block reader instead of redefining it locally.
#
# R14 broadens the sibling-job terminator from the identifier-only regex
# `/^  [A-Za-z_][A-Za-z0-9_-]*:/` — which only matched unquoted keys —
# to any exactly-two-space non-comment content line. Quoted job keys
# (`  "shadow":`, `  'shadow':`) and keys with pre-colon whitespace
# (`  shadow :`) all match the broader terminator, so a shadow job
# appended in ANY of those spellings will terminate block extraction
# right at its own header and never leak its body into the real job's
# block. Comments (`  # ...`) and blank lines continue to be part of
# the block body (workflow files legitimately embed both). The
# terminator only fires on 2-space indent because deeper indents are
# job-internal content, and 0-space is the outer `jobs:`-sibling case
# already covered by `/^[^ ]/`.
extract_job_block() {
  local workflow="$1" job="$2"
  awk -v marker="  ${job}:" '
    { sub(/\r$/, "") }
    $0 == marker { inside = 1; next }
    inside && /^[^ ]/ { exit }
    inside && /^  [^ #]/ { exit }
    inside { print }
  ' "$workflow"
}

# _count_yaml_key_at <text> <indent_spaces> <key>
#
# Emit (to stdout) the count of lines in <text> that carry the yaml
# key <key> at exactly <indent_spaces> leading spaces, ACROSS every
# spelling GitHub Actions' yaml parser accepts as the same key:
#
#   * unquoted:      `<indent><key>:`
#   * double-quoted: `<indent>"<key>":`
#   * single-quoted: `<indent>'<key>':`
#
# In all three forms, arbitrary POSIX whitespace between the closing
# key token and the `:` is legal yaml and is counted here (e.g.
# `<indent><key> :`, `<indent><key>   :`). Trailing CRs on <text> are
# stripped so a Windows-checkout worktree returns the same count as
# a Linux-style checkout.
#
# Written so `_check_drift_step_shape` can count every alternate
# spelling of a singleton key (migration-drift, strategy, matrix,
# fail-fast, if, continue-on-error) and reject any count other than
# the expected one. R13's `grep -Ec '^      matrix:'` and equivalent
# checks only counted the unquoted spelling — a hostile rewrite that
# used `      "matrix":` alongside the canonical unquoted `matrix:`
# would leave the R13 count at 1 (canonical) and the grep-qxF exact-
# line assertion would still tick green because the canonical line
# was still present, while the quoted duplicate silently overrode it
# in the yaml parser's view. R14 uses this helper for every singleton
# invariant so no spelling can bypass the count.
#
# Bash 3.2 / POSIX awk only; no bash regex or process substitution.
_count_yaml_key_at() {
  local text="$1" ind="$2" key="$3"
  local prefix
  prefix="$(printf '%*s' "$ind" '')"
  # Assemble the three regex alternates as awk vars so the awk program
  # body itself stays single-quoted and free of shell substitution.
  # `key` values used by callers (migration-drift, strategy, matrix,
  # fail-fast, continue-on-error, if) contain only letters and `-`;
  # `-` is literal outside character classes in POSIX ERE so no
  # additional escaping is needed. Any future key with regex specials
  # (`.`, `*`, `+`, `[`, `(`, `\`) would need escaping added here.
  printf '%s\n' "$text" | awk \
    -v pat_u="^${prefix}${key}[[:space:]]*:" \
    -v pat_d="^${prefix}\"${key}\"[[:space:]]*:" \
    -v pat_s="^${prefix}'${key}'[[:space:]]*:" '
    { sub(/\r$/, "") }
    $0 ~ pat_u || $0 ~ pat_d || $0 ~ pat_s { n++ }
    END { print n+0 }
  '
}

# _assert_no_quoted_key_at_indents <text> <label> <indent1> [<indent2> ...]
#
# R15 shape invariant: return 0 iff no line in <text> starts with
# exactly one of the given <indentN> leading-space counts followed by
# a quote character (`"` or `'`) that opens a KEY token (i.e. the
# quoted string is closed and followed by `:`, optionally with
# intervening whitespace). Return 1 with a diagnostic on stderr on
# the first line that matches at any of the requested indents.
#
# Motivation (R14 bypass Newt is closing):
#   R14's `_count_yaml_key_at` counts spellings by matching the
#   LITERAL characters between the quotes:
#
#     ^  "migration-drift"[[:space:]]*:
#
#   Under YAML, double-quoted strings support escape sequences
#   (`\x66` -> `f`, `\x78` -> `x`, `\x2d` -> `-`, ...). A hostile
#   rewrite that renames the shadow key using an escape-encoded
#   spelling like
#
#     "matri\x78":              # decodes to `matrix`
#     "i\x66": false            # decodes to `if`
#     "migration\x2ddrift":     # decodes to `migration-drift`
#
#   is byte-for-byte NOT the string `matrix` / `if` / `migration-
#   drift` in the source text, so R14's literal-string counters
#   stay at 1 (the canonical unquoted line still passes) while a
#   real YAML parser (GitHub Actions' psych, PyYAML 6.x, etc.)
#   resolves the escaped key to the same canonical name and
#   silently overrides the canonical entry under duplicate-key
#   last-wins semantics.
#
#   Because this workflow intentionally uses ONLY canonical unquoted
#   keys at the protected scopes (2-space job headers; 4-space job
#   keys; 6-space strategy children; 8-space step-item keys), a
#   zero-tolerance invariant that rejects the ENTIRE class of quoted
#   keys — regardless of what characters or escape sequences the
#   quoted content decodes to — closes the bypass without needing
#   the guard to know every YAML escape form. This is deliberately
#   conservative: if a future maintainer needs a quoted key for a
#   legitimate reason (unusual characters, YAML 1.1 reserved word),
#   they must update this guard AND update the reviewer-facing
#   canonical-line assertions in `_check_drift_step_shape`.
#
# The pattern matches only lines whose FIRST non-space character at
# column <ind>+1 is a quote AND the quoted string closes and is
# followed by `:`, so quoted VALUES that appear after an unquoted
# key on the same line (e.g. `      matrix: "${{ ... }}"`) are NOT
# rejected — the leading token at column <ind>+1 in that case is
# `m` (unquoted), not `"`. Only the leading token is examined.
#
# Bash 3.2 / POSIX awk only; no bash regex or process substitution.
_assert_no_quoted_key_at_indents() {
  local text="$1" label="$2"
  shift 2
  local ind hit
  for ind in "$@"; do
    local prefix
    prefix="$(printf '%*s' "$ind" '')"
    # Match: <indent><quote><any-non-quote>*<quote><ws>*:
    # We forbid the same quote char re-appearing inside the string,
    # which loses coverage of quoted keys containing escaped-quote
    # sequences (`"a\"b":`). This is acceptable because the intent
    # of R15 is to reject ANY quoted key at these indents, and a
    # key containing an escaped quote is even further outside the
    # canonical-unquoted contract than the escape-encoded aliases
    # this guard exists to catch — the caller's `_count_yaml_key_
    # at` singleton counters would still reject any surviving
    # canonical key that ended up counted twice, and the R14
    # canonical-line assertions still require the exact unquoted
    # form. Two quote alternates are checked in a single awk pass.
    hit="$(printf '%s\n' "$text" | awk \
      -v pat_d="^${prefix}\"[^\"]*\"[[:space:]]*:" \
      -v pat_s="^${prefix}'[^']*'[[:space:]]*:" '
      { sub(/\r$/, "") }
      $0 ~ pat_d || $0 ~ pat_s { print; exit }
    ')"
    if [[ -n "$hit" ]]; then
      printf '  %s: quoted YAML key not allowed at %d-space indent (found: %s)\n' \
        "$label" "$ind" "$hit" >&2
      return 1
    fi
  done
  return 0
}

# _check_drift_step_shape <workflow>
#
# Composite shape gate for the `Check EF Core migration drift` step in
# the supplied workflow file. Returns 0 iff every invariant holds and
# non-zero (with a diagnostic on stderr) if any of them fails. Written
# so `case_workflow_migration_drift_restores_before_ef` can call it on
# the real workflow and the R12/R13 adversarial mutation cases can call
# it on a mutated copy — the same guard must reject every mutation.
#
# Invariants (checked in order; return-on-first-failure):
#   * R13: Exactly one `  migration-drift:` job header at 2-space job
#     indent. Zero means the job was renamed/deleted; more than one
#     means a shadow job was added (a bypass shape where the second
#     job is what the summary gate actually reads, but the first is
#     what the shape check inspects via `extract_job_block`'s
#     first-match semantics). CRLF-tolerant awk count.
#   * Exactly one `- name: Check EF Core migration drift` step in the
#     whole workflow. Zero means the step was renamed or deleted; more
#     than one means a duplicate was added (a common bypass shape: a
#     shadow step with `if: false` next to the real one).
#   * The enclosing `migration-drift` job block exists and does NOT
#     carry a job-level `continue-on-error:` key. GitHub Actions
#     swallows step failures when the job continues on error, so a
#     drift check that fails inside would still report the job as
#     success and the summary gate `check_conditional migration-drift`
#     would tick green.
#   * R13: Exactly one `      matrix:` line at 6-space strategy-child
#     indent, and that line's value is verbatim
#     `${{ fromJson(needs.select.outputs.mig_matrix) }}`. Blocks any
#     mutation that swaps the selector-driven matrix for a hard-coded
#     `{include: [{...}]}` (inline or block-scalar form) — a hostile
#     rewrite that runs the drift job against a single hand-picked
#     provider/context pair would leave three of four EF pairs
#     unchecked while still reporting `migration-drift` as passing.
#   * R13: `strategy.fail-fast` is pinned to `false`. GitHub's default
#     for matrix `fail-fast` is `true`, which cancels sibling matrix
#     legs the moment one leg fails. For drift that is actively
#     harmful: a build/context error in one leg (e.g. AppPg) would
#     cancel the sibling legs before they could report their own
#     drift, and the reviewer would see a single failure that hides
#     several. Pin `fail-fast: false` verbatim.
#   * The `migration-drift` job's `if:` clause is the expected
#     selection expression (`needs.select.outputs.want_mig_drift ==
#     'true'`) — not something like `if: false` that would skip the
#     job unconditionally and defeat fail-closed semantics.
#   * The full step yaml (extracted by `extract_drift_step_block`)
#     matches the canonical snapshot embedded here byte-for-byte. Any
#     added, removed, or edited line inside the step — including a
#     sibling `continue-on-error: true`, `if: false`, altered
#     `working-directory`, altered env block, or edited shell body —
#     trips this diff. Reviewers who deliberately change the step must
#     also update the heredoc.
_check_drift_step_shape() {
  local workflow="$1"

  # R13/R14: Exactly one migration-drift job header at 2-space indent.
  # `extract_job_block` has first-match semantics — a duplicate job
  # header would let a shadow job impersonate the real one under the
  # summary gate's `needs.migration-drift.result` read, while this
  # shape guard inspected only the first block. R14 counts every yaml
  # spelling (unquoted / double-quoted / single-quoted, with optional
  # whitespace before the colon) so a shadow header written as
  # `  "migration-drift":` or `  'migration-drift' :` cannot slip past
  # the R13 exact-string count.
  #
  # `workflow_body` is CR-normalised so downstream `grep -qxF`
  # canonical-line checks compare byte-for-byte identically on a
  # Windows-checkout worktree (where ci.yml may have CRLF endings)
  # and a Linux-style checkout. `_count_yaml_key_at` strips CRs
  # internally, but the canonical `grep -qxF` calls used to pin the
  # unquoted spelling do not — so we normalise once, up-front.
  local workflow_body job_header_count
  workflow_body="$(awk '{ sub(/\r$/, ""); print }' "$workflow")"

  job_header_count="$(_count_yaml_key_at "$workflow_body" 2 migration-drift)"
  if [[ "$job_header_count" != "1" ]]; then
    printf '  expected exactly one migration-drift job header, found %s\n' \
      "$job_header_count" >&2
    return 1
  fi
  # R14: the surviving header must be the canonical unquoted form so a
  # single-spelling swap (e.g. replacing `  migration-drift:` with
  # `  "migration-drift":`) cannot pass the count-of-1 check by simply
  # substituting one spelling for another.
  if ! printf '%s\n' "$workflow_body" | grep -qxF "  migration-drift:"; then
    printf '  migration-drift job header must be the canonical unquoted form\n' >&2
    printf '    expected line: %q\n' "  migration-drift:" >&2
    return 1
  fi

  # R15: after the R14 literal-spelling counter and canonical-line
  # assertion have run for `migration-drift`, sweep the whole workflow
  # for ANY quoted YAML key at 2-space indent. R14's counter matches
  # only the literal string spellings `migration-drift`,
  # `"migration-drift"`, `'migration-drift'`, so an escape-encoded
  # shadow like `  "migration\x2ddrift":` decodes to the same YAML
  # key `migration-drift` under a compliant parser but is byte-for-
  # byte NOT the string `migration-drift`, leaving R14's counter at
  # 1 (only the canonical unquoted line counts) while duplicate-key
  # last-wins semantics let the shadow header take over the summary
  # gate's `needs.migration-drift.result` read. The zero-tolerance
  # invariant rejects the entire class of quoted 2-space keys — of
  # any name and any escape-decoded target — without needing to
  # enumerate escape forms. All non-shadow-job workflow content
  # lives at indents other than 2 (0 = top-level, 4+ = under a job),
  # so no legitimate line is affected. Placed AFTER R14 so a plain
  # literal-quoted duplicate still triggers the more informative
  # R14 "found 2" diagnostic; this check only fires on escape-
  # hidden shapes that survive R14 unnoticed.
  _assert_no_quoted_key_at_indents "$workflow_body" "workflow" 2 || return 1

  local drift_step_count
  drift_step_count="$(grep -c '^      - name: Check EF Core migration drift' "$workflow" || true)"
  if [[ "$drift_step_count" != "1" ]]; then
    printf '  expected exactly one Check EF Core migration drift step, found %s\n' \
      "$drift_step_count" >&2
    return 1
  fi

  local job_block
  job_block="$(extract_job_block "$workflow" migration-drift)"
  if [[ -z "$job_block" ]]; then
    printf '  migration-drift job block not found in %s\n' "$workflow" >&2
    return 1
  fi

  # Job-level keys sit at 4-space indent under `  migration-drift:`.
  # `continue-on-error:` at that indent applies to the whole job:
  # GitHub Actions treats it as "step failures do not fail the job",
  # so the summary gate's `needs.migration-drift.result` would tick
  # success even when the drift step exited non-zero. R14 counts
  # across every yaml spelling so `    "continue-on-error":` or
  # `    'continue-on-error' :` cannot slip past the R13 unquoted-
  # only `grep -Eq '^    continue-on-error:'` check.
  local job_coe_count
  job_coe_count="$(_count_yaml_key_at "$job_block" 4 continue-on-error)"
  if [[ "$job_coe_count" != "0" ]]; then
    printf '  migration-drift job must not set job-level continue-on-error (found %s)\n' \
      "$job_coe_count" >&2
    return 1
  fi

  # R14: Exactly one job-level `if:` at 4-space indent, and its
  # canonical value must match the selection expression. Any other
  # value (`if: false`, a different expression) would either skip
  # the job unconditionally or run it under unexpected gating. R14
  # extends the R13 canonical-line check with a spelling-aware count
  # so `    "if":` cannot appear alongside the unquoted canonical.
  local job_if_count
  job_if_count="$(_count_yaml_key_at "$job_block" 4 if)"
  if [[ "$job_if_count" != "1" ]]; then
    printf '  migration-drift job must have exactly one job-level if clause, found %s\n' \
      "$job_if_count" >&2
    return 1
  fi
  local expected_if="    if: \${{ needs.select.outputs.want_mig_drift == 'true' }}"
  if ! printf '%s\n' "$job_block" | grep -qxF "$expected_if"; then
    printf '  migration-drift job missing expected selection if clause\n' >&2
    printf '    expected line: %q\n' "$expected_if" >&2
    return 1
  fi

  # R14: Exactly one `    strategy:` block at 4-space indent, across
  # every yaml spelling. A duplicate strategy block (e.g. an appended
  # `    "strategy":` that overrides the first via yaml merge semantics
  # in some parsers) is rejected by the count; a single-spelling swap
  # is rejected by the canonical-line check below.
  local strategy_count
  strategy_count="$(_count_yaml_key_at "$job_block" 4 strategy)"
  if [[ "$strategy_count" != "1" ]]; then
    printf '  migration-drift job must have exactly one strategy block, found %s\n' \
      "$strategy_count" >&2
    return 1
  fi
  if ! printf '%s\n' "$job_block" | grep -qxF "    strategy:"; then
    printf '  migration-drift job strategy key must be the canonical unquoted form\n' >&2
    printf '    expected line: %q\n' "    strategy:" >&2
    return 1
  fi

  # R13/R14: Exactly one strategy.matrix line at 6-space indent. Both
  # inline (`      matrix: ${{ ... }}`) and block (`      matrix:` on
  # its own line followed by `        include:` etc.) forms produce
  # one line matching `^      matrix:`. R14 additionally counts every
  # yaml spelling so a duplicate `      "matrix":` line alongside the
  # canonical unquoted key cannot slip past the R13 grep-only counter.
  local matrix_lines
  matrix_lines="$(_count_yaml_key_at "$job_block" 6 matrix)"
  if [[ "$matrix_lines" != "1" ]]; then
    printf '  migration-drift job must have exactly one strategy.matrix line, found %s\n' \
      "$matrix_lines" >&2
    return 1
  fi

  # R13: pin the exact selector-driven matrix source. A mutant that
  # swaps this for an inline hard-coded `{include: [{...}]}` fails the
  # exact-line match; a mutant that switches to block-scalar (`matrix:`
  # alone on a line) also fails because the block-header line is
  # `      matrix:` without the `${{ fromJson ... }}` suffix; a mutant
  # that uses a quoted key `      "matrix": ${{ ... }}` fails because
  # the canonical unquoted line is absent.
  local expected_matrix="      matrix: \${{ fromJson(needs.select.outputs.mig_matrix) }}"
  if ! printf '%s\n' "$job_block" | grep -qxF "$expected_matrix"; then
    printf '  migration-drift job strategy.matrix must be exactly the selector-driven fromJson(needs.select.outputs.mig_matrix) form\n' >&2
    printf '    expected line: %q\n' "$expected_matrix" >&2
    return 1
  fi

  # R13/R14: pin `fail-fast: false` and reject duplicates. Default is
  # true, which would cancel sibling matrix legs on the first failure
  # and hide drift on the cancelled legs. R14 counts every spelling
  # so an appended `      fail-fast: true` alongside the canonical
  # `      fail-fast: false` (yaml's last-wins semantics would let the
  # true win in some parsers) is rejected on count, not on canonical
  # presence.
  local fail_fast_count
  fail_fast_count="$(_count_yaml_key_at "$job_block" 6 fail-fast)"
  if [[ "$fail_fast_count" != "1" ]]; then
    # shellcheck disable=SC2016  # backticks are literal in the message
    printf '  migration-drift job strategy.fail-fast must have exactly one entry (verbatim `fail-fast: false`), found %s\n' \
      "$fail_fast_count" >&2
    return 1
  fi
  local expected_fail_fast="      fail-fast: false"
  if ! printf '%s\n' "$job_block" | grep -qxF "$expected_fail_fast"; then
    # shellcheck disable=SC2016  # backticks are literal in the message
    printf '  migration-drift job strategy.fail-fast must be false (verbatim `fail-fast: false` at strategy-child indent)\n' >&2
    printf '    expected line: %q\n' "$expected_fail_fast" >&2
    return 1
  fi

  # R15: after every R14 literal-spelling counter and canonical-line
  # assertion has run for the migration-drift job block, sweep the
  # block for ANY quoted YAML key at the control indents:
  #   * 4 spaces — job-level keys (`if`, `strategy`, `steps`,
  #     `runs-on`, `needs`, `name`, `continue-on-error`). Escape-
  #     hidden shadows like `    "i\x66": false` or
  #     `    "continue\x2don\x2derror": true` decode to the same
  #     canonical key under YAML but leave R14's per-key
  #     `_count_yaml_key_at` counters unchanged (they only count
  #     the literal spellings of the specific key they were called
  #     with) while a compliant parser silently applies them under
  #     duplicate-key last-wins semantics.
  #   * 6 spaces — strategy children (`fail-fast`, `matrix`). A
  #     step-list-item leading dash sits at 6 spaces + `-`, which
  #     is NOT a leading quote and is therefore not affected.
  #   * 8 spaces — step-item keys under `      - name: ...`
  #     (`name`, `uses`, `with`, `env`, `run`, `working-directory`,
  #     `if`, `continue-on-error`). An escaped step-level shadow
  #     `        "continue\x2don\x2derror": true` neutralises the
  #     drift step's fail-closed semantics; R14's step-level
  #     canonical-block diff catches the unquoted spelling, but the
  #     escaped-quoted variant is not part of the canonical text,
  #     so without R15 the block diff would tolerate it.
  # This is a zero-tolerance invariant: the canonical migration-drift
  # job block contains NO quoted keys at any of these indents, so a
  # match is unambiguously a bypass shape. `run: |` script bodies
  # inside the block are indented at 10+ spaces (deeper than `run:`
  # at 8), so shell-content lines that happen to start with a quote
  # do not fall into any of the checked indents. Placed AFTER every
  # R14 assertion so any duplicate/canonical-swap of a KNOWN key
  # still triggers the more informative R14 diagnostics; R15 only
  # fires on escape-hidden or novel-key shapes that survive R14.
  _assert_no_quoted_key_at_indents "$job_block" "migration-drift job" 4 6 8 \
    || return 1

  local actual expected
  actual="$(extract_drift_step_block "$workflow")"
  expected="$(cat <<'CANONICAL_DRIFT_STEP_BLOCK'
      - name: Check EF Core migration drift
        working-directory: src
        env:
          MATRIX_LABEL: ${{ matrix.label }}
          MATRIX_PROJECT: ${{ matrix.project }}
          MATRIX_CONTEXT: ${{ matrix.context }}
          DB_PROVIDER: ${{ matrix.provider }}
        # `dotnet ef migrations has-pending-model-changes` returns 0 when the
        # snapshot matches the current model. Any non-zero exit code means the
        # check did not confirm "no drift", but the tool does NOT distinguish
        # real drift (rc=1 on success paths) from design-time failures
        # (missing/failed context factory, provider load errors, tool/version
        # mismatches, restore/build errors surfaced through the tool) — those
        # also exit non-zero, including 1, across supported EF Core versions.
        # Because we cannot classify the cause purely from the exit code, we
        # emit a single truthful annotation for any non-zero rc and direct the
        # reader to the tool output already streamed to the Actions log. The
        # job stays fail-closed either way: both drift and tool failures block
        # the workflow.
        run: |
          set -u
          echo "Checking $MATRIX_LABEL for pending EF Core model changes..."
          set +e
          dotnet ef migrations has-pending-model-changes \
            --project "./$MATRIX_PROJECT" \
            --startup-project "./$MATRIX_PROJECT" \
            --context "$MATRIX_CONTEXT" \
            --no-build
          rc=$?
          set -e
          if [ "$rc" -eq 0 ]; then
            echo "$MATRIX_LABEL: no pending model changes."
          else
            echo "::error title=EF Core migration drift check failed::$MATRIX_LABEL: 'dotnet ef migrations has-pending-model-changes' exited with $rc. This may indicate pending model changes (add a migration for $MATRIX_CONTEXT using DB_PROVIDER=$DB_PROVIDER) OR an EF Core tool / design-time context / provider failure. Inspect the tool output above to determine which."
            exit "$rc"
          fi
CANONICAL_DRIFT_STEP_BLOCK
)"
  if [[ "$actual" != "$expected" ]]; then
    printf '  drift step block does not match canonical snapshot\n' >&2
    printf '  (update the CANONICAL_DRIFT_STEP_BLOCK heredoc after reviewing the change)\n' >&2
    diff -u <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") | sed 's/^/    /' >&2
    return 1
  fi

  return 0
}


# The migration-drift matrix job is isolated from `dotnet-build` and must
# restore its own matrix project before invoking `dotnet ef`, otherwise
# NETSDK1004 fires because `obj/project.assets.json` doesn't exist. This
# test reads `.github/workflows/ci.yml`, extracts the `migration-drift:`
# job, and asserts:
#   * a "Restore migration project" step exists and references MATRIX_PROJECT
#   * a "Build migration project" step exists
#   * that restore step appears BEFORE "Check EF Core migration drift"
#   * the EF invocation uses `--no-build` (matching restore+build+no-build
#     ordering, so we do not re-trigger restore inside the EF tool)
#   * the drift step emits a truthful GENERIC annotation for any non-zero
#     exit code (`EF Core migration drift check failed`) — because
#     `dotnet ef` cannot be relied on to return a unique code for real
#     drift vs. tool / design-time context / provider failures, the check
#     must not classify rc=1 uniquely as drift.
#   * the drift step does NOT emit the old rc=1-only annotation
#     (`EF Core migration drift detected`), which would falsely tell
#     authors "you have pending model changes" when the tool actually
#     failed to run.
#   * the drift step's FULL yaml (working-directory, env, comment,
#     `run: |` body) matches the canonical snapshot embedded in
#     `_check_drift_step_shape` byte-for-byte; the enclosing
#     `migration-drift` job has no job-level `continue-on-error`; the
#     job carries the expected selection `if:`; the strategy.matrix is
#     pinned to `${{ fromJson(needs.select.outputs.mig_matrix) }}` so a
#     hard-coded single-entry matrix cannot pass; the strategy.fail-fast
#     is pinned to `false` so drift on one leg cannot cancel another —
#     all enforced by `_check_drift_step_shape`. R12 upgraded this gate
#     from a shell-body-only snapshot to a full-step-yaml snapshot after
#     Hicks showed the R11 gate was silent about yaml-level bypass keys
#     (`continue-on-error: true`, `if: false`) that would leave the
#     shell body byte-identical and slip through. R13 added the job-
#     count, strategy.matrix, and strategy.fail-fast invariants after
#     Hicks flagged that the R12 gate only pinned STEP shape and would
#     accept a job-level swap to a hard-coded matrix or fail-fast:true.
#     See adversarial mutation tests `case_drift_shape_rejects_*`.
case_workflow_migration_drift_restores_before_ef() {
  local workflow="$REPO_ROOT/.github/workflows/ci.yml"

  local block
  block="$(extract_job_block "$workflow" migration-drift)"
  if [[ -z "$block" ]]; then
    printf '  migration-drift job block not found in %s\n' "$workflow" >&2
    return 1
  fi

  assert_contains "restore step present" "$block" \
    'name: Restore migration project' || return 1
  assert_contains "restore uses MATRIX_PROJECT" "$block" \
    'dotnet restore "./$MATRIX_PROJECT"' || return 1
  assert_contains "build step present" "$block" \
    'name: Build migration project' || return 1
  assert_contains "ef step uses --no-build" "$block" \
    '--no-build' || return 1

  # Positive: single truthful generic annotation for any non-zero rc.
  assert_contains "drift step emits generic non-zero annotation" "$block" \
    'EF Core migration drift check failed' || return 1

  # Negative: the rejected R6 shape classified rc=1 uniquely as real drift
  # via a case arm `1) ... EF Core migration drift detected`. `dotnet ef`
  # returns non-zero (including 1) for design-time / tool / context /
  # provider failures too, so that annotation would send authors chasing
  # a migration they don't need. Guard against regression:
  assert_not_contains "no rc=1-only drift annotation" "$block" \
    'EF Core migration drift detected' || return 1

  # Full-step canonical-shape gate (R12 upgrade). Composite check covers:
  # exactly-one drift step, no job-level continue-on-error, preserved
  # selection if:, full step yaml byte-for-byte match. Adversarial
  # coverage: `case_drift_shape_rejects_*`.
  _check_drift_step_shape "$workflow" || return 1

  # Order check: "Restore migration project" must precede "Check EF Core
  # migration drift". Line numbers within the extracted block are enough
  # for a deterministic ordering assertion.
  local restore_line ef_line
  restore_line="$(printf '%s\n' "$block" | grep -n 'name: Restore migration project' | head -n1 | cut -d: -f1)"
  ef_line="$(printf '%s\n' "$block" | grep -n 'name: Check EF Core migration drift' | head -n1 | cut -d: -f1)"
  if [[ -z "$restore_line" || -z "$ef_line" ]]; then
    printf '  could not locate ordering markers (restore=%q ef=%q)\n' \
      "$restore_line" "$ef_line" >&2
    return 1
  fi
  if (( restore_line >= ef_line )); then
    printf '  restore step must precede EF drift step (restore=%d ef=%d)\n' \
      "$restore_line" "$ef_line" >&2
    return 1
  fi
}

# Synthetic CRLF fixture proof for extract_drift_run_body. The real
# workflow assertion above compares the extractor's output against a
# canonical snapshot, so the extractor's own robustness needs its own
# test — otherwise a regression that (e.g.) failed to strip trailing
# CRs on Windows checkouts, or terminated the block one line too early,
# would surface as a snapshot mismatch instead of an extractor bug and
# waste a reviewer's diff-reading time.
#
# The fixture is a minimal `migration-drift` job with CRLF line endings
# printed explicitly. It contains:
#   * a preceding step (`Build migration project`) whose `run:` is a
#     one-liner (no block scalar) — the extractor must not latch onto
#     this step
#   * the `Check EF Core migration drift` step with a two-line `run: |`
#     body at 10-space base indent
#   * a following step (`Post-drift diagnostics`) whose `- name:` marker
#     must terminate the block cleanly
#
# The expected output is the two body lines with base indent stripped
# and no CR characters. This proves the awk state machine and the CRLF
# scrub work together on the exact shape the real workflow uses.
case_drift_run_body_extractor_crlf_tolerant() {
  local fixture
  fixture="$(mktemp)"
  # NOTE: emit CRLF explicitly with `\r\n`. Do not rely on the host
  # runtime translating line endings — Linux CI runners write LF and we
  # need to prove tolerance of the CRLF a Windows checkout produces.
  printf '%s\r\n' \
    'jobs:' \
    '  migration-drift:' \
    '    steps:' \
    '      - name: Build migration project' \
    '        run: dotnet build ./x' \
    '      - name: Check EF Core migration drift' \
    '        run: |' \
    '          echo hello' \
    '          exit 0' \
    '      - name: Post-drift diagnostics' \
    '        run: echo done' \
    > "$fixture"

  local actual expected
  actual="$(extract_drift_run_body "$fixture")"
  rm -f -- "$fixture"

  expected=$'echo hello\nexit 0'
  if [[ "$actual" != "$expected" ]]; then
    printf '  extract_drift_run_body CRLF fixture mismatch\n' >&2
    printf '    expected: %q\n' "$expected" >&2
    printf '    actual:   %q\n' "$actual" >&2
    return 1
  fi

  # Belt-and-braces: no stray CRs in the extractor output. If the awk
  # `sub(/\r$/, "")` regressed, the byte-equality check above would
  # already fail — but pinning the invariant explicitly makes the
  # failure mode obvious in a green-vs-red diff.
  case "$actual" in
    *$'\r'*)
      printf '  extract_drift_run_body left CR bytes in its output\n' >&2
      return 1
      ;;
  esac
}


# R12 extractor robustness: `extract_drift_run_body` must bail promptly
# when a hostile or malformed workflow puts a zero-indent (column-0)
# non-blank line as the first content line of the `run: |` block. The
# pre-R12 body used `match($0, /^ +/)` to derive the base indent; POSIX
# awk sets `RLENGTH = -1` on no match, so `base_indent = -1` and the
# subsequent `RLENGTH < base_indent` guard (`0 < -1` is false) never
# fired. The extractor would then consume every remaining line of the
# workflow into its output and, worse, the shape-snapshot gate would
# report a huge unified diff instead of "extractor ran off the block".
#
# This synthetic CRLF fixture places a bare `RUNAWAY` line (no leading
# whitespace) directly under `run: |` and asserts:
#   * the extractor emits NOTHING (bails immediately on the zero-indent
#     first line, per the R12 `base_indent <= 0` guard), and
#   * critically, the fixture's `RUNAWAY_MARKER_NEXT_SECTION` line —
#     which sits farther down the file at column 0 — does NOT appear
#     anywhere in the extractor output. If the guard regresses to the
#     R11 `== 0` check, this marker would appear in output, and the
#     assertion fails loudly.
case_drift_run_body_extractor_bails_on_zero_indent() {
  local fixture
  fixture="$(mktemp)"
  # Structure: two-space job indent, four-space steps indent, six-space
  # step-header indent, eight-space step-body indent. The `run: |` block
  # opens but the very first content line is at column 0, which should
  # cause the extractor to bail before printing anything.
  printf '%s\r\n' \
    'jobs:' \
    '  migration-drift:' \
    '    steps:' \
    '      - name: Check EF Core migration drift' \
    '        run: |' \
    'RUNAWAY' \
    'RUNAWAY_MARKER_NEXT_SECTION' \
    'more content that must not leak' \
    > "$fixture"

  local actual
  actual="$(extract_drift_run_body "$fixture")"
  rm -f -- "$fixture"

  if [[ -n "$actual" ]]; then
    printf '  extract_drift_run_body did not bail on zero-indent first line\n' >&2
    printf '    unexpected output: %q\n' "$actual" >&2
    return 1
  fi
  if [[ "$actual" == *"RUNAWAY_MARKER_NEXT_SECTION"* ]]; then
    printf '  extract_drift_run_body ran away past zero-indent boundary\n' >&2
    return 1
  fi
}

# R12 extractor robustness: a `run: |` block whose first content line
# uses TAB indentation (rather than the POSIX-spec spaces) must also
# bail. Tabs are not counted by `/^ */` — which matches ASCII space
# only — so RLENGTH is 0 and the R12 `base_indent <= 0` guard exits.
# Without the guard, tab-indented content would either be re-interpreted
# with a broken base-indent of 0 (matching the pre-R12 buggy branch) or
# consumed line-by-line under a garbage base_indent.
#
# The fixture uses `\t` explicitly in printf to guarantee a tab byte,
# then asserts the extractor emits nothing and does not leak the
# `RUNAWAY_TAB_MARKER` sentinel positioned below.
case_drift_run_body_extractor_bails_on_tab_indent() {
  local fixture
  fixture="$(mktemp)"
  printf '%s\r\n' \
    'jobs:' \
    '  migration-drift:' \
    '    steps:' \
    '      - name: Check EF Core migration drift' \
    '        run: |' \
    > "$fixture"
  # Tab-indented content lines. Explicit \t to prevent editor
  # normalisation from turning these into spaces.
  printf '\t%s\r\n' \
    'echo tabbed' \
    'echo also tabbed' \
    'RUNAWAY_TAB_MARKER' \
    >> "$fixture"

  local actual
  actual="$(extract_drift_run_body "$fixture")"
  rm -f -- "$fixture"

  if [[ -n "$actual" ]]; then
    printf '  extract_drift_run_body did not bail on tab-indent first line\n' >&2
    printf '    unexpected output: %q\n' "$actual" >&2
    return 1
  fi
  if [[ "$actual" == *"RUNAWAY_TAB_MARKER"* ]]; then
    printf '  extract_drift_run_body consumed tab-indented content past base\n' >&2
    return 1
  fi
}

# R12 step-block extractor: mirror of `case_drift_run_body_extractor_
# crlf_tolerant` for the new higher-level `extract_drift_step_block`
# helper. Proves the step-block extractor (a) matches the step header
# under CRLF, (b) terminates cleanly at the next sibling step, and
# (c) drops trailing blank lines between the step body and the next
# section so whitespace edits below the step never destabilise the
# canonical snapshot.
case_drift_step_block_extractor_crlf_tolerant() {
  local fixture
  fixture="$(mktemp)"
  printf '%s\r\n' \
    'jobs:' \
    '  migration-drift:' \
    '    steps:' \
    '      - name: Build migration project' \
    '        run: dotnet build ./x' \
    '      - name: Check EF Core migration drift' \
    '        working-directory: src' \
    '        env:' \
    '          MATRIX_LABEL: label' \
    '        run: |' \
    '          echo hello' \
    '          exit 0' \
    '' \
    '' \
    '      - name: Post-drift diagnostics' \
    '        run: echo done' \
    > "$fixture"

  local actual expected
  actual="$(extract_drift_step_block "$fixture")"
  rm -f -- "$fixture"

  # Expected: the drift step from `- name:` through `exit 0`, with the
  # two trailing blank lines dropped (they belong to the section-break
  # whitespace, not the step body).
  expected="$(cat <<'EXPECTED_CRLF_STEP_BLOCK'
      - name: Check EF Core migration drift
        working-directory: src
        env:
          MATRIX_LABEL: label
        run: |
          echo hello
          exit 0
EXPECTED_CRLF_STEP_BLOCK
)"
  if [[ "$actual" != "$expected" ]]; then
    printf '  extract_drift_step_block CRLF fixture mismatch\n' >&2
    diff -u <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") | sed 's/^/    /' >&2
    return 1
  fi
  case "$actual" in
    *$'\r'*)
      printf '  extract_drift_step_block left CR bytes in its output\n' >&2
      return 1
      ;;
  esac
  # Sentinel: the following step must not leak.
  if [[ "$actual" == *"Post-drift diagnostics"* ]]; then
    printf '  extract_drift_step_block did not terminate at next step\n' >&2
    return 1
  fi
}


# R12 adversarial mutation tests, extended in R13. Each of the
# following cases takes a copy of the real `.github/workflows/ci.yml`,
# applies a targeted mutation representing a concrete bypass shape,
# and asserts that `_check_drift_step_shape` REJECTS the mutant with
# the SPECIFIC diagnostic that names the invariant the mutation
# violates. The shape gate must fail-closed against every one of
# these — if any mutant slips through, or is rejected for the wrong
# reason (a different invariant), the guard is not doing its job.
#
# R13 adds diagnostic-substring assertions to close a Bishop finding:
# an R12 mutation could satisfy `!_check_drift_step_shape` for reasons
# unrelated to the intended violation (e.g., a matrix rewrite that
# also happened to invalidate the canonical step snapshot would pass
# the R12 assertion, hiding that the matrix invariant itself is absent
# from the guard). The R13 assertion pins BOTH the rejection AND the
# reason.
#
# All mutation helpers use awk for surgical rewrites (no sed portability
# hazards) via the `_mutate` helper, which writes to a `.tmp` sibling
# and either atomically `mv`s on awk success or removes the tmp on awk
# failure. Each case also installs a RETURN trap so the mutant file and
# any stray `.tmp` sibling are removed on every exit path — including
# early `return 1` from a failed assertion — which closes a Bishop
# finding about leaked intermediate files on awk error paths.

# Copy the real workflow to <dst>. Kept as a helper so each adversarial
# case starts from an unmutated baseline; a real-workflow shape drift
# would surface in `case_workflow_migration_drift_restores_before_ef`,
# not in the adversarial suite.
_copy_real_workflow_for_mutation() {
  local dst="$1"
  cp "$REPO_ROOT/.github/workflows/ci.yml" "$dst"
}

# _mutate <file> <awk_program>
#
# Apply <awk_program> in place to <file>. Uses a `.tmp` sibling as
# intermediate storage and atomically `mv`s it into place on awk
# success. On awk failure the `.tmp` is removed and awk's real exit
# status is returned to the caller — so a failure never leaks the
# tmp file even if the caller neglects to install a cleanup trap.
#
# Bishop cleanup (R13): the R12 mutation cases used the pattern
# `awk … > "$mutant.tmp" && mv "$mutant.tmp" "$mutant"`. On awk failure
# the shell had already created (empty) `$mutant.tmp` via the `>`
# redirection, and the `&&` short-circuit skipped the mv, leaving the
# tmp behind. Every subsequent mutation from the same test run would
# collide on the deterministic-suffix path. `_mutate` centralises the
# cleanup so the pattern is bug-free at every call site.
#
# Hicks cleanup (R14): the R13 implementation captured awk's rc
# INSIDE `if ! awk …; then local rc=$?; …`. Inside the `then` branch
# of a negated pipeline, `$?` is 0 (the negation's own result), not
# awk's exit code — so the tmp cleanup path always `return 0`'d and
# the guard reported success on awk failure. R14 runs awk directly,
# captures `$?` on the very next line before ANY other command (so
# no intermediate command clobbers it), and branches on the captured
# value. Coverage: `case_mutate_helper_propagates_awk_failure`.
_mutate() {
  local file="$1" awk_prog="$2"
  local tmp="${file}.tmp"
  local rc
  awk "$awk_prog" "$file" > "$tmp"
  rc=$?
  if (( rc != 0 )); then
    rm -f -- "$tmp"
    return "$rc"
  fi
  mv -- "$tmp" "$file"
}

# _assert_shape_guard_rejects <label> <workflow> <expected_diagnostic>
#
# Assert that `_check_drift_step_shape` returns non-zero on <workflow>
# AND that its stderr contains <expected_diagnostic> as a substring.
# The diagnostic-substring check (R13, Bishop finding) prevents a
# mutation from silently satisfying a different invariant than the
# one it was written to exercise: if the guard rejects for the wrong
# reason we want a loud failure, not a false-green.
_assert_shape_guard_rejects() {
  local label="$1" workflow="$2" expected="$3"
  local stderr_output rc
  # Redirect stdout to /dev/null and capture stderr. The composite
  # substitution `$(cmd 2>&1 >/dev/null)` collects only cmd's stderr
  # because the redirections apply right-to-left: stdout is copied to
  # stderr's *original* destination (the pipe fd), then reassigned to
  # /dev/null. `$?` after the substitution is cmd's exit status.
  stderr_output="$(_check_drift_step_shape "$workflow" 2>&1 >/dev/null)"
  rc=$?
  if (( rc == 0 )); then
    printf '  shape guard failed to reject mutation: %s\n' "$label" >&2
    return 1
  fi
  if [[ "$stderr_output" != *"$expected"* ]]; then
    printf '  shape guard rejected mutation but with wrong diagnostic: %s\n' "$label" >&2
    printf '    expected substring: %q\n' "$expected" >&2
    printf '    actual stderr:      %q\n' "$stderr_output" >&2
    return 1
  fi
  return 0
}

# Mutation: inject `        continue-on-error: true` as a sibling yaml
# key immediately after the drift step's `- name:` header. This would
# make the step's failure non-fatal to the job, letting the migration-
# drift job report success while a real drift went undetected.
case_drift_shape_rejects_step_continue_on_error() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  _mutate "$mutant" '
    { print }
    /^      - name: Check EF Core migration drift\r?$/ && !inserted {
      print "        continue-on-error: true\r"
      inserted = 1
    }
  ' || return 1

  _assert_shape_guard_rejects "step-level continue-on-error" "$mutant" \
    "drift step block does not match canonical snapshot" || return 1
}

# Mutation: inject `        if: false` as a sibling yaml key immediately
# after the drift step's `- name:` header. The step would be skipped
# whenever the mutation is present, which would let the migration-drift
# job pass without ever invoking `dotnet ef`. This is a classic
# fail-open bypass and the shape guard must catch it.
case_drift_shape_rejects_step_if() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  _mutate "$mutant" '
    { print }
    /^      - name: Check EF Core migration drift\r?$/ && !inserted {
      print "        if: false\r"
      inserted = 1
    }
  ' || return 1

  _assert_shape_guard_rejects "step-level if: false" "$mutant" \
    "drift step block does not match canonical snapshot" || return 1
}

# Mutation: add `    continue-on-error: true` as a job-level key on
# `migration-drift`. GitHub Actions treats job-level continue-on-error
# as "step failures do not fail the job", so the summary gate's
# `needs.migration-drift.result` would tick success even when the drift
# step exited non-zero. Injected immediately after the job's `name:`
# key.
case_drift_shape_rejects_job_continue_on_error() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  _mutate "$mutant" '
    { print }
    inside && !inserted && /^    name:/ {
      print "    continue-on-error: true\r"
      inserted = 1
    }
    /^  migration-drift:\r?$/ { inside = 1 }
  ' || return 1

  _assert_shape_guard_rejects "job-level continue-on-error" "$mutant" \
    "must not set job-level continue-on-error" || return 1
}

# Mutation: duplicate the entire drift step. This is a subtle bypass
# shape where an attacker adds a shadow drift step (perhaps with
# `if: false` or a benign `run: true`) next to the real one; a naive
# guard that only inspects the first match could be fooled. The shape
# gate's exactly-one-step count check must reject any drift step count
# other than 1.
case_drift_shape_rejects_duplicate_drift_step() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Append a second (byte-identical) `- name: Check EF Core migration
  # drift` header at 6-space indent inside the same steps list. The
  # simplest injection: insert immediately after the real step's
  # `- name:` header. Body doesn't matter for the count check.
  _mutate "$mutant" '
    { print }
    /^      - name: Check EF Core migration drift\r?$/ && !inserted {
      print "        run: echo shadow\r"
      print "      - name: Check EF Core migration drift\r"
      inserted = 1
    }
  ' || return 1

  _assert_shape_guard_rejects "duplicate drift step" "$mutant" \
    "expected exactly one Check EF Core migration drift step, found 2" || return 1
}

# Mutation: reindent the drift step so its `- name:` sits at 8 spaces
# instead of the canonical 6. This misaligns the step under `steps:`
# and, if the shape guard tolerated the drift, could hide the step
# from grep-based selection or shift its ownership to an unexpected
# parent key. The guard's exactly-one-step count uses an anchored
# `^      - name:` pattern (6-space prefix) — a malformed 8-space
# indent produces a count of 0 and the guard rejects.
case_drift_shape_rejects_malformed_indentation() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  _mutate "$mutant" '
    /^      - name: Check EF Core migration drift\r?$/ {
      # Prepend two extra spaces to shift from 6-space to 8-space.
      sub(/^      /, "        ")
    }
    { print }
  ' || return 1

  _assert_shape_guard_rejects "malformed step indentation" "$mutant" \
    "expected exactly one Check EF Core migration drift step, found 0" || return 1
}

# R13 mutation: replace the selector-driven strategy.matrix line with a
# hard-coded inline single-entry matrix. This is the specific bypass
# Hicks flagged on R12 — the R12 guard pinned STEP shape byte-for-byte
# but was silent about the JOB-level matrix source, so a mutation that
# left the step untouched and merely swapped
# `matrix: ${{ fromJson(needs.select.outputs.mig_matrix) }}`
# for `matrix: {include: [{name: "AppPg", ...}]}` would silently run
# the drift check against ONE hand-picked provider/context pair while
# leaving the sibling three EF pairs unchecked. The migration-drift
# job would still report success, and the summary gate would tick
# green — a fail-open the R13 matrix invariant closes.
case_drift_shape_rejects_hardcoded_matrix() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Replace the exact selector-driven matrix line with a hand-picked
  # single-entry inline flow-style matrix. Fields chosen to look
  # plausible (`Farm.Migrations.PostgreSQL`, `AppDbContext`, `postgres`).
  # shellcheck disable=SC2016  # $0 is awk field ref, not shell param
  _mutate "$mutant" '
    { sub(/\r$/, "") }
    $0 == "      matrix: ${{ fromJson(needs.select.outputs.mig_matrix) }}" {
      print "      matrix: {include: [{name: \"AppPg\", label: \"App/Pg\", project: \"migrations/Farm.Migrations.PostgreSQL\", context: \"AppDbContext\", provider: \"postgres\"}]}\r"
      next
    }
    { print $0 "\r" }
  ' || return 1

  _assert_shape_guard_rejects "hard-coded inline matrix" "$mutant" \
    "must be exactly the selector-driven fromJson(needs.select.outputs.mig_matrix) form" || return 1
}

# R13 mutation: replace the selector-driven matrix with a BLOCK-scalar
# hard-coded matrix. The block form (`matrix:` on its own line then
# `        include:` indented children) is what a reviewer skimming
# the diff might read as "just moved formatting around" — but it has
# the same fail-open semantics as the inline form: the drift job runs
# against a single hand-picked pair, and sibling EF pairs go
# unchecked. Block form defeats a lazy exact-line check that only
# matched the inline form; the R13 guard rejects it via the exact-
# line pin on `      matrix: ${{ fromJson ... }}` (block form starts
# with `      matrix:` alone, no ${{ ... }} suffix on the same line).
case_drift_shape_rejects_block_style_matrix() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # shellcheck disable=SC2016  # $0 is awk field ref, not shell param
  _mutate "$mutant" '
    { sub(/\r$/, "") }
    $0 == "      matrix: ${{ fromJson(needs.select.outputs.mig_matrix) }}" {
      print "      matrix:\r"
      print "        include:\r"
      print "          - name: AppPg\r"
      print "            label: App/Pg\r"
      print "            project: migrations/Farm.Migrations.PostgreSQL\r"
      print "            context: AppDbContext\r"
      print "            provider: postgres\r"
      next
    }
    { print $0 "\r" }
  ' || return 1

  _assert_shape_guard_rejects "block-style hard-coded matrix" "$mutant" \
    "must be exactly the selector-driven fromJson(needs.select.outputs.mig_matrix) form" || return 1
}

# R13 mutation: flip `fail-fast: false` to `fail-fast: true`. The
# GitHub default for matrix `fail-fast` is true — meaning a failure
# in one matrix leg cancels sibling legs immediately. For drift that
# is actively harmful: a design-time context failure in AppPg would
# cancel AppSqlServer, SlicerPg, and SlicerSqlServer before they
# reported their own drift. The reviewer sees a single failed leg
# and a cluster of "cancelled" siblings and cannot tell whether
# those siblings would have passed or failed — the drift signal from
# them is lost. Pin `fail-fast: false` verbatim; any mutation trips.
case_drift_shape_rejects_fail_fast_true() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # shellcheck disable=SC2016  # $0 is awk field ref, not shell param
  _mutate "$mutant" '
    { sub(/\r$/, "") }
    $0 == "      fail-fast: false" {
      print "      fail-fast: true\r"
      next
    }
    { print $0 "\r" }
  ' || return 1

  _assert_shape_guard_rejects "fail-fast: true" "$mutant" \
    "strategy.fail-fast must be false" || return 1
}

# R13 mutation: duplicate the `  migration-drift:` JOB header itself.
# `extract_job_block` uses first-match semantics — a duplicate job
# header would let a shadow `migration-drift:` job (perhaps with
# `if: false` and no steps) shadow the real one under the summary
# gate's `needs.migration-drift.result` read, while this shape gate
# would inspect only the first block. The R13 job-header count guard
# rejects any count other than 1. The shadow job is appended after
# the real job's block terminator to keep the mutation surgical.
case_drift_shape_rejects_duplicate_migration_drift_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Append a second `  migration-drift:` header + minimal body at the
  # end of the file. Content of the shadow job body is irrelevant for
  # the count check; keeping it minimal and inert avoids collateral
  # yaml-parse damage in an already-adversarial fixture.
  if ! {
    cat "$mutant"
    printf '\n  migration-drift:\n    if: false\n    runs-on: ubuntu-latest\n    steps:\n      - run: "echo shadow"\n'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  _assert_shape_guard_rejects "duplicate migration-drift job header" "$mutant" \
    "expected exactly one migration-drift job header, found 2" || return 1
}

# R14 mutation: append `      "matrix": {include: [{...}]}` as a
# second yaml-level matrix key at 6-space strategy-child indent,
# ALONGSIDE the canonical unquoted `      matrix: ${{ ... }}`. Under
# R13's `grep -Ec '^      matrix:'` counter, the quoted spelling did
# not increment the count so the mutation slipped past the count
# invariant, and the canonical `grep -qxF` line assertion still
# passed because the unquoted canonical line was unchanged. Yaml
# parsers that resolve duplicate keys with last-wins semantics
# (including several used by third-party actions runners) would
# then execute the hand-picked single-entry quoted matrix and skip
# three of the four EF pairs — a fail-open the R14 spelling-aware
# `_count_yaml_key_at` closes by counting all three yaml spellings
# together and rejecting any count other than 1.
case_drift_shape_rejects_quoted_duplicate_matrix() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Inject the shadow quoted-key matrix line immediately after the
  # canonical selector-driven line. Both lines live at 6-space indent
  # inside the strategy block; the injected line's value is a hand-
  # picked inline single-entry matrix that would (under last-wins
  # yaml semantics) override the four-way sibling coverage.
  # shellcheck disable=SC2016  # $0 is awk field ref, not shell param
  _mutate "$mutant" '
    { sub(/\r$/, ""); print $0 "\r" }
    $0 == "      matrix: ${{ fromJson(needs.select.outputs.mig_matrix) }}" && !inserted {
      print "      \"matrix\": {include: [{name: \"AppPg\", label: \"App/Pg\", project: \"migrations/Farm.Migrations.PostgreSQL\", context: \"AppDbContext\", provider: \"postgres\"}]}\r"
      inserted = 1
    }
  ' || return 1

  _assert_shape_guard_rejects "quoted duplicate matrix" "$mutant" \
    "exactly one strategy.matrix line, found 2" || return 1
}

# R14 mutation: append `      fail-fast: true` as a second yaml key
# at 6-space strategy-child indent, ALONGSIDE the canonical
# `      fail-fast: false`. Under R13 the guard used only a canonical
# `grep -qxF "      fail-fast: false"` presence check with no count
# invariant, so appending a duplicate `fail-fast: true` left the
# canonical line untouched (grep still matched) while yaml last-wins
# semantics would have GitHub Actions read the second entry as the
# effective value, restoring the fail-fast:true behaviour the R13
# invariant was written to prevent. R14 counts every spelling of
# `fail-fast` at strategy-child indent and rejects any count other
# than 1, so duplicate keys of any spelling combination fail closed.
case_drift_shape_rejects_duplicate_fail_fast_true() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Inject the shadow `fail-fast: true` immediately after the
  # canonical `fail-fast: false` line, at the same indent, so both
  # lines sit inside the same strategy block.
  _mutate "$mutant" '
    { sub(/\r$/, ""); print $0 "\r" }
    $0 == "      fail-fast: false" && !inserted {
      print "      fail-fast: true\r"
      inserted = 1
    }
  ' || return 1

  _assert_shape_guard_rejects "duplicate fail-fast true" "$mutant" \
    "fail-fast must have exactly one entry" || return 1
}

# R14 mutation: inject `    "continue-on-error": true` at job level
# (4-space indent). Under R13 the guard used `grep -Eq
# '^    continue-on-error:'` which only matched the unquoted spelling
# and let the double-quoted variant slip past; the underlying yaml
# semantics are identical — GitHub Actions treats a job-level
# continue-on-error of any spelling as "step failures do not fail
# the job", so the summary gate's `needs.migration-drift.result`
# would tick success even when the drift step exited non-zero. R14's
# `_count_yaml_key_at` counts all three yaml spellings; the zero-
# tolerance invariant rejects any count > 0 with a specific
# diagnostic.
case_drift_shape_rejects_quoted_job_continue_on_error() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Insert the quoted job-level `continue-on-error: true` immediately
  # after the migration-drift job's `name:` line at 4-space indent.
  _mutate "$mutant" '
    { sub(/\r$/, ""); print $0 "\r" }
    inside && !inserted && /^    name:/ {
      print "    \"continue-on-error\": true\r"
      inserted = 1
    }
    $0 == "  migration-drift:" { inside = 1 }
  ' || return 1

  _assert_shape_guard_rejects "quoted job-level continue-on-error" "$mutant" \
    "must not set job-level continue-on-error" || return 1
}

# R14 mutation: append a second `    strategy:` key at 4-space
# job-indent, giving the migration-drift job two strategy blocks.
# The R13 guard had no `strategy` count invariant — it only checked
# strategy.matrix and strategy.fail-fast children, which came from
# the FIRST strategy block extracted by the yaml parser. A second
# strategy block appended after the first would (under yaml last-
# wins) override the first with whatever fail-fast/matrix children
# it declared, and R13 would silently pass. R14 counts every yaml
# spelling of `strategy` at 4-space indent and rejects any count
# other than 1.
case_drift_shape_rejects_duplicate_strategy() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Inject the shadow strategy header immediately after the migration-
  # drift `runs-on:` line at 4-space indent; deliberately empty (no
  # children) so the mutation surface stays surgical — the guard must
  # reject on the duplicate KEY count alone, regardless of children.
  _mutate "$mutant" '
    { sub(/\r$/, ""); print $0 "\r" }
    inside && !inserted && /^    runs-on:/ {
      print "    strategy:\r"
      inserted = 1
    }
    $0 == "  migration-drift:" { inside = 1 }
  ' || return 1

  _assert_shape_guard_rejects "duplicate strategy block" "$mutant" \
    "exactly one strategy block, found 2" || return 1
}

# R14 mutation: replace the canonical unquoted `    strategy:` with
# the double-quoted spelling `    "strategy":`. Under R13 there was
# no strategy invariant at all, so a strategy-key spelling swap was
# never checked. Under R14, the spelling-aware count is still 1
# (double-quoted spelling counts), but the canonical-form assertion
# fails: R14 requires the surviving spelling to be the unquoted
# canonical form so a reviewer skimming the diff cannot be misled
# by a quoted-key rewrite (which yaml parsers accept but reviewers
# rarely audit for equivalence).
case_drift_shape_rejects_quoted_strategy() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # shellcheck disable=SC2016  # $0 is awk field ref, not shell param
  _mutate "$mutant" '
    { sub(/\r$/, "") }
    $0 == "    strategy:" {
      print "    \"strategy\":\r"
      next
    }
    { print $0 "\r" }
  ' || return 1

  _assert_shape_guard_rejects "quoted strategy key" "$mutant" \
    "strategy key must be the canonical unquoted form" || return 1
}

# R14 mutation: append `  "migration-drift":` shadow job header
# using the double-quoted spelling. Under R13's exact-string count
# (`$0 == "  migration-drift:"`), the quoted spelling did not
# increment the counter and the shadow job would take over the
# summary gate's `needs.migration-drift.result` read — a bypass
# shape the R14 `_count_yaml_key_at` closes by counting all three
# yaml spellings at 2-space job indent together. The shadow body
# is a minimal inert `if: false` skeleton, same as the R13 duplicate
# case, so the mutation stays surgical.
case_drift_shape_rejects_quoted_shadow_migration_drift_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Append a second (double-quoted) `  "migration-drift":` header +
  # minimal body at the end of the file.
  if ! {
    cat "$mutant"
    printf '\n  "migration-drift":\n    if: false\n    runs-on: ubuntu-latest\n    steps:\n      - run: "echo shadow"\n'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  _assert_shape_guard_rejects "quoted shadow migration-drift job header" "$mutant" \
    "expected exactly one migration-drift job header, found 2" || return 1
}

# =============================================================================
# R15 (Newt): escape-encoded quoted-key mutations.
#
# R14 (Hudson, rejected 7fdcc1615) counted alternate yaml spellings by
# matching the LITERAL characters between the quotes:
#
#   pat_d="^${prefix}\"${key}\"[[:space:]]*:"
#
# Under YAML, double-quoted strings support escape sequences (`\x66` ->
# `f`, `\x78` -> `x`, `\x2d` -> `-`, and the full YAML 1.1/1.2 escape
# alphabet). A hostile rewrite that renames the shadow key using an
# escape-encoded spelling like
#
#   "matri\x78":                # decodes to `matrix`
#   "i\x66": false              # decodes to `if`
#   "migration\x2ddrift":       # decodes to `migration-drift`
#   "fail\x2dfast": true        # decodes to `fail-fast`
#   "continue\x2don\x2derror":  # decodes to `continue-on-error`
#   "strateg\x79":              # decodes to `strategy`
#
# is byte-for-byte NOT the string R14 was matching, so R14's per-key
# counters and canonical-line grep-qxF checks stay green while a
# compliant YAML parser (verified below with PyYAML 6.0.3, matching
# the psych parser GitHub Actions uses) silently overrides the
# canonical entry under duplicate-key last-wins semantics — restoring
# the exact bypass class R11-R14 were meant to close.
#
# R15 adds a zero-tolerance shape invariant (`_assert_no_quoted_key_
# at_indents`) that rejects ANY quoted YAML key at the protected
# indents regardless of what characters or escapes the quoted content
# decodes to. Because this workflow intentionally uses ONLY canonical
# unquoted keys at those indents, the invariant is safe and catches
# the entire class without enumerating escape forms.
#
# Every mutation below KEEPS the canonical unquoted line intact and
# ADDS an escape-encoded quoted duplicate/shadow — R14's counters
# and canonical-line assertions stay green (they only see the
# literal canonical line, unchanged) so the R15 quoted-key check is
# what actually fires. This is deliberate: if a future refactor
# weakens R15, these tests must still fail (via a different R14 or
# canonical assertion) OR the reviewer must consciously accept the
# regression by updating both the guard and these expectations.
# =============================================================================

# R15 sanity: prove the escape-encoded quoted keys used by the
# mutations below actually decode to the canonical key names under a
# real YAML parser. This is a whitebox validation of the attack
# surface, not a runtime gate on ci.yml — the static shape guard is
# the enforcement mechanism. If python3 or PyYAML is unavailable
# (missing on some minimal CI images), the case logs the reason and
# returns 0 so the suite stays green on such runners; the individual
# mutation cases still exercise the shape guard directly.
#
# PyYAML duplicate-key handling (verified with 6.0.3): safe_load
# silently accepts duplicate keys and returns a dict where the LAST
# occurrence wins. That matches GitHub Actions' behaviour closely
# enough that a duplicate `matrix:` + `"matri\x78":` pair would
# silently swap the selector-driven matrix for the escaped one at
# runtime, hiding three of four EF pairs from the drift check while
# reporting migration-drift as passing.
case_escape_hidden_keys_are_yaml_equivalent() {
  if ! command -v python3 >/dev/null 2>&1; then
    printf '  SKIP: python3 not available; static shape guard cases still cover the attack\n' >&2
    return 0
  fi
  if ! python3 -c 'import yaml' 2>/dev/null; then
    printf '  SKIP: PyYAML not available; static shape guard cases still cover the attack\n' >&2
    return 0
  fi

  local snippet_file result
  snippet_file="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$snippet_file'" RETURN
  # Six escape-encoded quoted keys, one per canonical name the R15
  # mutations target. Each key's value is a distinct sentinel so the
  # decoded dict's shape unambiguously proves the escape mapping.
  cat >"$snippet_file" <<'YAML_ESCAPE_SNIPPET'
"matri\x78": v_matrix
"i\x66": v_if
"migration\x2ddrift": v_mig
"fail\x2dfast": v_ff
"continue\x2don\x2derror": v_coe
"strateg\x79": v_strategy
YAML_ESCAPE_SNIPPET

  result="$(python3 - "$snippet_file" <<'PY_DECODE_CHECK'
import sys, yaml
with open(sys.argv[1], 'r', encoding='utf-8') as fh:
    d = yaml.safe_load(fh)
targets = {
    'matrix':            'v_matrix',
    'if':                'v_if',
    'migration-drift':   'v_mig',
    'fail-fast':         'v_ff',
    'continue-on-error': 'v_coe',
    'strategy':          'v_strategy',
}
for k, v in targets.items():
    if d.get(k) != v:
        print(f'MISMATCH: canonical key {k!r} not present or has wrong value; got {d.get(k)!r} expected {v!r}')
        sys.exit(1)
print('OK: all escape-encoded quoted keys decoded to canonical names')
PY_DECODE_CHECK
)"
  local rc=$?
  if (( rc != 0 )); then
    printf '  yaml decode check failed: %s\n' "$result" >&2
    return 1
  fi
  if [[ "$result" != OK:* ]]; then
    printf '  unexpected yaml decode result: %s\n' "$result" >&2
    return 1
  fi
  return 0
}

# R15 mutation: append `  "migration\x2ddrift":` shadow job header
# using the escape-encoded double-quoted spelling. YAML decodes
# `\x2d` to `-`, so the shadow header resolves to the same key as
# the canonical unquoted `  migration-drift:`. R14's
# `_count_yaml_key_at "$workflow_body" 2 migration-drift` counts
# only the three literal spellings of the string `migration-drift`
# (unquoted / "migration-drift" / 'migration-drift'), so this
# spelling does not increment the counter — R14 stays at count=1
# and the canonical grep-qxF check still finds the canonical line
# unchanged. Under duplicate-key last-wins semantics the shadow
# body then takes over `needs.migration-drift.result`. R15's
# workflow-scope zero-quoted-key check at 2-space indent catches
# the shadow header before duplicate-key semantics can take effect.
case_drift_shape_rejects_escaped_shadow_migration_drift_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Append the escape-encoded shadow header + minimal inert body.
  # The `\x2d` escape MUST reach the mutant file as a literal 4-char
  # sequence `\`, `x`, `2`, `d`; single-quoted printf preserves the
  # backslash and the `%s\n` format only interprets `\n`.
  if ! {
    cat "$mutant"
    printf '%s\n' '  "migration\x2ddrift":' \
      '    if: false' \
      '    runs-on: ubuntu-latest' \
      '    steps:' \
      '      - run: "echo shadow"'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  _assert_shape_guard_rejects "escape-encoded shadow migration-drift header" "$mutant" \
    "workflow: quoted YAML key not allowed at 2-space indent" || return 1
}

# R15 mutation: inject `      "matri\x78": {...}` at 6-space
# strategy-child indent, alongside the canonical selector-driven
# matrix line. `\x78` decodes to `x`, so the escaped key resolves
# to `matrix`. R14's `_count_yaml_key_at "$job_block" 6 matrix`
# counts only literal `matrix` spellings (matri x is not a
# substring match at parse time — it's a byte-level regex), so the
# escaped duplicate leaves R14's matrix_count at 1 and the canonical
# grep-qxF check still finds the canonical selector-driven line.
# Under duplicate-key last-wins the escaped hand-picked matrix would
# then execute — skipping three of the four EF pairs while reporting
# migration-drift as passing. R15's job-scope zero-quoted-key check
# at 6-space indent rejects the escaped line before the parser sees
# it as an override.
case_drift_shape_rejects_escaped_duplicate_matrix() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Inject the escaped duplicate immediately after the canonical
  # matrix line. `\\x78` in the awk string literal parses as `\`
  # (from `\\`) followed by `x78` (literal), producing the 4-char
  # sequence `\x78` in the mutant file — which is what YAML will
  # then decode back to `x`.
  # shellcheck disable=SC2016  # $0 is awk field ref, not shell param
  _mutate "$mutant" '
    { sub(/\r$/, ""); print $0 "\r" }
    $0 == "      matrix: ${{ fromJson(needs.select.outputs.mig_matrix) }}" && !inserted {
      print "      \"matri\\x78\": {include: [{name: \"AppPg\", label: \"App/Pg\", project: \"migrations/Farm.Migrations.PostgreSQL\", context: \"AppDbContext\", provider: \"postgres\"}]}\r"
      inserted = 1
    }
  ' || return 1

  _assert_shape_guard_rejects "escape-encoded duplicate matrix" "$mutant" \
    "migration-drift job: quoted YAML key not allowed at 6-space indent" || return 1
}

# R15 mutation: inject `    "i\x66": false` at 4-space job-level
# indent, alongside the canonical selection `if:`. `\x66` decodes to
# `f`, so the escaped key resolves to `if`. R14's
# `_count_yaml_key_at "$job_block" 4 if` counts only literal `if`
# spellings and stays at 1; the canonical if-line grep-qxF also still
# passes. Under duplicate-key last-wins the escaped `if: false` would
# then skip the migration-drift job unconditionally, silently
# short-circuiting the fail-closed selector-driven gating. R15's
# job-scope zero-quoted-key check at 4-space indent rejects.
case_drift_shape_rejects_escaped_duplicate_if() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Inject the escaped duplicate immediately after the canonical
  # `    if: ${{ ... }}` line.
  # shellcheck disable=SC2016  # $0 is awk field ref, not shell param
  _mutate "$mutant" '
    { sub(/\r$/, ""); print $0 "\r" }
    $0 == "    if: ${{ needs.select.outputs.want_mig_drift == '\''true'\'' }}" && !inserted {
      print "    \"i\\x66\": false\r"
      inserted = 1
    }
  ' || return 1

  _assert_shape_guard_rejects "escape-encoded duplicate if" "$mutant" \
    "migration-drift job: quoted YAML key not allowed at 4-space indent" || return 1
}

# R15 mutation: inject `      "fail\x2dfast": true` at 6-space
# strategy-child indent, alongside the canonical `fail-fast: false`.
# `\x2d` decodes to `-`, so the escaped key resolves to `fail-fast`.
# R14's `_count_yaml_key_at "$job_block" 6 fail-fast` counts only
# the literal `fail-fast` spellings and stays at 1; the canonical
# grep-qxF `fail-fast: false` still matches. Under duplicate-key
# last-wins the escaped `fail-fast: true` would then cancel sibling
# matrix legs on the first failure, hiding drift on the cancelled
# legs. R15's job-scope zero-quoted-key check at 6-space indent
# rejects.
case_drift_shape_rejects_escaped_duplicate_fail_fast() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Inject the escaped duplicate immediately after the canonical
  # `      fail-fast: false` line.
  _mutate "$mutant" '
    { sub(/\r$/, ""); print $0 "\r" }
    $0 == "      fail-fast: false" && !inserted {
      print "      \"fail\\x2dfast\": true\r"
      inserted = 1
    }
  ' || return 1

  _assert_shape_guard_rejects "escape-encoded duplicate fail-fast" "$mutant" \
    "migration-drift job: quoted YAML key not allowed at 6-space indent" || return 1
}

# R15 mutation: inject `    "continue\x2don\x2derror": true` at
# 4-space job-level indent inside the migration-drift job. Both
# `\x2d` escapes decode to `-`, so the escaped key resolves to
# `continue-on-error`. R14's `_count_yaml_key_at "$job_block" 4
# continue-on-error` counts only the literal `continue-on-error`
# spellings and would find zero (there is no canonical
# `continue-on-error` at job scope — the invariant is "count == 0")
# — so R14 stays green with the escaped shadow present. Under a
# compliant YAML parser the job-level `continue-on-error: true`
# then makes step failures non-fatal, letting the migration-drift
# job report success while the drift step exited non-zero. R15's
# job-scope zero-quoted-key check at 4-space indent rejects.
case_drift_shape_rejects_escaped_job_continue_on_error() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Insert the escaped job-level continue-on-error immediately after
  # the migration-drift job's `name:` line at 4-space indent.
  _mutate "$mutant" '
    { sub(/\r$/, ""); print $0 "\r" }
    inside && !inserted && /^    name:/ {
      print "    \"continue\\x2don\\x2derror\": true\r"
      inserted = 1
    }
    $0 == "  migration-drift:" { inside = 1 }
  ' || return 1

  _assert_shape_guard_rejects "escape-encoded job-level continue-on-error" "$mutant" \
    "migration-drift job: quoted YAML key not allowed at 4-space indent" || return 1
}

# R15 mutation: inject `        "continue\x2don\x2derror": true` at
# 8-space step-item indent, inside the `Check EF Core migration drift`
# step. `\x2d` decodes to `-`, so the escaped key resolves to
# `continue-on-error`. R14's canonical step-block diff would catch an
# UNQUOTED `        continue-on-error: true` addition, but the escape-
# encoded quoted form is a different byte sequence and the diff would
# fire with a "does not match canonical snapshot" diagnostic that
# does not surface the security impact. R15's job-scope zero-quoted-
# key check at 8-space indent fires FIRST (positioned before the
# block diff in `_check_drift_step_shape`) with a specific quoted-key
# diagnostic that makes the intent obvious. This also matters when
# the mutation is subtler than a full step-block rewrite — a single-
# line quoted-key insertion should reject on shape alone, not require
# the reviewer to chase a diff.
case_drift_shape_rejects_escaped_step_continue_on_error() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Insert the escaped step-level continue-on-error immediately after
  # the drift step's `        working-directory: src` line inside the
  # `Check EF Core migration drift` step. The awk state machine tracks
  # whether we're inside the drift step so we don't accidentally
  # mutate the earlier `Restore migration project` step (which has
  # the same working-directory line).
  _mutate "$mutant" '
    { sub(/\r$/, ""); print $0 "\r" }
    $0 == "      - name: Check EF Core migration drift" { in_drift = 1 }
    in_drift && !inserted && $0 == "        working-directory: src" {
      print "        \"continue\\x2don\\x2derror\": true\r"
      inserted = 1
    }
  ' || return 1

  _assert_shape_guard_rejects "escape-encoded step-level continue-on-error" "$mutant" \
    "migration-drift job: quoted YAML key not allowed at 8-space indent" || return 1
}

# R15 mutation: inject `    "strateg\x79":` at 4-space job-level
# indent, alongside the canonical `    strategy:` block. `\x79`
# decodes to `y`, so the escaped key resolves to `strategy`. R14's
# `_count_yaml_key_at "$job_block" 4 strategy` counts only the
# literal `strategy` spellings and stays at 1; the canonical
# grep-qxF `    strategy:` still matches. Under duplicate-key
# last-wins the escaped strategy block (whatever fail-fast/matrix
# children it declared, or an empty block that resets both to
# defaults) would then override the canonical strategy — silently
# restoring fail-fast:true and dropping the selector-driven matrix.
# R15's job-scope zero-quoted-key check at 4-space indent rejects.
case_drift_shape_rejects_escaped_duplicate_strategy() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Inject the escaped duplicate immediately after the canonical
  # `    strategy:` line at 4-space indent; deliberately empty (no
  # children) so the mutation surface stays surgical — the guard
  # must reject on the quoted-key SHAPE alone, regardless of the
  # duplicate block's contents.
  _mutate "$mutant" '
    { sub(/\r$/, ""); print $0 "\r" }
    $0 == "    strategy:" && !inserted {
      print "    \"strateg\\x79\":\r"
      inserted = 1
    }
  ' || return 1

  _assert_shape_guard_rejects "escape-encoded duplicate strategy" "$mutant" \
    "migration-drift job: quoted YAML key not allowed at 4-space indent" || return 1
}

# R14 focused test: `_mutate` must propagate awk's real exit status
# on failure. The R13 implementation captured `$?` INSIDE the
# `then` branch of `if ! awk …; then rc=$?; …`, at which point `$?`
# is 0 (the negation's own result), not awk's exit code — so on
# awk failure `_mutate` reported success with rc=0 while leaving the
# original file untouched, causing downstream `_assert_shape_guard_
# rejects` calls to test against an unmutated workflow (whose shape
# guard rightly PASSES) and report false positives for the mutation
# case. R14 runs awk directly, captures `$?` on the next line, and
# branches on the captured value. This test exercises that path by
# passing an awk program with a hard syntax error and asserting:
#   (a) `_mutate` returns non-zero,
#   (b) the target file is byte-identical to its pre-mutation content,
#   (c) the `.tmp` sibling is cleaned up on failure.
case_mutate_helper_propagates_awk_failure() {
  local target
  target="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$target' '${target}.tmp'" RETURN
  printf 'canonical content\n' > "$target"

  # Awk program with a hard syntax error — no valid interpretation.
  # Every awk implementation (gawk, mawk, BSD awk) exits non-zero
  # when it cannot parse the program at all.
  local rc=0
  _mutate "$target" 'BEGIN { !!! not valid awk !!! }' 2>/dev/null || rc=$?
  if (( rc == 0 )); then
    printf '  _mutate returned success on awk syntax error\n' >&2
    return 1
  fi

  # Original file must be byte-identical to its pre-mutation content.
  local content
  content="$(cat "$target")"
  if [[ "$content" != "canonical content" ]]; then
    printf '  _mutate mutated file despite awk failure\n' >&2
    printf '    expected: %q\n' "canonical content" >&2
    printf '    actual:   %q\n' "$content" >&2
    return 1
  fi

  # The `.tmp` sibling must be cleaned up on failure — a leaked tmp
  # would collide with the next _mutate call on the same file path
  # and (silently) short-circuit the rewrite.
  if [[ -e "${target}.tmp" ]]; then
    printf '  _mutate left tmp file behind on awk failure: %s.tmp\n' "$target" >&2
    return 1
  fi

  return 0
}


# =============================================================================
# Runner
# =============================================================================

TESTS=(
  case_react_only
  case_docs_only
  case_api_change
  case_infra_change
  case_infra_entity_change_selects_app_drift
  case_infra_configuration_change_selects_app_drift
  case_backend_plugin_change
  case_backend_plugin_change_flashforge
  case_backend_core_change_selects_both_tests
  case_backend_core_nested_path_selects_both_tests
  case_backend_core_and_plugin_mixed
  case_selector_backend_core_pattern_precedes_plugin
  case_slicer_change
  case_orca_worker_change
  case_migration_app_change
  case_migration_slicer_change
  case_test_only_api
  case_test_only_slicer
  case_tests_other_full_safe
  case_unknown_src_path
  case_shared_config_change
  case_shared_package_config_change
  case_ci_workflow_change
  case_hook_file_change
  case_ci_script_change
  case_tools_only_build_no_tests
  case_mobile_change_no_dotnet
  case_push_to_development_full_safe
  case_push_to_main_full_safe
  case_workflow_trusted_pushes_unfiltered
  case_workflow_dispatch_full_safe
  case_force_full_safe_from_caller
  case_empty_changes
  case_missing_github_output
  case_z_file_with_null_terminators
  case_z_file_not_terminated_forces_full_safe
  case_git_quoted_path_forces_full_safe
  case_hostile_metachar_in_reason_stripped
  case_multi_bucket_dedup
  case_devcontainer_change_full_safe
  case_discovery_full_safe
  case_settings_full_safe
  case_mixed_react_and_dotnet
  case_selector_uses_bash32_compatible_dedup
  case_selector_dedup_safe_for_empty_arrays
  case_selector_finish_tolerates_empty_args
  case_extract_event_block_crlf_tolerant
  case_workflow_publish_printf_option_safe
  case_workflow_migration_drift_restores_before_ef
  case_drift_run_body_extractor_crlf_tolerant
  case_drift_run_body_extractor_bails_on_zero_indent
  case_drift_run_body_extractor_bails_on_tab_indent
  case_drift_step_block_extractor_crlf_tolerant
  case_drift_shape_rejects_step_continue_on_error
  case_drift_shape_rejects_step_if
  case_drift_shape_rejects_job_continue_on_error
  case_drift_shape_rejects_duplicate_drift_step
  case_drift_shape_rejects_malformed_indentation
  case_drift_shape_rejects_hardcoded_matrix
  case_drift_shape_rejects_block_style_matrix
  case_drift_shape_rejects_fail_fast_true
  case_drift_shape_rejects_duplicate_migration_drift_job
  case_drift_shape_rejects_quoted_duplicate_matrix
  case_drift_shape_rejects_duplicate_fail_fast_true
  case_drift_shape_rejects_quoted_job_continue_on_error
  case_drift_shape_rejects_duplicate_strategy
  case_drift_shape_rejects_quoted_strategy
  case_drift_shape_rejects_quoted_shadow_migration_drift_job
  case_escape_hidden_keys_are_yaml_equivalent
  case_drift_shape_rejects_escaped_shadow_migration_drift_job
  case_drift_shape_rejects_escaped_duplicate_matrix
  case_drift_shape_rejects_escaped_duplicate_if
  case_drift_shape_rejects_escaped_duplicate_fail_fast
  case_drift_shape_rejects_escaped_job_continue_on_error
  case_drift_shape_rejects_escaped_step_continue_on_error
  case_drift_shape_rejects_escaped_duplicate_strategy
  case_mutate_helper_propagates_awk_failure
)

printf '=== select-dotnet-tests.sh test suite ===\n'
for t in "${TESTS[@]}"; do
  run_case "$t" "$t"
done

printf '\n=== summary ===\n'
printf 'passed: %d\n' "$PASSED"
printf 'failed: %d\n' "$FAILED"
if (( FAILED > 0 )); then
  printf 'failing cases:\n'
  for n in "${FAILED_NAMES[@]}"; do
    printf '  - %s\n' "$n"
  done
  exit 1
fi
exit 0
