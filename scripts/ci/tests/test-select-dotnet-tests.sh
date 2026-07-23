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

# _drift_block_rc1_violations <block>
#
# Emit (to stdout) the lines from <block> that classify a shell exit code
# uniquely as `1` — the specific antipattern rejected on this branch.
#
# `dotnet ef migrations has-pending-model-changes` returns non-zero (including
# `1`) for BOTH real pending model changes AND design-time / tool / provider /
# build failures. Any construct that branches purely on the exit code being
# `1` therefore falsely tells authors "you have drift" when the tool actually
# failed to run. We reject the common shell forms and let the migration-drift
# step propagate the raw rc with a single generic annotation instead.
#
# Covered forms (leading YAML/shell comments are stripped before checking so
# prose like `# rc=1 means drift` in documentation does not false-trip):
#   * `[ "$rc" -eq 1 ]`, `[ $rc -eq 1 ]`, `test "$rc" -eq 1`
#   * `[[ "$rc" -eq 1 ]]`, `[[ "$rc" == 1 ]]`, `[[ $rc = 1 ]]`
#   * quoted operands on either side: `[[ "$rc" == "1" ]]`, `[[ $rc == '1' ]]`,
#     `[[ '$rc' -eq 1 ]]`, `[ "1" = "$rc" ]`, `[[ '1' == '$rc' ]]`
#   * reversed: `[ 1 -eq "$rc" ]`, `[[ 1 == $rc ]]`, `[ 1 = "$rc" ]`
#   * arithmetic, whitespace-agnostic: `((rc==1))`, `(( rc==1 ))`,
#     `((rc == 1))`, `(( rc == 1 ))`, reversed `((1==rc))` / `((1 == rc))`,
#     and their `$rc` / `$?` variants — see `_drift_block_rc1_arith_violations`
#     for the exact grammar the arithmetic pass covers
#   * `$?` variants of the above (`[ $? -eq 1 ]`, `[ 1 -eq $? ]`)
#   * `case` arms whose pattern is literal `1`, `"1"`, or `'1'`
#
# Deliberately NOT flagged (legitimate constructs in the current drift step
# and its neighbours):
#   * `[ "$rc" -eq 0 ]` — success check
#   * `exit "$rc"` — fail-closed propagation of the tool's raw exit code
#   * unconditional `exit 1` bailouts (e.g. missing `dotnet` binary) — these
#     do not classify `rc`, they force an unrelated failure
#   * assignments `rc=1`, `foo=$rc` — shell assignment forbids whitespace
#     around `=`, so requiring whitespace on both sides of every operator
#     naturally excludes them without conflating with the test-command `=`
#     comparison, which mandates whitespace
#   * arithmetic ASSIGNMENT `((rc = 1))` / `((rc=1))` — a single `=` inside
#     `(( ))` is assignment, not comparison; the arithmetic pass only
#     recognizes `==` (the sole arithmetic equality operator; `-eq` is a
#     bash syntax error inside `(( ))`)
#   * literals adjacent to other digits or dots (`10`, `100`, `1.2`, `21`) —
#     the boundary character classes reject them on both sides
#   * annotation text that mentions `$rc` in prose
#
# Portability & shape guarantees:
#   * grep -nE only — POSIX ERE, no PCRE features. Runs on BSD grep (macOS)
#     as well as GNU grep. `[[:space:]]` is portable.
#   * Bash 3.2 safe — no bash-4 associative arrays, mapfile, or PCRE.
#   * CRLF-tolerant — line-anchored patterns end at `[^…]|$`, and the caller
#     strips trailing `\r` where relevant (see extract_job_block).
_drift_block_rc1_violations() {
  local block="$1"
  # Strip pure comment lines (YAML `# …` and shell `# …`); they may reference
  # `rc=1` in prose without classifying anything.
  local code
  code="$(printf '%s\n' "$block" | grep -Ev '^[[:space:]]*#' || true)"

  # Building blocks reused across the forward/reversed patterns.
  #
  # RC_TOKEN matches the LHS/RHS operand that names the exit code, in any of
  # the shell forms authors reach for:
  #   * bare `rc` or `$rc`
  #   * double-quoted `"rc"` / `"$rc"`
  #   * single-quoted `'rc'` / `'$rc'` (literal; unusual but syntactically valid)
  #   * `$?`, bare or quoted
  # Trailing/leading quote characters are matched as a symmetric pair only
  # (both single, both double, or none) so we do not accept mismatched
  # `"rc'` / `'$rc"` shapes that no shell would ever accept.
  local rc_token='("\$?rc"|'\''\$?rc'\''|\$?rc|"\$\?"|'\''\$\?'\''|\$\?)'

  # ONE_TOKEN matches the literal `1` operand with the same balanced-quote
  # policy. Callers must additionally enforce a non-digit / non-`.` boundary
  # on the unquoted form so `10` / `100` / `1.2` never match.
  local one_token='("1"|'\''1'\''|1)'

  # OP matches the comparison operator. We deliberately require whitespace on
  # BOTH sides at the call site (see the patterns below) rather than inside
  # this fragment, because whitespace-around-`=` is what distinguishes a
  # `[ "$rc" = 1 ]` comparison from an `rc=1` assignment.
  local op='(-eq|==|=)'

  # Boundary character classes. `LB_LHS` (left-boundary for the LHS operand)
  # ensures we do not match `myrc == 1` where `myrc` incidentally ends in
  # `rc`. `LB_ONE` additionally excludes `.` so `1.2 == $rc` does not match.
  # `RB_RC` and `RB_ONE` are their right-side counterparts.
  local lb_lhs='(^|[^A-Za-z0-9_])'
  local lb_one='(^|[^A-Za-z0-9_.])'
  local rb_rc='([^A-Za-z0-9_]|$)'
  local rb_one='([^0-9.]|$)'

  # Three passes, each targeting a distinct syntactic shape. `|| true` keeps
  # `set -e` from tripping when a pass finds nothing.
  {
    # Forward comparison: (rc | $rc | $?) OP 1, with balanced optional
    # quoting on either operand. Whitespace on both sides of OP is
    # mandatory — this is what excludes `rc=1` assignment (no whitespace)
    # from the `=` alternative without needing to special-case it.
    printf '%s\n' "$code" | grep -nE \
      "${lb_lhs}${rc_token}[[:space:]]+${op}[[:space:]]+${one_token}${rb_one}" \
      || true
    # Reversed comparison: 1 OP (rc | $rc | $?). Same whitespace and
    # boundary contract, mirrored.
    printf '%s\n' "$code" | grep -nE \
      "${lb_one}${one_token}[[:space:]]+${op}[[:space:]]+${rc_token}${rb_rc}" \
      || true
    # `case` arm whose pattern token is literal 1 (bare, single-, or
    # double-quoted). The token must be preceded by leading whitespace or
    # the start of the line so we only match arm headers, not `1)` that
    # might appear inside `printf` strings, arithmetic, or prose.
    printf '%s\n' "$code" | grep -nE \
      "^[[:space:]]+('1'|\"1\"|1)\)" \
      || true
    # Arithmetic pass: `(( ... rc == 1 ... ))` and reversed. The rules
    # above mandate whitespace around the operator to keep `rc=1`
    # assignments out of the shell/test regex, but that mandate is
    # wrong for arithmetic context — `((rc==1))` and `(( rc == 1 ))`
    # are equally valid classifications there. Delegate to a scoped
    # helper that keeps the whitespace-flexible rule from leaking back
    # into the shell/test regex above.
    _drift_block_rc1_arith_violations "$code"
  }
}

