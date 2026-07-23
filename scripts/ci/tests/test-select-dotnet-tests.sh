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
# the migration-drift shape gate and the R12 adversarial mutation tests
# can share one job-block reader instead of redefining it locally.
extract_job_block() {
  local workflow="$1" job="$2"
  awk -v marker="  ${job}:" '
    { sub(/\r$/, "") }
    $0 == marker { inside = 1; next }
    inside && (/^[^ ]/ || /^  [A-Za-z_][A-Za-z0-9_-]*:/) { exit }
    inside { print }
  ' "$workflow"
}

# _check_drift_step_shape <workflow>
#
# Composite shape gate for the `Check EF Core migration drift` step in
# the supplied workflow file. Returns 0 iff every invariant holds and
# non-zero (with a diagnostic on stderr) if any of them fails. Written
# so `case_workflow_migration_drift_restores_before_ef` can call it on
# the real workflow and the R12 adversarial mutation cases can call it
# on a mutated copy — the same guard must reject every mutation.
#
# Invariants:
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
  # `continue-on-error:` at that indent applies to the whole job.
  if printf '%s\n' "$job_block" | grep -Eq '^    continue-on-error:'; then
    printf '  migration-drift job must not set job-level continue-on-error\n' >&2
    return 1
  fi

  # Preserve the expected selection `if:` — anything else (`if: false`,
  # a different expression) would either skip the job unconditionally
  # or run it under unexpected gating.
  local expected_if="    if: \${{ needs.select.outputs.want_mig_drift == 'true' }}"
  if ! printf '%s\n' "$job_block" | grep -qxF "$expected_if"; then
    printf '  migration-drift job missing expected selection if clause\n' >&2
    printf '    expected line: %q\n' "$expected_if" >&2
    return 1
  fi

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
#   * the drift step's `run: |` body matches an exact canonical snapshot
#     of the fail-closed script (set flags, `dotnet ef` invocation,
#     capture rc, restore set -e, success-only check `[ "$rc" -eq 0 ]`,
#     one truthful generic error annotation, `exit "$rc"`). This gate
#     replaces earlier regex-based attempts to prove absence of every
#     Bash rc==1 shape (`[[ $rc -eq 1 ]]`, `((rc==1))`, `(((rc)==1))`,
#     `\$(( (rc) == 1 ))`, `1)` case arms, and so on) which Hicks showed
#     could be bypassed by nested arithmetic and confused by compound
#     expressions and prose. Any added or changed control-flow line in
#     the drift step now trips this test until the canonical expected
#     body is deliberately updated — diagnostic-shape changes therefore
#     require human review, not regex correctness.
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
#     job carries the expected selection `if:` — all enforced by
#     `_check_drift_step_shape`. R12 upgraded this gate from a
#     shell-body-only snapshot to a full-step-yaml snapshot after Hicks
#     showed the R11 gate was silent about yaml-level bypass keys
#     (`continue-on-error: true`, `if: false`) that would leave the
#     shell body byte-identical and slip through. See adversarial
#     mutation tests `case_drift_shape_rejects_*`.
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


# R12 adversarial mutation tests. Each of the following cases takes a
# copy of the real `.github/workflows/ci.yml`, applies a targeted
# mutation representing a concrete bypass shape, and asserts that
# `_check_drift_step_shape` REJECTS the mutant. The shape gate must
# fail-closed against every one of these — if any mutant slips through,
# the guard is not doing its job.
#
# All mutation helpers use awk for surgical rewrites (no sed portability
# hazards) and write to a temp workflow that the shape check is invoked
# against. Diagnostic output from the shape check is redirected to
# /dev/null — a failure here is the expected outcome, not a signal.

# Copy the real workflow to <dst>. Kept as a helper so each adversarial
# case starts from an unmutated baseline; a real-workflow shape drift
# would surface in `case_workflow_migration_drift_restores_before_ef`,
# not in the adversarial suite.
_copy_real_workflow_for_mutation() {
  local dst="$1"
  cp "$REPO_ROOT/.github/workflows/ci.yml" "$dst"
}

