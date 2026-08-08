---
name: "ralph-loop"
description: "Ralph's standing operating rules for the hourly backlog-monitor workflow: safety boundary, context budget, delta scanning, triage, epic maintenance, the Dallas analysis gate, READY dispatch, full issue accounting, PR lifecycle ownership, session lifecycle and the reap report, merge safety, and report format."
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

Every issue MUST leave triage with **exactly one `squad:*` label AND one `priority:*`
label**. Both are required; neither substitutes for the other.

Label hygiene (both problems exist in the live label set):

- The bare `squad` marker is not an owner. Once a member label is applied, **remove the
  bare `squad` marker** (e.g. #628 carries `squad` alongside `squad:lambert`).
- Emoji and plain forms of the same member coexist (`squad:🏗️ dallas` on #705/#1125 vs.
  `squad:dallas` on #1134). Treat both forms as the **same owner** when querying or
  counting, and apply the **plain form** for any new label.

Do not assign implementation work directly to `type:epic` issues — epics go to EPIC
MAINTENANCE each round and, when they need decomposition, to the ANALYSIS GATE.

## EPIC MAINTENANCE

Run this for **every open `type:epic`, every round**. Epics are decomposed and tracked,
never implemented directly.

1. **Enumerate children two ways and union the results** — the repo uses both mechanisms,
   so neither alone is complete:

   ```bash
   gh api repos/OlyForge3D/PrintFarmer/issues/{n}/sub_issues \
     --jq '.[] | "\(.number) \(.state)"'

   gh issue list --repo OlyForge3D/PrintFarmer --state all --label epic-child \
     --json number,state,title
   ```

2. **Post or refresh a single progress comment** — `X of Y children closed`, listing the
   open children by number and title. Find Ralph's existing progress comment and **edit
   it in place** (`gh issue comment --edit-last`). Never add a second progress comment;
   duplicate progress comments each round are an anti-pattern.
3. **Tick the epic body checklist** for completed children where the body contains one.
4. **Close the epic** when every child is closed AND the epic's own acceptance checklist
   is satisfied. Close with a summary comment listing the delivered children.
5. **Route to the ANALYSIS GATE** an epic that has zero children, or that has no open
   actionable children while its acceptance criteria are still unsatisfied.

Worked case: #705 has 15 children (#706–#715, #723, #724, #725, #794, #805), 13 closed.
It must carry a refreshed `13 of 15 children closed` comment naming #723 and #724 as the
open remainder — not sit silently at ~87% forever.

## ANALYSIS GATE (Dallas)

The escape hatch for work that cannot go straight to an implementer. Without it, gated
issues stall permanently.

**Triggers** — any one of:

- a `type:epic` that needs decomposition into child issues;
- an issue whose body declares an **unmet architecture/audit gate** — e.g. #1134 states
  "**Architecture gate:** Dallas must complete and sign off on a repository-wide audit
  before this work is handed to implementation";
- any issue Ralph judges too under-specified to hand to an implementer.

**Action:**

1. Label `status:needs-analysis`.
2. Comment naming the **specific gate** and **what would satisfy it**.
3. Dispatch a Dallas session via `create_session` — PrintFarmer project,
   `base_branch: development`.

**Rules:**

- Dallas's deliverable is **child issues or a written audit sign-off comment — NEVER
  implementation code**.
- Analysis sessions **count against the 5-slot budget** like any other session.
- **Do not re-dispatch Dallas** for an issue already carrying `status:needs-analysis` with
  a live analysis session. Check `list_sessions_and_chats` first to avoid duplicate spawns.
- Once Dallas satisfies the gate, **remove `status:needs-analysis`**. The resulting child
  issues become normal dispatch candidates on the next round.

## DISPATCH POLICY

- Maintain **at most 5 active implementation sessions**.
- Fill free slots only from **READY** issues (defined below).

### READY (definition)

An issue is **READY** when ALL of the following hold:

1. it is **open**;
2. it carries **exactly one** `squad:*` member label (emoji and plain forms count as the
   same owner);
3. it is **unassigned and unclaimed**;
4. it is **NOT** `type:epic`;
5. it is **NOT** `status:in-progress`;
6. it is **NOT** `status:needs-analysis`;
7. it has **no unsatisfied blocking dependency**.

A `dependencies` label is not itself a blocker. For any issue labelled `dependencies` or
otherwise marked blocked, Ralph MUST **verify whether the named blocking issue is still
open**. If the blocker has closed, the issue becomes READY and is dispatched. Silently
skipping such an issue is an error.

Concrete case this fixes: #723 (p0, `squad:🧪 kane`) and #724 (p0, `squad:⚙️ parker`) are
open, labelled, unassigned and top-priority, but carry `dependencies` and were skipped
every round with no recorded reason. Resolve their blockers and dispatch or record them as
`blocked` naming the open blocking issue.

### Queue order

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

Each **implementation** kickoff prompt must state:

- the assigned squad member and the issue number
- the acceptance criteria
- the branch convention (`dev/jpapiez/<kebab-slug>`)
- required PR linkage (`Closes #N` in the PR body)
- the targeted validation commands for the touched layer
  (`cd src && dotnet test ...` / `cd src/Web/ReactApp && npm run test:run ...`)
- the **verbatim closing clause and rider** from SESSION LIFECYCLE AND REAPING, as the
  final two paragraphs of the kickoff prompt. A kickoff prompt without both is malformed.

Each **analysis or research** kickoff prompt, including every Dallas dispatch, must instead
state:

- the assigned squad member and the issue number
- the exact non-code deliverable and where to publish it
- that the session must not implement code or open a PR
- that its final report must link the completed deliverable, record the linked issue's
  disposition, and explicitly attest `working tree clean` and `all commits pushed`
- that the session must report and stop without calling `archive_session`; Ralph will keep
  it under `Sessions retained` until all reap-readiness criteria are proven

## ACCOUNT FOR EVERY ISSUE

**No open issue may be silently skipped.** Every open issue must end each round in
**exactly one** bucket:

| Bucket | Meaning |
|---|---|
| `dispatched` | claimed and a session spawned this round |
| `in-flight` | already owned by a live session |
| `awaiting-analysis` | `status:needs-analysis`, Dallas session live or just dispatched |
| `blocked` | **name the specific open blocking issue** |
| `epic-tracking` | `type:epic` maintained under EPIC MAINTENANCE |

An open issue matching none of these is an **error**: report it as `unaccounted` with its
number and current labels.

The bucket counts must reconcile against the open-issue total. `dispatched + in-flight +
awaiting-analysis + blocked + epic-tracking + unaccounted == open issues`.

## PR LIFECYCLE OWNERSHIP

The session that implements an issue owns that issue's lifecycle until its PR is **merged
or definitively closed**. Opening a PR is not completion.

A session is done only when all three hold:

1. its PR is merged or definitively closed, **and**
2. that final status is recorded in the round report, **and**
3. no active work remains in the session.

While a PR is open, keep the owning session alive. If checks fail or changes are
requested, message that session to address them.

Ownership decides **when** a session ends. SESSION LIFECYCLE AND REAPING decides how a
finished session is surfaced for removal — no automated archival path reaches it, so the
session simply reports its final status and stops.

## SESSION LIFECYCLE AND REAPING

### The constraint

Two platform limits, both verified against the live runtime:

1. **`archive_session` only works on sessions the caller created.** Every Ralph round is a
   brand-new session, so **a round CANNOT archive a session spawned by any previous
   round** — the round can see it via `list_sessions_and_chats`, but the call fails.
2. **A session cannot archive itself.** `archive_session` on your own session id returns
   `Cannot archive the current session`. True self-archiving does not exist.

Together these close every automatic route for any session that outlives the round that
spawned it — which, because rounds exit within minutes, is every real implementation
session. Although the runtime could let a live round archive a child that both started
and finished during that same round, normal implementation work never completes that
fast. That own-child path is not a cleanup mechanism and nothing may depend on it.

**Ralph archives nothing, ever** — not a prior round's session, not its own current-round
child, and not itself. Ralph must never attempt any of these calls.

### Why hand-off was tried and removed

An earlier revision told each finished session to message its creating session and request
archival. It does not work, and it is actively harmful:

- **The creator is already gone.** Ralph's workflow prompt ends with a hard EXIT step, so a
  round terminates within minutes of reporting. Implementation work routinely finishes hours
  later, long after the creating round exited. The request goes nowhere.
- **Messaging an idle session wakes it.** A cleanup request can restart a completed round,
  which may then re-run round logic and re-triage or re-dispatch issues as a side effect —
  a worse failure than the stale sessions the rule was meant to solve.

Do not reintroduce hand-off in any form.

### The closing clause a finished session gets instead

Every dispatch kickoff prompt Ralph writes MUST end with this **closing clause and rider**,
verbatim, as its final two paragraphs. They must match Ralph's workflow prompt
word-for-word; the two must not drift.

```
When your PR is merged and you have verified the merge landed and the linked issue closed, report your final status as your last action and stop. If your PR is definitively closed without merge, report `CLOSED WITHOUT MERGE`, the reason, and the linked issue's current disposition instead; do not claim the issue closed unless you verified it. Do NOT attempt to archive yourself — the runtime refuses `archive_session` on the current session and the call will fail. Do not attempt to archive any other session either. Cleanup is handled only by Ralph's `🧹 Ready to reap` report and a human.

Before stopping in either terminal path, make the worktree clean and push every intended commit. Your final report MUST explicitly attest `working tree clean` and `all commits pushed`. If either statement is false or unknown, report that fact; Ralph must keep the session under `Sessions retained` and must not list it as ready to reap.
```

### The reap report is the ONLY mechanism

Because no automated path reaches a session that outlives its round, the `🧹 Ready to reap`
report is not one safety net among several — it is the only way a finished session ever
gets cleaned up, and **a human performs the removal**. Treat it as load-bearing.

Each round, call `list_sessions_and_chats`. A session is ready to reap only when **all four**
of these criteria are proven:

1. **Terminal deliverable state** — an implementation PR is squash-safely verified as
   merged or definitively closed without merge, or a non-PR analysis/research deliverable
   is verified at its required publication location.
2. **Final status recorded** — the owning session reported the terminal state and linked
   issue's disposition, plus the closure reason for a closed-unmerged PR or the deliverable
   link and result for non-PR work.
3. **Clean worktree attested** — the owning session explicitly reported
   `working tree clean`.
4. **Push complete attested** — the owning session explicitly reported
   `all commits pushed`.

These criteria are conjunctive. Missing evidence or an unknown state for **any** criterion
is disqualifying: keep the session under `Sessions retained`, naming the unknown or failed
criterion. Do not infer a clean or pushed state from PR status.

List every session that meets all four criteria under a `🧹 Ready to reap` heading, with:

- session name
- branch
- PR number and merged / closed state, or non-PR deliverable type and link
- linked issue disposition

**This is REPORT ONLY.** Ralph archives nothing. A human evaluates the report and performs
the reaping.

Produce the `🧹 Ready to reap` heading **every round, even when it is empty** — a missing
section is indistinguishable from an unchecked one.

Note: `delete_item` **does** work across sessions regardless of creator, unlike the
creator-scoped `archive_session`. It is destructive deletion, not archival, and that does
not make it available to automation. Reaping stays a **human** decision, and Ralph or any
autopilot session must never call `delete_item` on a session: squash-merge verification is
error-prone (see below) and a false positive destroys unpushed work.

### Merge verification is squash-merge-safe

This repo **squash-merges** pull requests. A squash merge creates a **new** commit on
`development`; the branch's own commits never land there. So on a fully merged branch
`git log origin/development..HEAD` still shows commits, and any naive "are this branch's
commits on `development`?" check will **wrongly** conclude the work is unmerged and unsafe
to reap.

Never use commit-containment of the branch head as merge evidence. Read the PR's own state
and merge commit instead:

```bash
git fetch origin development

gh pr view <n> --repo OlyForge3D/PrintFarmer --json state,mergeCommit
# -> {"state":"MERGED","mergeCommit":{"oid":"<sha>"}}

git branch -r --contains <sha>
# -> must list origin/development
```

A PR counts as merged for reaping only when `state` is `MERGED` **and** its
`mergeCommit.oid` is contained in `origin/development` after a fresh fetch.

For a PR closed without merge, require `state: CLOSED` plus the owning session's explicit
`CLOSED WITHOUT MERGE` final report and reason. The linked issue may remain open, be
reassigned, or be closed separately; record its actual disposition rather than treating
the closed PR as proof that the issue closed.

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
2. **Issue accounting** — every open issue in exactly one bucket, with per-bucket counts
   and numbers: `dispatched`, `in-flight`, `awaiting-analysis`, `blocked` (each naming its
   open blocking issue), `epic-tracking`, and `unaccounted` (must be zero)
3. **Epic status** — per open epic: `X of Y children closed`, open children, action taken
4. **Analysis gate** — issues newly gated, Dallas sessions dispatched, gates satisfied
5. Queue order (issue numbers in dispatch order)
6. Sessions dispatched this round
7. **Sessions retained** — every session that does not satisfy all four reap-readiness
   criteria, with each failed or unknown criterion named
8. **`🧹 Ready to reap`** — every session satisfying all four conjunctive readiness
   criteria, with session name, branch, terminal PR or non-PR deliverable evidence, and
   linked issue disposition. Report only; emit the heading every round even when the list
   is empty.
9. Active slot count (`n/5`)
10. Gate failures
11. PRs awaiting review or merge
12. Blockers
13. Remaining backlog and trend vs. previous round
14. Next action

When nothing is eligible, report exactly:

```
📋 Board is clear and fully triaged. Ralph is idling.
```

## Anti-Patterns

- Reading `.github/agents/squad.agent.md` wholesale for label or member lookups.
- Deep-inspecting issues or PRs whose comparison fields did not change.
- Calling `archive_session` from Ralph for any session, including a current-round child.
- Calling `archive_session` on a session created by a previous round — the call fails.
- Instructing a session to archive itself — `archive_session` on your own id returns
  `Cannot archive the current session`.
- Instructing a finished session to message its creating session to request archival — the
  creating round has already exited, and waking an idle round can make it re-run its logic.
- Assuming any automation reaps sessions, or calling `delete_item` on one. Nothing reaps
  automatically; the `🧹 Ready to reap` report plus a human is the whole mechanism.
- Writing a kickoff prompt that omits the verbatim closing clause or rider.
- Giving an analysis or research session the implementation-only branch, PR, validation,
  or closed-unmerged instructions instead of its non-PR deliverable contract.
- Omitting the `🧹 Ready to reap` heading because the list is empty.
- Listing a session as ready to reap when any of the four readiness criteria is false,
  missing, or unknown.
- Treating a closed-unmerged PR as proof that its linked issue closed, or omitting the
  owning session's closure reason and clean/pushed attestations.
- Treating "this branch's commits are not on `development`" as proof the work is unmerged —
  squash merges never put branch commits on `development`.
- Phrasing a cleanup rule as a permission ("may archive once…") instead of an instruction
  ("each round, list every session ready to reap"). A permission never gets executed.
- Merging on green CI without an approval at the current head SHA.
- Editing files, committing, or resolving conflicts from the workflow checkout.
- Exceeding 5 active sessions, or dispatching by age when a higher-priority issue is queued.
- Leaving an epic unmaintained, or posting a fresh progress comment instead of refreshing
  the existing one.
- Skipping a `dependencies`-labelled issue without checking whether its blocker is closed.
- Ending a round with an open issue in no accounting bucket.
- Dispatching Dallas again for an issue already `status:needs-analysis` with a live session,
  or letting a Dallas analysis session write implementation code.
