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

# assert_required_matrix <matrix_json>
#
# Full-safe selections must include every required project, including projects
# that intentionally live outside farm-web.sln and are executed project-scoped.
assert_required_matrix() {
  local matrix="$1" name
  for name in \
      Farm.Web.Api.Tests \
      Farm.Slicer.Module.Tests \
      Farm.OrcaSlicer.Worker.Tests \
      Farm.Web.IntegrationTests; do
    assert_contains "required matrix project" "$matrix" "\"name\":\"$name\"" || return 1
  done
  assert_contains "integration opt-in" "$matrix" '"name":"Farm.Web.IntegrationTests"' || return 1
  assert_contains "integration run flag" "$matrix" '"run_integration":"true"' || return 1
  assert_contains "default api filter" "$matrix" '"filter":"Category!=DbHeavy&Category!=Docker"' || return 1
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
  assert_contains "matrix integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
  assert_contains "api filter" "$matrix" '"filter":"Category!=DbHeavy&Category!=Docker"' || return 1
  assert_contains "integration opt-in" "$matrix" '"run_integration":"true"' || return 1
  assert_not_contains "no orca for api-only" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
}

case_auth_forwarded_headers_change_selects_integration() {
  local out="$1"
  CHANGED_FILES="src/api/Configuration/ForwardedHeadersConfiguration.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "auth api selects integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
  assert_contains "auth integration opt-in" "$matrix" '"run_integration":"true"' || return 1
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
  assert_contains "matrix orca" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
  assert_contains "matrix integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
  assert_contains "matrix smartplug" "$matrix" "Farm.Modules.SmartPlug.Tests" || return 1
  assert_contains "matrix printqueue" "$matrix" "Farm.Modules.PrintQueue.Tests" || return 1
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
  assert_contains "matrix integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
  assert_contains "integration opt-in" "$matrix" '"run_integration":"true"' || return 1
  # Farm.Modules.PrintQueue.Tests (issue #2040) references Farm.Backend.Plugin.OctoPrint
  # directly, so any backend-plugin edit must also select it.
  assert_contains "matrix printqueue" "$matrix" "Farm.Modules.PrintQueue.Tests" || return 1
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
  assert_contains "matrix integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
  assert_not_contains "matrix slicer absent" "$matrix" "Farm.Slicer.Module.Tests" || return 1
}

# Farm.Backend.Plugin.Core is the shared plugin abstraction. It is a direct
# ProjectReference of Farm.Web.Api.Tests and is transitively consumed by slicer,
# worker, and integration-test graphs. A Core edit must therefore select every
# directly affected required test project, not just Api.Tests.
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
  assert_contains "matrix orca" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
  assert_contains "matrix integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
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
  assert_contains "matrix orca nested" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
  assert_contains "matrix integration nested" "$matrix" "Farm.Web.IntegrationTests" || return 1
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
  assert_contains "matrix orca mixed" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
  assert_contains "matrix integration mixed" "$matrix" "Farm.Web.IntegrationTests" || return 1
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
  local orca_count integration_count
  orca_count="$(grep -o '"name":"Farm\.OrcaSlicer\.Worker\.Tests"' <<< "$matrix" | wc -l | tr -d ' ')"
  integration_count="$(grep -o '"name":"Farm\.Web\.IntegrationTests"' <<< "$matrix" | wc -l | tr -d ' ')"
  if [[ "$orca_count" != "1" ]]; then
    printf '  orca appears %s times in mixed matrix: %s\n' "$orca_count" "$matrix" >&2
    return 1
  fi
  if [[ "$integration_count" != "1" ]]; then
    printf '  integration appears %s times in mixed matrix: %s\n' "$integration_count" "$matrix" >&2
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
  assert_contains "matrix orca" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
  assert_contains "matrix integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
  assert_contains "matrix printqueue" "$matrix" "Farm.Modules.PrintQueue.Tests" || return 1
  local mig ; mig="$(get_output "$out" mig_matrix)"
  assert_contains "mig slicer pg" "$mig" "SlicerPg" || return 1
  assert_contains "mig slicer sql" "$mig" "SlicerSqlServer" || return 1
}

case_orca_worker_change() {
  local out="$1"
  CHANGED_FILES="src/orcaslicer-worker/Program.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "true" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix orca" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
  assert_not_contains "no api" "$matrix" "Farm.Web.Api.Tests" || return 1
  local reason ; reason="$(get_output "$out" reason)"
  assert_contains "reason orca" "$reason" "orcaslicer-worker" || return 1
}

case_smartplug_change() {
  local out="$1"
  CHANGED_FILES="src/modules/Farm.Modules.SmartPlug/Services/SmartPlug/KasaSmartPlugProvider.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "true" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix smartplug" "$matrix" "Farm.Modules.SmartPlug.Tests" || return 1
  # AdminPowerMonitorsController's own coverage (RouteTableSnapshotTests,
  # AdminPowerMonitorsControllerTests) intentionally stayed in
  # Farm.Web.Api.Tests, so a controller-owning module must select it too.
  assert_contains "api covers controller" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_not_contains "no orca" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
  local reason ; reason="$(get_output "$out" reason)"
  assert_contains "reason smartplug" "$reason" "smartplug" || return 1
}

case_test_only_smartplug() {
  local out="$1"
  CHANGED_FILES="src/tests/Farm.Modules.SmartPlug.Tests/Services/SmartPlug/KasaSmartPlugProviderTests.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix smartplug" "$matrix" "Farm.Modules.SmartPlug.Tests" || return 1
  assert_not_contains "no api" "$matrix" "Farm.Web.Api.Tests" || return 1
}

case_smartplug_mixed_with_unrelated_backend() {
  local out="$1"
  CHANGED_FILES=$'src/modules/Farm.Modules.SmartPlug/Services/PowerMonitor/PowerMonitorPollingService.cs\nsrc/backends/Farm.Backends.SomePlugin/SomeFile.cs'
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix smartplug" "$matrix" "Farm.Modules.SmartPlug.Tests" || return 1
}

case_printqueue_change() {
  local out="$1"
  CHANGED_FILES="src/modules/Farm.Modules.PrintQueue/Services/PrintQueue/PrintJobManagementService.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "true" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix printqueue" "$matrix" "Farm.Modules.PrintQueue.Tests" || return 1
  # The Dispatch/ integration suite and RouteTableSnapshotTests intentionally
  # stayed in Farm.Web.Api.Tests (see docs/MODULE_MIGRATION_PATTERN.md), so a
  # controller-owning module must select it too.
  assert_contains "api covers controller" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_not_contains "no orca" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
  local reason ; reason="$(get_output "$out" reason)"
  assert_contains "reason printqueue" "$reason" "printqueue" || return 1
}

case_test_only_printqueue() {
  local out="$1"
  CHANGED_FILES="src/tests/Farm.Modules.PrintQueue.Tests/Controllers/JobQueueControllerTests.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix printqueue" "$matrix" "Farm.Modules.PrintQueue.Tests" || return 1
  assert_not_contains "no api" "$matrix" "Farm.Web.Api.Tests" || return 1
}

case_printqueue_mixed_with_unrelated_backend() {
  local out="$1"
  CHANGED_FILES=$'src/modules/Farm.Modules.PrintQueue/Controllers/JobQueueController.cs\nsrc/backends/Farm.Backends.SomePlugin/SomeFile.cs'
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix printqueue" "$matrix" "Farm.Modules.PrintQueue.Tests" || return 1
}

