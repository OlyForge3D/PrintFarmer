---
name: "ralph-loop"
description: "One-round Ralph dispatcher: fresh state, safe routing, and conditional policy loading."
domain: "work-monitor"
confidence: "high"
source: "Ralph workflow policy"
---

## Round Contract

**Native role packages take a separate entrypoint.** When the approved saved
prompt contains `NATIVE-MAILBOX-ROLE-V2`, follow [native-roles.md](native-roles.md)
instead of the legacy first-actions/dispatch loop below. The mini coordinator
alone triages all issues (including quota-accounted `go:needs-research`); device
consumers only accept assigned work and follow its native local lifecycle.
Require the private queue/genesis, explicit local-owner trust and migration
attestations, actual isolated-worktree checks and atomic round token. V1 packages
need consented policy renewal, not invented current-automation metadata.
Do not run both dispatch paths, invent a fallback or enable any schedule.

The shared scheduled entrypoint is [automation.md](automation.md), deployed with
[bootstrap.md](bootstrap.md) and [hosts.json](hosts.json). Read it first. It governs
both macOS-mobile and Windows-general instances, including PR-first recovery and
host-specific capabilities. Missing/unverified host bindings fail closed.
The remaining sections are conditional reference, not a second issue-first loop.

This scheduled workflow performs **one round and exits**: no sleep, polling, implementation,
or worktree mutation. Ralph is a monitor. It may only update issue labels/comments, make an
authorized safe merge, and refresh `development` as documented below. One session per issue,
five implementation/analysis slots maximum per host: macOS 1 mobile + 4 general,
Windows 0 mobile + 5 general, no borrowing; reviewers are task agents, not sessions.

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

No named non-workflow test entrypoint discovers these helpers: existing CI lists Node tests
explicitly, and this change must not alter live workflows. Preserve manual targeted validation:
`node --test scripts/ci/tests/test-ralph-round-cache.mjs scripts/ci/tests/test-ralph-macos-ssh.mjs scripts/ci/tests/test-ralph-macos-worker.mjs`.

## First Actions

1. Fetch `origin`; report the local-vs-remote `development` gap without changing any checkout.
   `strict: false`: `BEHIND` alone is not a blocker.
2. Collect every open issue and PR with complete pagination. Account for each open issue as
   dispatched, in-flight, awaiting-analysis, blocked (name each open blocker), epic-tracking,
   deferred-to-macOS-Ralph, or unaccounted.
3. Windows never starts mobile work, locally or through SSH. Retain and reconcile
   historical remote workers through `status-remote` by their original job/fence;
   never abandon records because new mobile dispatch is forbidden.
   macOS eligibility includes all issues but has hard 1-mobile + 4-general caps,
   5 total; Windows permits only 5 general. No category-slot borrowing.
   Legacy Mac general cross-host admission stays blocked; only explicitly
   attested native-role packages use the new private authority path in
   `native-roles.md`. Eligibility alone is not dispatch authority.
   Classify from labels, paths, acceptance criteria, or Swift/Xcode signals—not owner identity.
   For every configured local or remote dispatch, use the exact admission command sequence in
   `operations.md`; no direct `create_session` or SSH delivery is permitted outside that sequence.
4. Before admitting new work, apply `automation.md`'s PR-first recovery and
   `operations.md`'s reconciliation policy: establish one writer,
   reconcile existing remote jobs by ID without new-issue eligibility, verify missing local
   sessions against archived/terminal history, and account live/resumed handoffs under new fences.
   Report ledger and effective union counts separately. Unsupported legacy-worker recovery retains
   uncertainty; never reset slots or ignore live work to make the ledger fit.

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

Never implement. Never dispatch new mobile work from Windows, including SSH.
Never silently skip an issue. Never self-review or invent/substitute a reviewer/model. Apply the
canonical risk-based scope in `.github/copilot-instructions.md` § `Risk-Based Review Scope`:
standard review uses one qualified non-author reviewer from a different model family; high-risk
review requires two qualified reviewers with distinct primary lenses. Use the
configured exact model and supported effort for each selected reviewer.
An unavailable required model is a blocker.
If a required panel member authored a high-risk PR, block it; never substitute another roster
member.
Reviewers read only and never build, install, or test. End with the compact accounting report
and exit.
