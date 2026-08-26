#!/usr/bin/env bash
# =============================================================================
# test-dotnet-test-manifest.sh — completeness/consistency validator for
# scripts/ci/dotnet-test-manifest.json (issue #2031).
#
# Fails (rc=1) with a specific, itemized reason if:
#   1. Any `src/tests/**/*.Tests.csproj` on disk is missing from the manifest,
#      or is registered more than once.
#   2. Any manifest entry's `testProject` path does not exist on disk
#      relative to `src/`.
#   3. `Farm.Web.IntegrationTests` — which intentionally does not match the
#      `*.Tests.csproj` glob (it is "...IntegrationTests.csproj", not
#      "...Integration.Tests.csproj") and so is never auto-discovered by (1)
#      — is present in the manifest anyway, by explicit name.
#   4. `Farm.Web.Api.Tests`'s shards are not exhaustive (every subdirectory
#      under src/tests/Farm.Web.Api.Tests/ covered by exactly one shard) or
#      not mutually exclusive (no subdirectory listed twice) or any shard is
#      empty (zero namespace prefixes).
#   5. The #2022 fix regresses: `Farm.Moonraker.Emulator.Tests` and
#      `Farm.Slicer.ProfileParsing.Tests` must remain registered in the
#      manifest (they are only reachable via the tests_other/full-safe
#      fallback bucket today — this script does not change that, it only
#      guards the registration itself from disappearing again).
#
# This script does not build or run any .NET code; it only reads the
# manifest JSON and walks the filesystem, so it is safe to run without a
# restore and fast enough for every PR (`ci-tools` job).
# =============================================================================

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
MANIFEST="${TEST_MANIFEST_PATH:-$REPO_ROOT/scripts/ci/dotnet-test-manifest.json}"
SRC_ROOT="$REPO_ROOT/src"

FAILURES=()

if [[ ! -r "$MANIFEST" ]]; then
  echo "FATAL: manifest not readable at $MANIFEST" >&2
  exit 1
fi

# Prefer python3 (matches select-dotnet-tests.sh and ci.yml's TRX parsing);
# fall back to `python` for local dev shells where `python3` may be an
# unconfigured Windows Store execution-alias stub that exists on PATH but
# does not actually run.
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

# ---------------------------------------------------------------------------
# 1 & 2 & 3 & 5: manifest structural checks, delegated to Python for JSON
# parsing and duplicate/shard-coverage detection. Emits one "ERROR: ..." line
# per problem found and exits non-zero if any were found; prints nothing on
# success.
# ---------------------------------------------------------------------------
manifest_report="$("$PYTHON_BIN" - "$MANIFEST" "$SRC_ROOT" <<'PYEOF'
import json
import os
import re
import sys

manifest_path, src_root = sys.argv[1], sys.argv[2]

with open(manifest_path, encoding="utf-8") as f:
    data = json.load(f)

entries = data.get("testProjects", [])
errors = []

if not entries:
    errors.append("manifest has zero testProjects entries")

seen_names = {}
for entry in entries:
    name = entry.get("name")
    if not name:
        errors.append("manifest entry missing 'name' field")
        continue
    seen_names[name] = seen_names.get(name, 0) + 1
for name, count in seen_names.items():
    if count > 1:
        errors.append(f"duplicate manifest entry for {name} ({count} occurrences)")

# 1b. Two entries with *different* names but the same testProject path would
# still register one physical .csproj twice in the CI matrix -- de-duping by
# 'name' alone does not catch this, so also de-dupe by normalized path.
seen_test_project_paths = {}
for entry in entries:
    rel = entry.get("testProject")
    if not rel:
        continue
    norm = os.path.normpath(rel)
    seen_test_project_paths.setdefault(norm, []).append(entry.get("name", "<unnamed>"))
for norm, owners in seen_test_project_paths.items():
    if len(owners) > 1:
        errors.append(
            f"duplicate testProject path {norm} registered under multiple names: {', '.join(owners)}"
        )

# 2. Every manifest testProject file must exist under src/.
for entry in entries:
    name = entry.get("name", "<unnamed>")
    rel = entry.get("testProject")
    if not rel:
        errors.append(f"{name}: manifest entry missing 'testProject' field")
        continue
    abs_path = os.path.join(src_root, rel)
    if not os.path.isfile(abs_path):
        errors.append(f"{name}: testProject path does not exist on disk: {rel}")

