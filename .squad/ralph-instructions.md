# Ralph Instructions
<!-- User-owned: customize this file to override Ralph's autonomous-execution behavior.
     squad init creates this file on first install; squad upgrade never overwrites it. -->

<!--
  PURPOSE
  -------
  When `.squad/ralph-instructions.md` exists, `squad watch --execute` instructs the
  spawned Copilot session to read this file and follow ALL sections here instead of
  the built-in fallback prompt.  If the file is absent, the built-in prompt is used.

  CONTRACT (stable — safe to build on)
  --------------------------------------
  YOU CAN  customize via this file:
    • Extra instructions given to Ralph at session start (Teams/Slack notifications,
      calendar checks, post-task hooks, MCP-powered side effects, escalation paths)
    • Additional eligibility rules or priority ordering for issue selection
    • Agent persona, tone, or verbosity for session output

  YOU CANNOT override via this file:
    • Parallelism — Ralph spawns agents for all actionable issues simultaneously,
      bounded only by this host's limits (see "Host Limits", which takes precedence)
    • Core eligibility filter (squad/squad:* label required, not blocked, not assigned)
    • The underlying `gh` / Copilot CLI command used to spawn each session

  TRUST IMPLICATIONS
  ------------------
  This file is read by the spawned Copilot session with full agent permissions.
  Treat it like code — never paste untrusted content here.  Anyone with write access
  to this file can influence what the agent does on your behalf.

  If this file is missing or empty, `squad watch --execute` falls back to the
  built-in prompt with no behavioral change.

  PLACEHOLDERS
  ------------
  The following values are injected by execute.ts before the session reads this file:
    (none currently — Ralph builds the issue list dynamically at runtime)

  FORMAT
  ------
  Plain markdown.  Structure with ## sections.  The spawned session reads the whole
  file, so keep it concise — one screen of instructions is ideal.
-->

## Ralph, Go!

Read this file for your full instructions.  Follow ALL sections.
MAXIMIZE PARALLELISM — spawn agents for ALL actionable issues simultaneously,
up to this host's limits (see "Host Limits" below).

### Host Limits

These limits take precedence over every "parallel" or "all simultaneously"
instruction in this file, `.github/ralph-reference.md` and the Squad templates.

Before starting or resuming any issue, read `~/.squad/machine-capabilities.json`:
- No file, or no `maxConcurrent` key: this host is uncapped.
- `maxConcurrent.xcode` limits `needs:xcode` issue sessions and
  `maxConcurrent.other` limits all other issue sessions. A missing, non-integer
  or negative value means 1; 0 means that category never runs on this host.
- File present but unreadable or invalid JSON: use 1 for both, and say so in
  your round report.

**Slots count running sessions, not claims.** Each round, call
`get_sessions_status` and count, per category, the issue sessions in this project
that are running (`is_running: true`), whoever created them. Under
`squad watch --execute`, count the agents running in this invocation; agents from
earlier rounds have exited. Review sub-agents run inside their issue session and
do not count.

**Tie each session to its issue by branch, not name** (the app renames sessions
to their PR title). An issue session's branch contains `squad/{issue}-` or
`squad-{issue}-`, possibly after a prefix such as `jpapiez-`. When you create an
issue session, tell it to call `rename_branch` with `squad-{issue}-{slug}` first
if its branch lacks that pattern. Otherwise, map a session to its issue through
`created_pr_number` and that PR's `Closes #N`. The category comes from that
issue's `needs:xcode` label. A running `autopilot` session you cannot map
counts against every category; report it. Your own Ralph session and
non-`autopilot` (owner-driven) sessions do not count.

A **paused** session is an issue session on this host that is idle or interrupted
before its issue is done (PR merged, blocked, or released). It keeps its GitHub
claim but holds no slot.

- When a category is at its limit, start and resume nothing in that category.
  The other category may still work up to its own limit.
- When a category has a free slot, resume its highest-priority paused session
  first by messaging it (`send_session_message`). Under `squad watch --execute`,
  spawn one agent that continues from the issue's existing branch or PR. Start a
  new issue in that category only when it has no resumable paused sessions. A
  session awaiting user input or plan approval cannot be resumed by you: list it
  in your round report for the owner, and do not let it block the category.
- Never create a second session for an issue that already has one.
- Over the limit (for example after a crash): message nothing beyond the limit;
  the extra sessions become paused when their turn ends. Report the
  over-subscription and the sessions involved in your round report.
- A paused session with an open PR keeps its claim; comment once "Paused on
  <machine>: waiting for a slot". A paused session with no open PR is released:
  push any commits to its branch, unassign the issue, and comment "Released by
  <machine>: no slot; continue from branch `<branch>`" (or "nothing pushed").
- Before starting any issue, check for an existing remote branch containing
  `squad/{issue}-` or `squad-{issue}-`, and continue it rather than creating a
  new one.
- A session that cannot be resumed (worktree gone, repeated failure) goes through
  Escalation below.
- Leave issues you have not started unassigned so another host can take them.

`squad watch --execute` does not enforce these limits: squad-cli 0.13.1 ignores
`--max-concurrent` on that path and hands every eligible issue to one Ralph
invocation. The cap depends entirely on Ralph following this section, so on a
capped host prefer an in-app "Ralph, go" session, which can see every session.

The Mac mini sets `{ "xcode": 1, "other": 1 }` because it runs out of memory
with more.

### Issue Selection

Work on every open, unblocked, unassigned issue labeled `squad` or `squad:{member}`.
Skip issues that are assigned to a human, blocked, or marked `status:on-hold`.

PrintFarmer rules:
- Scan open PRs (including drafts) first: route review feedback and CI failures
  to the PR author, and merge approved PRs per `.github/ralph-reference.md`.
- Priority: `priority:p0` > `p1` > `p2` > `p3`; within a priority, children of open
  `priority:p0`/`p1` epics first, then oldest. `TEST ONLY` spikes go last.
- Skip issues labelled `go:no` or `status:needs-analysis` without `go:yes`. For
  `go:needs-research`, post findings as an issue comment, then remove
  `go:needs-research`; no PR is needed unless files change.
- **Claim before starting:** another machine may also run Ralph. Skip any issue
  that already has an assignee or an open PR linked by `Closes #N`. Before
  starting, assign the issue to yourself (`gh issue edit N --add-assignee @me`),
  re-read it, and stop if it now also has another claim or linked PR.
- Skip `needs:xcode` issues unless this machine's capabilities include `xcode`,
  and run at most one of them at a time per Mac.
- Parallelize only independent work: issues that edit the same files go to one agent.

### Pull Requests

Branch `squad/{issue}-{slug}` from `development`, open a draft PR with the `squad`
label and `Closes #N`, then follow the review gate in `.github/copilot-instructions.md`
("Readiness and Merge Review Gate"). Merge only `squad`-labelled PRs with a
passing `squad/pre-pr-verdict`, using `gh pr merge --match-head-commit <sha>`.

### Post-Task Actions

After completing work on an issue, unassign yourself if no PR was opened, and
leave a one-line status comment.

### Escalation

If you are blocked on an issue, comment on it explaining why, add a `status:blocked`
label, unassign yourself, and move to the next actionable item.  Do not halt the loop.
