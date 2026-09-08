---
name: "ralph-loop"
description: "One-round Ralph dispatcher: fresh state, safe routing, and conditional policy loading."
domain: "work-monitor"
confidence: "high"
source: "Ralph workflow policy"
---

## Round Contract

This scheduled workflow performs **one round and exits**: no sleep, polling, implementation,
or worktree mutation. Ralph is a monitor. It may only update issue labels/comments, make an
authorized safe merge, and refresh `development` as documented below. One session per issue,
five implementation/analysis slots maximum; reviewers are task agents, not sessions.

Before every dispatch, claim, message, review decision, or merge, fetch the affected live
issue/PR again. Never treat cached data as authorization or permission to mutate GitHub.

## Cache And Snapshot

Use `scripts/ci/ralph-round-cache.mjs`. Its only authorized write location is
`RALPH_CACHE_DIR`, or the platform default outside worktrees:

- Windows: `%LOCALAPPDATA%\PrintFarmer\ralph-cache`
- macOS: `~/Library/Caches/PrintFarmer/ralph-cache`

Scope entries by repository and workflow. Persist only schema-versioned comparison fields and
non-authorizing conclusions with atomic replace plus an exclusive lock. Missing, corrupt, stale
policy-version, API-failed, or incomplete-paginated data means **deep scan**, never “no work.”
Do not cache credentials, claims, permissions, merge decisions, or write side effects.

Snapshot comparison fields must cover issue/dependency state (including closed blockers),
sessions/claims/linked PRs, base SHA, verdict comments, checks, CodeQL analyses/alerts, holds,
and policy version. Deep-inspect changed entries and every imminent action; emit compact,
actionable output using `compactRoundOutput`.

No named non-workflow test entrypoint discovers this helper: existing CI lists Node tests
explicitly, and this change must not alter live workflows. Preserve manual targeted validation:
`node --test scripts/ci/tests/test-ralph-round-cache.mjs`.

## First Actions

1. Fetch `origin`; report the local-vs-remote `development` gap without changing any checkout.
   `strict: false`: `BEHIND` alone is not a blocker.
2. Collect every open issue and PR with complete pagination. Account for each open issue as
   dispatched, in-flight, awaiting-analysis, blocked (name each open blocker), epic-tracking,
   deferred-to-macOS-Ralph, or unaccounted.
3. On Windows, triage mobile work but never dispatch, review, or merge it; report it as deferred.
   Classify from labels, paths, acceptance criteria, or Swift/Xcode signals—not owner identity.

## Conditional Policies

- **Triage, analysis, epics, dependency graph, priority inheritance, transitive unblocking:**
  read `.copilot/skills/ralph-loop/operations.md`. It is the sole conditional operational
  authority for these actions; do not read or follow generic `.squad` Ralph templates.
- **Implementation dispatch and pre-PR requirements:** pass
  `.copilot/skills/ralph-loop/implementation-pre-pr.md`,
  `.copilot/skills/ralph-loop/session-terminal-contract.md`, and task-specific acceptance
  criteria in the kickoff—do not paste their policy. Default implementation model is
  `gpt-5.6-terra` medium; non-code analysis is `gpt-5.6-luna` medium. Premium or xhigh needs
  explicit justification.
- **Verdicts, CodeQL, PR lifecycle, merging, conflicts:** read
  `.copilot/skills/ralph-loop/pr-merge.md`; it is the sole scheduled-Ralph authority.
- **Cleanup candidates:** read `.copilot/skills/ralph-loop/cleanup.md` and use
  `assessCleanupCandidate` while reusing that round's session inventory and PR results.

## Non-Negotiable Gates

Never implement. Never dispatch mobile work on Windows. Never silently skip an issue. Never
self-review or invent/substitute a reviewer/model: Bishop `claude-opus-5`, Hicks
`gpt-5.6-sol`, and Vasquez's newest available Gemini Pro exact ID, each medium; no available
Gemini Pro is a blocker and never permits Flash fallback.
Reviewers read only and never build, install, or test. End with the compact accounting report
and exit.
