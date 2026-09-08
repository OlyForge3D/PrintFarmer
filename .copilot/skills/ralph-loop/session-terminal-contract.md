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
