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
#      subprocess crashes outright rather than reporting itemized errors,
#   6. FAILs when the 'name' field is missing/blank/non-string,
#   7. FAILs when a known project's pinned defaultFilter/testProject/
#      productionProject/runIntegration is silently narrowed or swapped, and
#   8. FAILs when a known project's 'name' is renamed while its testProject
#      path is left unchanged (would otherwise silently vanish from the CI
#      matrix since select-dotnet-tests.sh looks entries up by name), and
#   9. FAILs when an API shard filter drops a root-level test class even though
#      the directory-level `(root)` ownership marker still exists, and
#  10. FAILs when two new, unregistered top-level sibling classes in one file
#      are declared at different indentation (a stray extra space on the
#      second) -- both must still be reported as uncovered, proving neither
#      is ever silently excluded as "nested" merely because of how it is
#      indented, and
#  11. FAILs closed (rather than silently guessing) when a file's public
#      class(es) are all nested at a non-zero code-brace depth (e.g. wrapped
#      in a block-scoped `namespace X { ... }` instead of this codebase's
#      usual file-scoped `namespace X;`), so there is no class at namespace
#      scope to safely treat as the owner of its [Fact]/[Theory] attributes,
#  12. does NOT desync its brace-depth count on a C# 11 raw string literal
#      (`"""..."""`) containing an embedded `"` character and a `//`-looking
#      substring -- both classes in the file must still be correctly
#      attributed and reported as coverage gaps, proving the raw string's
#      own closing delimiter was not swallowed by a mistaken ordinary-string
#      or line-comment match, and
#  13. FAILs closed (rather than silently using a desynced depth) when any
#      construct -- not just an unhandled string form -- leaves the file's
#      overall code-brace depth unbalanced at EOF, and
#  14. does NOT desync its brace-depth count when a raw string literal's own
#      interpolation hole contains a NESTED raw string literal whose opening
#      quote run is LONGER than the outer literal's own (round-7 reviewer
#      finding) OR EQUAL to it (round-8 reviewer finding, the more dangerous
#      case: the hole's open/close braces are swallowed symmetrically, so
#      the file's overall brace count happens to stay balanced at EOF and
#      the #13 EOF-balance guard alone cannot catch it) -- `scan_raw_string`
#      now recursively hands an interpolated raw string's hole to the same
#      `scan_code(..., stop_at_hole_close=True)` hole scanner ordinary
#      interpolated strings already use, instead of blanking the whole body
#      in one blunt, hole-unaware pass, so a nested literal inside the hole
#      is scanned by its own dedicated, independent closing-delimiter search
#      and can never be mistaken for the outer literal's own closer, and
#  15. correctly requires a run of exactly `dollar_count` consecutive braces
#      (not just one) to open or close an interpolation hole in a raw string
#      opened with two or more leading '$' signs (round-9 reviewer finding:
#      the round-8 fix modeled hole-opening as "single '{' opens, doubled
#      '{{' is an escaped literal", borrowing ordinary interpolated-string
#      escaping semantics that do not exist in raw strings at all -- for a
#      `dollar_count == 2` literal, a single '{' is literal text and only a
#      genuine '{{' run opens a hole, the reverse of what the round-8 fix
#      assumed; 37 files in this repository use two-dollar raw strings
#      today, so this was a live gap, not a theoretical one), AND correctly
#      attributes EXCESS braces in a longer-than-`dollar_count` run to the
#      START of an opening run and the END of a closing run as literal
#      content, per the C# 11 spec (round-10 reviewer finding: the round-9
#      fix consumed the FIRST `dollar_count` braces of an opening run as the
#      hole opener rather than the LAST `dollar_count`, mis-attributing any
#      excess leading brace as a nested code brace and desyncing
#      `hole_depth` whenever the matching closing run has no excess of its
#      own to absorb it, which silently swallowed a following sibling class
#      and left the file's overall brace count unbalanced at EOF).
# =============================================================================

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
VALIDATOR="$SCRIPT_DIR/test-dotnet-test-manifest.sh"
REAL_MANIFEST="$REPO_ROOT/scripts/ci/dotnet-test-manifest.json"
REAL_CI_WORKFLOW="$REPO_ROOT/.github/workflows/ci.yml"

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

