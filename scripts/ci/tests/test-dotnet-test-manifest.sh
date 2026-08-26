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
#   4. Any manifest entry's shards (Farm.Web.Api.Tests and
#      Farm.Infrastructure.Tests today) are not exhaustive, not mutually
#      exclusive, or any shard is empty (zero namespace prefixes). Exhaustive/
#      mutually-exclusive is proven at the directory level for entries whose
#      declared prefixes are flat top-level names, and always at the
#      per-source-file level via a filter-vs-namespace match (this is the
#      only proof available for entries like Farm.Infrastructure.Tests that
#      split a single top-level directory, e.g. Services/, across shards
#      using nested "Parent/Child" namespacePrefixes).
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


def _strip_noncode(text):
    """Return a copy of `text` with every character that is not real C#
    code -- comments, and the contents of char/string literals, including
    the delimiter braces of interpolation holes such as `$"{expr}"` and any
    string nested inside one -- replaced with a space (newlines are kept so
    line/character positions are unaffected). Brace-counting over the
    result therefore reflects only genuine code-block nesting (namespace,
    class, method, lambda, etc.), and is not perturbed by a `{`/`}` that
    only exists as interpolation-hole punctuation or as an ordinary
    character inside a string/char literal or comment.
    """
    n = len(text)
    blanks = []

    def scan_string(i, verbatim, interpolated):
        start = i
        while i < n:
            c = text[i]
            if not verbatim and c == "\\" and i + 1 < n:
                i += 2
                continue
            if verbatim and c == '"':
                if i + 1 < n and text[i + 1] == '"':
                    i += 2
                    continue
                blanks.append((start, i))
                return i + 1
            if not verbatim and c == '"':
                blanks.append((start, i))
                return i + 1
            if interpolated and c == "{":
                if i + 1 < n and text[i + 1] == "{":
                    i += 2
                    continue
                blanks.append((start, i))
                blanks.append((i, i + 1))
                i = scan_code(i + 1, stop_at_hole_close=True)
                start = i
                continue
            if interpolated and c == "}" and i + 1 < n and text[i + 1] == "}":
                i += 2
                continue
            i += 1
        blanks.append((start, n))
        return n

    def scan_raw_string(open_start, body_start, quote_run):
        """Scan a C# 11 raw string literal's body, starting right after its
        opening delimiter (`open_start..body_start`, the run of any leading
        `$` interpolation markers plus the opening `quote_run`-length run of
        '"' characters), and blank the entire literal -- opening delimiter,
        body, and closing delimiter alike. The closing delimiter is the
        first run of at least `quote_run` consecutive '"' characters found
        at or after `body_start`; searching from `body_start` (not
        `open_start`) is essential, since the opening delimiter is itself
        such a run and must never be mistaken for its own closer.

        Unlike ordinary interpolated strings, interpolation holes inside a
        raw string literal are NOT specially unblanked here: the whole body
        -- hole braces included -- is blanked. This is deliberately blunter
        than `scan_string`'s hole handling, but safe for brace-depth
        purposes, since a hole's braces never contribute to the code-side
        count either way (blanked or exposed-then-immediately-rebalanced),
        so they cannot desynchronize `_code_brace_depths`.
        """
        i = body_start
        while i < n:
            if text[i] == '"':
                j = i
                while j < n and text[j] == '"':
                    j += 1
                if j - i >= quote_run:
                    blanks.append((open_start, j))
                    return j
                i = j
                continue
            i += 1
        blanks.append((open_start, n))
        return n

    def scan_code(i, stop_at_hole_close):
        hole_depth = 0
        while i < n:
            c = text[i]
            if c == "/" and i + 1 < n and text[i + 1] == "/":
                j = text.find("\n", i)
                j = n if j == -1 else j
                blanks.append((i, j))
                i = j
                continue
            if c == "/" and i + 1 < n and text[i + 1] == "*":
                j = text.find("*/", i + 2)
                j = n if j == -1 else j + 2
                blanks.append((i, j))
                i = j
                continue
            if c == "'":
                j = i + 1
                j = j + 2 if j < n and text[j] == "\\" else j + 1
                while j < n and text[j] != "'":
                    j += 1
                j = min(j + 1, n)
                blanks.append((i, j))
                i = j
                continue
            if c == '"' or c == "$":
                # C# 11 raw string literal: zero or more '$' (interpolation
                # markers), followed by a run of 3-or-more '"' characters.
                # A run of only 1-2 quotes (an ordinary/empty string, or the
                # `$"`/`@"`/`$@"`/`@$"` prefixes handled below) is not a raw
                # string and falls through untouched.
                j = i
                while j < n and text[j] == "$":
                    j += 1
                k = j
                while k < n and text[k] == '"':
                    k += 1
                quote_run = k - j
                if quote_run >= 3:
                    i = scan_raw_string(i, k, quote_run)
                    continue
            if text[i:i + 3] in ("$@\"", "@$\""):
                blanks.append((i, i + 3))
                i = scan_string(i + 3, verbatim=True, interpolated=True)
                continue
            if text[i:i + 2] == '@"':
                blanks.append((i, i + 2))
                i = scan_string(i + 2, verbatim=True, interpolated=False)
                continue
            if text[i:i + 2] == '$"':
                blanks.append((i, i + 2))
                i = scan_string(i + 2, verbatim=False, interpolated=True)
                continue
            if c == '"':
                blanks.append((i, i + 1))
                i = scan_string(i + 1, verbatim=False, interpolated=False)
                continue
            if stop_at_hole_close and c == "{":
                hole_depth += 1
                i += 1
                continue
            if stop_at_hole_close and c == "}":
                if hole_depth == 0:
                    blanks.append((i, i + 1))
                    return i + 1
                hole_depth -= 1
                i += 1
                continue
            i += 1
        return i

    scan_code(0, stop_at_hole_close=False)
    out = list(text)
    for s, e in blanks:
        for k in range(s, e):
            if out[k] != "\n":
                out[k] = " "
    return "".join(out)


