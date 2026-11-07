---
name: "pr-landing"
description: "Land a PrintFarmer pull request after review, CI, sync, and thread checks. Use when asked to mark a PR ready or merge it."
---

## Procedure

1. Read the [readiness and merge gate](../../copilot-instructions.md#readiness-and-merge-review-gate), [Ralph merge reference](../../ralph-reference.md), and [Ralph instructions](../../../.squad/ralph-instructions.md). Reuse the repository's exact-head rules; do not invent a shortcut.
2. Inspect the live PR and checks:

   ```bash
   gh pr view <N> --json number,title,isDraft,baseRefName,headRefOid,statusCheckRollup,reviewDecision,url
   gh pr checks <N>
   ```

   Squad PRs target `development`, carry the bare `squad` label, and include
   `Closes #N` for each issue they resolve. Draft PRs may remain open during
   review; readiness is a separate gate.
3. If the branch is behind `development`, sync before collecting final evidence:

   ```bash
   git fetch origin development
   git merge --no-edit origin/development
   ```

   Do not rebase to preserve a review record. The clean-sync carry-forward is
   narrow: the PR diff must be unchanged and every introduced commit must be a
   clean merge. Any other head movement needs fresh exact-head review evidence.
   See the merge reference for the full proof requirements.
4. Verify the current-head verdict:

   ```bash
   node scripts/ci/verify-squad-verdict.mjs \
     --repo OlyForge3D/PrintFarmer --pr <N> --json
   ```

   Require valid evidence for the full change's risk class. `NOT_APPLICABLE`,
   `MISSING`, `INVALID`, `SUPERSEDED`, and `CHANGES_REQUESTED` are not merge
   approval. Use the canonical review-scope rules to choose reviewer count.
5. Resolve review feedback and run the exact-head review-thread preflight:

   ```bash
   HEAD=$(gh pr view <N> --json headRefOid --jq .headRefOid)
   node scripts/ci/verify-pr-review-threads.mjs \
     --repo OlyForge3D/PrintFarmer --pr <N> \
     --expected-head "$HEAD" --json
   ```

   Fix or reply to every reported thread, then rerun the preflight. Never
   auto-resolve or ignore outdated threads. Exit `1`, `2`, or `3` blocks merge.
6. Require all required checks to pass at the same head. The owner has marked
   the unrelated iPhone `LoginFlowUITests` password-entry flake out of scope:
   do not expand an unrelated PR to chase it. If that known failure is the only
   red iOS check, record its run/job and apply only this narrow owner-authorized
   exception; every other required check and review gate must still pass. Do
   not generalize the exception to other iOS jobs. Also distinguish the CI
   runner's successful, explicitly selected test retry (green with a flaky
   warning) from a still-red job. See
   [CI failure triage](../ci-failure-triage/SKILL.md).
7. Mark ready only after the required review, thread, and CI evidence is
   current:

   ```bash
   gh pr ready <N>
   ```

   Recheck the live head and checks after any new push or review-thread change.
8. Immediately before merge, rerun both verifiers against the same `HEAD`, then
   merge with the reviewed SHA:

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
