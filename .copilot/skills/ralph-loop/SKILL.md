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

## First Actions

1. Fetch `origin`; report local-vs-remote `development` gap. A failed fast-forward update of
   local `development` is report-only. `strict: false`: `BEHIND` alone is not a blocker.
2. Collect every open issue and PR with complete pagination. Account for each open issue as
   dispatched, in-flight, awaiting-analysis, blocked (name each open blocker), epic-tracking,
   deferred-to-macOS-Ralph, or unaccounted.
3. On Windows, triage mobile work but never dispatch, review, or merge it; report it as deferred.
   Classify from labels, paths, acceptance criteria, or Swift/Xcode signals—not owner identity.

## Conditional Policies

- **Triage, analysis, epics, dependency graph, priority inheritance, transitive unblocking:**
  read `.squad/templates/ralph-reference.md`. Native dependency edges are authoritative; resolve
  live prose markers additionally. Detect cycles, deduplicate descendants, prioritize inherited
  priority then unblock value, and report cross-platform blockers.
- **Implementation dispatch and pre-PR requirements:** pass
  `.copilot/skills/ralph-loop/implementation-pre-pr.md` and task-specific acceptance criteria
  in the kickoff—do not paste its policy. Default implementation model is `gpt-5.6-terra`
  medium; non-code analysis is `gpt-5.6-luna` medium. Premium or xhigh needs explicit
  justification.
- **Verdicts, CodeQL, PR lifecycle, merging:** read `.github/ralph-reference.md` and run
  `scripts/ci/verify-squad-verdict.mjs --json`. Verify messages/authors/current state first.
  `squad` label scope, SHA-bound unanimous reviews, green required checks, CodeQL completion and
  new-vs-baseline behavior, serialized `--match-head-commit` merges, and no unattended bypass
  remain mandatory. A base-sync carry-forward has only the verifier/gate's actual semantics;
  heuristic diff equality cannot grant authorization.
- **Conflicts:** only a fresh targeted review of hand-authored resolution hunks is permitted;
  it must agree across every resolved file. Clean syncs follow the verifier’s carry rules.
- **Reap:** read the reap section of `.squad/templates/ralph-reference.md`. Report only; never
  archive/delete others or nominate dirty/unpushed sessions. Verify squash merges against the
  branch's remote evidence, not commit containment of its feature head.

## Non-Negotiable Gates

Never implement. Never dispatch mobile work on Windows. Never silently skip an issue. Never
self-review or invent/substitute a reviewer/model: Bishop `claude-opus-5`, Hicks
`gpt-5.6-sol`, Vasquez `gemini-3.1-pro-preview`, each medium; unavailable is a blocker.
Reviewers read only and never build, install, or test. End with the compact accounting report
and exit.
