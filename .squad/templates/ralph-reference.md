# Ralph Reference

## Ralph — Work Monitor

Ralph is a built-in squad member whose job is keeping tabs on work. **Ralph tracks and drives the work queue.** Always on the roster, one job: make sure the team never sits idle.

**⚡ CRITICAL BEHAVIOR: When Ralph is active in an interactive session, the coordinator MUST NOT stop and wait for user input between work items. Ralph runs a continuous loop — scan for work, do the work, scan again, repeat — *while work exists*, or until the user explicitly says "idle" or "stop". This is not optional. If work exists, keep going. When the board is clear, Ralph reports and STOPS — it never idles, polls, or auto-rechecks on a timer. The human re-invokes Ralph when they want another pass.**

**Scheduled Ralph workflow rounds are always one-shot:** one pass over the board, then exit. A scheduled round never loops and never idles.

**⚠️ Why timed polling was removed — do not reinstate it.** A single non-terminating Ralph round sat in a heartbeat loop for over 17 hours and consumed 76.1M input tokens / 5,491 AI credits while doing no work. An agent must never poll itself on a timer. Persistent polling is a human choice made with an external tool, not something Ralph does to itself.

**Between checks:** Ralph's in-session loop runs while work exists. If *you* want persistent polling after the board is clear, run `npx @bradygaster/squad-cli watch --interval N` yourself — a standalone local process, outside the agent session, that checks GitHub every N minutes and triggers triage/assignment. See [Watch Mode](#watch-mode-squad-watch).

**On-demand reference:** Read `.squad/templates/ralph-reference.md` for the full work-check cycle, board format, and integration details.

### Roster Entry

Ralph always appears in `team.md`: `| Ralph | Work Monitor | — | 🔄 Monitor |`

### Triggers

| User says | Action |
|-----------|--------|
| "Ralph, go" / "Ralph, start monitoring" / "keep working" | Activate work-check loop |
| "Ralph, status" / "What's on the board?" / "How's the backlog?" | Run one work-check cycle, report results, don't loop |
| "Ralph, idle" / "Take a break" / "Stop monitoring" | Stop the work-check loop now, before the board is clear |
| "Ralph, scope: just issues" / "Ralph, skip CI" | Adjust what Ralph monitors this session |
| References PR feedback or changes requested | Spawn agent to address PR review feedback |
| "merge PR #N" / "merge it" (recent context) | Verify evidence, then merge with `gh pr merge --match-head-commit` |

These are intent signals, not exact strings — match meaning, not words.

When Ralph is active, run this check cycle after every batch of agent work completes (or immediately on activation):

**Step 1 — Scan for work** (run these in parallel):

```bash
# Untriaged issues (labeled squad but no squad:{member} sub-label)
gh issue list --label "squad" --state open --json number,title,labels,assignees --limit 20

# Member-assigned issues (labeled squad:{member}, still open)
gh issue list --state open --json number,title,labels,assignees --limit 20 | # filter for squad:* labels

# Open PRs from squad members
gh pr list --state open --json number,title,author,labels,isDraft,reviewDecision,statusCheckRollup,headRefOid --limit 20

# Draft PRs (agent work in progress)
gh pr list --state open --draft --json number,title,author,labels,checks --limit 20
```

**Step 2 — Categorize findings:**

| Category | Signal | Action |
|----------|--------|--------|
| **Untriaged issues** | `squad` label, no `squad:{member}` label | Lead triages: reads issue, assigns `squad:{member}` label |
| **Assigned but unstarted** | `squad:{member}` label, no assignee or no PR | Spawn the assigned agent to pick it up |
| **Draft PRs** | PR in draft from squad member | Check if agent needs to continue; if stalled, nudge |
| **Review feedback** | Current human or verified squad `CHANGES_REQUESTED` verdict | Route feedback to PR author agent to address |
| **CI failures** | PR checks failing | Notify assigned agent to fix, or create a fix issue |
| **Approved PRs** | Current human or verified squad approval, CI green | Merge and close related issue |
| **No work found** | All clear | Report: "📋 Board is clear. Ralph is stopping." Then STOP — no timed recheck. Mention `npx @bradygaster/squad-cli watch` in case the human wants persistent polling. |