# 2b. Schema: every entry must declare all fields the manifest contract
# promises (see docs/CI.md), with the expected JSON type. A missing or
# mistyped field (e.g. requiresProviders as a string instead of a list, or
# runIntegration as "true" instead of true) would silently break downstream
# consumers without this check.
REQUIRED_STRING_FIELDS = ("name", "productionProject", "testProject", "defaultFilter", "leg")
REQUIRED_LIST_FIELDS = ("pathPrefixes", "dependsOnProjects", "shards", "requiresProviders")
REQUIRED_BOOL_FIELDS = ("runIntegration",)
for entry in entries:
    name = entry.get("name", "<unnamed>")
    for field in REQUIRED_STRING_FIELDS:
        value = entry.get(field)
        if not isinstance(value, str) or not value.strip():
            errors.append(f"{name}: field '{field}' must be a non-empty string, got {value!r}")
    for field in REQUIRED_LIST_FIELDS:
        if field not in entry:
            errors.append(f"{name}: missing required field '{field}'")
            continue
        value = entry[field]
        if not isinstance(value, list):
            errors.append(f"{name}: field '{field}' must be a list, got {type(value).__name__}")
    for field in REQUIRED_BOOL_FIELDS:
        if field not in entry:
            errors.append(f"{name}: missing required field '{field}'")
            continue
        value = entry[field]
        if not isinstance(value, bool):
            errors.append(f"{name}: field '{field}' must be a boolean, got {value!r}")

# 2c. Pin the canonical name -> testProject/productionProject/defaultFilter/
# runIntegration mapping for every known entry. Structural presence/type
# checks alone would still accept a PR that (accidentally or otherwise)
# repoints a project's `testProject` at a different .csproj, silently swaps
# `runIntegration`, or narrows `defaultFilter` to a smaller passing subset --
# none of that would fail the checks above, and it would quietly weaken what
# CI actually exercises for that leg. Changing one of these pinned values is
# still possible, but only by also updating EXPECTED_CANONICAL here, which
# puts the change in the diff a reviewer sees rather than letting it hide in
# a JSON-only edit.
EXPECTED_CANONICAL = {
    "Farm.Web.Api.Tests": {
        "testProject": "tests/Farm.Web.Api.Tests/Farm.Web.Api.Tests.csproj",
        "productionProject": "api/Farm.Web.Api.csproj",
        "defaultFilter": "Category!=DbHeavy&Category!=Docker",
        "runIntegration": False,
    },
    "Farm.Slicer.Module.Tests": {
        "testProject": "tests/Farm.Slicer.Module.Tests/Farm.Slicer.Module.Tests.csproj",
        "productionProject": "slicer/Farm.Slicer.Module/Farm.Slicer.Module.csproj",
        "defaultFilter": "Category!=DbHeavy&Category!=Docker",
        "runIntegration": False,
    },
    "Farm.OrcaSlicer.Worker.Tests": {
        "testProject": "tests/Farm.OrcaSlicer.Worker.Tests/Farm.OrcaSlicer.Worker.Tests.csproj",
        "productionProject": "orcaslicer-worker/Farm.OrcaSlicer.Worker.csproj",
        "defaultFilter": "Category!=DbHeavy&Category!=Docker",
        "runIntegration": False,
    },
    "Farm.Moonraker.Emulator.Tests": {
        "testProject": "tests/Farm.Moonraker.Emulator.Tests/Farm.Moonraker.Emulator.Tests.csproj",
        "productionProject": "moonraker-emulator/Farm.Moonraker.Emulator/Farm.Moonraker.Emulator.csproj",
        "defaultFilter": "Category!=DbHeavy&Category!=Docker",
        "runIntegration": False,
    },
    "Farm.Slicer.ProfileParsing.Tests": {
        "testProject": "tests/Farm.Slicer.ProfileParsing.Tests/Farm.Slicer.ProfileParsing.Tests.csproj",
        "productionProject": "slicer/Farm.Slicer.ProfileParsing/Farm.Slicer.ProfileParsing.csproj",
        "defaultFilter": "Category!=DbHeavy&Category!=Docker",
        "runIntegration": False,
    },
    "Farm.Web.IntegrationTests": {
        "testProject": "tests/Farm.Web.IntegrationTests/Farm.Web.IntegrationTests.csproj",
        "productionProject": "api/Farm.Web.Api.csproj",
        "defaultFilter": "Category!=DbHeavy&Category!=Docker",
        "runIntegration": True,
    },
}
entries_by_name = {entry.get("name"): entry for entry in entries if entry.get("name")}
for pinned_name, pinned_fields in EXPECTED_CANONICAL.items():
    entry = entries_by_name.get(pinned_name)
    if entry is None:
        # A missing canonical name is NOT already caught elsewhere: only
        # Farm.Web.IntegrationTests and the two #2022 regression projects are
        # separately hard-required by name below. select-dotnet-tests.sh's
        # bucket-routing logic (classify_path) still hardcodes these exact
        # canonical names, so a rename here -- even with testProject left
        # unchanged, which would otherwise satisfy the auto-discovery check
        # further down -- would make finish() silently drop the renamed
        # project from every matrix it should appear in (it looks up entries
        # by name, not by testProject path). Fail loudly instead.
        errors.append(
            f"canonical name '{pinned_name}' not found in manifest (was it "
            "renamed? select-dotnet-tests.sh's bucket-routing logic "
            "hardcodes this exact name and looks entries up by name, not by "
            "testProject path, so a rename would silently drop this project "
            "from the CI matrix even if its testProject path is unchanged -- "
            "if this rename is intentional, update EXPECTED_CANONICAL and "
            "the corresponding name literals in select-dotnet-tests.sh in "
            "the same PR)"
        )
        continue
    for field, expected_value in pinned_fields.items():
        actual_value = entry.get(field)
        if actual_value != expected_value:
            errors.append(
                f"{pinned_name}: field '{field}' does not match its pinned canonical "
                f"value (expected {expected_value!r}, got {actual_value!r} -- if this "
                "is an intentional change to what CI selects/runs for this project, "
                "update EXPECTED_CANONICAL in test-dotnet-test-manifest.sh in the same PR)"
            )

