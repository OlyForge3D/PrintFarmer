---
name: "pr-landing"
description: "Land a PrintFarmer pull request after review, CI, sync, and thread checks. Use when asked to mark a PR ready or merge it."
---

## Procedure

1. Identify the requested scope: inspection/review/checks only, readiness only,
   or delivery through merge. For inspection-only work, report the requested
   evidence and stop before any mutation: do not sync the branch, resolve
   threads, mark ready, or merge. A readiness-only request stops after readiness.
   If the scope is ambiguous, ask before any mutation. For delivery through
   merge, no additional per-PR merge request is needed, but merge into
   `development` only after all four conditions are verified at the same head:
   all required reviews are complete, every issue they found is fixed, the
   complete fix delta has been re-reviewed, and all PR gates pass. Readiness
   alone never permits merging.
2. Read the [readiness and merge gate](../../copilot-instructions.md#readiness-and-merge-review-gate), [Ralph merge reference](../../ralph-reference.md), and [Ralph instructions](../../../.squad/ralph-instructions.md). Reuse the repository's exact-head rules; do not invent a shortcut.
3. Inspect the live PR and checks:

   ```bash
   gh pr view <N> --json number,title,isDraft,baseRefName,headRefOid,statusCheckRollup,reviewDecision,labels,closingIssuesReferences,url
   gh pr checks <N>
   ```

   Squad PRs target `development`, carry the bare `squad` label, and include
   `Closes #N` for each issue they resolve. Draft PRs may remain open during
   review; readiness is a separate gate. Before proceeding, verify the live base
   is `development`, the `squad` label is present, and every issue this PR
   resolves appears in `closingIssuesReferences`. Correct missing labels or
   closing references before readiness; do not merge without them.
4. If the branch is behind `development`, sync before collecting final evidence:

   ```bash
   git fetch origin development
   git merge --no-edit origin/development
   ```

   Do not rebase to preserve a review record. The clean-sync carry-forward is
   narrow: the PR diff must be unchanged and every introduced commit must be a
   clean merge. Any other head movement needs fresh exact-head review evidence.
   See the merge reference for the full proof requirements.
5. Verify the current-head verdict:

   ```bash
   node scripts/ci/verify-squad-verdict.mjs \
     --repo OlyForge3D/PrintFarmer --pr <N> --json
   ```

   Require valid evidence for the full change's risk class. `NOT_APPLICABLE`,
   `MISSING`, `INVALID`, `SUPERSEDED`, and `CHANGES_REQUESTED` are not merge
   approval. Use the canonical review-scope rules to choose reviewer count.
   A repository administrator override may satisfy the repository verifier,
   but it does not replace required reviews, finding fixes, delta rereview, or
   CI under this delivery rule. Do not use it to bypass any of the four conditions.
6. Fix every issue the required reviews found. Follow
   [delta-only panel rereview](../../copilot-instructions.md#delta-only-panel-rereview):
   each reviewer checks the complete delta from their own last reviewed SHA to
   the new head, with prior findings and closure evidence, not the original
   change again. Keep findings open until their fixes are verified, and collect
   fresh current-head verdicts. Repeat steps 5-6 after fixes; do not proceed
   while a finding is unresolved. Then run the exact-head review-thread preflight:

   ```bash
   HEAD=$(gh pr view <N> --json headRefOid --jq .headRefOid)
   node scripts/ci/verify-pr-review-threads.mjs \
     --repo OlyForge3D/PrintFarmer --pr <N> \
     --expected-head "$HEAD" --json
   ```

   Fix every reported issue before resolving its thread; an explanation alone
   is not a fix. Explicitly resolve addressed threads before rerunning the
   preflight. Never auto-resolve or ignore outdated threads. Exit `1`, `2`, or
   `3` blocks merge.
7. Require all required checks to pass at the same head. The unrelated iPhone
   `LoginFlowUITests` password-entry flake is out of scope for unrelated PRs:
   do not expand an unrelated PR to chase it. If it is a required red
   check, it still blocks landing; record its run/job evidence and handle the
   flake separately. This does not waive the check or any other gate. Also
   distinguish the CI runner's successful, explicitly selected test retry
   (green with a flaky warning) from a still-red job. See
   [CI failure triage](../ci-failure-triage/SKILL.md).
8. Mark ready only after the required review, thread, and CI evidence is
   current:

   ```bash
   gh pr ready <N>
   ```

   Recheck the live head and checks after any new push or review-thread change.
   If the user asked only to mark the PR ready, stop here.
9. For delivery through merge, immediately before merging verify all four
   conditions again: required reviews complete, every finding fixed, complete
   fix delta re-reviewed, and all PR gates passing at the same live `HEAD`.
   Rerun both verifiers with `--expected-head "$HEAD"` and recheck required CI.
   A passing verdict or ready state alone is insufficient. If the head changes,
   stop and refresh the affected review and gate evidence. Only then merge
   with the reviewed SHA:

   ```bash
   gh pr merge <N> --squash --match-head-commit "$HEAD"
   gh pr view <N> --json state,mergedAt,closingIssuesReferences
   ```

   If GitHub refuses the merge after clean evidence, report the refusal; do
   not retry blindly.

## Existing guidance

- [Stalled approved PR fallback](../../../.squad/skills/stalled-approved-pr-merge/SKILL.md)
  covers the narrow rebase-drift or broken-`gh`-auth recovery path.
- [CI documentation](../../../docs/CI.md) explains the workflow-specific checks
  and artifacts.
