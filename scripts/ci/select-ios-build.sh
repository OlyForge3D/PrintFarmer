#!/usr/bin/env bash
# =============================================================================
# select-ios-build.sh — decides whether the expensive macOS/Xcode jobs in
# .github/workflows/ios-pr-ci.yml have real work to do, mirroring the
# `Determine whether iOS-relevant paths changed` step in that workflow.
#
# Extracted into a standalone script (as scripts/ci/compute-change-set.sh was
# for ci.yml) so scripts/ci/tests/test-select-ios-build.sh can exercise the
# real `git merge-base`/`git diff` logic against scratch git repositories.
# The workflow trigger deliberately has NO `paths:` filter — every PR must
# post the required "Build (iOS)" status (#1365) — so this selector is the
# only thing standing between a non-mobile PR and a full Xcode toolchain run.
#
# Inputs (env vars):
#   EVENT_NAME    github.event_name. Anything other than "pull_request" runs
#                 the full iOS build.
#   PR_BASE_SHA   github.event.pull_request.base.sha (pull_request only).
#   PR_HEAD_SHA   github.event.pull_request.head.sha (pull_request only).
#   GITHUB_OUTPUT Path to append `should_run=` / `reason=` to, as GitHub
#                 Actions does. Tests may point this at a scratch file.
#
# Every failure path fails SAFE (should_run=true): a missing SHA, an
# unresolvable or ambiguous merge-base, or a failed diff runs the real build
# rather than letting an unvalidated PR skip straight to green. A skipped job
# reports "Success" to branch protection, so a wrong `false` here is silently
# fatal while a wrong `true` only costs a macOS runner minute.
#
# For `pull_request` events, github.event.pull_request.base.sha tracks the
# base branch's CURRENT tip, not the PR's fork point — GitHub advances it
# whenever the base branch moves (other PRs merging into `development`, a
# `synchronize`, etc.). A two-dot `git diff base_sha head_sha` therefore
# reports every file that landed on the base branch after the PR forked. On
# #1418 that made a C#-only PR match `^mobile/` — because unrelated Swift
# commits had landed on `development` in the meantime — and pay for the whole
# macOS build plus iOS unit tests. Diff against the true merge-base so only
# the PR's own commits are considered.
# =============================================================================

set -uo pipefail

# Paths whose modification requires the real iOS build. Canonical API corpus
# inputs and the backend source files that define their serialized shape must
# exercise the real APIClient decoders alongside direct mobile changes.
IOS_RELEVANT_PATHS_RE='^(mobile/|fixtures/wire-contracts/(manifest\.json|api/.*\.json)$|src/api/Program\.cs$|src/infra/(Contracts|Domain|Dtos|Json|Models|Serialization)/.*\.cs$|src/infra/.*Contract\.cs$|\.github/workflows/ios-pr-ci\.yml$|scripts/ci/resolve-ios-simulator\.sh$|scripts/ci/test-resolve-ios-simulator\.sh$|scripts/ci/select-ios-build\.sh$|scripts/ci/tests/test-select-ios-build\.sh$)'

should_run=true
reason="non-pull_request event — running full iOS build"

if [[ "${EVENT_NAME:-}" == "pull_request" ]]; then
  base_sha="${PR_BASE_SHA:-}"
  head_sha="${PR_HEAD_SHA:-}"

  if [[ -n "$base_sha" && -n "$head_sha" ]]; then
    failure=""
    changed_paths=()

    # `git merge-base` prints ONE base even when the history has several
    # (a criss-cross merge), and *which* one it picks depends on traversal
    # order — `merge-base A B` and `merge-base B A` can disagree. Diffing from
    # the wrong base can hide a genuine mobile/ change entirely and produce an
    # empty diff, so demand exactly one base and otherwise fail safe.
    merge_bases=""
    if ! merge_bases="$(git merge-base --all "$base_sha" "$head_sha" 2>/dev/null)"; then
      failure="merge-base failed — running full iOS build to be safe"
    fi

    base_list=()
    if [[ -z "$failure" ]]; then
      while IFS= read -r line; do
        [[ -n "$line" ]] && base_list+=("$line")
      done <<< "$merge_bases"

      if (( ${#base_list[@]} != 1 )); then
        failure="expected exactly 1 merge-base, found ${#base_list[@]} — running full iOS build to be safe"
      fi
    fi

    if [[ -z "$failure" ]]; then
      # -z emits NUL-terminated paths and never applies git's C-style quoting,
      # so paths containing non-ASCII bytes, spaces, quotes, or newlines are
      # matched verbatim instead of evading the anchored regex. Command
      # substitution cannot hold NUL bytes, hence the temp file.
      diff_out=""
      if ! diff_out="$(mktemp 2>/dev/null)"; then
        failure="could not create temp file — running full iOS build to be safe"
      elif git -c core.quotePath=false diff -z --no-renames --name-only \
        "${base_list[0]}" "$head_sha" > "$diff_out" 2>/dev/null; then
        # Bash 3.2-compatible NUL reader so the same selector suite runs on
        # macOS developer hosts and the Ubuntu CI runner.
        while IFS= read -r -d '' changed_path; do
          changed_paths+=("$changed_path")
        done < "$diff_out"
      else
        failure="diff failed — running full iOS build to be safe"
      fi
      [[ -n "$diff_out" ]] && rm -f "$diff_out"
    fi

    if [[ -n "$failure" ]]; then
      should_run=true
      reason="$failure"
    elif (( ${#changed_paths[@]} == 0 )); then
      should_run=false
      reason="no changed files detected"
    else
      # Matched in-process rather than by piping to grep: grep exits 2 on
      # error, which an `if grep ...; then ... else ...` would silently treat
      # as "no iOS paths changed" and skip the build.
      should_run=false
      reason="no iOS-relevant paths changed"
      for changed_path in "${changed_paths[@]}"; do
        if [[ "$changed_path" =~ $IOS_RELEVANT_PATHS_RE ]]; then
          should_run=true
          reason="iOS-relevant paths changed"
          break
        fi
      done
    fi
  else
    should_run=true
    reason="missing base/head SHA — running full iOS build to be safe"
  fi
fi

{
  echo "should_run=$should_run"
  echo "reason=$reason"
} >> "${GITHUB_OUTPUT:?GITHUB_OUTPUT must be set}"

echo "iOS build selector: should_run=$should_run reason=$reason"
