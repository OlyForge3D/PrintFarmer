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

## App Tasks Versus OS Processes

Successful app `task_complete` followed by the corresponding turn end is a task lifecycle
result, not a process exit. Report the real job/session/fence and deliverable proof, then stop;
do not restart work or invent an integer exit code merely to satisfy accounting.

Ralph may use `complete-local-session` for verified code delivery after checking the existing
host runtime journal, exact published/clean HEAD, validation results and fresh stopped/no-queued-
follow-up app state. The adapter records `sessionCompletion.kind:"app-session-task"` without
an `exitCode`. A literal success statement or idle row cannot substitute for runtime events.
See [the operations contract](operations.md#app-session-completion-without-process-exit) for
identity/chronology checks, evidence trust boundaries, concurrency and idempotent replay.
`terminal-local` is unchanged and remains reserved for actual correlated process results.
If a new issue already resumed in the same session before bookkeeping caught up, only an
explicitly authorized atomic `handoff-local-session` may record the old completion and new
admission together. Keep both task boundaries and never label the later result with the old
job/fence. See [atomic handoffs](operations.md#atomic-completed-task-to-resumed-task-handoff).

## Admission Identity And Resumed Work

Report the current admission `jobId`, `fence` and app `sessionId` with terminal evidence.
Success requires the verified deliverable and publication evidence above, not merely an idle
session or a closed issue. Lost local sessions are `abandoned`; worker-attested failures are
`failed`. Neither is successful completion or permission to clean up evidence.
An explicitly authorized worker-attested incomplete abandonment is also `abandoned`, even when
the recorded process exited zero. It requires fresh verified process cessation, incomplete Git
delivery evidence and a durable launch fence; it never claims the work was delivered and retains
all unpublished artifacts.

If a terminal session resumes work, Ralph must account it as a new job/fence with an explicit
terminal predecessor and fresh post-terminal activity evidence. Preserve the old result and use
the newly supplied admission identity in the next terminal report. Do not create a second session
or reuse the completed job identifier. Until accounted, the live session still consumes capacity.