def _code_brace_depths(code_text):
    """Return a list, parallel to `code_text`, of the C#-code brace depth at
    each character position (the depth *before* consuming that position's
    character): depth 0 is outside any {}-delimited block. Callers must
    pass text already run through `_strip_noncode`, so that braces inside
    comments and string/char literals (including interpolation-hole
    delimiters) do not perturb the count.
    """
    depths = [0] * (len(code_text) + 1)
    depth = 0
    for idx, ch in enumerate(code_text):
        depths[idx] = depth
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
    depths[len(code_text)] = depth
    return depths


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
    "Farm.Modules.SmartPlug.Tests": {
        "testProject": "tests/Farm.Modules.SmartPlug.Tests/Farm.Modules.SmartPlug.Tests.csproj",
        "productionProject": "modules/Farm.Modules.SmartPlug/Farm.Modules.SmartPlug.csproj",
        "defaultFilter": "Category!=DbHeavy&Category!=Docker",
        "runIntegration": False,
    },
    "Farm.Modules.Maintenance.Tests": {
        "testProject": "tests/Farm.Modules.Maintenance.Tests/Farm.Modules.Maintenance.Tests.csproj",
        "productionProject": "modules/Farm.Modules.Maintenance/Farm.Modules.Maintenance.csproj",
        "defaultFilter": "Category!=DbHeavy&Category!=Docker",
        "runIntegration": False,
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

# 4. Shard exhaustiveness / mutual exclusivity / non-empty, generalized over
# every manifest entry that declares shards (originally Farm.Web.Api.Tests
# only; Farm.Infrastructure.Tests added in #2033 with the same obligation).
# bin/obj are dotnet build output, never test-namespace directories; they
# only exist locally after a build has run and must not affect shard
# exhaustiveness checks.
BUILD_OUTPUT_DIRS = {"bin", "obj"}

if not any(e.get("shards") for e in entries):
    errors.append("no manifest entry declares any shards (expected at least Farm.Web.Api.Tests and Farm.Infrastructure.Tests)")

for sharded_entry in entries:
    entry_name = sharded_entry.get("name", "<unnamed>")
    shards = sharded_entry.get("shards", [])
    if not shards:
        continue

    test_project_rel = sharded_entry.get("testProject")
    entry_test_dir = os.path.join(src_root, os.path.dirname(test_project_rel)) if test_project_rel else None
    if not entry_test_dir or not os.path.isdir(entry_test_dir):
        errors.append(f"{entry_name}: has shards but its test directory does not exist: {entry_test_dir}")
        continue

    seen_prefixes = {}
    for shard in shards:
        shard_name = shard.get("name", "<unnamed shard>")
        prefixes = shard.get("namespacePrefixes", [])
        if not prefixes:
            errors.append(f"{entry_name}: shard '{shard_name}' has zero namespacePrefixes (must be non-empty)")
        for p in prefixes:
            seen_prefixes.setdefault(p, []).append(shard_name)

    # Mutually exclusive: no declared prefix claimed by more than one shard.
    for p, owners in seen_prefixes.items():
        if len(owners) > 1:
            errors.append(f"{entry_name}: namespace prefix '{p}' claimed by multiple shards: {', '.join(owners)}")

    # Directory-level exhaustiveness only applies cleanly when every declared
    # prefix is a flat top-level directory name (Farm.Web.Api.Tests' shards).
    # Farm.Infrastructure.Tests uses nested "Parent/Child" prefixes (e.g.
    # "Services/Notifications") to split a single top-level directory (e.g.
    # "Services") across several shards, so top-level-directory enumeration
    # cannot prove exhaustiveness there -- the per-file filter check below
    # (which does not depend on directory structure at all) is the actual
    # proof for those entries.
    all_flat = all("/" not in p for p in seen_prefixes)
    if all_flat:
        actual_subdirs = set()
        for child in os.listdir(entry_test_dir):
            if child in BUILD_OUTPUT_DIRS:
                continue
            if os.path.isdir(os.path.join(entry_test_dir, child)):
                actual_subdirs.add(child)
        # "(root)" is a synthetic bucket for loose top-level .cs files that
        # are not inside any namespace subdirectory -- not a real directory.
        expected = actual_subdirs | {"(root)"}
        covered = set(seen_prefixes.keys())
        missing = expected - covered
        if missing:
            errors.append(f"{entry_name}: shards do not cover: {', '.join(sorted(missing))}")
        unexpected = covered - expected
        if unexpected:
            errors.append(f"{entry_name}: shards reference nonexistent namespaces: {', '.join(sorted(unexpected))}")

    # Directory ownership alone cannot prove the VSTest FullyQualifiedName
    # expressions select every test, and for entries with nested prefixes it
    # is not even attempted above. Check every source file containing xUnit
    # facts/theories directly against every shard's filter: it must match
    # exactly one. Root-level classes share the base namespace, and a file
    # may deliberately use a namespace that differs from its directory (this
    # is why this check does not rely on which directory the file is in). A
    # trailing dot is required in every filter term so `Data` cannot
    # accidentally match `DataManagement`, or a root class whose name starts
    # with a directory/shard name.
    #
    # The candidate FullyQualifiedName is derived from the actual top-level
    # `class` declaration nearest above each [Fact]/[Theory] attribute, not
    # from the file name: several files in this codebase legitimately
    # declare more than one test class (e.g. a class under test plus its
    # in-memory fake), and relying on the file name would either miss the
    # non-eponymous classes entirely or produce a false failure once a class
    # is renamed independently of its file. See the code-nesting-depth
    # reasoning further below for how nested (non-owning) classes, such as
    # an `IClassFixture` factory declared inside its test class, are
    # excluded from this association.
    for root, dirs, files in os.walk(entry_test_dir):
        dirs[:] = [d for d in dirs if d not in BUILD_OUTPUT_DIRS]
        for file_name in files:
            if not file_name.endswith(".cs"):
                continue
            path = os.path.join(root, file_name)
            with open(path, encoding="utf-8-sig") as source:
                text = source.read()
            if not re.search(r"\[\s*(?:Fact|Theory)\b", text):
                continue

            relative = os.path.relpath(path, entry_test_dir)
            namespace_match = re.search(
                r"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)",
                text,
                re.MULTILINE,
            )
            if namespace_match is None:
                errors.append(f"{entry_name}: test source has no namespace: {relative}")
                continue
            namespace = namespace_match.group(1)

            # Restricted to `public` classes: xUnit only discovers [Fact]/
            # [Theory] methods on a publicly reflectable type, so a private
            # or internal nested helper/fake class (e.g. a local
            # IHttpClientFactory stub) can never itself own a test and must
            # not be picked up as an owning class.
            #
            # Public classes alone are not sufficient, though: this codebase
            # also has a recurring xUnit fixture idiom --
            #   public class FooTests : IClassFixture<FooTests.Factory>
            #   {
            #       public class Factory : CustomWebApplicationFactory { ... }
            #       [Fact] public async Task Bar() { ... }
            #   }
            # -- where the nested `Factory` class is itself public. Treating
            # every public class as a candidate and picking the "nearest
            # preceding" one by raw text position mis-attributes facts
            # declared in the outer class (after the nested class) to the
            # nested class instead, because the nested class's own body has
            # already closed by the time the fact appears.
            #
            # Distinguishing a true top-level test class from a nested one
            # requires knowing where each class's body actually ends. Naive
            # brace counting over the raw source is unreliable because C#
            # interpolated strings (`$"...{expr}..."`) contain `{`/`}`
            # characters that are not real code-block delimiters -- but
            # `_strip_noncode` resolves that by blanking out comments and
            # the contents of every string/char literal (including
            # interpolation-hole delimiters and any string nested inside a
            # hole) before brace-counting, so `_code_brace_depths` gives the
            # syntactically-grounded nesting depth of each class
            # declaration: a class at namespace scope (this codebase uses
            # file-scoped namespaces exclusively) sits at depth 0, and a
            # class nested inside another class's body (e.g. the
            # `public class Factory : CustomWebApplicationFactory` xUnit
            # fixture idiom, or a `[CollectionDefinition]` marker class)
            # sits at depth 1 or deeper -- regardless of how either class
            # happens to be indented, so a genuine top-level sibling that is
            # accidentally mis-indented can never be misattributed as
            # nested, and a genuinely nested class can never be mistaken for
            # top-level. If `_strip_noncode` ever fails to recognize some
            # C# construct (e.g. an unhandled string-literal form), the
            # resulting depth desync cannot silently misattribute a class:
            # the brace-balance check below fails closed the moment the
            # file's overall code-brace depth does not return to 0 at EOF.
            class_matches = list(
                re.finditer(
                    r"\bpublic\s+(?:(?:sealed|abstract|static|partial)\s+)*class\s+"
                    r"([A-Za-z_][A-Za-z0-9_]*)",
                    text,
                )
            )
            if not class_matches:
                errors.append(
                    f"{entry_name}: test source {relative} has [Fact]/[Theory] "
                    "attributes but no public class declaration"
                )
                continue

            code_depths = _code_brace_depths(_strip_noncode(text))
            if code_depths[-1] != 0:
                errors.append(
                    f"{entry_name}: test source {relative} has unbalanced "
                    f"braces after comment/string stripping (ends at code "
                    f"depth {code_depths[-1]} instead of 0) -- the "
                    "shard-coverage validator's C# tokenizer may not "
                    "understand a construct in this file; refusing to guess "
                    "class ownership rather than risk a silent "
                    "misattribution"
                )
                continue

            class_depths = {m.start(): code_depths[m.start()] for m in class_matches}
            min_depth = min(class_depths.values())
            if min_depth != 0:
                errors.append(
                    f"{entry_name}: test source {relative} has every public class "
                    f"declaration nested at code depth {min_depth} (none at "
                    "namespace scope), which the shard-coverage validator did "
                    "not expect -- refusing to guess which class(es) own the "
                    "[Fact]/[Theory] attributes here"
                )
                continue

            class_positions = [
                (m.start(), m.group(1))
                for m in class_matches
                if class_depths[m.start()] == min_depth
            ]

            active_classes = set()
            for attr_match in re.finditer(r"\[\s*(?:Fact|Theory)\b", text):
                attr_pos = attr_match.start()
                owning_class = None
                for class_pos, class_name in class_positions:
                    if class_pos <= attr_pos:
                        owning_class = class_name
                    else:
                        break
                if owning_class is None:
                    errors.append(
                        f"{entry_name}: test source {relative} has a "
                        "[Fact]/[Theory] attribute before any top-level class "
                        "declaration"
                    )
                    continue
                active_classes.add(owning_class)

            for class_name in sorted(active_classes):
                candidate = f"{namespace}.{class_name}."

                matching_shards = []
                for shard in shards:
                    positive_prefixes = re.findall(
                        r"FullyQualifiedName~([^|&()]+)",
                        shard.get("filter", ""),
                    )
                    if any(
                        token.endswith(".") and candidate.startswith(token)
                        for token in positive_prefixes
                    ):
                        matching_shards.append(shard.get("name", "<unnamed shard>"))

                if not matching_shards:
                    errors.append(
                        f"{entry_name}: no shard filter covers test source {relative} "
                        f"class {class_name} (candidate FullyQualifiedName prefix "
                        f"{candidate})"
                    )
                elif len(matching_shards) > 1:
                    errors.append(
                        f"{entry_name}: test source {relative} class {class_name} is "
                        f"matched by multiple shard filters: "
                        f"{', '.join(matching_shards)} (candidate FullyQualifiedName "
                        f"prefix {candidate})"
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
