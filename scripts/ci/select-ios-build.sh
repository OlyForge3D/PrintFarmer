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
# unresolvable merge-base, or a failed diff runs the real build rather than
# letting an unvalidated PR skip straight to green.
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

# Paths whose modification requires the real iOS build. Kept as a single
# extended-regexp anchored at the start of each changed path.
IOS_RELEVANT_PATHS_RE='^(mobile/|\.github/workflows/ios-pr-ci\.yml$|scripts/ci/resolve-ios-simulator\.sh$|scripts/ci/test-resolve-ios-simulator\.sh$|scripts/ci/select-ios-build\.sh$|scripts/ci/tests/test-select-ios-build\.sh$)'

should_run=true
reason="non-pull_request event — running full iOS build"

if [[ "${EVENT_NAME:-}" == "pull_request" ]]; then
  base_sha="${PR_BASE_SHA:-}"
  head_sha="${PR_HEAD_SHA:-}"

  if [[ -n "$base_sha" && -n "$head_sha" ]]; then
    rc=0
    changed=""
    if diff_base_sha="$(git merge-base "$base_sha" "$head_sha" 2>/dev/null)"; then
      changed="$(git -c core.quotePath=false diff --no-renames --name-only "$diff_base_sha" "$head_sha" 2>/dev/null)" || rc=$?
    else
      rc=1
    fi

    if (( rc != 0 )); then
      should_run=true
      reason="diff failed — running full iOS build to be safe"
    elif [[ -z "$changed" ]]; then
      should_run=false
      reason="no changed files detected"
    elif printf '%s\n' "$changed" | grep -Eq "$IOS_RELEVANT_PATHS_RE"; then
      should_run=true
      reason="iOS-relevant paths changed"
    else
      should_run=false
      reason="no iOS-relevant paths changed"
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
