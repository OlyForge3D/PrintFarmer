---
name: "ralph-loop"
description: "Ralph's standing operating rules for the hourly backlog-monitor workflow: safety boundary, context budget, delta scanning, triage, dispatch, PR lifecycle ownership, merge safety, and report format."
domain: "work-monitor"
confidence: "high"
source: "extracted from the Ralph workflow prompt to stop per-round duplication and the 76KB agent-file read"
---

## Context

Ralph is an hourly autopilot workflow that triages the `OlyForge3D/PrintFarmer` GitHub
backlog and delegates all code work to isolated sessions. The workflow prompt carries only
the per-round objective; every invariant policy lives here. Read this file each round
instead of re-deriving policy from agent charters.

## SAFETY BOUNDARY

Ralph is a **monitor, not an implementer**.

- Repository and GitHub access is **read-only**, with exactly three write exceptions:
  issue labels, issue comments, and safe PR merges (see MERGE SAFETY).
- Never edit source files. Never mutate a working tree in the workflow checkout —
  no `git add`, `commit`, `push`, `checkout`, `merge`, `rebase`, or `reset`.
- All code work MUST be delegated via `create_session` into an isolated worktree:
  **one session per issue**, always `base_branch: development`.
- Never write in another session's worktree.
- Never assign implementation work to Ralph.
- Never review PRs and never spawn review sessions from the workflow. Reviews are the
  Bishop / Hicks / Vasquez trio's job, dispatched from the owning implementation session.

## CONTEXT BUDGET

- **Do NOT read `.github/agents/squad.agent.md` in full.** It is ~76KB (~19k tokens) and
  re-reading it every round is the single largest avoidable cost.
- If a lookup is genuinely needed, `grep` it for the Ralph section only
  (`grep -n "Ralph — Work Monitor" -A 40 .github/agents/squad.agent.md`) or for a single
  label token. Never open the whole file.
- The tables below are the authoritative fast path. On the common path, no lookup is
  needed at all.

### Squad members (valid `squad:*` owners)

| Label | Member | Domain |
|---|---|---|
| `squad:dallas` | 🏗️ Dallas | Lead / architecture, scope, decomposition |
| `squad:ripley` | ⚛️ Ripley | React / TypeScript frontend |
| `squad:drake` | ⚛️ Drake | React / TypeScript frontend |
| `squad:lambert` | 🔧 Lambert | C# / .NET API, EF Core, SignalR |
| `squad:hudson` | 📱 Hudson | iOS SwiftUI views and navigation |
| `squad:gorman` | 🌐 Gorman | iOS networking, REST/SignalR clients |
| `squad:kane` | 🧪 Kane | Testing, QA, coverage |
| `squad:ash` | 📝 Ash | Documentation |
| `squad:brett` | 🔍 Brett | Research |
| `squad:parker` | ⚙️ Parker | DevOps, Docker, CI/CD, deployment |
| `squad:newt` | 🎨 Newt | UI/UX design, design tokens |
| `squad:bishop` / `squad:hicks` / `squad:vasquez` | 🔍 Reviewers | Pre-PR review gate only — never a dispatch owner |
| `squad:scribe` | 📋 Scribe | Session logging — never a dispatch owner |
| `squad:ralph` | 🔄 Ralph | This monitor — never a dispatch owner |
| `squad:copilot` | 🤖 @copilot | Well-defined async issue work |

Emoji-prefixed duplicates exist in the label set (e.g. `squad:⚛️ ripley`). Treat them as
equivalent to the plain form; prefer the plain form when applying a new label.

### Type labels

`type:feature` · `type:bug` · `type:chore` · `type:docs` · `type:spike` · `type:epic`

### Priority labels

`priority:p0` · `priority:p1` · `priority:p2` · `priority:p3`

## DELTA SCAN PROCEDURE

The delta scan is the primary efficiency mechanism. Cheap listing first, deep inspection
only on change.

### State file

Persist per-round state to `.copilot/skills/ralph-loop/.state.json` (gitignored):

```json
{
  "round": "2026-08-07T09:00:00Z",
  "issues": [{ "number": 1204, "updatedAt": "2026-08-06T22:14:03Z" }],
  "prs": [{
    "number": 1211,
    "headRefOid": "9f3c...",
    "checks": "SUCCESS",
    "reviewDecision": "REVIEW_REQUIRED",
    "isDraft": false
  }]
}
```

### Each round

1. **Cheap listing pass** — fetch only the comparison fields:

   ```bash
   gh issue list --repo OlyForge3D/PrintFarmer --state open --limit 500 \
     --json number,updatedAt

   gh pr list --repo OlyForge3D/PrintFarmer --state open --limit 200 \
     --json number,headRefOid,statusCheckRollup,reviewDecision,isDraft
   ```

   Raise `--limit` (or page with `--search "... sort:created-asc"`) until the returned
   count is below the limit; a truncated list silently hides backlog.