# Same as build_mutant, but for check 6 (manifest <-> ci.yml upload-artifact
# drift, issue #2091): mutates the real .github/workflows/ci.yml text instead
# of the manifest JSON. The mutation script receives the output path as
# sys.argv[1] and the real ci.yml path as sys.argv[2].
build_ci_mutant() {
  local out_file="$1"
  local py_script="$2"
  "$PYTHON_BIN" -c "$py_script" "$out_file" "$REAL_CI_WORKFLOW"
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

case_canonical_name_renamed_fails() {
  # Negative: renaming a known project's 'name' while leaving its
  # 'testProject' path unchanged would still satisfy testProject-based
  # auto-discovery and would leave EXPECTED_CANONICAL's per-field checks
  # unreached (no entry is found under the old name to compare fields
  # against). select-dotnet-tests.sh's bucket-routing logic hardcodes exact
  # canonical names and looks entries up by name (not testProject path), so
  # this rename would silently drop the project from the CI matrix. The
  # validator must fail when an EXPECTED_CANONICAL name is missing, not just
  # when a *found* entry's fields mismatch.
  local mutant ; mutant="$(mktemp)"
  build_mutant "$mutant" '
import json, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    data = json.load(f)
for entry in data["testProjects"]:
    if entry.get("name") == "Farm.Web.Api.Tests":
        entry["name"] = "Farm.Web.Api.UnitTests"
        break
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(data, f)
'
  local rc=0
  local report
  report="$(TEST_MANIFEST_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail when a canonical name is renamed, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"canonical name 'Farm.Web.Api.Tests' not found in manifest"* ]]; then
    printf '  expected a missing-canonical-name error, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_api_shard_filter_coverage_fails() {
  # Negative: directory ownership still says `(root)` belongs to services, but
  # removing one root class from the actual VSTest filter would silently skip
  # that class once the project is sharded.
  local mutant ; mutant="$(mktemp)"
  build_mutant "$mutant" '
import json, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    data = json.load(f)
for entry in data["testProjects"]:
    if entry.get("name") != "Farm.Web.Api.Tests":
        continue
    for shard in entry["shards"]:
        if shard.get("name") == "services":
            token = "|FullyQualifiedName~Farm.Web.Api.Tests.PasswordSecurityTests."
            shard["filter"] = shard["filter"].replace(token, "")
            break
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(data, f)
'
  local rc=0
  local report
  report="$(TEST_MANIFEST_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail when a shard filter drops a root test, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"no shard filter covers test source PasswordSecurityTests.cs class PasswordSecurityTests"* ]]; then
    printf '  expected a shard-filter-coverage error, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_sibling_mismatched_indentation_both_flagged_uncovered_fails() {
  # Negative (proves no misattribution): two public top-level sibling
  # classes at different indentation (a stray extra space on the second)
  # must BOTH still be recognized as real, independent owning classes --
  # indentation is just formatting and must not affect whether a class is
  # treated as top-level. Since these are brand-new class names with no
  # registered shard filter, the validator must report a coverage gap for
  # *each* of them; if the mis-indented sibling were ever silently excluded
  # as "nested" (the exact false pass this check guards against), only one
  # gap -- or none -- would be reported instead of two.
  local scratch="$REPO_ROOT/src/tests/Farm.Web.Api.Tests/_ScratchSiblingCoverageTests.cs"
  cat >"$scratch" <<'EOF'
namespace Farm.Web.Api.Tests;

public class ScratchSiblingCoverageTests
{
    [Fact]
    public void Foo()
    {
    }
}

 public class ScratchSiblingCoverageSiblingTests
{
    [Fact]
    public void Bar()
    {
    }
}
EOF
  local rc=0
  local report
  report="$(bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$scratch"
  if (( rc == 0 )); then
    printf '  expected validator to fail (both new sibling classes are uncovered), but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"no shard filter covers test source _ScratchSiblingCoverageTests.cs class ScratchSiblingCoverageTests"* ]]; then
    printf '  expected a coverage-gap error naming the first (indent-0) sibling, got:\n%s\n' "$report" >&2
    return 1
  fi
  if [[ "$report" != *"no shard filter covers test source _ScratchSiblingCoverageTests.cs class ScratchSiblingCoverageSiblingTests"* ]]; then
    printf '  expected a coverage-gap error naming the second (mis-indented) sibling too -- it must not have been silently treated as nested, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_block_scoped_namespace_fails_closed() {
  # Negative: every public class in a file at code-nesting depth > 0 (e.g.
  # every class sits inside a block-scoped `namespace X { ... }` wrapper
  # instead of this codebase's usual file-scoped `namespace X;`) means the
  # validator found no class at namespace scope at all -- it must refuse to
  # guess which class(es) own the [Fact]/[Theory] attributes rather than
  # silently treating the shallowest-nested class as if it were top-level.
  local scratch="$REPO_ROOT/src/tests/Farm.Web.Api.Tests/Controllers/_ScratchBlockScopedNamespaceTests.cs"
  cat >"$scratch" <<'EOF'
namespace Farm.Web.Api.Tests.Controllers
{
    public class ScratchBlockScopedNamespaceTests
    {
        [Fact]
        public void Foo()
        {
        }
    }
}
EOF
  local rc=0
  local report
  report="$(bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$scratch"
  if (( rc == 0 )); then
    printf '  expected validator to fail closed on an every-class-nested file, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"has every public class declaration nested at code depth"* ]]; then
    printf '  expected a nested-at-every-class error, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_raw_string_literal_does_not_desync_brace_depth() {
  # Negative (proves no misattribution): a C# 11 raw string literal
  # (`"""..."""`) containing an embedded `"` character and a `//`-looking
  # substring must not be mistaken for an ordinary string terminator or a
  # line comment -- either mistake would blank past the raw string's own
  # closing delimiter and desync the brace count for the rest of the file,
  # potentially hiding or misattributing the second sibling class below it.
  # (This reproduces the exact false-pass mechanism a round-6 reviewer
  # demonstrated against `PerToolAttributionDtoSerializationTests.cs`.)
  local scratch="$REPO_ROOT/src/tests/Farm.Web.Api.Tests/_ScratchRawStringTests.cs"
  cat >"$scratch" <<'EOF'
namespace Farm.Web.Api.Tests;

public class ScratchRawStringTests
{
    private const string Payload = """
        {"legacy": "http://old.example" // not a real comment
        """;

    [Fact]
    public void Foo()
    {
    }
}

public class ScratchRawStringSiblingTests
{
    [Fact]
    public void Bar()
    {
    }
}
EOF
  local rc=0
  local report
  report="$(bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$scratch"
  if (( rc == 0 )); then
    printf '  expected validator to fail (both new classes are uncovered), but it passed\n' >&2
    return 1
  fi
  if [[ "$report" == *"unbalanced braces"* ]]; then
    printf '  raw string literal desynced the brace count instead of being handled, got:\n%s\n' "$report" >&2
    return 1
  fi
  if [[ "$report" != *"no shard filter covers test source _ScratchRawStringTests.cs class ScratchRawStringTests"* ]]; then
    printf '  expected a coverage-gap error naming the first class, got:\n%s\n' "$report" >&2
    return 1
  fi
  if [[ "$report" != *"no shard filter covers test source _ScratchRawStringTests.cs class ScratchRawStringSiblingTests"* ]]; then
    printf '  expected a coverage-gap error naming the sibling after the raw string too -- the raw string must not have swallowed its closing delimiter, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_unbalanced_braces_fails_closed() {
  # Negative: a stray, unmatched brace anywhere in the file (simulating any
  # future C# construct `_strip_noncode` does not yet understand) must make
  # the file's overall code-brace depth fail to return to 0 at EOF. The
  # validator must refuse to guess class ownership in that case rather than
  # silently using a desynced depth, regardless of what specific construct
  # caused the desync.
  local scratch="$REPO_ROOT/src/tests/Farm.Web.Api.Tests/_ScratchUnbalancedBraceTests.cs"
  cat >"$scratch" <<'EOF'
namespace Farm.Web.Api.Tests;

public class ScratchUnbalancedBraceTests
{
    [Fact]
    public void Foo()
    {
    }
}
{
EOF
  local rc=0
  local report
  report="$(bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$scratch"
  if (( rc == 0 )); then
    printf '  expected validator to fail closed on an unbalanced-brace file, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"has unbalanced braces after comment/string stripping"* ]]; then
    printf '  expected an unbalanced-braces error, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_nested_raw_string_in_hole_does_not_desync_brace_depth() {
  # Positive (round-8 fix, proves no misattribution): `scan_raw_string` now
  # recursively hands an interpolated raw string's hole to
  # `scan_code(..., stop_at_hole_close=True)` instead of blanking the whole
  # body in one blunt, hole-unaware pass. This closes two related false-close
  # scenarios reviewers identified across rounds 7 and 8: a nested raw string
  # literal inside the hole whose own opening quote run is LONGER than the
  # outer literal's (4 vs 3, round 7) or EQUAL to it (3 vs 3, round 8) must
  # not be mistaken for the outer literal's own closing delimiter. The
  # equal-length case is the more dangerous of the two -- before this fix, it
  # happened to leave the file's overall brace count balanced at EOF (the
  # hole's open and close braces were both swallowed symmetrically), so the
  # `case_unbalanced_braces_fails_closed` EOF-balance guard alone could not
  # catch it. Proving all three classes below (both nested-raw-string cases
  # plus their sibling) are still correctly separated and reported as
  # coverage gaps -- and that no "unbalanced braces" error fires at all -- is
  # the only way to confirm this construct is now handled correctly at the
  # source, rather than merely backstopped.
  local scratch="$REPO_ROOT/src/tests/Farm.Web.Api.Tests/_ScratchNestedRawStringHoleTests.cs"
  cat >"$scratch" <<'EOF'
namespace Farm.Web.Api.Tests;

public class ScratchNestedRawStringHoleLongerTests
{
    private static string Build(string inner) => inner;

    [Fact]
    public void Foo()
    {
        string s = $"""outer{Build(""""nested"""")}outer""";
    }
}

public class ScratchNestedRawStringHoleEqualTests
{
    private static string Build(string inner) => inner;

    [Fact]
    public void Bar()
    {
        string s = $"""outer{Build("""nested""")}outer""";
    }
}

public class ScratchNestedRawStringHoleSiblingTests
{
    [Fact]
    public void Baz()
    {
    }
}
EOF
  local rc=0
  local report
  report="$(bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$scratch"
  if (( rc == 0 )); then
    printf '  expected validator to fail (all three new classes are uncovered), but it passed\n' >&2
    return 1
  fi
  if [[ "$report" == *"unbalanced braces"* ]]; then
    printf '  nested raw string in a hole desynced the brace count instead of being handled, got:\n%s\n' "$report" >&2
    return 1
  fi
  if [[ "$report" != *"no shard filter covers test source _ScratchNestedRawStringHoleTests.cs class ScratchNestedRawStringHoleLongerTests"* ]]; then
    printf '  expected a coverage-gap error naming the longer-nested-quote-run class, got:\n%s\n' "$report" >&2
    return 1
  fi
  if [[ "$report" != *"no shard filter covers test source _ScratchNestedRawStringHoleTests.cs class ScratchNestedRawStringHoleEqualTests"* ]]; then
    printf '  expected a coverage-gap error naming the equal-nested-quote-run class, got:\n%s\n' "$report" >&2
    return 1
  fi
  if [[ "$report" != *"no shard filter covers test source _ScratchNestedRawStringHoleTests.cs class ScratchNestedRawStringHoleSiblingTests"* ]]; then
    printf '  expected a coverage-gap error naming the sibling after both nested-raw-string classes too -- neither must have swallowed the rest of the file, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_multi_dollar_raw_string_hole_requires_matching_brace_run() {
  # Positive (round-10 fix, proves no misattribution): per the C# 11
  # raw-string-interpolation spec, when a run of consecutive '{' is longer
  # than the literal's own `dollar_count`, the EXCESS braces at the START
  # of the run are literal content and only the LAST `dollar_count` braces
  # of the run are the actual hole opener (mirrored on the closing side:
  # excess trailing '}' beyond `dollar_count` are literal content that
  # follows the hole). The round-9 fix got this backwards -- it consumed
  # the FIRST `dollar_count` braces of the run as the opener and handed any
  # excess brace to the hole's own code scan, which mis-attributes that
  # excess brace as a real nested code brace and desyncs `hole_depth`
  # whenever the matching close side has no excess of its own to absorb
  # it. That is not a cosmetic difference: this fixture's open run is three
  # '{' (dollar_count=2, one excess) but its close run is exactly two '}'
  # (no excess), so under the round-9 fix the hole never closes via its
  # intended matching run and the scanner runs on past the literal's true
  # closing delimiter, swallowing the sibling class below entirely and
  # leaving the file's overall brace count UNBALANCED at EOF -- proven by
  # empirically running both the round-9 and round-10 tokenizers against
  # this exact fixture. Proving both classes below are still correctly
  # separated and reported as coverage gaps, with no "unbalanced braces"
  # false pass, confirms the excess-brace attribution is now correct on
  # both sides of the hole, not just the equal-run-length case rounds 8
  # and 9 exercised.
  local scratch="$REPO_ROOT/src/tests/Farm.Web.Api.Tests/_ScratchMultiDollarRawStringHoleTests.cs"
  cat >"$scratch" <<'EOF'
namespace Farm.Web.Api.Tests;

public class ScratchMultiDollarRawStringHoleTests
{
    private static string Build(string inner) => inner;

    [Fact]
    public void Foo()
    {
        string s = $$"""literal{{{Build("""nested""")}}outer""";
    }
}

public class ScratchMultiDollarRawStringHoleSiblingTests
{
    [Fact]
    public void Baz()
    {
    }
}
EOF
  local rc=0
  local report
  report="$(bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$scratch"
  if (( rc == 0 )); then
    printf '  expected validator to fail (both new classes are uncovered), but it passed\n' >&2
    return 1
  fi
  if [[ "$report" == *"unbalanced braces"* ]]; then
    printf '  multi-dollar raw string hole with excess leading braces desynced the brace count instead of being handled, got:\n%s\n' "$report" >&2
    return 1
  fi
  if [[ "$report" != *"no shard filter covers test source _ScratchMultiDollarRawStringHoleTests.cs class ScratchMultiDollarRawStringHoleTests"* ]]; then
    printf '  expected a coverage-gap error naming the multi-dollar-hole class, got:\n%s\n' "$report" >&2
    return 1
  fi
  if [[ "$report" != *"no shard filter covers test source _ScratchMultiDollarRawStringHoleTests.cs class ScratchMultiDollarRawStringHoleSiblingTests"* ]]; then
    printf '  expected a coverage-gap error naming the sibling after the multi-dollar hole class too -- it must not have swallowed the rest of the file, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_ci_missing_upload_step_fails() {
  # Negative (check 6, issue #2091): register a brand-new project in the
  # manifest without adding a matching "Upload <name> build" step to
  # ci.yml. A project reachable from the manifest but never published
  # would otherwise compile fine and fail late, at the consumer leg, with
  # a misleading artifact-not-found error -- this must be caught here
  # instead. CI_WORKFLOW_PATH is left unset (real ci.yml) since only the
  # manifest is mutated.
  local mutant ; mutant="$(mktemp)"
  build_mutant "$mutant" '
import json, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    data = json.load(f)
data["testProjects"].append({
    "name": "Farm.Scratch.Unpublished.Tests",
    "testProject": "tests/Farm.Modules.Gcode.Tests/Farm.Modules.Gcode.Tests.csproj",
    "productionProject": "modules/Farm.Modules.Gcode/Farm.Modules.Gcode.csproj",
    "pathPrefixes": ["src/tests/Farm.Modules.Gcode.Tests/"],
    "dependsOnProjects": [],
    "defaultFilter": "Category!=DbHeavy&Category!=Docker",
    "shards": [],
    "requiresProviders": [],
    "runIntegration": False,
    "leg": "Farm.Scratch.Unpublished.Tests",
})
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(data, f)
'
  local rc=0
  local report
  report="$(TEST_MANIFEST_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail on a manifest project with no upload-artifact step, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"Farm.Scratch.Unpublished.Tests is registered in"*"but has no matching 'Upload Farm.Scratch.Unpublished.Tests build' upload-artifact step"* ]]; then
    printf '  expected a missing-upload-step error naming the new project, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_ci_orphaned_upload_step_fails() {
  # Negative (check 6, issue #2091): add an extra "Upload <name> build"
  # step to ci.yml (a duplicate of a real one, renamed) whose project is
  # not registered in the manifest. Proves the guard is bidirectional --
  # an orphaned publish step that no manifest entry drives must be
  # flagged, not just a missing one.
  local mutant ; mutant="$(mktemp)"
  build_ci_mutant "$mutant" '
import re, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    text = f.read()
pattern = re.compile(
    r"(      - name: Upload Farm\.Modules\.Gcode\.Tests build\n"
    r"(?:.*\n)*?"
    r"          if-no-files-found: error\n)"
)
m = pattern.search(text)
assert m, "fixture step block not found -- ci.yml step shape changed"
block = m.group(1)
orphan_block = block.replace("Farm.Modules.Gcode.Tests", "Farm.Scratch.Orphaned.Tests")
mutated = text[: m.end()] + orphan_block + text[m.end():]
with open(out_path, "w", encoding="utf-8") as f:
    f.write(mutated)
'
  local rc=0
  local report
  report="$(CI_WORKFLOW_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail on an orphaned upload-artifact step, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"has an 'Upload Farm.Scratch.Orphaned.Tests build' upload-artifact step"*"but Farm.Scratch.Orphaned.Tests is not registered"* ]]; then
    printf '  expected an orphaned-upload-step error naming the fake project, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_ci_upload_wrong_action_fails() {
  # Negative (check 6, issue #2091, Hicks review finding): a step whose
  # title matches "Upload <name> build" but whose 'uses:' was repurposed
  # to a different action (e.g. a copy-paste that forgot to restore
  # actions/upload-artifact) would otherwise satisfy a title-only match
  # while never actually publishing anything.
  local mutant ; mutant="$(mktemp)"
  build_ci_mutant "$mutant" '
import re, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    text = f.read()
pattern = re.compile(
    r"(      - name: Upload Farm\.Modules\.Gcode\.Tests build\n(?:.*\n)*?        )uses: actions/upload-artifact@v7\n"
)
m = pattern.search(text)
assert m, "fixture step block/uses: line not found -- ci.yml step shape changed"
mutated = text[: m.start()] + m.group(1) + "uses: actions/checkout@v4\n" + text[m.end():]
with open(out_path, "w", encoding="utf-8") as f:
    f.write(mutated)
'
  local rc=0
  local report
  report="$(CI_WORKFLOW_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail on an upload step not using actions/upload-artifact, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"Upload Farm.Modules.Gcode.Tests build' does not use actions/upload-artifact"* ]]; then
    printf '  expected a wrong-action error, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_ci_upload_lookalike_action_name_fails() {
  # Negative (check 6, issue #2091, Hicks round-2 review finding): the
  # 'uses:' check must be anchored on the '@' after the action name, not
  # a bare substring test -- "actions/upload-artifact" is itself a
  # substring of a differently-named action such as
  # "actions/upload-artifact-mirror@v1", so a naive `"uses: actions/"
  # "upload-artifact" in chunk` test would be fooled by a look-alike
  # action name into treating it as the real publisher.
  local mutant ; mutant="$(mktemp)"
  build_ci_mutant "$mutant" '
import re, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    text = f.read()
pattern = re.compile(
    r"(      - name: Upload Farm\.Modules\.Gcode\.Tests build\n(?:.*\n)*?        )uses: actions/upload-artifact@v7\n"
)
m = pattern.search(text)
assert m, "fixture step block/uses: line not found -- ci.yml step shape changed"
mutated = (
    text[: m.start()]
    + m.group(1)
    + "uses: actions/upload-artifact-mirror@v1\n"
    + text[m.end():]
)
with open(out_path, "w", encoding="utf-8") as f:
    f.write(mutated)
'
  local rc=0
  local report
  report="$(CI_WORKFLOW_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail on a look-alike action name, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"Upload Farm.Modules.Gcode.Tests build' does not use actions/upload-artifact"* ]]; then
    printf '  expected a wrong-action error for the look-alike action name, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_ci_upload_action_masked_by_comment_fails() {
  # Negative (check 6, issue #2091, Hicks round-3 review finding): the
  # 'uses:' check must be anchored on the START of a YAML line, not merely
  # searched anywhere in the step's chunk -- otherwise a step whose real
  # 'uses:' points at a different action (e.g. actions/checkout) could be
  # masked by an unrelated comment line elsewhere in the same chunk that
  # happens to contain the literal text
  # "uses: actions/upload-artifact@v7" (e.g. a stale commented-out line
  # left behind by a previous edit), fooling a plain substring/search
  # check into believing the step really publishes an artifact.
  local mutant ; mutant="$(mktemp)"
  build_ci_mutant "$mutant" '
import re, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    text = f.read()
pattern = re.compile(
    r"(      - name: Upload Farm\.Modules\.Gcode\.Tests build\n(?:.*\n)*?        )uses: actions/upload-artifact@v7\n"
)
m = pattern.search(text)
assert m, "fixture step block/uses: line not found -- ci.yml step shape changed"
mutated = (
    text[: m.start()]
    + m.group(1)
    + "uses: actions/checkout@v4\n"
    + m.group(1)
    + "# uses: actions/upload-artifact@v7 (left over from a prior edit)\n"
    + text[m.end():]
)
with open(out_path, "w", encoding="utf-8") as f:
    f.write(mutated)
'
  local rc=0
  local report
  report="$(CI_WORKFLOW_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail when the real uses: is wrong but masked by a comment, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"Upload Farm.Modules.Gcode.Tests build' does not use actions/upload-artifact"* ]]; then
    printf '  expected a wrong-action error despite the masking comment, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_ci_upload_if_condition_wrong_project_fails() {
  # Negative (check 6, issue #2091, Vasquez review finding #1): a step
  # whose title says "Upload Farm.Modules.Gcode.Tests build" but whose
  # if: condition's matrix-selector csproj name was left pointing at a
  # different project (classic copy-paste drift) must fail even though
  # the step *looks* correct by title and by 'uses:' alone -- the old
  # check only flagged a *mismatch when a wrong project happened to be
  # referenced*, so this is exactly that case, made explicit.
  local mutant ; mutant="$(mktemp)"
  build_ci_mutant "$mutant" '
import sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    text = f.read()
needle = "'"'"'Farm.Modules.Gcode.Tests.csproj'"'"'"
assert text.count(needle) == 1, "expected exactly one occurrence of the fixture csproj selector"
mutated = text.replace(needle, "'"'"'Farm.Modules.Inventory.Tests.csproj'"'"'")
with open(out_path, "w", encoding="utf-8") as f:
    f.write(mutated)
'
  local rc=0
  local report
  report="$(CI_WORKFLOW_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail when if: guards on the wrong csproj, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"Upload Farm.Modules.Gcode.Tests build' if: condition does not guard on 'Farm.Modules.Gcode.Tests.csproj'"* ]]; then
    printf '  expected an if:-condition-mismatch error, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_ci_upload_if_condition_missing_fails() {
  # Negative (check 6, issue #2091, Hicks round-2 review finding): the
  # previous if:-condition case only proved the guard catches a
  # *different* project being referenced -- itself already caught by the
  # pre-existing mismatch check this guard superseded. This case instead
  # drops the 'if:' condition entirely (an always-run step), which only
  # the NEW positive assertion ("has no 'if:' condition guarding it")
  # can catch, since there is no wrong-project reference to compare
  # against at all.
  local mutant ; mutant="$(mktemp)"
  build_ci_mutant "$mutant" '
import re, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    text = f.read()
pattern = re.compile(
    r"      - name: Upload Farm\.Modules\.Gcode\.Tests build\n"
    r"        if: >-\n"
    r"(?:.*\n)*?"
    r"          \}\}\n"
    r"(        uses: actions/upload-artifact@v7\n)"
)
m = pattern.search(text)
assert m, "fixture step block/if: block not found -- ci.yml step shape changed"
mutated = (
    text[: m.start()] + "      - name: Upload Farm.Modules.Gcode.Tests build\n"
    + m.group(1) + text[m.end():]
)
with open(out_path, "w", encoding="utf-8") as f:
    f.write(mutated)
'
  local rc=0
  local report
  report="$(CI_WORKFLOW_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail on an upload step with no if: condition at all, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"Upload Farm.Modules.Gcode.Tests build' has no '"*"if:"*"' condition guarding it"* ]]; then
    printf '  expected a missing-if:-condition error, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_ci_upload_with_name_mismatch_fails() {
  # Negative (check 6, issue #2091, Vasquez review finding #2): a step
  # whose title/if: still correctly name Farm.Modules.Gcode.Tests, but
  # whose with: name: field was left pointing at a different project
  # (the copy-paste drifted one field deeper than title/if:) must be
  # caught -- otherwise the guard would pass while ci.yml still uploads
  # the wrong artifact under the wrong name.
  local mutant ; mutant="$(mktemp)"
  build_ci_mutant "$mutant" '
import re, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    text = f.read()
pattern = re.compile(
    r"(      - name: Upload Farm\.Modules\.Gcode\.Tests build\n(?:.*\n)*?        with:\n          name: )Farm\.Modules\.Gcode\.Tests\n"
)
m = pattern.search(text)
assert m, "fixture step block/with:-name: line not found -- ci.yml step shape changed"
mutated = text[: m.start()] + m.group(1) + "Farm.Modules.Inventory.Tests\n" + text[m.end():]
with open(out_path, "w", encoding="utf-8") as f:
    f.write(mutated)
'
  local rc=0
  local report
  report="$(CI_WORKFLOW_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail when with: name: does not match the step title/project, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"uploads artifact name 'Farm.Modules.Inventory.Tests', which does not match its own step title/project 'Farm.Modules.Gcode.Tests'"* ]]; then
    printf '  expected a with:-name-mismatch error, got:\n%s\n' "$report" >&2
    return 1
  fi
}

case_ci_upload_with_path_mismatch_fails() {
  # Negative (check 6, issue #2091, Vasquez review finding #2): a step
  # whose title/if:/with:-name all still correctly name
  # Farm.Modules.Gcode.Tests, but whose with: path: was left pointing at
  # a different project's build output directory, must be caught -- this
  # is the deepest layer of copy-paste drift: everything about the step
  # *looks* right except the one field that actually determines which
  # build output gets published.
  local mutant ; mutant="$(mktemp)"
  build_ci_mutant "$mutant" '
import re, sys
out_path, src_path = sys.argv[1], sys.argv[2]
with open(src_path, encoding="utf-8") as f:
    text = f.read()
pattern = re.compile(
    r"(      - name: Upload Farm\.Modules\.Gcode\.Tests build\n(?:.*\n)*?          path: )"
    r"src/tests/Farm\.Modules\.Gcode\.Tests/bin/Debug/net10\.0\n"
)
m = pattern.search(text)
assert m, "fixture step block/with:-path: line not found -- ci.yml step shape changed"
mutated = (
    text[: m.start()]
    + m.group(1)
    + "src/tests/Farm.Modules.Inventory.Tests/bin/Debug/net10.0\n"
    + text[m.end():]
)
with open(out_path, "w", encoding="utf-8") as f:
    f.write(mutated)
'
  local rc=0
  local report
  report="$(CI_WORKFLOW_PATH="$mutant" bash "$VALIDATOR" 2>&1)" || rc=$?
  rm -f "$mutant"
  if (( rc == 0 )); then
    printf '  expected validator to fail when with: path: does not match the manifest testProject directory, but it passed\n' >&2
    return 1
  fi
  if [[ "$report" != *"uploads path 'src/tests/Farm.Modules.Inventory.Tests/bin/Debug/net10.0', which does not match the manifest's testProject directory for Farm.Modules.Gcode.Tests"* ]]; then
    printf '  expected a with:-path-mismatch error, got:\n%s\n' "$report" >&2
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
  case_canonical_name_renamed_fails
  case_api_shard_filter_coverage_fails
  case_sibling_mismatched_indentation_both_flagged_uncovered_fails
  case_block_scoped_namespace_fails_closed
  case_raw_string_literal_does_not_desync_brace_depth
  case_unbalanced_braces_fails_closed
  case_nested_raw_string_in_hole_does_not_desync_brace_depth
  case_multi_dollar_raw_string_hole_requires_matching_brace_run
  case_ci_missing_upload_step_fails
  case_ci_orphaned_upload_step_fails
  case_ci_upload_wrong_action_fails
  case_ci_upload_lookalike_action_name_fails
  case_ci_upload_action_masked_by_comment_fails
  case_ci_upload_if_condition_wrong_project_fails
  case_ci_upload_if_condition_missing_fails
  case_ci_upload_with_name_mismatch_fails
  case_ci_upload_with_path_mismatch_fails
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
