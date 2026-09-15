---
name: "ralph-session-terminal-contract"
description: "Required terminal evidence contract for every Ralph-dispatched session."
domain: "work-monitor"
confidence: "high"
---

## Terminal Evidence

Read this before work and include the required proof in the final report. Keep the worktree clean,
push every intended commit, and explicitly attest `working tree clean` and `all commits pushed`.

For a merged PR, prove the merge commit is on `origin/development`, prove current HEAD preserved
the merged result, and report no commits were added after merge. For a closed-unmerged PR, report
the literal `CLOSED WITHOUT MERGE`, its closure reason, and the verified linked-issue disposition.
For analysis/non-code work, report a completed, verified deliverable link and result.

Stop after reporting. Never archive any session. Ralph will assess this evidence only; it never
deletes sessions unattended.

## Admission Identity And Resumed Work

Report the current admission `jobId`, `fence` and app `sessionId` with terminal evidence.
Success requires the verified deliverable and publication evidence above, not merely an idle
session or a closed issue. Lost local sessions are `abandoned`; worker-attested failures are
`failed`. Neither is successful completion or permission to clean up evidence.

If a terminal session resumes work, Ralph must account it as a new job/fence with an explicit
terminal predecessor and fresh post-terminal activity evidence. Preserve the old result and use
the newly supplied admission identity in the next terminal report. Do not create a second session
or reuse the completed job identifier. Until accounted, the live session still consumes capacity.
