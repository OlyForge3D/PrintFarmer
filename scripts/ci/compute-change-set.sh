#!/usr/bin/env bash
# =============================================================================
# compute-change-set.sh — computes the NUL-terminated changed-file list that
# feeds scripts/ci/select-dotnet-tests.sh, mirroring the `Compute change set`
# step in .github/workflows/ci.yml.
#
# Extracted into a standalone script so scripts/ci/tests/test-select-dotnet-tests.sh
# can exercise the real `git diff`/`git merge-base` logic against a scratch git
# repository instead of only feeding pre-computed CHANGED_FILES to the selector.
#
# Inputs (env vars):
#   EVENT_NAME    "pull_request", "push", or anything else (no diff computed).
#   PR_BASE_SHA   github.event.pull_request.base.sha (pull_request only).
#   PR_HEAD_SHA   github.event.pull_request.head.sha (pull_request only).
#   BEFORE_SHA    github.event.before (push only).
#   AFTER_SHA     github.sha (push only).
#   OUT_FILE      Path to write the NUL-terminated changed-path list to.
#                 Defaults to "$RUNNER_TEMP/changed.z", falling back to a
#                 mktemp'd file when RUNNER_TEMP is unset (e.g. under test).
#   GITHUB_OUTPUT Path to append `changed_file=` / `force_full_safe=` to, as
#                 GitHub Actions does. Required when invoked from the workflow;
#                 tests may point this at a scratch file.
#
# For `pull_request` events, github.event.pull_request.base.sha tracks the
# base branch's current tip, not the PR's actual fork point — GitHub updates
# it whenever the base branch advances (e.g. on `synchronize`/`reopened`, or
# just because other PRs merged into the base while this PR was open).
# Diffing against that drifted base.sha would pull in unrelated commits that
# landed on the base branch after the PR was forked, spuriously widening the
# changed-file set. Diff against the true merge-base instead so only the PR's
# own commits are considered. The `push` event path already diffs a real
# before/after ancestry pair (before/after are consecutive states of the same
# ref), so it does not need this treatment.
# =============================================================================

set -uo pipefail

out="${OUT_FILE:-${RUNNER_TEMP:+$RUNNER_TEMP/changed.z}}"
if [[ -z "$out" ]]; then
  out="$(mktemp)"
fi
: > "$out"

case "${EVENT_NAME:-}" in
  pull_request)
    base_sha="${PR_BASE_SHA:-}"
    head_sha="${PR_HEAD_SHA:-}"
    ;;
  push)
    base_sha="${BEFORE_SHA:-}"
    head_sha="${AFTER_SHA:-}"
    ;;
  *)
    base_sha=""
    head_sha=""
    ;;
esac

rc=0
if [[ -n "$base_sha" && -n "$head_sha" && "$base_sha" != "0000000000000000000000000000000000000000" ]]; then
  diff_base_sha="$base_sha"
  if [[ "${EVENT_NAME:-}" == "pull_request" ]]; then
    if merge_base_sha="$(git merge-base "$base_sha" "$head_sha" 2>/dev/null)"; then
      diff_base_sha="$merge_base_sha"
    else
      rc=1
    fi
  fi
  if (( rc == 0 )) && ! git -c core.quotePath=false diff -z --no-renames --name-only "$diff_base_sha" "$head_sha" > "$out" 2>/dev/null; then
    rc=1
  fi
fi

{
  echo "changed_file=$out"
  if (( rc != 0 )); then
    echo "force_full_safe=diff-failed"
  else
    echo "force_full_safe="
  fi
} >> "${GITHUB_OUTPUT:?GITHUB_OUTPUT must be set}"
