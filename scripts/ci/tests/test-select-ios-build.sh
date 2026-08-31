#!/usr/bin/env bash
# =============================================================================
# test-select-ios-build.sh — deterministic regression suite for the iOS PR
# build selector (scripts/ci/select-ios-build.sh, used by
# .github/workflows/ios-pr-ci.yml).
#
# Each case builds a real scratch git repository, runs the selector exactly
# as the workflow invokes it with GITHUB_OUTPUT pointed at a temp file, then
# asserts the `should_run` / `reason` outputs.
#
# Emits a compact PASS/FAIL line per case plus a summary, and exits non-zero
# if any case fails.
# =============================================================================

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
SELECTOR="$REPO_ROOT/scripts/ci/select-ios-build.sh"

if [[ ! -r "$SELECTOR" ]]; then
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
  awk -v k="$key" '$0 ~ "^"k"=" { print substr($0, length(k)+2); exit }' "$out"
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

# init_repo <dir> — a scratch repo on `development` with one root commit.
init_repo() {
  local dir="$1"
  git -C "$dir" init -q -b development
  git -C "$dir" config user.email "test@example.com"
  git -C "$dir" config user.name "Test"
  printf 'root\n' > "$dir/README.md"
  git -C "$dir" add README.md
  git -C "$dir" commit -q -m "fork point"
}

# commit_file <dir> <path> <message>
commit_file() {
  local dir="$1" path="$2" message="$3"
  mkdir -p "$dir/$(dirname "$path")"
  printf 'content %s\n' "$RANDOM" > "$dir/$path"
  git -C "$dir" add "$path"
  git -C "$dir" commit -q -m "$message"
}

# run_selector <repo> <out> [env assignments...]
# Runs the selector with CWD inside the scratch repo, as the workflow does.
run_selector() {
  local repo="$1" out="$2"
  shift 2
  ( cd "$repo" && env "$@" GITHUB_OUTPUT="$out" bash "$SELECTOR" >/dev/null 2>&1 )
}

# run_case <name> <function>
run_case() {
  local name="$1" fn="$2"
  local out
  out="$(mktemp)"
  if "$fn" "$out"; then
    printf 'PASS %s\n' "$name"
    PASSED=$((PASSED + 1))
  else
    printf 'FAIL %s\n' "$name"
    FAILED=$((FAILED + 1))
    FAILED_NAMES+=("$name")
  fi
  rm -f "$out"
}

# ---------------------------------------------------------------------------
# Cases.
# ---------------------------------------------------------------------------

# Regression (#1418): github.event.pull_request.base.sha drifts ahead of the
# PR's actual fork point whenever the base branch advances while the PR is
# open. The selector must diff against `git merge-base base_sha head_sha`,
# not `base_sha` directly, or unrelated mobile/** commits that landed on
# `development` after the fork get folded into the PR's changed-file set and
# a C#-only PR pays for the entire macOS/Xcode toolchain.
#
# Reproduces exactly that: a PR touching only src/**.cs, with Swift commits
# landing on `development` after the fork point.
case_drifted_base_sha_ignores_base_branch_mobile_commits() {
  local out="$1" repo pr_head drifted_base
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  init_repo "$repo" || return 1
  git -C "$repo" checkout -q -b pr-branch
  commit_file "$repo" "src/api/Controllers/PrintersController.cs" "C#-only PR change" || return 1
  pr_head="$(git -C "$repo" rev-parse HEAD)"

  git -C "$repo" checkout -q development
  commit_file "$repo" "mobile/PrintFarmer/Services/PushNotificationManager.swift" "mobile work lands on development" || return 1
  drifted_base="$(git -C "$repo" rev-parse HEAD)"

  if [[ "$drifted_base" == "$(git -C "$repo" merge-base "$drifted_base" "$pr_head")" ]]; then
    echo "  setup error: development did not advance past the fork point" >&2
    return 1
  fi

  run_selector "$repo" "$out" \
    EVENT_NAME=pull_request PR_BASE_SHA="$drifted_base" PR_HEAD_SHA="$pr_head" || return 1

  assert_eq "should_run" "$(get_output "$out" should_run)" "false" || return 1
  assert_eq "reason" "$(get_output "$out" reason)" "no iOS-relevant paths changed" || return 1
}