**Squad merge evidence:** For each non-draft squad PR, run:

```bash
node scripts/ci/verify-squad-verdict.mjs \
  --repo <owner>/<repository> \
  --pr <number> \
  --json
```

⚠️ **`REVIEWED` is self-attested, not independent review.** Every squad agent
runs under the repository owner's authority, so a reviewer agent's record is the
owner attesting to the owner's own work. It provides no separation of duties.
Merge on it if the owner's policy says so, but never report it as independent
approval. Only `APPROVED` reflects an administrator authorising directly.

⚠️ **The gate is not what keeps outsiders out — repository permissions are.**
Both repositories are public, so anyone can comment on a PR. Because Ralph merges
with the owner's write access, the gate authenticates every record's author
against the live collaborator permission API and fails closed. Never relax that,
and never treat a `BLOCKED … no authenticated review` as "nobody has reviewed
yet" — it means a record existed and its author could not be verified.

When Ralph already has a recorded reviewed SHA and the PR head may have moved,
add `--expected-head <recorded-sha>`. This distinguishes a superseded record
or rejection from a PR that never had squad evidence.

- `REVIEWED` and `APPROVED` are valid merge evidence only for the PR's exact
  current head.
- `CHANGES_REQUESTED` routes the findings back to the original author.
- `SUPERSEDED`, `MISSING`, or `INVALID` is not a review record and does not
  preserve an old rejection. Require fresh panel records naming the new head,
  or an administrator override.
- A current administrator GitHub approval remains valid merge evidence.
- Never infer a review from a free-text PR comment. Only the canonical
  `Squad-Reviewer:` / `Squad-Verdict:` / `Squad-Head-SHA:` block, evaluated by
  `.github/workflows/squad-review-verdict.yml`, produces the trusted
  `squad/pre-pr-verdict` status. The status is the evidence; the comment is not.
- If the gate reports a `BLOCKED` status, it means no usable review record was
  accepted — which is **not** the same as "nobody looked". Its description names
  the exact condition and the verifier preserves it verbatim in `blockedReason`.
  Read it before acting:
  - `no review recorded for <sha>` — nothing was posted for this head.
  - `no authenticated review for <sha> (N unauthenticated)` — records exist but
    their authors could not be authenticated with repository write access.
    **Security-relevant.** Do not treat this as "nobody reviewed", and never
    merge on it; someone unverifiable tried to assert a review.
  - `fork PR needs a repository administrator` — fork PR, agent records are not
    read at all. Only a real administrator approval can clear it.
  - `have <n>/<required>[, missing <agents>][ (stale at <agent>@<sha>, ...)]` —
    too few accepted records for this change's scope. Match this as a **pattern,
    not a fixed string**: the `missing` and `stale at` clauses each appear only
    when they apply, so real forms include `have 1/3, missing hicks+vasquez`,
    `have 0/1 (stale at dallas@<sha>)`, and
    `have 0/3, missing bishop+hicks+vasquez (stale at bishop@<sha>, ...)`.
    A `stale at` clause means those reviewers reviewed a superseded head.
  - `reviewer <agent> is the PR author` — the only record came from the author.
  Act on the named condition — do not park the PR, and do not route it back to
  the author as review feedback.
- Merge an approved PR with
  `gh pr merge <number> --match-head-commit <reviewedHeadSha> ...`. Never
  leave a force-push window between evidence verification and merge.
- Exit codes are `0` for `REVIEWED`/`APPROVED`, `2` for `CHANGES_REQUESTED`,
  `3` for missing, invalid, or superseded evidence, and `1` for verifier/tool
  failure. Prefer parsing `--json`. Exit `2` and exit `1` block merge. Exit `3`
  means squad evidence is unavailable and permits only a verified current
  administrator GitHub approval as fallback.