# _drift_block_rc1_arith_violations <code>
#
# Emit (to stdout) rc==1 classifications that occur inside a Bash
# arithmetic context (`(( ... ))` or `$(( ... ))`) on the same line.
# Complements the shell/test regex in `_drift_block_rc1_violations`,
# which cannot be reused verbatim because arithmetic and POSIX-test
# obey opposite whitespace rules:
#
#   * `[ "$rc" -eq 1 ]` requires whitespace on both sides of every
#     operator; the shell/test pass leans on that to safely reject the
#     `rc=1` shell-assignment shape.
#   * `((rc==1))` is a valid arithmetic comparison with zero whitespace
#     around `==`. Requiring whitespace there would silently pass a
#     regression that ships `((rc==1))` in the drift step.
#
# Assignment-safety inside `(( ))` comes from ONLY recognizing `==`:
#   * `-eq` is not a valid arithmetic operator — bash reports
#     `syntax error in expression (error token is "1")` on
#     `(( rc -eq 1 ))`, so it is not a real form we need to detect.
#   * A single `=` inside `(( ))` is arithmetic assignment (`((rc = 1))`
#     sets rc to 1 and evaluates to 1 — always truthy). Matching `=`
#     here would false-fire on assignments and yield the opposite of
#     the bug the shell/test regex avoids.
#
# Operands: bare `rc`, `$rc`, and `$?` only. Bash strips quotes before
# parsing arithmetic, so quoted operands sometimes work (`(( "rc" == 1 ))`)
# but they always have whitespace around `==` — the shell/test regex
# already catches those. We do not add quote alternation here.
#
# Boundary rules match the shell/test pass:
#   * word boundary on `rc` — `((myrc==1))` and `((rc==1foo))` are not
#     matched (the second is also a syntax error, but the boundary is
#     what the regex enforces)
#   * digit / dot boundary on `1` — `((rc==10))`, `((rc==100))`,
#     `((10==rc))`, `((1.2==rc))` are not matched. Arithmetic is
#     integer-only in bash, so `1.2` is actually a syntax error, but
#     the boundary is still enforced for defense in depth.
#
# Grep is line-scoped: multi-line arithmetic such as
#   ```
#   (( rc
#      ==
#      1 ))
#   ```
# is not covered. Extraordinarily rare, and not observed anywhere in
# this repo's `.github/workflows/*.yml`.
_drift_block_rc1_arith_violations() {
  local code="$1"

  # ARITH_RC matches the exit-code operand as it can appear unquoted
  # inside `(( ))`: bare `rc`, `$rc`, or `$?`. The `\$?` fragment is
  # `$` optional so a single alternative covers both `rc` and `$rc`.
  local arith_rc='(\$?rc|\$\?)'

  # Boundary character classes. The LHS boundary is expressed as an
  # OPTIONAL group `(.*[^A-Za-z0-9_])?` after the `((` opener so that
  # the operand may appear either immediately after `((` (with `(` as
  # the natural boundary, already consumed by `\(\(`) or later in the
  # expression (with `.*` skipping over `foo && ` etc. and a trailing
  # non-word character enforcing the boundary at the operand's start).
  local arith_lb_rc='(.*[^A-Za-z0-9_])?'
  local arith_rb_rc='([^A-Za-z0-9_]|$)'
  local arith_lb_one='(.*[^A-Za-z0-9_.])?'
  local arith_rb_one='([^0-9.]|$)'

  # Two passes, forward and reversed, mirroring the shell/test regex.
  # `|| true` keeps `set -e` from tripping on grep's "no match" exit.
  #
  # NOTE: the LHS boundary group is intentionally OPTIONAL so that
  # `((rc==1))` matches (nothing between `((` and `rc`), while
  # `((myrc==1))` does not (there is no way to consume `my` and still
  # end on a non-word char before `rc`).
  {
    # Forward: `(( … rc == 1 … ))`
    printf '%s\n' "$code" | grep -nE \
      "\\(\\(${arith_lb_rc}${arith_rc}[[:space:]]*==[[:space:]]*1${arith_rb_one}" \
      || true
    # Reversed: `(( … 1 == rc … ))`
    printf '%s\n' "$code" | grep -nE \
      "\\(\\(${arith_lb_one}1[[:space:]]*==[[:space:]]*${arith_rc}${arith_rb_rc}" \
      || true
  }
}

