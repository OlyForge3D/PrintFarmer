#!/usr/bin/env bash
# =============================================================================
# test-dotnet-test-manifest-checks.sh — regression tests for the manifest
# validator itself (scripts/ci/tests/test-dotnet-test-manifest.sh), as
# opposed to that script's own run against the real checked-in manifest.
#
# The validator's SRC_ROOT is always the real repo's src/ (only the manifest
# path is overridable via TEST_MANIFEST_PATH), so these cases load the real
# checked-in manifest, mutate a single field with Python, write the result to
# a temp file, and point TEST_MANIFEST_PATH at that mutant. This keeps every
# filesystem-backed check (testProject existence, Farm.Web.Api.Tests shard
# coverage) satisfied by the real tree while isolating the one property under
# test. Confirms the validator:
#   1. still PASSes when merely round-tripped through Python unmodified
#      (baseline / proves the override mechanism itself works),
#   2. FAILs when two entries share the same testProject path under
#      different names (duplicate-by-path, not just duplicate-by-name),
#   3. FAILs when a required schema field is missing from an entry,
#   4. FAILs when a required schema field has the wrong JSON type, and
#   5. FAILs closed (rc=1, not a silent PASS) when the embedded Python
#      subprocess crashes outright rather than reporting itemized errors.
# =============================================================================

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
VALIDATOR="$SCRIPT_DIR/test-dotnet-test-manifest.sh"
REAL_MANIFEST="$REPO_ROOT/scripts/ci/dotnet-test-manifest.json"

PYTHON_BIN=""
for candidate in python3 python; do
  candidate_path="$(command -v "$candidate" 2>/dev/null || true)"
  if [[ -n "$candidate_path" ]] && "$candidate_path" --version >/dev/null 2>&1; then
    PYTHON_BIN="$candidate_path"
    break
  fi
done
if [[ -z "$PYTHON_BIN" ]]; then
  echo "FATAL: no working python3/python interpreter found" >&2
  exit 1
fi

PASSED=0
FAILED=0

run_case() {
  local name="$1"
  local fn="$2"
  if "$fn"; then
    printf 'PASS  %s\n' "$name"
    PASSED=$((PASSED + 1))
  else
    printf 'FAIL  %s\n' "$name"
    FAILED=$((FAILED + 1))
  fi
}

# Runs a Python mutation script (arg $2) against the real manifest and writes
# the result to the temp file named in $1. The mutation script receives the
# output path as sys.argv[1] and the real manifest path as sys.argv[2].
build_mutant() {
  local out_file="$1"
  local py_script="$2"
  "$PYTHON_BIN" -c "$py_script" "$out_file" "$REAL_MANIFEST"
}

case_baseline_roundtrip_passes() {
  # Positive: an unmodified round-trip through Python (load, dump, no
  # mutation) must still PASS -- proves the override mechanism and fixture
  # plumbing below are sound, so a later FAIL can only be attributed to the
  # specific mutation under test.
  local mutant ; mutant="$(mktemp)"
  build_mutant "$mutant" '
import json, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    data = json.load(f)
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(data, f)
'
  local rc=0
  local report
  report="$(TEST_MANIFEST_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc != 0 )); then
    printf '  expected an unmodified round-trip to pass, got rc=%d:\n%s\n' "$rc" "$report" >&2
    return 1
  fi
}