# Assert that `_check_drift_step_shape` returns non-zero on <workflow>.
# Silences the shape check's own diagnostics (expected failure).
_assert_shape_guard_rejects() {
  local label="$1" workflow="$2"
  if _check_drift_step_shape "$workflow" 2>/dev/null; then
    printf '  shape guard failed to reject mutation: %s\n' "$label" >&2
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
  _copy_real_workflow_for_mutation "$mutant"
  awk '
    { print }
    /^      - name: Check EF Core migration drift\r?$/ && !inserted {
      print "        continue-on-error: true\r"
      inserted = 1
    }
  ' "$mutant" > "$mutant.tmp" && mv "$mutant.tmp" "$mutant"

  local rc=0
  _assert_shape_guard_rejects "step-level continue-on-error" "$mutant" || rc=1
  rm -f -- "$mutant"
  return "$rc"
}

# Mutation: inject `        if: false` as a sibling yaml key immediately
# after the drift step's `- name:` header. The step would be skipped
# whenever the mutation is present, which would let the migration-drift
# job pass without ever invoking `dotnet ef`. This is a classic
# fail-open bypass and the shape guard must catch it.
case_drift_shape_rejects_step_if() {
  local mutant
  mutant="$(mktemp)"
  _copy_real_workflow_for_mutation "$mutant"
  awk '
    { print }
    /^      - name: Check EF Core migration drift\r?$/ && !inserted {
      print "        if: false\r"
      inserted = 1
    }
  ' "$mutant" > "$mutant.tmp" && mv "$mutant.tmp" "$mutant"

  local rc=0
  _assert_shape_guard_rejects "step-level if: false" "$mutant" || rc=1
  rm -f -- "$mutant"
  return "$rc"
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
  _copy_real_workflow_for_mutation "$mutant"
  awk '
    { print }
    inside && !inserted && /^    name:/ {
      print "    continue-on-error: true\r"
      inserted = 1
    }
    /^  migration-drift:\r?$/ { inside = 1 }
  ' "$mutant" > "$mutant.tmp" && mv "$mutant.tmp" "$mutant"

  local rc=0
  _assert_shape_guard_rejects "job-level continue-on-error" "$mutant" || rc=1
  rm -f -- "$mutant"
  return "$rc"
}

# Mutation: duplicate the entire drift step. This is a subtle bypass
# shape where an attacker adds a shadow drift step (perhaps with
# `if: false` or a benign `run: true`) next to the real one; a naive
# guard that only inspects the first match could be fooled. The shape
# gate's exactly-one-step count check must reject any drift step count
# other than 1.
case_drift_shape_rejects_duplicate_drift_step() {
  local mutant tmp
  mutant="$(mktemp)"
  tmp="$(mktemp)"
  _copy_real_workflow_for_mutation "$mutant"
  # Append a second (byte-identical) `- name: Check EF Core migration
  # drift` header at 6-space indent inside the same steps list. The
  # simplest injection: insert immediately after the real step's
  # `- name:` header. Body doesn't matter for the count check.
  awk '
    { print }
    /^      - name: Check EF Core migration drift\r?$/ && !inserted {
      print "        run: echo shadow\r"
      print "      - name: Check EF Core migration drift\r"
      inserted = 1
    }
  ' "$mutant" > "$tmp" && mv "$tmp" "$mutant"

  local rc=0
  _assert_shape_guard_rejects "duplicate drift step" "$mutant" || rc=1
  rm -f -- "$mutant"
  return "$rc"
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
  _copy_real_workflow_for_mutation "$mutant"
  awk '
    /^      - name: Check EF Core migration drift\r?$/ {
      # Prepend two extra spaces to shift from 6-space to 8-space.
      sub(/^      /, "        ")
    }
    { print }
  ' "$mutant" > "$mutant.tmp" && mv "$mutant.tmp" "$mutant"

  local rc=0
  _assert_shape_guard_rejects "malformed step indentation" "$mutant" || rc=1
  rm -f -- "$mutant"
  return "$rc"
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