# 3. Farm.Web.IntegrationTests must be explicitly registered (it never
# matches the '*.Tests.csproj' glob used for auto-discovery below).
if "Farm.Web.IntegrationTests" not in seen_names:
    errors.append(
        "Farm.Web.IntegrationTests is not registered in the manifest "
        "(it does not match *.Tests.csproj and is never auto-discovered "
        "-- it must be listed explicitly)"
    )

# 5. #2022 regression guard: these two must remain registered even though
# neither has a dedicated path-selection bucket (they run only via the
# tests_other/full-safe fallback today).
for regression_name in ("Farm.Moonraker.Emulator.Tests", "Farm.Slicer.ProfileParsing.Tests"):
    if regression_name not in seen_names:
        errors.append(
            f"{regression_name} is missing from the manifest "
            "(regression of #2022 -- this project must stay registered so "
            "CI actually compiles and executes it)"
        )

# 1. Auto-discover every *.Tests.csproj under src/tests/**, excluding the
# manifest file itself, and confirm each corresponds to exactly one manifest
# entry by testProject path.
manifest_test_projects = {
    os.path.normpath(entry["testProject"])
    for entry in entries
    if entry.get("testProject")
}
tests_dir = os.path.join(src_root, "tests")
discovered = []
if os.path.isdir(tests_dir):
    for root, dirs, files in os.walk(tests_dir):
        # Never descend into build output; it can't contain a real project file.
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for fname in files:
            if fname.endswith(".Tests.csproj"):
                rel = os.path.relpath(os.path.join(root, fname), src_root)
                discovered.append(os.path.normpath(rel))

for rel in discovered:
    if rel not in manifest_test_projects:
        errors.append(f"discovered test project not registered in manifest: {rel}")

# 4. Farm.Web.Api.Tests shard exhaustiveness / mutual exclusivity / non-empty.
api_entry = next((e for e in entries if e.get("name") == "Farm.Web.Api.Tests"), None)
if api_entry is None:
    errors.append("Farm.Web.Api.Tests entry not found in manifest (required for shard validation)")