case_duplicate_test_project_path_fails() {
  # Negative: append a copy of the first entry under a different 'name' but
  # the identical 'testProject' path. De-duping by name alone would miss
  # this and let CI register one physical .csproj under two logical names.
  local mutant ; mutant="$(mktemp)"
  build_mutant "$mutant" '
import json, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    data = json.load(f)
dup = dict(data["testProjects"][0])
dup["name"] = dup["name"] + ".Duplicate"
data["testProjects"].append(dup)
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(data, f)
'
  local rc=0
  local report
  report="$(TEST_MANIFEST_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail on duplicate testProject path, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"duplicate testProject path"* ]]; then
    printf '  expected a duplicate-testProject-path error, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_missing_required_field_fails() {
  # Negative: dropping the required 'leg' field from one entry must fail
  # with a message naming the missing/invalid field, not pass silently.
  local mutant ; mutant="$(mktemp)"
  build_mutant "$mutant" '
import json, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    data = json.load(f)
del data["testProjects"][1]["leg"]
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(data, f)
'
  local rc=0
  local report
  report="$(TEST_MANIFEST_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail on missing required field, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"'leg' must be a non-empty string"* ]]; then
    printf '  expected a missing/invalid-leg error, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_wrong_type_field_fails() {
  # Negative: 'runIntegration' as a string instead of a JSON boolean must
  # fail with a type-mismatch message.
  local mutant ; mutant="$(mktemp)"
  build_mutant "$mutant" '
import json, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    data = json.load(f)
data["testProjects"][1]["runIntegration"] = "false"
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(data, f)
'
  local rc=0
  local report
  report="$(TEST_MANIFEST_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail on wrong-typed runIntegration, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"must be a boolean"* ]]; then
    printf '  expected a type-mismatch error for runIntegration, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_crash_fails_closed() {
  # Negative: valid JSON that is not an object (a bare array) makes
  # `data.get("testProjects", [])` raise AttributeError inside the embedded
  # Python subprocess. The validator must fail closed (rc=1) rather than
  # falling through the empty-report path to a silent PASS.
  local mutant ; mutant="$(mktemp)"
  printf '[1, 2, 3]' > "$mutant"
  local rc=0
  local report
  report="$(TEST_MANIFEST_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail closed on a Python crash, but it passed\n' >&2
    return 1
  fi
}

case_invalid_name_fails() {
  # Negative: a whitespace-only 'name' is falsy-checked by the duplicate
  # detector but is a valid (truthy for non-empty, but garbage) string that
  # earlier passed schema validation since 'name' was not itself a checked
  # field. Confirms 'name' is now enforced as a non-empty, trimmed string.
  local mutant ; mutant="$(mktemp)"
  build_mutant "$mutant" '
import json, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    data = json.load(f)
data["testProjects"][1]["name"] = "   "
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(data, f)
'
  local rc=0
  local report
  report="$(TEST_MANIFEST_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail on a whitespace-only name, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"field 'name' must be a non-empty string"* ]]; then
    printf '  expected a name-field type/empty error, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_pinned_default_filter_narrowed_fails() {
  # Negative: silently narrowing a known project's 'defaultFilter' (or
  # swapping its 'testProject'/'runIntegration') would weaken what CI
  # actually exercises for that leg without failing any structural/schema
  # check. The validator pins canonical values for known entries in
  # EXPECTED_CANONICAL specifically to catch this.
  local mutant ; mutant="$(mktemp)"
  build_mutant "$mutant" '
import json, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    data = json.load(f)
for entry in data["testProjects"]:
    if entry.get("name") == "Farm.Web.Api.Tests":
        entry["defaultFilter"] = "FullyQualifiedName~SomeTinySubset"
        break
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(data, f)
'
  local rc=0
  local report
  report="$(TEST_MANIFEST_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail on a narrowed pinned defaultFilter, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"does not match its pinned canonical value"* ]]; then
    printf '  expected a pinned-canonical-value mismatch error, got:\n%s\n' "$report" >&2
    return 1
  fi
}

TESTS=(
  case_baseline_roundtrip_passes
  case_duplicate_test_project_path_fails
  case_missing_required_field_fails
  case_wrong_type_field_fails
  case_crash_fails_closed
  case_invalid_name_fails
  case_pinned_default_filter_narrowed_fails
)

printf '=== dotnet test manifest validator regression suite ===\n'
for t in "${TESTS[@]}"; do
  run_case "$t" "$t"
done

printf '\n=== summary ===\n'
printf 'passed: %d\n' "$PASSED"
printf 'failed: %d\n' "$FAILED"

if (( FAILED > 0 )); then
  exit 1
fi
exit 0