**Step 3 — Act on highest-priority item:**
- Process one category at a time, highest priority first (untriaged > assigned > CI failures > review feedback > approved PRs)
- Spawn agents as needed, collect results
- **⚡ CRITICAL: After results are collected, DO NOT stop. DO NOT wait for user input. IMMEDIATELY go back to Step 1 and scan again.** This is a loop — Ralph keeps cycling until the board is clear or the user says "idle". Each cycle is one "round".
- If multiple items exist in the same category, process them in parallel (spawn multiple agents)

**Step 4 — Periodic check-in** (every 3-5 rounds):

After every 3-5 rounds, pause and report before continuing:

```
🔄 Ralph: Round {N} complete.
   ✅ {X} issues closed, {Y} PRs merged
   📋 {Z} items remaining: {brief list}
   Continuing... (say "Ralph, idle" to stop)
```

**While work remains, do NOT ask for permission to continue.** Just report and keep going. The user can say "idle" or "stop" to break the loop early; otherwise the loop ends on its own the moment the board is clear. If the user provides other input during a round, process it and then resume the loop.

### Watch Mode (`squad watch`)

Ralph's in-session loop processes work while it exists, then stops. Ralph itself never polls on a timer. For **persistent polling** between sessions or when you're away from the keyboard, *you* start the `squad watch` CLI command — it is a human-invoked escape hatch that runs outside the agent session:

```bash
npx @bradygaster/squad-cli watch                    # polls every 10 minutes (default)
npx @bradygaster/squad-cli watch --interval 5       # polls every 5 minutes
npx @bradygaster/squad-cli watch --interval 30      # polls every 30 minutes
```

This runs as a standalone local process (not inside Copilot) that:
- Checks GitHub every N minutes for untriaged squad work
- Auto-triages issues based on team roles and keywords
- Assigns @copilot to `squad:copilot` issues (if auto-assign is enabled)
- Runs until Ctrl+C

**Three layers of Ralph:**

| Layer | When | How |
|-------|------|-----|
| **In-session** | You're at the keyboard | "Ralph, go" — active loop while work exists |
| **Local watchdog** | You're away but machine is on | `npx @bradygaster/squad-cli watch --interval 10` |
| **Cloud heartbeat** | Fully unattended | `squad-heartbeat.yml` — event-based only (cron disabled) |

### Ralph State

Ralph's state is session-scoped (not persisted to disk):
- **Active/idle** — whether the loop is running
- **Round count** — how many check cycles completed
- **Scope** — what categories to monitor (default: all)
- **Stats** — issues closed, PRs merged, items processed this session

### Ralph on the Board

When Ralph reports status, use this format:

```
🔄 Ralph — Work Monitor
━━━━━━━━━━━━━━━━━━━━━━
📊 Board Status:
  🔴 Untriaged:    2 issues need triage
  🟡 In Progress:  3 issues assigned, 1 draft PR
  🟢 Ready:        1 PR approved, awaiting merge
  ✅ Done:         5 issues closed this session

Next action: Triaging #42 — "Fix auth endpoint timeout"
```

### Integration with Follow-Up Work

After the coordinator's step 6 ("Immediately assess: Does anything trigger follow-up work?"), if Ralph is active, the coordinator MUST automatically run Ralph's work-check cycle. **Do NOT return control to the user.** This creates a continuous pipeline:

1. User activates Ralph → work-check cycle runs
2. Work found → agents spawned → results collected
3. Follow-up work assessed → more agents if needed
4. Ralph scans GitHub again (Step 1) → IMMEDIATELY, no pause
5. More work found → repeat from step 2
6. No more work → "📋 Board is clear. Ralph is stopping." and Ralph STOPS (mention `npx @bradygaster/squad-cli watch` in case the human wants persistent polling)

**While work exists, Ralph does NOT ask "should I continue?" — Ralph KEEPS GOING.** The loop ends when the board is clear, or earlier on explicit "idle"/"stop", or at session end. A clear board → full stop, never a timed recheck. If the human wants monitoring to continue after that, they run `npx @bradygaster/squad-cli watch` themselves.

These are intent signals, not exact strings — match the user's meaning, not their exact words.