# A PR that genuinely touches mobile/** must still run the full build — the
# merge-base fix must not silence real iOS work.
case_pr_touching_mobile_runs_build() {
  local out="$1" repo pr_head drifted_base
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  init_repo "$repo" || return 1
  git -C "$repo" checkout -q -b pr-branch
  commit_file "$repo" "mobile/PrintFarmer/Views/PrinterDetailView.swift" "real mobile PR change" || return 1
  pr_head="$(git -C "$repo" rev-parse HEAD)"

  git -C "$repo" checkout -q development
  commit_file "$repo" "src/api/Program.cs" "unrelated backend work on development" || return 1
  drifted_base="$(git -C "$repo" rev-parse HEAD)"

  run_selector "$repo" "$out" \
    EVENT_NAME=pull_request PR_BASE_SHA="$drifted_base" PR_HEAD_SHA="$pr_head" || return 1

  assert_eq "should_run" "$(get_output "$out" should_run)" "true" || return 1
  assert_eq "reason" "$(get_output "$out" reason)" "iOS-relevant paths changed" || return 1
}

# Canonical API fixtures and the backend sources that define their serialized
# shape must exercise the real iOS APIClient tests even without a mobile/** edit.
case_api_wire_contract_inputs_run_build() {
  local out="$1" repo base_sha path
  for path in \
      "fixtures/wire-contracts/manifest.json" \
      "fixtures/wire-contracts/api/inventory/parts.populated.json" \
      "src/api/Program.cs" \
      "src/infra/Contracts/Auth/AuthDtos.cs" \
      "src/infra/Domain/PartInventoryAdjustment.cs" \
      "src/infra/Dtos/PartsInventory/PartsInventoryDtos.cs" \
      "src/infra/Json/EnumJsonConverters.cs" \
      "src/infra/Models/PrinterBackendCapabilitiesDto.cs" \
      "src/infra/Serialization/ImportExportTypeInfoResolver.cs" \
      "src/infra/SlicerHostLookupContract.cs" \
      "src/infra/Services/ExampleWireContract.cs"; do
    repo="$(mktemp -d)"
    init_repo "$repo" || { rm -rf -- "$repo"; return 1; }
    base_sha="$(git -C "$repo" rev-parse HEAD)"
    git -C "$repo" checkout -q -b pr-branch
    commit_file "$repo" "$path" "edit $path" || { rm -rf -- "$repo"; return 1; }

    run_selector "$repo" "$out" \
      EVENT_NAME=pull_request PR_BASE_SHA="$base_sha" \
      PR_HEAD_SHA="$(git -C "$repo" rev-parse HEAD)" || { rm -rf -- "$repo"; return 1; }
    rm -rf -- "$repo"

    assert_eq "should_run ($path)" "$(get_output "$out" should_run)" "true" || return 1
    assert_eq "reason ($path)" "$(get_output "$out" reason)" "iOS-relevant paths changed" || return 1
    : > "$out"
  done
}

# The workflow itself and its supporting scripts are iOS-relevant: editing the
# selector or the simulator resolver must run the build that validates them.
case_ios_support_paths_run_build() {
  local out="$1" repo base_sha path
  for path in \
      ".github/workflows/ios-pr-ci.yml" \
      "scripts/ci/resolve-ios-simulator.sh" \
      "scripts/ci/test-resolve-ios-simulator.sh" \
      "scripts/ci/select-ios-build.sh" \
      "scripts/ci/tests/test-select-ios-build.sh"; do
    repo="$(mktemp -d)"
    init_repo "$repo" || { rm -rf -- "$repo"; return 1; }
    base_sha="$(git -C "$repo" rev-parse HEAD)"
    git -C "$repo" checkout -q -b pr-branch
    commit_file "$repo" "$path" "edit $path" || { rm -rf -- "$repo"; return 1; }

    run_selector "$repo" "$out" \
      EVENT_NAME=pull_request PR_BASE_SHA="$base_sha" \
      PR_HEAD_SHA="$(git -C "$repo" rev-parse HEAD)" || { rm -rf -- "$repo"; return 1; }
    rm -rf -- "$repo"

    if ! assert_eq "should_run ($path)" "$(get_output "$out" should_run)" "true"; then
      return 1
    fi
    : > "$out"
  done
}

