---
name: "ralph-pr-merge"
description: "Scheduled Ralph PR verification and merge policy."
domain: "work-monitor"
confidence: "high"
---

## Verify, Do Not Review

For non-mobile, `squad`-labelled PRs only, re-fetch current fields and run
`node scripts/ci/verify-squad-verdict.mjs --repo <owner/repo> --pr <n> --json`. Verify the
current message author and evidence, never free text. Required checks must be green, CodeQL must
have completed, and CodeQL alerts must be compared with the development baseline; empty alerts
without a completed analysis are unknown. New high/critical alerts block; route advisory alerts
to the owning session. `strict: false` means `BEHIND` alone is not a blocker.

Only the verifier's current-SHA `REVIEWED`/`APPROVED` evidence can proceed. A carried base-sync
record has only the verifier's stated semantics; diff similarity never grants authorization.
`CHANGES_REQUESTED`, missing, invalid, unauthenticated, stale, fork, or out-of-scope evidence
never authorizes a merge. Do not commission reviewers for valid pre-PR records.

## Merge And Conflict Safety

Never merge drafts, unlabelled PRs, mobile PRs on Windows, or two PRs concurrently. Immediately
re-fetch `headRefOid` then use `gh pr merge --squash --match-head-commit <reviewedHeadSha>`;
verify the merge and linked issue before the next merge. No unattended bypass exists.

For a hand-authored conflict resolution, request a targeted fresh review of every resolved file;
all reviewers must agree. A clean base sync follows only verifier carry-forward semantics.
