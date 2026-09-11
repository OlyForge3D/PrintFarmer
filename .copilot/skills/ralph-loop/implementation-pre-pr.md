---
name: "ralph-implementation-pre-pr"
description: "Required implementation-session and pre-PR gate for Ralph-dispatched work."
domain: "work-monitor"
confidence: "high"
---

## Required Before Implementation

Read this file before starting Ralph-dispatched work. Work only on the assigned issue and its
acceptance criteria. One session owns one issue. Do not implement if the issue is mobile/iOS on
Windows; report the cross-platform blocker instead.

Keep the worktree clean and push every intended commit. Use the specified targeted validation;
do not substitute a full suite. Add tests for changed behavior. For affected .NET code, run
`dotnet format ./farm-web.sln` and `dotnet format ./farm-web.sln --verify-no-changes`. For EF
changes, create migrations for every affected context/provider pair and cover them from a .NET
test assembly.

## Pre-PR Gate

Before opening a PR, fetch then merge `origin/development` into the branch, resolve only actual
conflicts, and rerun targeted validation. Do not rebase. Reviewer count and risk classification are governed solely by
`.github/copilot-instructions.md` § `Risk-Based Review Scope`. For standard and
documentation-only changes, dispatch one qualified non-author reviewer using a different model
family than the implementation agent. For high-risk changes, dispatch Bishop, Hicks, and Vasquez
as parallel read-only task agents—not sessions—and explicitly prohibit builds, installs, and
tests. An unavailable required model blocks the PR; never substitute or self-review. Post exactly
the number of genuine canonical verdict comments the canonical rule requires: one for a standard
or documentation-only change and three for a high-risk change.
If a required high-risk panel member authored the PR, block the PR; never substitute Dallas or
another roster member.

Fix every blocker and repeat review at the new head. If conflict resolution authored hunks,
request narrowly targeted review that agrees across all resolved files. Then create the PR with
`Closes #<issue>`, apply `squad`, and post the required canonical verdict comments at the
reviewed 40-character SHA. Watch CodeQL through completion; compare PR alerts with development,
fix or justify new findings, and do not act on the pre-existing backlog. Never claim
base-sync/diff heuristics authorize a merge.

## Completion

Do not merge unattended. After a merged PR or definitive closure, verify the issue disposition,
report it with `working tree clean` and `all commits pushed`, then stop. Do not archive yourself
or another session.