# Paths that merely LOOK iOS-relevant must not match: the regex is anchored,
# so a nested `mobile/` or a same-named script elsewhere in the tree is not
# a reason to boot the Xcode toolchain.
case_lookalike_paths_do_not_run_build() {
  local out="$1" repo base_sha path
  for path in \
      "docs/mobile/README.md" \
      "fixtures/wire-contracts/README.md" \
      "fixtures/wire-contracts/manifest.json.lock" \
      "fixtures/other/api/inventory/parts.populated.json" \
      "src/Web/ReactApp/src/mobile/useMobileLayout.ts" \
      "tools/scripts/ci/select-ios-build.sh"; do
    repo="$(mktemp -d)"
    init_repo "$repo" || { rm -rf -- "$repo"; return 1; }
    base_sha="$(git -C "$repo" rev-parse HEAD)"
    git -C "$repo" checkout -q -b pr-branch
    commit_file "$repo" "$path" "edit $path" || { rm -rf -- "$repo"; return 1; }

    run_selector "$repo" "$out" \
      EVENT_NAME=pull_request PR_BASE_SHA="$base_sha" \
      PR_HEAD_SHA="$(git -C "$repo" rev-parse HEAD)" || { rm -rf -- "$repo"; return 1; }
    rm -rf -- "$repo"

    if ! assert_eq "should_run ($path)" "$(get_output "$out" should_run)" "false"; then
      return 1
    fi
    : > "$out"
  done
}

# An unresolvable merge-base (unrelated histories, or a base SHA the runner
# never fetched) must fail SAFE toward running the real build rather than
# skipping to green on unvalidated code.
case_unresolvable_merge_base_fails_safe() {
  local out="$1" repo
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  init_repo "$repo" || return 1

  run_selector "$repo" "$out" \
    EVENT_NAME=pull_request \
    PR_BASE_SHA=0123456789012345678901234567890123456789 \
    PR_HEAD_SHA="$(git -C "$repo" rev-parse HEAD)" || return 1

  assert_eq "should_run" "$(get_output "$out" should_run)" "true" || return 1
  assert_eq "reason" "$(get_output "$out" reason)" "merge-base failed — running full iOS build to be safe" || return 1
}

# Missing SHAs (a malformed/absent pull_request payload) must also fail safe.
case_missing_shas_fails_safe() {
  local out="$1" repo
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  init_repo "$repo" || return 1

  run_selector "$repo" "$out" \
    EVENT_NAME=pull_request PR_BASE_SHA="" PR_HEAD_SHA="" || return 1

  assert_eq "should_run" "$(get_output "$out" should_run)" "true" || return 1
  assert_eq "reason" "$(get_output "$out" reason)" "missing base/head SHA — running full iOS build to be safe" || return 1
}

# Non-pull_request events (workflow_dispatch, push, …) always run the build.
case_non_pull_request_event_runs_build() {
  local out="$1" repo
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  init_repo "$repo" || return 1

  run_selector "$repo" "$out" EVENT_NAME=workflow_dispatch || return 1

  assert_eq "should_run" "$(get_output "$out" should_run)" "true" || return 1
  assert_eq "reason" "$(get_output "$out" reason)" "non-pull_request event — running full iOS build" || return 1
}