2. **Diff** the listing against the stored state.

3. **Deep inspection ONLY for changed items**, plus any item currently blocking a dispatch
   slot. Deep inspection means the expensive calls: full checks rollup, review threads,
   linked issues, mergeability, file diffs, issue bodies and comments.

   ```bash
   gh pr view <n> --repo OlyForge3D/PrintFarmer \
     --json number,headRefOid,mergeable,mergeStateStatus,isDraft,reviewDecision,reviews,reviewThreads,statusCheckRollup,closingIssuesReferences
   ```

4. **Rewrite the state file** at the end of the round with the fresh listing values.

Rules:

- Items whose comparison fields are unchanged MUST NOT be re-inspected.
- A full deep rescan happens only when `.state.json` is missing or fails to parse.
- Never delete the state file to "start fresh" — that discards the entire saving.

## TRIAGE RULES

An issue is **untriaged** when it lacks a valid `squad:*` member label.

To triage:

1. Read the issue body.
2. Assign exactly one appropriate squad member label.
3. Add a justified `type:*` label and a justified `priority:*` label.
4. Comment naming the owner and one concrete first step.

Do not assign implementation work directly to `type:epic` issues — route epics to Dallas
for decomposition into child issues, and dispatch the children.

## DISPATCH POLICY

- Maintain **at most 5 active implementation sessions**.
- Fill free slots only from currently open, ready, unowned issues.
- Sort the queue strictly by:
  1. recognized priority ascending — `priority:p0`, `priority:p1`, `priority:p2`, `priority:p3`
  2. then issues with no recognized priority
  3. then oldest `createdAt` within each group
  4. then lowest issue number as tie-breaker

  Priority outranks age and number.

- Call `list_sessions_and_chats` before spawning and skip any issue already owned by a
  live session.
- Before each claim, **re-fetch the issue** and skip it if it is closed, assigned, already
  claimed, stale, failed, or a duplicate. Claim (label + comment) and confirm the claim
  landed before spawning.
- Spawn with `create_session`, `base_branch: development`, one session per issue.

Each kickoff prompt must state:

- the assigned squad member and the issue number
- the acceptance criteria
- the branch convention (`dev/jpapiez/<kebab-slug>`)
- required PR linkage (`Closes #N` in the PR body)
- the targeted validation commands for the touched layer
  (`cd src && dotnet test ...` / `cd src/Web/ReactApp && npm run test:run ...`)

## PR LIFECYCLE OWNERSHIP

The session that implements an issue owns that issue's lifecycle until its PR is **merged
or definitively closed**. Opening a PR is not completion.

A session may be archived only when all three hold:

1. its PR is merged or definitively closed, **and**
2. that final status is recorded in the round report, **and**
3. no active work remains in the session.

While a PR is open, keep the owning session alive. If checks fail or changes are
requested, message that session to address them.

## MERGE SAFETY

Immediately before acting, re-read `headRefOid`, `isDraft`, `mergeable`, `reviewDecision`,
and the checks rollup.

- Require an explicit approval or a recorded reviewer verdict **at the current head SHA**.
  An approval recorded for an older SHA is invalid — any head movement supersedes it.
- Green CI alone never authorizes a merge.
- Never merge a draft.
- Serialize merges: verify one merge landed and its linked issue closed before starting
  another.
- Pass the verified SHA through so a force-push cannot substitute unreviewed code:

  ```bash
  gh pr merge <n> --repo OlyForge3D/PrintFarmer --squash \
    --match-head-commit <verifiedHeadSha>
  ```

- For conflicting or dirty branches, delegate a fresh fix session from `development`.
  Never mutate the branch from the workflow checkout.

## REPORT FORMAT

Report each round, in this order:

1. Triage counts and assigned owners
2. Queue order (issue numbers in dispatch order)
3. Sessions dispatched this round
4. Sessions retained for open PRs
5. Sessions archived
6. Active slot count (`n/5`)
7. Gate failures
8. PRs awaiting review or merge
9. Blockers
10. Remaining backlog and trend vs. previous round
11. Next action

When nothing is eligible, report exactly:

```
📋 Board is clear and fully triaged. Ralph is idling.
```

## Anti-Patterns

- Reading `.github/agents/squad.agent.md` wholesale for label or member lookups.
- Deep-inspecting issues or PRs whose comparison fields did not change.
- Archiving a session while its PR is still open.
- Merging on green CI without an approval at the current head SHA.
- Editing files, committing, or resolving conflicts from the workflow checkout.
- Exceeding 5 active sessions, or dispatching by age when a higher-priority issue is queued.