# _drift_block_has_rc1_classification <block>
# Returns 0 (true) iff _drift_block_rc1_violations produces any output.
_drift_block_has_rc1_classification() {
  local block="$1" out
  out="$(_drift_block_rc1_violations "$block")"
  [[ -n "$out" ]]
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
#   * the drift step body contains no rc==1 classification construct in
#     any of the common shell forms (see _drift_block_rc1_violations for
#     the full list). Independent fixture-based coverage of the detector
#     itself lives in case_drift_detector_catches_representative_rc1_forms.
case_workflow_migration_drift_restores_before_ef() {
  local workflow="$REPO_ROOT/.github/workflows/ci.yml"
  extract_job_block() {
    local job="$1"
    # Job blocks are indented two spaces under `jobs:`. Terminate at the
    # next sibling job (another two-space `name:` header) or a top-level
    # key. Also strip trailing CRs so a CRLF checkout compares byte-for-
    # byte against a Linux-style checkout (same reason as the event-block
    # extractor above).
    awk -v marker="  ${job}:" '
      { sub(/\r$/, "") }
      $0 == marker { inside = 1; next }
      inside && (/^[^ ]/ || /^  [A-Za-z_][A-Za-z0-9_-]*:/) { exit }
      inside { print }
    ' "$workflow"
  }

  local block
  block="$(extract_job_block migration-drift)"
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

  # Belt-and-braces: no rc==1 classification anywhere in the drift job in
  # any of the common shell forms — `[ … -eq 1 ]`, `[[ … == 1 ]]`,
  # `test … -eq 1`, `(( rc == 1 ))`, reversed-operand variants, and `1)`
  # case arms. This subsumes the earlier `1)`-only regex, which only
  # caught one syntactic shape and would silently pass a regression that
  # branched on rc==1 through, e.g., `[[ "$rc" -eq 1 ]]`. See the
  # _drift_block_rc1_violations docstring for the full covered set and
  # for the constructs (`[ "$rc" -eq 0 ]`, `exit "$rc"`, unconditional
  # `exit 1` bailouts) that remain legitimate.
  if _drift_block_has_rc1_classification "$block"; then
    printf '  drift step must not classify rc=1 uniquely as drift; offending lines:\n' >&2
    _drift_block_rc1_violations "$block" | sed 's/^/    /' >&2
    return 1
  fi

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

# Fixture-based, workflow-independent proof that
# _drift_block_has_rc1_classification actually catches the shell forms it
# claims to. This decouples the detector's correctness from any specific
# state of `.github/workflows/ci.yml` — if someone later replaces the
# workflow with an rc==1-branching construct we do not yet cover, the
# workflow-scoped test above may still trip, but this case guarantees
# every form we've enumerated is caught in isolation. Also asserts NO
# false positives on the legitimate constructs the drift step currently
# uses, plus the assignment / boundary shapes that a naive detector
# commonly conflates with a comparison (`rc=1`, `foo=$rc`, `10`, `1.2`).
case_drift_detector_catches_representative_rc1_forms() {
  local out="$1" ; : "$out"  # unused; keep run_case signature

  local -a rejected=(
    # POSIX test with $rc, forward and reversed
    $'rc=$?\nif [ "$rc" -eq 1 ]; then echo drift; fi'
    $'rc=$?\nif [ $rc -eq 1 ]; then echo drift; fi'
    $'rc=$?\nif [ 1 -eq "$rc" ]; then echo drift; fi'
    # `test` builtin
    $'rc=$?\nif test "$rc" -eq 1; then echo drift; fi'
    # Bash `[[ … ]]`, numeric and lexical
    $'rc=$?\nif [[ "$rc" -eq 1 ]]; then echo drift; fi'
    $'rc=$?\nif [[ $rc == 1 ]]; then echo drift; fi'
    $'rc=$?\nif [[ $rc = 1 ]]; then echo drift; fi'
    $'rc=$?\nif [[ 1 == "$rc" ]]; then echo drift; fi'
    # POSIX `=` string equality
    $'rc=$?\nif [ "$rc" = 1 ]; then echo drift; fi'
    # Arithmetic
    $'rc=$?\nif (( rc == 1 )); then echo drift; fi'
    # `$?` shortcut instead of a captured `rc`
    $'dotnet ef ...\nif [ $? -eq 1 ]; then echo drift; fi'
    # `$?` on the RHS of a reversed comparison
    $'dotnet ef ...\nif [ 1 -eq $? ]; then echo drift; fi'
    # Quoted RHS: `"1"` and `'1'` on the RHS of the comparison. These are
    # the same classification as bare `1`, dressed in shell quoting the
    # detector must see through.
    $'rc=$?\nif [[ "$rc" == "1" ]]; then echo drift; fi'
    $'rc=$?\nif [[ $rc == '\''1'\'' ]]; then echo drift; fi'
    $'rc=$?\nif [ "$rc" -eq "1" ]; then echo drift; fi'
    # Quoted LHS: `'$rc'` / `"rc"` on the LHS. Rare but syntactically
    # valid; the detector must catch it because the intent is still
    # rc==1 classification.
    $'rc=$?\nif [[ '\''$rc'\'' -eq 1 ]]; then echo drift; fi'
    # Reversed with quoted 1 operand.
    $'rc=$?\nif [ "1" = "$rc" ]; then echo drift; fi'
    $'rc=$?\nif [[ '\''1'\'' == '\''$rc'\'' ]]; then echo drift; fi'
    # `case` arm, bare / double- / single-quoted `1` token
    $'case "$rc" in\n  0) echo ok ;;\n  1) echo drift ;;\nesac'
    $'case "$rc" in\n  0) echo ok ;;\n  "1") echo drift ;;\nesac'
    $'case "$rc" in\n  0) echo ok ;;\n  \'1\') echo drift ;;\nesac'
    # Arithmetic, compact form — no whitespace anywhere around the
    # operator or inside `(( ))`. The R9 shell/test regex required
    # `[[:space:]]+` on both sides of the operator to safely
    # distinguish `[ = 1 ]` from `rc=1`; that constraint leaves the
    # equally-valid arithmetic `((rc==1))` uncovered because bash
    # arithmetic permits any amount of whitespace around `==`. The
    # dedicated arith pass covers it. See `_drift_block_rc1_arith_
    # violations` for the exact grammar.
    $'rc=$?\nif ((rc==1)); then echo drift; fi'
    # Arithmetic, mixed-space form — spaces at the parens but none
    # around `==`.
    $'rc=$?\nif (( rc==1 )); then echo drift; fi'
    # Arithmetic, mixed-space form — spaces around `==` but none at
    # the parens. Bash accepts this; the arith pass must too.
    $'rc=$?\nif ((rc == 1)); then echo drift; fi'
    # Arithmetic, reversed compact.
    $'rc=$?\nif ((1==rc)); then echo drift; fi'
    # Arithmetic, reversed with spaces.
    $'rc=$?\nif (( 1 == rc )); then echo drift; fi'
    # Arithmetic with `$rc` — bash strips the `$` before parsing the
    # arithmetic expression, but the syntactic shape authors reach
    # for still uses the sigil, so the detector must see through it.
    $'rc=$?\nif (($rc==1)); then echo drift; fi'
    $'rc=$?\nif (( $rc == 1 )); then echo drift; fi'
    # Arithmetic with `$?` on either side — no intermediate rc capture.
    $'dotnet ef ...\nif (($?==1)); then echo drift; fi'
    $'dotnet ef ...\nif ((1==$?)); then echo drift; fi'
    # Arithmetic EXPANSION `$((...))` that materializes the rc==1
    # classification into a variable. Same intent as `if ((rc==1))`,
    # different syntactic dress; the detector's `((` anchor is
    # deliberately loose enough to see `((` inside `$((`.
    $'rc=$?\nis_drift=$((rc==1))\nexit "$is_drift"'
  )
  local snip idx=0
  for snip in "${rejected[@]}"; do
    if ! _drift_block_has_rc1_classification "$snip"; then
      printf '  detector MISSED rejected form #%d:\n' "$idx" >&2
      printf '%s\n' "$snip" | sed 's/^/    /' >&2
      return 1
    fi
    idx=$((idx+1))
  done

  # Positive controls — must NOT trip the detector.
  local -a accepted=(
    # Current drift step shape: check for success, otherwise print a
    # generic annotation and propagate the raw rc. No rc==1 classification.
    $'rc=$?\nif [ "$rc" -eq 0 ]; then\n  echo ok\nelse\n  echo "::error::drift or tool failure"\n  exit "$rc"\nfi'
    # Unconditional `exit 1` bailout on a precondition failure — this is
    # not classifying `rc`, it is forcing an unrelated failure.
    $'if ! command -v dotnet >/dev/null; then\n  echo "missing dotnet" >&2\n  exit 1\nfi'
    # Prose comment that mentions `rc=1` — comments are stripped before
    # the check runs, so this must not false-positive.
    $'# real drift (rc=1 on success paths) vs. tool failure\nrc=$?\nexit "$rc"'
    # Success check with reversed operand order — legitimate, does not
    # classify rc==1.
    $'rc=$?\nif [ 0 -eq "$rc" ]; then echo ok; fi'
    # Comparison against a different value (10) that happens to contain
    # the digit `1` — must not trigger the boundary-aware detector.
    $'rc=$?\nif [ "$rc" -eq 10 ]; then echo weird; fi'
    # Bare assignment `rc=1` — no whitespace around `=`, therefore an
    # assignment and not a comparison. This is a common shape in
    # unrelated jobs of `ci.yml` (e.g. the `select` job seeds `rc=0`
    # then flips it to `rc=1` on a git-diff failure). The detector must
    # not conflate it with the `[ "$rc" = 1 ]` comparison it targets.
    $'rc=0\nif ! some_command; then\n  rc=1\nfi\nexit "$rc"'
    # Symmetric case: assigning `$rc` into another variable via
    # `foo=$rc`. The token `rc` appears on the RHS of an assignment,
    # but there is no comparison here at all — a naive regex that
    # matched `rc[[:space:]]*=[[:space:]]*1` without the RHS constraint
    # (or without the whitespace constraint) has been known to fire on
    # this line by drifting the LHS token match into the RHS.
    $'foo=$rc\nbar=$foo\nexit "$rc"'
    # Assignments where the digit-boundary alone (without a whitespace
    # rule) would false-fire: `rc=10` reads like an rc==1 comparison
    # only if boundary and whitespace are BOTH ignored. Guard both.
    $'rc=10\nexit "$rc"'
    # A dotted literal `1.2` that starts with `1` and could false-fire
    # if the digit boundary were only enforced on one side.
    $'rc=$?\nif [ "$rc" -eq 12 ] || printf "%s\\n" "1.2 ignored"; then :; fi'
    # `case` arm on `10)` — must not conflate with a `1)` arm. The
    # digit boundary in the case-arm pattern is enforced by the `)`
    # terminator: `1)` closes the token, `10)` does not.
    $'case "$rc" in\n  0) echo ok ;;\n  10) echo other ;;\nesac'
    # Prose in a normal comment line (not code): mentions the `rc == 1`
    # antipattern while explaining why it is rejected. Stripped by the
    # leading-comment filter, so must not false-fire.
    $'# We used to write [ "$rc" -eq 1 ] here; do not do that.\nrc=$?\nexit "$rc"'
    # Arithmetic ASSIGNMENT — compact form only. `((rc=1))` sets rc to
    # 1 and evaluates to 1 (always truthy); a bug in its own right,
    # but not the rc==1 CLASSIFICATION we detect. The arith pass
    # only recognizes `==`, so this shape is deliberately out of
    # scope. Note: the whitespace-around-`=` form `((rc = 1))` is
    # textually indistinguishable from a POSIX-test comparison and
    # is still caught by the shell/test regex above — that's a
    # cross-pass side effect the prompt requires we preserve (do
    # not loosen the shell/test regex to accommodate arithmetic
    # context), and in practice it blocks a buggy shape that
    # should never ship anyway.
    $'rc=$?\nif ((rc=1)); then echo weird; fi'
    # Arithmetic success check — `((rc==0))` is the correct
    # fail-closed shape (drift step returns 0 only when the tool
    # ran successfully and reported no pending changes). It must
    # not be conflated with rc==1 classification.
    $'rc=$?\nif ((rc==0)); then echo ok; fi'
    # Arithmetic comparison against a different integer value that
    # starts with the digit `1`. The RHS digit boundary must reject
    # `10`, or the arith pass would over-fire on unrelated integer
    # comparisons in unrelated jobs (e.g. rate-limit retries).
    $'rc=$?\nif ((rc==10)); then echo weird; fi'
    $'rc=$?\nif ((10==rc)); then echo weird; fi'
    # A `1.2`-shaped literal inside the arithmetic block — bash's
    # integer arithmetic rejects `1.2` at parse time, but the
    # detector inspects TEXT, not runtime validity, so the dot
    # boundary must still keep `1.2` out of the rc==1 match set.
    # Defense in depth against future ksh/zsh-style arithmetic
    # extensions that permit non-integer literals.
    $'rc=$?\nif ((rc==12)) || printf "%s\\n" "((1.2==rc)) is nonsense"; then :; fi'
    # Different variable — `foo` and `myrc` are not `rc`. The word
    # boundary on the rc operand must not let `myrc==1` or
    # `foo==1` slip through as rc==1 classification.
    $'foo=$?\nif ((foo==1)); then echo unrelated; fi'
    $'myrc=$?\nif ((myrc==1)); then echo unrelated; fi'
    # Arithmetic expansion used for a value computation rather than
    # a boolean — `$((rc + 1))` returns rc plus one, not an rc==1
    # boolean, and must not classify.
    $'rc=$?\nnext=$((rc + 1))\nexit "$next"'
  )
  local ok
  idx=0
  for ok in "${accepted[@]}"; do
    if _drift_block_has_rc1_classification "$ok"; then
      printf '  detector FALSE-POSITIVE on accepted form #%d:\n' "$idx" >&2
      printf '%s\n' "$ok" | sed 's/^/    /' >&2
      printf '  matched lines:\n' >&2
      _drift_block_rc1_violations "$ok" | sed 's/^/    /' >&2
      return 1
    fi
    idx=$((idx+1))
  done
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
  case_drift_detector_catches_representative_rc1_forms
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