# An empty PR (head identical to the merge-base) has nothing to validate.
case_empty_diff_skips_build() {
  local out="$1" repo head_sha
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  init_repo "$repo" || return 1
  head_sha="$(git -C "$repo" rev-parse HEAD)"

  run_selector "$repo" "$out" \
    EVENT_NAME=pull_request PR_BASE_SHA="$head_sha" PR_HEAD_SHA="$head_sha" || return 1

  assert_eq "should_run" "$(get_output "$out" should_run)" "false" || return 1
  assert_eq "reason" "$(get_output "$out" reason)" "no changed files detected" || return 1
}

# A mixed PR touching both mobile/** and backend code must run the build —
# any iOS-relevant path in the set is enough.
case_mixed_pr_runs_build() {
  local out="$1" repo base_sha
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  init_repo "$repo" || return 1
  base_sha="$(git -C "$repo" rev-parse HEAD)"
  git -C "$repo" checkout -q -b pr-branch
  commit_file "$repo" "src/api/Program.cs" "backend half" || return 1
  commit_file "$repo" "mobile/PrintFarmer/PFarmApp.swift" "mobile half" || return 1

  run_selector "$repo" "$out" \
    EVENT_NAME=pull_request PR_BASE_SHA="$base_sha" \
    PR_HEAD_SHA="$(git -C "$repo" rev-parse HEAD)" || return 1

  assert_eq "should_run" "$(get_output "$out" should_run)" "true" || return 1
  assert_eq "reason" "$(get_output "$out" reason)" "iOS-relevant paths changed" || return 1
}

# Bishop finding: `git merge-base` returns only ONE base even when history has
# several (a criss-cross merge), and which one it returns depends on traversal
# order. Here the PR genuinely adds a Swift file, but one of the two valid
# bases already contains it — diffing from that base yields no mobile/ path at
# all and would skip the iOS build on a real mobile change. The selector must
# refuse to guess and fail safe.
case_criss_cross_merge_bases_fails_safe() {
  local out="$1" repo head_sha base_sha base_count sha_a sha_b
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  init_repo "$repo" || return 1

  git -C "$repo" checkout -q -b feat-a
  commit_file "$repo" "mobile/PrintFarmer/CrissCross.swift" "swift change on feat-a" || return 1
  sha_a="$(git -C "$repo" rev-parse HEAD)"

  git -C "$repo" checkout -q -b feat-b development
  commit_file "$repo" "src/api/Beta.cs" "backend change on feat-b" || return 1
  sha_b="$(git -C "$repo" rev-parse HEAD)"

  # Merge by SHA, not by branch name: after the first merge `feat-a` already
  # contains `feat-b`, so `merge feat-a` would fast-forward and collapse the
  # criss-cross back into a single merge-base.
  git -C "$repo" checkout -q feat-a
  git -C "$repo" merge -q --no-edit "$sha_b" >/dev/null 2>&1 || return 1
  head_sha="$(git -C "$repo" rev-parse HEAD)"

  git -C "$repo" checkout -q feat-b
  git -C "$repo" merge -q --no-edit "$sha_a" >/dev/null 2>&1 || return 1
  base_sha="$(git -C "$repo" rev-parse HEAD)"

  # Guard the fixture itself: if this ever stops producing two bases the case
  # would pass vacuously against a single-base selector.
  base_count="$(git -C "$repo" merge-base --all "$base_sha" "$head_sha" | grep -c .)"
  if [[ "$base_count" != "2" ]]; then
    echo "  fixture produced $base_count merge-bases, expected 2" >&2
    return 1
  fi

  run_selector "$repo" "$out" \
    EVENT_NAME=pull_request PR_BASE_SHA="$base_sha" PR_HEAD_SHA="$head_sha" || return 1

  assert_eq "should_run" "$(get_output "$out" should_run)" "true" || return 1
  assert_eq "reason" "$(get_output "$out" reason)" \
    "expected exactly 1 merge-base, found 2 — running full iOS build to be safe" || return 1
}