case_migration_app_change() {
  local out="$1"
  CHANGED_FILES="src/migrations/Farm.Migrations.PostgreSQL/Migrations/20260101_AddThing.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "true" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix api" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_contains "matrix integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
  assert_not_contains "no slicer test" "$matrix" "Farm.Slicer.Module.Tests" || return 1
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
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "true" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix api" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_contains "matrix slicer" "$matrix" "Farm.Slicer.Module.Tests" || return 1
  assert_contains "matrix integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
  assert_not_contains "no orca" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
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

case_test_only_orca() {
  local out="$1"
  CHANGED_FILES="src/tests/Farm.OrcaSlicer.Worker.Tests/ProfilesTests.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix orca" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
  assert_not_contains "no integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
}

case_test_only_integration() {
  local out="$1"
  CHANGED_FILES="src/tests/Farm.Web.IntegrationTests/AuthenticationTests.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
  assert_contains "integration opt-in" "$matrix" '"run_integration":"true"' || return 1
  assert_not_contains "no orca" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
}

case_unknown_test_project_full_safe() {
  local out="$1"
  CHANGED_FILES="src/tests/Farm.Future.Tests/Foo.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
  assert_required_matrix "$(get_output "$out" matrix)" || return 1
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
  assert_required_matrix "$(get_output "$out" matrix)" || return 1
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
  assert_required_matrix "$(get_output "$out" matrix)" || return 1
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

case_ci_other_workflow_change_no_dotnet() {
  # Regression (#1562): editing an unrelated workflow must not force the
  # full .NET matrix. Only .github/workflows/ci.yml (and the other narrow
  # ci_selector paths) remain full-safe; every other workflow is ci_other.
  local out="$1"
  CHANGED_FILES=".github/workflows/docs-health.yml"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_frontend" "$(get_output "$out" want_frontend)" "false" || return 1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "false" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "false" || return 1
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "false" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local reason ; reason="$(get_output "$out" reason)"
  assert_contains "reason ci-other" "$reason" "ci-other" || return 1
}

case_ci_other_ci_script_change_no_dotnet() {
  # A script under scripts/ci/** that is NOT select-dotnet-tests.sh or
  # compute-change-set.sh (e.g. its own test file, or an unrelated CI script)
  # must be ci_other, not ci_selector.
  local out="$1"
  CHANGED_FILES="scripts/ci/tests/test-daily-development-images.mjs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "false" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "false" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
}

case_compute_change_set_script_change_full_safe() {
  # scripts/ci/compute-change-set.sh feeds the selector's own input and must
  # remain full-safe.
  local out="$1"
  CHANGED_FILES="scripts/ci/compute-change-set.sh"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
}

case_pr1562_file_set_narrowed_no_dotnet() {
  # Regression (#1562): this exact three-file change set must no longer
  # force the full .NET matrix.
  local out="$1"
  CHANGED_FILES=$'.github/workflows/daily-development-images.yml\nscripts/ci/tests/test-daily-development-images.mjs\nscripts/docker/dockerfiles/Dockerfile.multistage'
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "false" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "false" || return 1
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "false" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  assert_eq "matrix" "$(get_output "$out" matrix)" '{"include":[]}' || return 1
}

case_ci_other_mixed_with_api_still_selects_api() {
  # A ci_other workflow change alongside a real .NET change must not
  # suppress the real signal — api-scoped tests/migrations still run.
  local out="$1"
  CHANGED_FILES=$'.github/workflows/docs-health.yml\nsrc/api/Controllers/PrintersController.cs'
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "true" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "true" || return 1
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "true" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "matrix api" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_contains "matrix integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
  local mig ; mig="$(get_output "$out" mig_matrix)"
  assert_contains "mig app pg" "$mig" '"name":"AppPg"' || return 1
  local reason ; reason="$(get_output "$out" reason)"
  assert_contains "reason ci-other" "$reason" "ci-other" || return 1
  assert_contains "reason api" "$reason" "api" || return 1
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

# -----------------------------------------------------------------------------
# Regression (#1397): github.event.pull_request.base.sha drifts ahead of the
# PR's actual fork point whenever the base branch advances while the PR is
# open (e.g. other PRs merging into `development`). The `Compute change set`
# step must diff against `git merge-base base_sha head_sha`, not `base_sha`
# directly, or those unrelated base-branch commits get folded into the PR's
# own changed-file set — spuriously widening the selector's decision (e.g.
# forcing the full .NET/migration-drift/CodeQL matrix on a mobile-only PR
# just because an unrelated commit touching scripts/ci/** landed on
# `development` in the meantime).
#
# This builds a real scratch git repo reproducing the exact scenario:
#   1. A fork-point commit on `development`.
#   2. A PR branch forked from it, adding only a mobile/** file.
#   3. Further commits on `development` — AFTER the fork — that touch
#      `scripts/ci/**` (an `ci_selector` path). These simulate unrelated
#      work landing on the base branch while the PR is open, and are what
#      `github.event.pull_request.base.sha` would resolve to.
#   4. scripts/ci/compute-change-set.sh is invoked exactly as the workflow
#      invokes it, with PR_BASE_SHA set to the DRIFTED development tip
#      (not the fork point) and PR_HEAD_SHA set to the PR branch head.
#   5. The resulting changed-file set is fed into select-dotnet-tests.sh.
#
# Asserts the changed-file set contains only the PR's own mobile/** file
# (not the unrelated scripts/ci/** file), and that the selector therefore
# reports want_dotnet_build=false, want_dotnet_test=false,
# want_mig_drift=false, full_matrix=false — exactly the outcome PR #1393
# should have gotten had this fix been in place.
case_merge_base_diverged_pr_base_sha_mobile_only() {
  local out="$1"
  local repo
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  (
    set -e
    cd "$repo"
    git init -q -b development
    git config user.email "test@example.com"
    git config user.name "Test"

    printf 'root\n' > README.md
    git add README.md
    git commit -q -m "fork point"
    local fork_point
    fork_point="$(git rev-parse HEAD)"

    # PR branch forked here, touching only a mobile/** file.
    git checkout -q -b pr-branch
    mkdir -p mobile/PrintFarmer
    printf 'struct View {}\n' > mobile/PrintFarmer/View.swift
    git add mobile/PrintFarmer/View.swift
    git commit -q -m "mobile-only PR change"
    local pr_head
    pr_head="$(git rev-parse HEAD)"

    # Unrelated commits land on development AFTER the fork, while the PR is
    # open — this is what makes github.event.pull_request.base.sha drift.
    git checkout -q development
    mkdir -p scripts/ci
    printf '#!/usr/bin/env bash\necho unrelated\n' > scripts/ci/unrelated-tool.sh
    git add scripts/ci/unrelated-tool.sh
    git commit -q -m "unrelated ci_selector change landing on development"
    local drifted_base
    drifted_base="$(git rev-parse HEAD)"

    if [[ "$drifted_base" == "$fork_point" ]]; then
      echo "setup error: development did not advance past fork point" >&2
      exit 1
    fi

    local z_file changed_output
    z_file="$repo/.changed.z"
    changed_output="$repo/.changed-output"

    EVENT_NAME="pull_request" \
      PR_BASE_SHA="$drifted_base" \
      PR_HEAD_SHA="$pr_head" \
      OUT_FILE="$z_file" \
      GITHUB_OUTPUT="$changed_output" \
      bash "$REPO_ROOT/scripts/ci/compute-change-set.sh"

    local changed_files
    changed_files="$(tr '\0' '\n' < "$z_file")"

    if [[ "$changed_files" == *"scripts/ci/unrelated-tool.sh"* ]]; then
      echo "FAIL: unrelated development commit leaked into PR diff (merge-base fix not applied)" >&2
      exit 1
    fi
    if [[ "$changed_files" != *"mobile/PrintFarmer/View.swift"* ]]; then
      echo "FAIL: PR's own mobile file missing from diff" >&2
      exit 1
    fi

    EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
      CHANGED_FILES_FROM_Z="$z_file" CHANGED_FILES="" \
      GITHUB_OUTPUT="$out" \
      bash "$SELECTOR"
  ) || return 1

  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "false" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "false" || return 1
  assert_eq "want_mig_drift" "$(get_output "$out" want_mig_drift)" "false" || return 1
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

# Regression guard (#1397): the merge-base fix must be scoped to `pull_request`
# events only. The `push` event path's `before`/`after` are already a real
# ancestry pair on the trusted branch's own history (or, on a force-push, two
# states of the same ref that git diff can compare directly regardless of
# ancestry) — it must never be routed through `git merge-base`. This builds a
# scratch repo simulating a force-push on `development` where `before` is NOT
# an ancestor of `after` (the classic force-push edge case), and asserts
# compute-change-set.sh still diffs `before` directly against `after` and
# reports the force-pushed commit's own file, with no merge-base indirection.
case_compute_change_set_push_force_push_diffs_before_after_directly() {
  local repo
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  (
    set -e
    cd "$repo"
    git init -q -b development
    git config user.email "test@example.com"
    git config user.name "Test"

    printf 'root\n' > README.md
    git add README.md
    git commit -q -m "initial"

    printf 'before state\n' >> README.md
    git add README.md
    git commit -q -m "before commit"
    local before_sha
    before_sha="$(git rev-parse HEAD)"

    # Force-push edge case: reset to the initial commit and commit different
    # content, so `before_sha` is NOT an ancestor of the new tip (`after_sha`).
    git reset -q --hard HEAD~1
    printf 'force-pushed content\n' > force-pushed.txt
    git add force-pushed.txt
    git commit -q -m "force-pushed commit"
    local after_sha
    after_sha="$(git rev-parse HEAD)"

    if git merge-base --is-ancestor "$before_sha" "$after_sha" 2>/dev/null; then
      echo "setup error: before_sha must not be an ancestor of after_sha for this test" >&2
      exit 1
    fi

    local z_file changed_output
    z_file="$repo/.changed.z"
    changed_output="$repo/.changed-output"

    EVENT_NAME="push" \
      BEFORE_SHA="$before_sha" \
      AFTER_SHA="$after_sha" \
      OUT_FILE="$z_file" \
      GITHUB_OUTPUT="$changed_output" \
      bash "$REPO_ROOT/scripts/ci/compute-change-set.sh"

    local force_safe
    force_safe="$(get_output "$changed_output" force_full_safe)"
    if [[ -n "$force_safe" ]]; then
      echo "FAIL: push event diff failed (force_full_safe=$force_safe) — merge-base must not apply to push events" >&2
      exit 1
    fi

    local changed_files
    changed_files="$(tr '\0' '\n' < "$z_file")"
    if [[ "$changed_files" != *"force-pushed.txt"* ]]; then
      echo "FAIL: push event diff did not report the force-pushed file: $changed_files" >&2
      exit 1
    fi
  ) || return 1
  return 0
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

case_devcontainer_change_no_dotnet() {
  # .devcontainer/** does not govern .NET test selection; it is ci_other,
  # not full-safe.
  local out="$1"
  CHANGED_FILES=".devcontainer/devcontainer.json"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "want_dotnet_build" "$(get_output "$out" want_dotnet_build)" "false" || return 1
  assert_eq "want_dotnet_test" "$(get_output "$out" want_dotnet_test)" "false" || return 1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "false" || return 1
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

case_modules_full_safe() {
  # src/modules/** (Farm.Modules.Abstractions, issue #2035) is foundational
  # like discovery/settings -- treated as full-safe.
  local out="$1"
  CHANGED_FILES="src/modules/Farm.Modules.Abstractions/IApiModule.cs"
  EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="$CHANGED_FILES" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
}

case_tests_modules_full_safe() {
  # src/tests/Farm.Modules.Abstractions.Tests/** is not yet wired into a
  # narrower path-selection bucket (mirrors Farm.Slicer.ProfileParsing.Tests
  # and Farm.Moonraker.Emulator.Tests) -- treated as full-safe.
  local out="$1"
  CHANGED_FILES="src/tests/Farm.Modules.Abstractions.Tests/CatalogNameNormalizerTests.cs"
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

case_workflow_test_job_passes_integration_property() {
  local workflow="$REPO_ROOT/.github/workflows/ci.yml"
  local body
  body="$(extract_job_block "$workflow" "dotnet-test")"
  if [[ -z "$body" ]]; then
    printf '  could not locate dotnet-test job body\n' >&2
    return 1
  fi
  assert_contains "matrix run flag env" "$body" 'RUN_INTEGRATION_TESTS: ${{ matrix.run_integration }}' || return 1
  assert_contains "restore integration property" "$body" 'dotnet restore "./$MATRIX_PROJECT" ${integration_args[@]+"${integration_args[@]}"}' || return 1
  assert_contains "build integration property" "$body" 'dotnet build "./$MATRIX_PROJECT" -c Debug --no-restore ${integration_args[@]+"${integration_args[@]}"}' || return 1
  assert_contains "test integration property" "$body" 'dotnet test "./$MATRIX_PROJECT" -c Debug --no-build \' || return 1
  assert_contains "test includes integration args" "$body" '${integration_args[@]+"${integration_args[@]}"} \' || return 1
  assert_contains "integration msbuild property" "$body" 'integration_args+=("-p:RunIntegrationTests=true")' || return 1
  local unsafe
  unsafe="$(printf '%s\n' "$body" \
    | grep -nF '"${integration_args[@]}"' \
    | grep -vF '${integration_args[@]+"${integration_args[@]}"}' \
    || true)"
  if [[ -n "$unsafe" ]]; then
    printf '  dotnet-test has unguarded integration_args expansions:\n%s\n' "$unsafe" >&2
    return 1
  fi
}


# extract_job_block <workflow> <job>
#
# Emit (to stdout) the yaml BODY of the named job — everything under
# `  <job>:` at 2-space indent, up to the next sibling job header or the
# next top-level key. The `  <job>:` header line itself is NOT emitted;
# workflow-scope shape assertions cover the header.
#
# Terminator rules (executed against CR-stripped input):
#   * `^[^ ]`     — a top-level key (column 0 non-space). Stop.
#   * `^  [^ #]`  — a 2-space non-comment content line. Stops at any
#                   sibling job header spelling — unquoted, quoted,
#                   tag-prefixed (`!!str`), anchor-prefixed (`&anchor`),
#                   explicit-key (`?`), flow-mapping (`{`, `[`) — each
#                   opens with a non-comment char at column 3, so the
#                   extractor terminates cleanly at any shadow header
#                   and never leaks its body into the canonical job.
# Comments (`  # ...`) and blank lines stay inside the body because a
# `#` at column 3 does not match `[^ #]` and a blank line matches
# neither `^[^ ]` nor `^  ...`.
#
# Bash 3.2 / POSIX awk only.
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

# extract_migration_drift_full_block <workflow>
#
# Emit (to stdout) the ENTIRE migration-drift job block INCLUDING its
# header line `  migration-drift:`, through immediately before the next
# sibling job or the next top-level key. Same terminator rules as
# `extract_job_block`. Trailing CRs stripped so a Windows-checkout
# worktree returns byte-identical output to a Linux-style checkout.
#
# Used by `_check_drift_full_job_snapshot` to compare the full canonical
# job block byte-for-byte against a snapshot heredoc. This subsumes the
# R11-R15 per-key invariants (job-header count, strategy.matrix pin,
# strategy.fail-fast pin, job-level continue-on-error, step-block
# snapshot, quoted-key counters, escape-encoded quoted-key sweeps) —
# any change inside the block (YAML representation, control keys,
# matrix source, step body, env, comments) reads as a snapshot
# mismatch regardless of parser semantics. Duplicate keys inside the
# block break the snapshot on the added line alone; there is no need
# to enumerate every YAML escape / tag / anchor spelling.
extract_migration_drift_full_block() {
  local workflow="$1"
  awk '
    { sub(/\r$/, "") }
    state == 0 && $0 == "  migration-drift:" {
      state = 1
      print
      next
    }
    state == 1 && /^[^ ]/ { exit }
    state == 1 && /^  [^ #]/ { exit }
    state == 1 { print }
  ' "$workflow"
}

# _check_drift_full_job_snapshot <workflow>
#
# R17 shape gate. R16 replaced R11-R15's stack of per-key invariants
# with a full-block byte snapshot + workflow-scope shape scan. R17
# closes the remaining gap discovered by adversarial review: appended
# plain-unquoted `migration-drift` key variants that satisfy neither
# the exact-line canonical count nor the indicator-character scan.
# Three orthogonal checks:
#
#   (1) Three-part workflow-scope key shape gate at 2-space indent:
#       (1a) EXACTLY-one canonical `  migration-drift:` line match
#            (`grep -cxE '^  migration-drift:$'`). Rejects missing-
#            canonical and plain duplicates whose whole line equals
#            the canonical.
#       (1c) EXACTLY-one CRLF-tolerant plain-key match
#            (`grep -cE '^  migration-drift[[:blank:]]*:'`). Rejects
#            `  migration-drift :` (space-before-colon), inline-comment
#            shadow `  migration-drift: # ...`, and inline-flow shadow
#            `  migration-drift: { ... }` — all of which resolve to
#            the same YAML key under duplicate-key last-wins semantics
#            but slip past (1a) because the whole line is not equal
#            to the canonical. Prefix variants like
#            `  migration-drift-extra:` do NOT match (the `-` breaks
#            `[[:blank:]]*:`) so no false positives.
#       (1b) Zero 2-space lines opening with a YAML node-property or
#            key indicator (`"` `'` `?` `!` `&` `*` `<` `{` `[`).
#            Every shadow-job form — quoted `"..."` / `'...'`, escape-
#            encoded `"migration\x2ddrift"`, tag-prefixed `!!str "..."`,
#            anchor-prefixed `&anchor "..."`, alias `*ref`, explicit-
#            key `? "..."`, flow-mapping `{...}:` / `[...]:` — opens
#            with one of those indicators and is rejected before
#            duplicate-key last-wins semantics can override the
#            canonical job. Legitimate 2-space keys in ci.yml
#            (`  push:`, `  pull_request:`, `  contents:`, `  group:`,
#            `  select:`, `  ci-tools:`, `  dotnet-build:`,
#            `  dotnet-test:`, `  summary:`, `  migration-drift:`,
#            etc.) all open with a letter, so the zero-tolerance
#            indicator scan is safe.
#
#   (2) A byte-for-byte comparison of the ENTIRE migration-drift job
#       block against the canonical snapshot heredoc below. Any change
#       to job-level control flow, strategy config, matrix source,
#       step body, env, or comments reads as a snapshot mismatch. A
#       reviewer intentionally editing the job MUST also update the
#       heredoc — that pairing is the review gate. The snapshot is
#       independent of YAML parser semantics: duplicate keys inside
#       the block, quoted-key spellings, escape sequences, tag /
#       anchor prefixes, and explicit-key forms all add or change
#       bytes that break the diff.
#
# Why the switch (R16, refined R17): enumerating every YAML spelling
# the parser accepts as the same key (as R11-R15 attempted with per-
# key counters and quoted-key sweeps) is unwinnable — every closed
# hole exposes another (`\x2d` -> `-`, `!!str`, `&anchor`, `? "..."`,
# flow-mapping keys, YAML 1.1 vs 1.2 reserved words, ...). A full-
# block snapshot, a canonical-header count, a plain-key CRLF-tolerant
# count, and an indicator-shape scan together anchor the invariant
# to bytes, not to enumeration of parser behaviours.
#
# Defence-in-depth: `ci-tools` already runs shellcheck (`-S warning`
# blocking) and `bash -n` on both this test script and the selector
# it exercises, and runs the selector regression suite itself, so a
# syntax or portability regression in the guard is caught before
# this gate ever runs against a mutation. `ci-tools` does not
# currently run actionlint on ci.yml, but any future addition of
# actionlint would layer cleanly on top of this static snapshot —
# actionlint validates schema/expression correctness while the
# snapshot pins the exact content the reviewer approved. The static
# snapshot below is the primary enforcement and does not depend on
# any external tool being installed at CI time.
_check_drift_full_job_snapshot() {
  local workflow="$1"

  # CR-normalise once so canonical comparisons are byte-for-byte
  # identical on Windows and Linux checkouts.
  local workflow_body
  workflow_body="$(awk '{ sub(/\r$/, ""); print }' "$workflow")"

  # (1a) Exactly one canonical unquoted `  migration-drift:` header at
  # 2-space workflow indent. `grep -cxE` requires the whole line to
  # match the anchored pattern; anything other than 1 (duplicate or
  # missing header) rejects. This complements (1b) below: (1b) rejects
  # noncanonical spellings; (1a) rejects missing-canonical AND plain
  # unquoted duplicates.
  local canonical_headers
  canonical_headers="$(printf '%s\n' "$workflow_body" | grep -cxE '^  migration-drift:$' || true)"
  if [[ "$canonical_headers" != "1" ]]; then
    printf '  expected exactly one canonical `  migration-drift:` job header, found %s\n' \
      "$canonical_headers" >&2
    return 1
  fi

  # (1c) CRLF-tolerant plain-key count. The (1a) `grep -cxE '^  migration-drift:$'`
  # requires the WHOLE line to equal `  migration-drift:` and therefore misses
  # plain-unquoted-key variants that still resolve to the same YAML key under
  # duplicate-key last-wins semantics:
  #
  #   `  migration-drift :`                      (whitespace before colon)
  #   `  migration-drift: # trailing comment`    (inline comment shadow)
  #   `  migration-drift: { runs-on: ..., steps: [{run: true}] }`
  #                                              (inline flow-mapping value)
  #
  # None open with an indicator character, so (1b) below also misses them.
  # This scan matches any 2-space unquoted `migration-drift` key with
  # optional horizontal whitespace before the colon and REGARDLESS of what
  # follows the colon. `workflow_body` is already CR-normalised so CRLF
  # line endings are handled transparently.
  #
  # Prefix variants (e.g. `  migration-drift-extra:`) do NOT match because
  # the character following `migration-drift` is `-` before any blanks or
  # colon — `[[:blank:]]*:` cannot bridge that. Comments and blank lines
  # never start with `migration-drift` at column 3 and are ignored.
  #
  # See `case_drift_shape_rejects_space_before_colon_plain_migration_drift_job`,
  # `case_drift_shape_rejects_inline_comment_shadow_migration_drift_job`,
  # `case_drift_shape_rejects_inline_flow_shadow_migration_drift_job`, and
  # the accepted fixture `case_drift_shape_accepts_migration_drift_extra_sibling_job`.
  local plain_key_count
  plain_key_count="$(printf '%s\n' "$workflow_body" | grep -cE '^  migration-drift[[:blank:]]*:' || true)"
  if [[ "$plain_key_count" != "1" ]]; then
    printf '  workflow: plain-key `migration-drift` collision — expected exactly one 2-space `migration-drift[:blank:]*:` line, found %s\n' \
      "$plain_key_count" >&2
    return 1
  fi

  # (1b) Reject any 2-space non-comment content line whose first char at
  # column 3 is a YAML node-property or key indicator that could open a
  # shadow job header. Canonical job ids only use `[A-Za-z_][A-Za-z0-9_-]*`;
  # any leading `"`, `'`, `?`, `!`, `&`, `*`, `<`, `{`, or `[` at column
  # 3 is a shadow-job shape. Zero-tolerance, whole-workflow scope.
  # Comments (`  # ...`) start with `#` (not in the indicator set) so
  # they never match; blank lines match neither the `^  ` prefix nor
  # the char class. Legitimate 2-space keys inspected in ci.yml all
  # open with letters (`  push:`, `  pull_request:`, `  contents:`,
  # `  group:`, `  cancel-in-progress:`, `  select:`, `  ci-tools:`,
  # `  frontend:`, `  dotnet-build:`, `  migration-drift:`,
  # `  dotnet-test:`, `  summary:`) — no false positives.
  local suspicious
  suspicious="$(printf '%s\n' "$workflow_body" | awk '
    BEGIN { cls = "[\"\047?!&*<{[]" }
    $0 ~ ("^  " cls) { print NR ": " $0 }
  ')"
  if [[ -n "$suspicious" ]]; then
    printf '  workflow: noncanonical 2-space job-key shape detected (possible shadow job):\n' >&2
    printf '%s\n' "$suspicious" | sed 's/^/    /' >&2
    return 1
  fi

  # (2) Full-job snapshot: extract the entire migration-drift block
  # (header line through immediately before the next sibling job)
  # and compare byte-for-byte against the canonical text below.
  # Any change inside the block reads as a snapshot mismatch and
  # requires the reviewer to update this heredoc alongside the
  # workflow edit — the intentional pairing is the review gate.
  local actual expected
  actual="$(extract_migration_drift_full_block "$workflow")"
  expected="$(cat <<'CANONICAL_MIGRATION_DRIFT_JOB'
  migration-drift:
    name: Migration drift (${{ matrix.label }})
    needs: [select, ci-tools]
    if: ${{ needs.select.outputs.want_mig_drift == 'true' }}
    runs-on: ubuntu-latest
    strategy:
      fail-fast: false
      matrix: ${{ fromJson(needs.select.outputs.mig_matrix) }}
    steps:
      - name: Checkout
        uses: actions/checkout@v7

      - name: Setup .NET
        uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x

      - name: Install dotnet-ef
        run: |
          dotnet tool install -g dotnet-ef
          echo "$HOME/.dotnet/tools" >> "$GITHUB_PATH"

      # Restore + build the specific migration project so its
      # `obj/project.assets.json` and compiled assemblies exist before
      # `dotnet ef` reflects over them. Without this step EF fails with
      # NETSDK1004 because the migration-drift job is isolated from
      # dotnet-build and never restores the matrix project itself.
      - name: Restore migration project
        working-directory: src
        env:
          MATRIX_PROJECT: ${{ matrix.project }}
        run: dotnet restore "./$MATRIX_PROJECT"

      - name: Build migration project
        working-directory: src
        env:
          MATRIX_PROJECT: ${{ matrix.project }}
        run: dotnet build "./$MATRIX_PROJECT" -c Debug --no-restore

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

  # ---------------------------------------------------------------------------
  # dotnet-test — one matrix leg per affected test project.
  # ---------------------------------------------------------------------------
CANONICAL_MIGRATION_DRIFT_JOB
)"
  if [[ "$actual" != "$expected" ]]; then
    printf '  migration-drift job does not match canonical full-job snapshot\n' >&2
    printf '  (update the CANONICAL_MIGRATION_DRIFT_JOB heredoc only after reviewing the change)\n' >&2
    diff -u <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") | sed 's/^/    /' >&2
    return 1
  fi

  return 0
}


# The migration-drift matrix job is isolated from `dotnet-build` and must
# restore its own matrix project before invoking `dotnet ef`, otherwise
# NETSDK1004 fires because `obj/project.assets.json` doesn't exist. This
# test reads `.github/workflows/ci.yml` and delegates to
# `_check_drift_full_job_snapshot`, which is a single-source assertion
# covering every prior R11-R15 invariant:
#   * exactly one canonical unquoted `  migration-drift:` job header
#     (rejects unquoted duplicates AND noncanonical shadow spellings —
#     quoted, escape-encoded, tag-prefixed, anchor-prefixed,
#     explicit-key, flow-mapping)
#   * the full job block (header through immediately before the next
#     sibling job) matches the canonical snapshot byte-for-byte —
#     which anchors: the exact selection `if:`, the strategy.matrix
#     source (selector-driven `fromJson(needs.select.outputs.mig_matrix)`),
#     `strategy.fail-fast: false`, every step's presence / order /
#     yaml keys / shell body, the truthful generic drift annotation
#     (NOT the rejected rc=1-only classification), and the `--no-build`
#     flag on the EF invocation. Any change to any of these fails the
#     snapshot diff.
# See adversarial mutation tests `case_drift_snapshot_rejects_*` and
# `case_drift_shape_rejects_*_migration_drift_job` for the specific
# bypass shapes each invariant closes.
case_workflow_migration_drift_restores_before_ef() {
  local workflow="$REPO_ROOT/.github/workflows/ci.yml"
  _check_drift_full_job_snapshot "$workflow" || return 1
}


# CRLF-tolerance proof for `extract_migration_drift_full_block`. The real
# workflow assertion above delegates to the byte-for-byte snapshot gate,
# so the extractor's own robustness needs its own test — otherwise a
# regression that (e.g.) failed to strip trailing CRs on Windows
# checkouts, or terminated the block one line too early, would surface
# as a snapshot mismatch instead of an extractor bug and waste a
# reviewer's diff-reading time.
#
# The fixture is a minimal `migration-drift` job with CRLF line endings
# printed explicitly. It contains the job header, a minimal body, and a
# following sibling job (`  dotnet-test:`) whose header must terminate
# the block cleanly. The expected output is the header + body with no
# CR characters and no leakage from the sibling job.
case_drift_full_block_extractor_crlf_tolerant() {
  local fixture
  fixture="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$fixture'" RETURN
  # NOTE: emit CRLF explicitly with `\r\n`. Do not rely on the host
  # runtime translating line endings — Linux CI runners write LF and we
  # need to prove tolerance of the CRLF a Windows checkout produces.
  printf '%s\r\n' \
    'jobs:' \
    '  migration-drift:' \
    '    runs-on: ubuntu-latest' \
    '    steps:' \
    '      - name: X' \
    '        run: echo x' \
    '  dotnet-test:' \
    '    runs-on: ubuntu-latest' \
    > "$fixture"

  local actual expected
  actual="$(extract_migration_drift_full_block "$fixture")"

  expected="$(cat <<'EXPECTED_CRLF_JOB_BLOCK'
  migration-drift:
    runs-on: ubuntu-latest
    steps:
      - name: X
        run: echo x
EXPECTED_CRLF_JOB_BLOCK
)"
  if [[ "$actual" != "$expected" ]]; then
    printf '  extract_migration_drift_full_block CRLF fixture mismatch\n' >&2
    diff -u <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") | sed 's/^/    /' >&2
    return 1
  fi

  # Belt-and-braces: no stray CRs in the extractor output. If the awk
  # `sub(/\r$/, "")` regressed, the byte-equality check above would
  # already fail — but pinning the invariant explicitly makes the
  # failure mode obvious in a green-vs-red diff.
  case "$actual" in
    *$'\r'*)
      printf '  extract_migration_drift_full_block left CR bytes in its output\n' >&2
      return 1
      ;;
  esac

  # Sentinel: the sibling job `dotnet-test:` must NOT leak into the
  # extracted block — the terminator rule must fire at the sibling's
  # 2-space non-comment key line.
  if [[ "$actual" == *"dotnet-test"* ]]; then
    printf '  extract_migration_drift_full_block did not terminate at sibling job\n' >&2
    return 1
  fi
}




# R16 adversarial mutation tests, evolved from R12/R13. Each of the
# following cases takes a copy of the real `.github/workflows/ci.yml`,
# applies a targeted mutation representing a concrete bypass shape,
# and asserts that `_check_drift_full_job_snapshot` REJECTS the mutant
# with the SPECIFIC diagnostic that names the invariant the mutation
# violates. The shape gate must fail-closed against every one of
# these — if any mutant slips through, or is rejected for the wrong
# reason (a different invariant), the guard is not doing its job.
#
# R13 introduced diagnostic-substring assertions to close a Bishop
# finding: an R12 mutation could satisfy `!_check_drift_step_shape`
# for reasons unrelated to the intended violation (e.g., a matrix
# rewrite that also happened to invalidate the canonical step snapshot
# would pass the R12 assertion, hiding that the matrix invariant itself
# was absent from the guard). The assertion pins BOTH the rejection
# AND the reason. R16 preserves that discipline against the full-job
# snapshot AND the workflow-scope job-key shape scan.
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
# Assert that `_check_drift_full_job_snapshot` returns non-zero on
# <workflow> AND that its stderr contains <expected_diagnostic> as a
# substring. The diagnostic-substring check (R13, Bishop finding)
# prevents a mutation from silently satisfying a different invariant
# than the one it was written to exercise: if the guard rejects for
# the wrong reason we want a loud failure, not a false-green.
_assert_shape_guard_rejects() {
  local label="$1" workflow="$2" expected="$3"
  local stderr_output rc
  # Redirect stdout to /dev/null and capture stderr. The composite
  # substitution `$(cmd 2>&1 >/dev/null)` collects only cmd's stderr
  # because the redirections apply right-to-left: stdout is copied to
  # stderr's *original* destination (the pipe fd), then reassigned to
  # /dev/null. `$?` after the substitution is cmd's exit status.
  stderr_output="$(_check_drift_full_job_snapshot "$workflow" 2>&1 >/dev/null)"
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


# R16 adversarial mutation tests. Each case takes a copy of the real
# `.github/workflows/ci.yml`, applies a targeted mutation representing
# a concrete bypass shape, and asserts that `_check_drift_full_job_snapshot`
# REJECTS the mutant. The two orthogonal invariants
# (`_check_drift_full_job_snapshot` covers both) are exercised
# separately:
#
#   * `case_drift_snapshot_rejects_*` — mutations INSIDE the job body.
#     The full-job byte snapshot catches these regardless of YAML
#     representation: an added step-level `continue-on-error: true`,
#     a duplicate drift step, a swap of the selector-driven matrix
#     for a hard-coded one, a flip of `fail-fast: false` to `true`,
#     a job-level `continue-on-error: true`, or a step-level
#     `if: false` all add or change bytes that break the diff.
#
#   * `case_drift_shape_rejects_*_migration_drift_job` — SHADOW-JOB
#     shapes at 2-space workflow indent. The workflow-scope header
#     shape check rejects each attempt to hide a second
#     `migration-drift` job under a noncanonical spelling: plain
#     unquoted duplicate (caught by canonical-header count > 1),
#     quoted double-quote, escape-encoded quoted (`\x2d` decodes
#     to `-`), YAML tag-prefixed (`!!str`), YAML anchor-prefixed
#     (`&anchor`), and YAML explicit-key form (`? "..."`). Each
#     mutation opens with one of the reject indicators (`"`, `!`,
#     `&`, `?`) at column 3 and is caught by the indicator scan
#     BEFORE duplicate-key last-wins semantics can override the
#     canonical job.
#
# Every case installs a RETURN trap so the mutant file and any stray
# `.tmp` sibling are removed on every exit path, and uses `_mutate`
# for surgical rewrites (which itself propagates awk's real exit
# status — see `case_mutate_helper_propagates_awk_failure`).


# --------------------------------------------------------------------------
# Mutations inside the job body — the full-job snapshot catches them all.
# --------------------------------------------------------------------------

# Inject `        continue-on-error: true` as a sibling yaml key
# immediately after the drift step's `- name:` header. Under a
# compliant YAML parser this would make the step's failure non-fatal
# to the job, letting the migration-drift job report success while
# a real drift went undetected. The snapshot has no such key at that
# position; the added bytes break the diff.
case_drift_snapshot_rejects_step_continue_on_error() {
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
    "does not match canonical full-job snapshot" || return 1
}

# Inject `        if: false` as a sibling yaml key immediately after
# the drift step's `- name:` header. The step would be skipped whenever
# the mutation is present, letting the migration-drift job pass without
# ever invoking `dotnet ef`. Classic fail-open.
case_drift_snapshot_rejects_step_if() {
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
    "does not match canonical full-job snapshot" || return 1
}

# Add `    continue-on-error: true` as a job-level key on
# `migration-drift`. GitHub Actions treats job-level continue-on-error
# as "step failures do not fail the job", so the summary gate's
# `needs.migration-drift.result` would tick success even when the drift
# step exited non-zero.
case_drift_snapshot_rejects_job_continue_on_error() {
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
    "does not match canonical full-job snapshot" || return 1
}

# Replace the selector-driven strategy.matrix with a hand-picked
# inline single-entry matrix. Under a compliant YAML parser this
# would run drift against ONE hand-picked provider/context pair
# while leaving the sibling three EF pairs unchecked. Any deviation
# from `      matrix: ${{ fromJson(needs.select.outputs.mig_matrix) }}`
# breaks the snapshot.
case_drift_snapshot_rejects_hardcoded_matrix() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
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
    "does not match canonical full-job snapshot" || return 1
}

# Flip `fail-fast: false` to `fail-fast: true`. The GitHub default
# for matrix `fail-fast` is `true`, which cancels sibling matrix
# legs on the first failure — hiding drift on the cancelled legs.
case_drift_snapshot_rejects_fail_fast_true() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  _mutate "$mutant" '
    { sub(/\r$/, "") }
    $0 == "      fail-fast: false" { print "      fail-fast: true\r"; next }
    { print $0 "\r" }
  ' || return 1

  _assert_shape_guard_rejects "fail-fast: true" "$mutant" \
    "does not match canonical full-job snapshot" || return 1
}

# Duplicate the drift step: append a shadow `- name: Check EF Core
# migration drift` header inside the same steps list. Any added line
# breaks the byte snapshot.
case_drift_snapshot_rejects_duplicate_drift_step() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  _mutate "$mutant" '
    { print }
    /^      - name: Check EF Core migration drift\r?$/ && !inserted {
      print "        run: echo shadow\r"
      print "      - name: Check EF Core migration drift\r"
      inserted = 1
    }
  ' || return 1

  _assert_shape_guard_rejects "duplicate drift step" "$mutant" \
    "does not match canonical full-job snapshot" || return 1
}


# --------------------------------------------------------------------------
# Workflow-scope shadow-job header shapes — the header shape check
# rejects each attempt to hide a second `migration-drift` job at
# 2-space indent.
# --------------------------------------------------------------------------

# Plain unquoted duplicate `  migration-drift:` header appended at
# end-of-file. `grep -cxE '^  migration-drift:$'` counts 2, which
# rejects with the "expected exactly one canonical" diagnostic.
case_drift_shape_rejects_plain_duplicate_migration_drift_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  if ! {
    cat "$mutant"
    printf '\n  migration-drift:\n    if: false\n    runs-on: ubuntu-latest\n    steps:\n      - run: "echo shadow"\n'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  _assert_shape_guard_rejects "plain duplicate migration-drift job header" "$mutant" \
    "expected exactly one canonical" || return 1
}

# Quoted shadow header `  "migration-drift":`. Under duplicate-key
# semantics the quoted spelling would resolve to the same key as the
# canonical unquoted line and override it under last-wins. Rejected
# by the shape-indicator scan (leading `"`).
case_drift_shape_rejects_quoted_shadow_migration_drift_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  if ! {
    cat "$mutant"
    printf '\n  "migration-drift":\n    if: false\n    runs-on: ubuntu-latest\n    steps:\n      - run: "echo shadow"\n'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  _assert_shape_guard_rejects "quoted shadow migration-drift job" "$mutant" \
    "noncanonical 2-space job-key shape" || return 1
}

# Escape-encoded quoted shadow header `  "migration\x2ddrift":`.
# YAML decodes `\x2d` to `-`, so the shadow resolves to the same
# key `migration-drift` under a compliant parser. Rejected by the
# shape-indicator scan (leading `"`) before the parser can apply
# duplicate-key semantics.
case_drift_shape_rejects_escape_encoded_shadow_migration_drift_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  # Single-quoted printf preserves the backslash so `\x2d` reaches
  # the mutant as a literal 4-char sequence — which YAML decodes
  # back to `-`.
  if ! {
    cat "$mutant"
    printf '%s\n' '' \
      '  "migration\x2ddrift":' \
      '    if: false' \
      '    runs-on: ubuntu-latest' \
      '    steps:' \
      '      - run: "echo shadow"'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  _assert_shape_guard_rejects "escape-encoded shadow migration-drift job" "$mutant" \
    "noncanonical 2-space job-key shape" || return 1
}

# YAML tag-prefixed shadow: `  !!str "migration-drift":`. The `!!str`
# tag prefix is a YAML tag directive that resolves the following node
# to the string type; a compliant parser then reads the key as the
# string `migration-drift`, colliding with the canonical unquoted
# header under duplicate-key last-wins semantics. Rejected by the
# shape-indicator scan (leading `!`).
case_drift_shape_rejects_tagged_shadow_migration_drift_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  if ! {
    cat "$mutant"
    printf '\n  !!str "migration-drift":\n    if: false\n    runs-on: ubuntu-latest\n    steps:\n      - run: "echo shadow"\n'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  _assert_shape_guard_rejects "tagged shadow migration-drift job" "$mutant" \
    "noncanonical 2-space job-key shape" || return 1
}

# YAML anchor-prefixed shadow: `  &shadow "migration-drift":`. The
# `&anchor` prefix names the following node for later `*alias`
# reference; the key itself is still `migration-drift` under a
# compliant parser and collides with the canonical header. Rejected
# by the shape-indicator scan (leading `&`).
case_drift_shape_rejects_anchored_shadow_migration_drift_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  if ! {
    cat "$mutant"
    printf '\n  &shadow "migration-drift":\n    if: false\n    runs-on: ubuntu-latest\n    steps:\n      - run: "echo shadow"\n'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  _assert_shape_guard_rejects "anchored shadow migration-drift job" "$mutant" \
    "noncanonical 2-space job-key shape" || return 1
}

# YAML explicit-key shadow: `  ? "migration-drift"` on its own line,
# followed by `  : <value>` on the next. Explicit-key form is a
# fully-supported YAML 1.2 spelling of a mapping key; the parser
# resolves the key to the same string `migration-drift`. Rejected
# by the shape-indicator scan (leading `?`).
case_drift_shape_rejects_explicit_key_shadow_migration_drift_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  if ! {
    cat "$mutant"
    printf '\n  ? "migration-drift"\n  :\n    if: false\n    runs-on: ubuntu-latest\n    steps:\n      - run: "echo shadow"\n'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  _assert_shape_guard_rejects "explicit-key shadow migration-drift job" "$mutant" \
    "noncanonical 2-space job-key shape" || return 1
}

# R17 plain-unquoted-key bypasses of the exact-match count (1a) and the
# indicator scan (1b). Each appended shadow spelling opens with a letter
# (so it passes 1b) and its whole line does NOT equal `  migration-drift:`
# (so it passes 1a) yet the YAML parser resolves it to the same key and
# duplicate-key last-wins would silently override the canonical job.
# Caught by the (1c) CRLF-tolerant plain-key count.

# `  migration-drift :` — plain unquoted, single space between key and
# colon. YAML allows optional whitespace before the mapping colon; the
# parser resolves the key to `migration-drift`. `grep -cxE '^  migration-drift:$'`
# counts 1 (canonical only, whole-line match fails on the trailing space).
# The indicator scan sees a leading `m` and does not fire. The (1c)
# plain-key count sees BOTH the canonical and the shadow (both match
# `^  migration-drift[[:blank:]]*:`), so the count is 2 and rejects.
case_drift_shape_rejects_space_before_colon_plain_migration_drift_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  if ! {
    cat "$mutant"
    printf '\n  migration-drift :\n    if: false\n    runs-on: ubuntu-latest\n    steps:\n      - run: "echo shadow"\n'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  _assert_shape_guard_rejects "space-before-colon plain migration-drift job" "$mutant" \
    "plain-key \`migration-drift\` collision" || return 1
}

# `  migration-drift: # shadow ...` — plain unquoted, canonical colon
# followed by an inline comment. The whole line is NOT equal to the
# canonical (`# shadow ...` after the colon), so (1a) misses it. The
# indicator scan sees a leading `m` and does not fire. Under a strict
# YAML parser this is a duplicate key entry with a null value scalar
# (or a mapping value on subsequent lines) — either way it collides
# with the canonical `migration-drift`. Caught by (1c).
case_drift_shape_rejects_inline_comment_shadow_migration_drift_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  if ! {
    cat "$mutant"
    printf '\n  migration-drift: # shadow duplicate silently overrides canonical\n    if: false\n    runs-on: ubuntu-latest\n    steps:\n      - run: "echo shadow"\n'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  _assert_shape_guard_rejects "inline-comment shadow migration-drift job" "$mutant" \
    "plain-key \`migration-drift\` collision" || return 1
}

# `  migration-drift: { runs-on: ubuntu-latest, steps: [{ run: true }] }`
# — plain unquoted key with an inline flow-mapping value. A compliant
# YAML parser accepts flow-style job bodies; the key still resolves to
# `migration-drift` and collides with the canonical. The whole line
# has content after the colon so (1a) misses it. The line's leading
# `m` sails past the indicator scan (the flow-mapping opener `{` is
# in column 21, not column 3). Caught by (1c).
case_drift_shape_rejects_inline_flow_shadow_migration_drift_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  if ! {
    cat "$mutant"
    printf '\n  migration-drift: { runs-on: ubuntu-latest, steps: [{ run: true }] }\n'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  _assert_shape_guard_rejects "inline-flow shadow migration-drift job" "$mutant" \
    "plain-key \`migration-drift\` collision" || return 1
}

# Accepted fixture: `  migration-drift-extra:` is a DISTINCT YAML key
# (prefix variant) that the (1c) plain-key count must NOT flag. The
# `-extra` suffix breaks `[[:blank:]]*:` because the character after
# `migration-drift` is `-`, not blank or `:`. Also, `migration-drift-extra`
# opens with a letter so (1b) does not fire. The full-job snapshot (2)
# is unaffected because the extractor terminates at the next `/^  [^ #]/`
# line — the canonical migration-drift block ends at `  dotnet-test:`
# regardless of what appears further down. This case guards against the
# obvious false-positive shape: a legitimate second job whose name begins
# with the same substring.
case_drift_shape_accepts_migration_drift_extra_sibling_job() {
  local mutant
  mutant="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '$mutant' '${mutant}.tmp'" RETURN
  _copy_real_workflow_for_mutation "$mutant"
  if ! {
    cat "$mutant"
    printf '\n  migration-drift-extra:\n    if: false\n    runs-on: ubuntu-latest\n    steps:\n      - run: "echo distinct job with a similar name"\n'
  } > "${mutant}.tmp"; then
    rm -f -- "${mutant}.tmp"
    return 1
  fi
  mv -- "${mutant}.tmp" "$mutant"

  local stderr_output rc
  stderr_output="$(_check_drift_full_job_snapshot "$mutant" 2>&1 >/dev/null)"
  rc=$?
  if (( rc != 0 )); then
    printf '  shape guard falsely rejected legitimate prefix-variant sibling `migration-drift-extra:`\n' >&2
    printf '    stderr: %q\n' "$stderr_output" >&2
    return 1
  fi
  return 0
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

# ---------------------------------------------------------------------------
# Manifest-backed loading (issue #2031). These cases prove the selector
# actually reads scripts/ci/dotnet-test-manifest.json (via TEST_MANIFEST_PATH)
# rather than a hardcoded array, that it fails closed on a missing/empty
# manifest, and that a real changed-path run against the checked-in default
# manifest still produces exactly today's per-bucket matrix (positive,
# negative, and mixed-path coverage for the new loader; full-safe coverage
# for the manifest itself is exercised by case_unknown_test_project_full_safe
# and friends above, which are unaffected by the manifest source).
# ---------------------------------------------------------------------------

case_manifest_default_path_reflects_checked_in_file() {
  # Positive: with no override, the selector must resolve the manifest next
  # to itself (scripts/ci/dotnet-test-manifest.json) and load all 6 checked-in
  # test projects for a full-safe run.
  local out="$1"
  EVENT_NAME="workflow_dispatch" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" CHANGED_FILES="" \
    select_run >/dev/null 2>&1
  assert_eq "full_matrix" "$(get_output "$out" full_matrix)" "true" || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "manifest api" "$matrix" "Farm.Web.Api.Tests" || return 1
  assert_contains "manifest slicer" "$matrix" "Farm.Slicer.Module.Tests" || return 1
  assert_contains "manifest orca" "$matrix" "Farm.OrcaSlicer.Worker.Tests" || return 1
  assert_contains "manifest moonraker (#2022 regression guard)" "$matrix" "Farm.Moonraker.Emulator.Tests" || return 1
  assert_contains "manifest profile parsing (#2022 regression guard)" "$matrix" "Farm.Slicer.ProfileParsing.Tests" || return 1
  assert_contains "manifest integration" "$matrix" "Farm.Web.IntegrationTests" || return 1
}

case_manifest_missing_file_fails_closed() {
  # Negative: an unreadable manifest path must exit rc=3, not silently fall
  # back to an empty/hardcoded test list.
  local rc=0
  local missing ; missing="$(mktemp -u)"
  CHANGED_FILES="src/api/Foo.cs" \
    EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" \
    TEST_MANIFEST_PATH="$missing" \
    bash "$SELECTOR" >/dev/null 2>&1 || rc=$?
  if (( rc != 3 )); then
    printf '  expected rc=3 for missing manifest, got %d\n' "$rc" >&2
    return 1
  fi
}

case_manifest_empty_test_projects_fails_closed() {
  # Negative: a syntactically valid manifest with zero entries must fail
  # closed (rc=3) rather than silently produce an empty test matrix, which
  # would look identical to "nothing changed" and mask a broken manifest.
  local rc=0
  local empty_manifest ; empty_manifest="$(mktemp)"
  printf '{"testProjects": []}\n' > "$empty_manifest"
  CHANGED_FILES="src/api/Foo.cs" \
    EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" \
    TEST_MANIFEST_PATH="$empty_manifest" \
    bash "$SELECTOR" >/dev/null 2>&1 || rc=$?
  rm -f "$empty_manifest"
  if (( rc != 3 )); then
    printf '  expected rc=3 for empty manifest, got %d\n' "$rc" >&2
    return 1
  fi
}

case_manifest_malformed_json_fails_closed() {
  # Negative: invalid JSON must fail closed (rc=3), not swallow the parse
  # error and continue with an empty/partial project list.
  local rc=0
  local bad_manifest ; bad_manifest="$(mktemp)"
  printf '{ this is not valid json' > "$bad_manifest"
  CHANGED_FILES="src/api/Foo.cs" \
    EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" \
    TEST_MANIFEST_PATH="$bad_manifest" \
    bash "$SELECTOR" >/dev/null 2>&1 || rc=$?
  rm -f "$bad_manifest"
  if (( rc != 3 )); then
    printf '  expected rc=3 for malformed manifest JSON, got %d\n' "$rc" >&2
    return 1
  fi
}

case_manifest_partial_crash_fails_closed() {
  # Negative: a manifest that is valid JSON but whose second entry is
  # missing the required 'name' key makes the Python reader print one valid
  # line and then crash with a KeyError. The selector must fail closed
  # (rc=3) on this partial-output crash rather than silently proceeding with
  # only the first entry, which would look like an innocuous, intentionally
  # small manifest instead of a broken one.
  local rc=0
  local partial_manifest ; partial_manifest="$(mktemp)"
  cat > "$partial_manifest" <<'JSON'
{
  "testProjects": [
    {
      "name": "Farm.Web.Api.Tests",
      "testProject": "tests/Farm.Web.Api.Tests/Farm.Web.Api.Tests.csproj",
      "runIntegration": false,
      "defaultFilter": "Category!=DbHeavy"
    },
    {
      "testProject": "tests/Farm.Slicer.Module.Tests/Farm.Slicer.Module.Tests.csproj",
      "runIntegration": false,
      "defaultFilter": "Category!=DbHeavy"
    }
  ]
}
JSON
  CHANGED_FILES="src/api/Foo.cs" \
    EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" \
    TEST_MANIFEST_PATH="$partial_manifest" \
    bash "$SELECTOR" >/dev/null 2>&1 || rc=$?
  rm -f "$partial_manifest"
  if (( rc != 3 )); then
    printf '  expected rc=3 for a manifest reader crash after partial output, got %d\n' "$rc" >&2
    return 1
  fi
}

case_manifest_custom_projects_reflected_in_matrix() {
  # Mixed-path: point at a custom single-project manifest (fabricated
  # project name, still routed through the unchanged api bucket) and prove
  # the matrix output tracks the *substituted* manifest content rather than
  # any residual hardcoded array — i.e. this is really manifest-driven.
  local out="$1"
  local custom_manifest ; custom_manifest="$(mktemp)"
  cat > "$custom_manifest" <<'JSON'
{
  "testProjects": [
    {
      "name": "Farm.Web.Api.Tests",
      "testProject": "tests/Farm.Web.Api.Tests/Farm.Web.Api.Tests.csproj",
      "runIntegration": false,
      "defaultFilter": "Category=CustomManifestProbe"
    }
  ]
}
JSON
  CHANGED_FILES="src/api/Controllers/PrintersController.cs" \
    EVENT_NAME="pull_request" BASE_REF="development" FORCE_FULL_SAFE="" \
    CHANGED_FILES_FROM_Z="" \
    TEST_MANIFEST_PATH="$custom_manifest" \
    select_run >/dev/null 2>&1
  local rc=$?
  rm -f "$custom_manifest"
  (( rc == 0 )) || return 1
  local matrix ; matrix="$(get_output "$out" matrix)"
  assert_contains "custom filter propagated" "$matrix" '"filter":"Category=CustomManifestProbe"' || return 1
  # api bucket normally also selects Farm.Slicer.Module.Tests and
  # Farm.Web.IntegrationTests (see case_api_change); a 1-entry manifest must
  # not conjure test names it doesn't declare.
  assert_not_contains "no slicer for 1-entry manifest" "$matrix" "Farm.Slicer.Module.Tests" || return 1
  assert_not_contains "no integration for 1-entry manifest" "$matrix" "Farm.Web.IntegrationTests" || return 1
}


# =============================================================================
# Runner
# =============================================================================

TESTS=(
  case_react_only
  case_docs_only
  case_api_change
  case_auth_forwarded_headers_change_selects_integration
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
  case_smartplug_change
  case_test_only_smartplug
  case_smartplug_mixed_with_unrelated_backend
  case_printqueue_change
  case_test_only_printqueue
  case_printqueue_mixed_with_unrelated_backend
  case_migration_app_change
  case_migration_slicer_change
  case_test_only_api
  case_test_only_slicer
  case_test_only_orca
  case_test_only_integration
  case_unknown_test_project_full_safe
  case_unknown_src_path
  case_shared_config_change
  case_shared_package_config_change
  case_ci_workflow_change
  case_hook_file_change
  case_ci_script_change
  case_ci_other_workflow_change_no_dotnet
  case_ci_other_ci_script_change_no_dotnet
  case_compute_change_set_script_change_full_safe
  case_pr1562_file_set_narrowed_no_dotnet
  case_ci_other_mixed_with_api_still_selects_api
  case_tools_only_build_no_tests
  case_mobile_change_no_dotnet
  case_merge_base_diverged_pr_base_sha_mobile_only
  case_push_to_development_full_safe
  case_push_to_main_full_safe
  case_compute_change_set_push_force_push_diffs_before_after_directly
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
  case_devcontainer_change_no_dotnet
  case_discovery_full_safe
  case_settings_full_safe
  case_modules_full_safe
  case_tests_modules_full_safe
  case_mixed_react_and_dotnet
  case_selector_uses_bash32_compatible_dedup
  case_selector_dedup_safe_for_empty_arrays
  case_selector_finish_tolerates_empty_args
  case_extract_event_block_crlf_tolerant
  case_workflow_publish_printf_option_safe
  case_workflow_test_job_passes_integration_property
  case_workflow_migration_drift_restores_before_ef
  case_drift_full_block_extractor_crlf_tolerant
  case_drift_snapshot_rejects_step_continue_on_error
  case_drift_snapshot_rejects_step_if
  case_drift_snapshot_rejects_job_continue_on_error
  case_drift_snapshot_rejects_hardcoded_matrix
  case_drift_snapshot_rejects_fail_fast_true
  case_drift_snapshot_rejects_duplicate_drift_step
  case_drift_shape_rejects_plain_duplicate_migration_drift_job
  case_drift_shape_rejects_quoted_shadow_migration_drift_job
  case_drift_shape_rejects_escape_encoded_shadow_migration_drift_job
  case_drift_shape_rejects_tagged_shadow_migration_drift_job
  case_drift_shape_rejects_anchored_shadow_migration_drift_job
  case_drift_shape_rejects_explicit_key_shadow_migration_drift_job
  case_drift_shape_rejects_space_before_colon_plain_migration_drift_job
  case_drift_shape_rejects_inline_comment_shadow_migration_drift_job
  case_drift_shape_rejects_inline_flow_shadow_migration_drift_job
  case_drift_shape_accepts_migration_drift_extra_sibling_job
  case_mutate_helper_propagates_awk_failure
  case_manifest_default_path_reflects_checked_in_file
  case_manifest_missing_file_fails_closed
  case_manifest_empty_test_projects_fails_closed
  case_manifest_malformed_json_fails_closed
  case_manifest_partial_crash_fails_closed
  case_manifest_custom_projects_reflected_in_matrix
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