else:
    shards = api_entry.get("shards", [])
    if not shards:
        errors.append("Farm.Web.Api.Tests has no shards defined")

    # bin/obj are dotnet build output, never test-namespace directories; they
    # only exist locally after a build has run and must not affect shard
    # exhaustiveness checks.
    BUILD_OUTPUT_DIRS = {"bin", "obj"}
    api_test_dir = os.path.join(src_root, "tests", "Farm.Web.Api.Tests")
    actual_subdirs = set()
    if os.path.isdir(api_test_dir):
        for entry_name in os.listdir(api_test_dir):
            if entry_name in BUILD_OUTPUT_DIRS:
                continue
            if os.path.isdir(os.path.join(api_test_dir, entry_name)):
                actual_subdirs.add(entry_name)
    # "(root)" is a synthetic bucket for loose top-level .cs files that are
    # not inside any namespace subdirectory -- not a real directory.
    expected = actual_subdirs | {"(root)"}

    seen_prefixes = {}
    for shard in shards:
        shard_name = shard.get("name", "<unnamed shard>")
        prefixes = shard.get("namespacePrefixes", [])
        if not prefixes:
            errors.append(f"Farm.Web.Api.Tests shard '{shard_name}' has zero namespacePrefixes (must be non-empty)")
        for p in prefixes:
            seen_prefixes.setdefault(p, []).append(shard_name)

    # Mutually exclusive: no prefix claimed by more than one shard.
    for p, owners in seen_prefixes.items():
        if len(owners) > 1:
            errors.append(f"Farm.Web.Api.Tests namespace prefix '{p}' claimed by multiple shards: {', '.join(owners)}")

    # Exhaustive: every real subdirectory (+ the root bucket) must be
    # claimed by exactly one shard.
    covered = set(seen_prefixes.keys())
    missing = expected - covered
    if missing:
        errors.append(f"Farm.Web.Api.Tests shards do not cover: {', '.join(sorted(missing))}")

    # No shard should claim something that doesn't exist (catches typos/rot).
    unexpected = covered - expected
    if unexpected:
        errors.append(f"Farm.Web.Api.Tests shards reference nonexistent namespaces: {', '.join(sorted(unexpected))}")

    # Directory ownership alone cannot prove the VSTest FullyQualifiedName
    # expressions select every test. Root-level classes share the base
    # namespace, and a file may deliberately use a namespace that differs from
    # its directory (IAppSettingTests does). Check every source file containing
    # xUnit facts/theories against the positive filter prefixes of its owning
    # shard. A trailing dot is required so `Data` cannot accidentally match
    # `DataManagement`, or a root class whose name starts with a directory.
    shard_by_name = {shard.get("name"): shard for shard in shards}
    for root, dirs, files in os.walk(api_test_dir):
        dirs[:] = [d for d in dirs if d not in BUILD_OUTPUT_DIRS]
        for file_name in files:
            if not file_name.endswith(".cs"):
                continue
            path = os.path.join(root, file_name)
            with open(path, encoding="utf-8-sig") as source:
                text = source.read()
            if not re.search(r"\[\s*(?:Fact|Theory)\b", text):
                continue

            relative = os.path.relpath(path, api_test_dir)
            parts = relative.split(os.sep)
            prefix = "(root)" if len(parts) == 1 else parts[0]
            owners = seen_prefixes.get(prefix, [])
            if len(owners) != 1:
                continue

            namespace_match = re.search(
                r"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)",
                text,
                re.MULTILINE,
            )
            if namespace_match is None:
                errors.append(f"Farm.Web.Api.Tests test source has no namespace: {relative}")
                continue
            namespace = namespace_match.group(1)
            candidate = f"{namespace}.{os.path.splitext(file_name)[0]}."
            owner_filter = shard_by_name[owners[0]].get("filter", "")
            positive_prefixes = re.findall(
                r"FullyQualifiedName~([^|&()]+)",
                owner_filter,
            )
            if not any(
                token.endswith(".") and candidate.startswith(token)
                for token in positive_prefixes
            ):
                errors.append(
                    "Farm.Web.Api.Tests shard "
                    f"'{owners[0]}' filter does not cover test source {relative} "
                    f"(expected a prefix matching {candidate})"
                )

for e in errors:
    print(f"ERROR: {e}")
PYEOF
)"
manifest_reader_rc=$?

# A nonzero exit here means the Python subprocess crashed (e.g. an uncaught
# exception from malformed JSON structure that json.load() itself accepted
# but the schema checks above did not anticipate) rather than completing and
# reporting zero or more "ERROR: ..." lines. Ignoring this would let a
# validator crash look identical to "no problems found" -- fail closed
# instead of falling through to the empty-report PASS path below.
if [[ $manifest_reader_rc -ne 0 ]]; then
  echo "FAIL: dotnet test manifest validator crashed (python exit code $manifest_reader_rc); see stderr above for the traceback" >&2
  exit 1
fi

if [[ -n "$manifest_report" ]]; then
  while IFS= read -r line; do
    # Strip a trailing CR: some local Windows Python interpreters emit CRLF
    # even though the script only ever prints "\n".
    line="${line%$'\r'}"
    [[ -z "$line" ]] && continue
    FAILURES+=("$line")
  done <<< "$manifest_report"
fi

if (( ${#FAILURES[@]} > 0 )); then
  echo "FAIL: dotnet test manifest validation found ${#FAILURES[@]} problem(s):" >&2
  for f in "${FAILURES[@]}"; do
    echo "  - $f" >&2
  done
  exit 1
fi

echo "PASS: dotnet test manifest is complete and consistent"
exit 0