# A PR that only DELETES Swift files still changes the iOS build. `--no-renames`
# reports the old mobile/ path, so this must match.
case_deleted_mobile_file_runs_build() {
  local out="$1" repo base_sha
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  init_repo "$repo" || return 1
  commit_file "$repo" "mobile/PrintFarmer/Doomed.swift" "add file to be deleted" || return 1
  base_sha="$(git -C "$repo" rev-parse HEAD)"

  git -C "$repo" checkout -q -b pr-branch
  git -C "$repo" rm -q "mobile/PrintFarmer/Doomed.swift" || return 1
  git -C "$repo" commit -q -m "delete swift file" || return 1

  run_selector "$repo" "$out" \
    EVENT_NAME=pull_request PR_BASE_SHA="$base_sha" \
    PR_HEAD_SHA="$(git -C "$repo" rev-parse HEAD)" || return 1

  assert_eq "should_run" "$(get_output "$out" should_run)" "true" || return 1
  assert_eq "reason" "$(get_output "$out" reason)" "iOS-relevant paths changed" || return 1
}

# Moving a file OUT of mobile/ must still build: with --no-renames the removal
# is reported under its old mobile/ path.
case_rename_out_of_mobile_runs_build() {
  local out="$1" repo base_sha
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  init_repo "$repo" || return 1
  commit_file "$repo" "mobile/PrintFarmer/Movable.swift" "add file to be moved" || return 1
  base_sha="$(git -C "$repo" rev-parse HEAD)"

  git -C "$repo" checkout -q -b pr-branch
  mkdir -p "$repo/docs"
  git -C "$repo" mv "mobile/PrintFarmer/Movable.swift" "docs/Movable.swift" || return 1
  git -C "$repo" commit -q -m "move swift file out of mobile" || return 1

  run_selector "$repo" "$out" \
    EVENT_NAME=pull_request PR_BASE_SHA="$base_sha" \
    PR_HEAD_SHA="$(git -C "$repo" rev-parse HEAD)" || return 1

  assert_eq "should_run" "$(get_output "$out" should_run)" "true" || return 1
  assert_eq "reason" "$(get_output "$out" reason)" "iOS-relevant paths changed" || return 1
}

# Vasquez finding: git C-quotes paths with non-ASCII bytes, quotes, or control
# characters in its default line-oriented output, which breaks a `^mobile/`
# match and would silently skip the build. `git diff -z` never quotes.
case_exotic_path_characters_still_match() {
  local out="$1" repo base_sha
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  init_repo "$repo" || return 1
  base_sha="$(git -C "$repo" rev-parse HEAD)"

  git -C "$repo" checkout -q -b pr-branch
  commit_file "$repo" 'mobile/PrintFarmer/Ünïcode "quoted" spaced.swift' "exotic path" || return 1

  run_selector "$repo" "$out" \
    EVENT_NAME=pull_request PR_BASE_SHA="$base_sha" \
    PR_HEAD_SHA="$(git -C "$repo" rev-parse HEAD)" || return 1

  assert_eq "should_run" "$(get_output "$out" should_run)" "true" || return 1
  assert_eq "reason" "$(get_output "$out" reason)" "iOS-relevant paths changed" || return 1
}

# The match must stay case-SENSITIVE. A case-insensitive selector would drag
# every macOS runner into PRs that merely touch an unrelated `Mobile/` tree.
case_uppercase_mobile_lookalike_does_not_run_build() {
  local out="$1" repo base_sha
  repo="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf -- '$repo'" RETURN

  init_repo "$repo" || return 1
  base_sha="$(git -C "$repo" rev-parse HEAD)"

  git -C "$repo" checkout -q -b pr-branch
  commit_file "$repo" "Mobile/PrintFarmer/Overview.swift" "unrelated uppercase Mobile path" || return 1

  run_selector "$repo" "$out" \
    EVENT_NAME=pull_request PR_BASE_SHA="$base_sha" \
    PR_HEAD_SHA="$(git -C "$repo" rev-parse HEAD)" || return 1

  assert_eq "should_run" "$(get_output "$out" should_run)" "false" || return 1
  assert_eq "reason" "$(get_output "$out" reason)" "no iOS-relevant paths changed" || return 1
}

# Guard against drift between the workflow and the extracted script: the
# workflow must actually invoke this selector rather than reintroducing an
# inline `git diff` that no test covers.
case_workflow_invokes_selector_script() {
  local out="$1" workflow="$REPO_ROOT/.github/workflows/ios-pr-ci.yml" var
  : > "$out"

  if [[ ! -r "$workflow" ]]; then
    echo "  workflow not found at $workflow" >&2
    return 1
  fi
  if ! grep -q 'bash scripts/ci/select-ios-build\.sh' "$workflow"; then
    echo "  workflow does not invoke scripts/ci/select-ios-build.sh" >&2
    return 1
  fi
  if grep -Eq 'git diff (-z )?--no-renames' "$workflow"; then
    echo "  workflow reintroduced an inline selector diff — extend the selector script instead" >&2
    return 1
  fi
  # `fetch-depth: 0` is what makes the merge-base reachable; without it the
  # selector silently degrades to its fail-safe on every PR.
  if ! grep -q 'fetch-depth: 0' "$workflow"; then
    echo "  workflow lost 'fetch-depth: 0' — merge-base would be unreachable" >&2
    return 1
  fi
  # Without these the script sees an empty EVENT_NAME, takes the
  # non-pull_request branch and runs the full macOS build on EVERY PR — a cost
  # regression invisible to every other assertion here.
  for var in EVENT_NAME PR_BASE_SHA PR_HEAD_SHA; do
    if ! grep -q "^ *$var: " "$workflow"; then
      echo "  workflow no longer passes $var to the selector step" >&2
      return 1
    fi
  done
}

# A skipped job reports "Success" to branch protection, so the downstream macOS
# jobs must not skip merely because `select` failed and left `should_run`
# empty. They may skip only on an explicit `false`.
case_downstream_jobs_skip_only_on_explicit_false() {
  local out="$1" workflow="$REPO_ROOT/.github/workflows/ios-pr-ci.yml"
  : > "$out"

  # Any `== 'true'` gate is acceptable ONLY when it also consults
  # `needs.select.result`, i.e. the `build` job's step conditions, which are
  # OR'd with `needs.select.result != 'success'` and so already fail safe.
  if grep -n "if:.*should_run == 'true'" "$workflow" | grep -qv 'needs\.select\.result'; then
    echo "  a job/step gates on should_run == 'true' without consulting needs.select.result — a selector crash would skip it into a green required check" >&2
    grep -n "if:.*should_run == 'true'" "$workflow" | grep -v 'needs\.select\.result' >&2
    return 1
  fi
  if ! grep -q "needs.select.outputs.should_run != 'false'" "$workflow"; then
    echo "  no job gates on should_run != 'false' — downstream fail-safe lost" >&2
    return 1
  fi
}

# ---------------------------------------------------------------------------
# Runner.
# ---------------------------------------------------------------------------

TESTS=(
  case_drifted_base_sha_ignores_base_branch_mobile_commits
  case_pr_touching_mobile_runs_build
  case_api_wire_contract_inputs_run_build
  case_ios_support_paths_run_build
  case_lookalike_paths_do_not_run_build
  case_unresolvable_merge_base_fails_safe
  case_missing_shas_fails_safe
  case_non_pull_request_event_runs_build
  case_empty_diff_skips_build
  case_mixed_pr_runs_build
  case_criss_cross_merge_bases_fails_safe
  case_deleted_mobile_file_runs_build
  case_rename_out_of_mobile_runs_build
  case_exotic_path_characters_still_match
  case_uppercase_mobile_lookalike_does_not_run_build
  case_workflow_invokes_selector_script
  case_downstream_jobs_skip_only_on_explicit_false
)

for t in "${TESTS[@]}"; do
  run_case "$t" "$t"
done

printf '\n=== summary ===\n'
printf 'passed: %d\nfailed: %d\n' "$PASSED" "$FAILED"
if (( FAILED > 0 )); then
  printf 'failed cases:\n'
  for n in "${FAILED_NAMES[@]}"; do
    printf '  - %s\n' "$n"
  done
  exit 1
fi
exit 0
