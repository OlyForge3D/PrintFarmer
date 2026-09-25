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
MAXIMIZE PARALLELISM within the rules below: spawn agents for all actionable
issues at once, but never beyond this host's limits ("Host Limits"), and give
issues that edit the same files to one agent ("Issue Selection").

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
- `<machine>` in the comments and reports below is the file's `machine` value,
  or this host's hostname if there is none.

**Slots count running sessions, not claims.** Each round, call
`get_sessions_status` and count, per category, the issue sessions in this project
that are running (`is_running: true`), whoever created them. Under
`squad watch --execute`, count the agents running in this invocation; agents from
earlier rounds have exited. Review sub-agents run inside their issue session and
do not count.

**Tie each session to its issue by branch, not name** (the app renames sessions
to their PR title). An issue session's branch contains `squad/{issue}-` or
`squad-{issue}-`, possibly after a prefix such as `jpapiez-`. When you create an
issue session with the app's `create_session` tool, tell it to call
`rename_branch` with `squad-{issue}-{slug}` first if its branch lacks that
pattern. Agents spawned by `squad watch --execute` have no `rename_branch`;
they name their branch as in "Pull Requests". If a session's branch has neither
pattern, map it to its issue through `created_pr_number` and that PR's
`Closes #N`. The category comes from that issue's `needs:xcode` label; PR work
that changes non-markdown files under `mobile/` is `xcode` work even without
the label.

A running `autopilot` session you cannot map counts against every category if
it has a `creator_session_id`, whatever that creator is: any session, including
an interactive Ralph or Ralph on another host, may have started it. One with no
`creator_session_id` was started directly by the owner and does not count.
List every unmapped running session in your round report either way. Your own
Ralph session and non-`autopilot` (owner-driven) sessions do not count.

A **paused** session is an issue session on this host that is idle or interrupted
before its issue is done (PR merged, blocked, or released). It keeps its GitHub
claim but holds no slot.

- When a category is at its limit, start and resume nothing in that category.
  The other category may still work up to its own limit.
- When a category has a free slot, resume its highest-priority paused session
  first by messaging it (`send_session_message`). Under `squad watch --execute`,
  spawn one agent that continues from the issue's existing branch or PR. Start a
  new issue in that category only when it has no resumable paused sessions. A
  session awaiting user input or plan approval must not be resumed by you: do
  not answer it or approve its plan for the owner (`answer_session_input`,
  `respond_to_session_plan`). List it in your round report for the owner, and
  do not let it block the category.
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

### Round Report

A round is one pass over the board. Each round ends with a round report in
your session output: a **Host** line (`<machine>`, its limits, and the slots in
use per category) and, below it, only the items that apply: an unreadable
capabilities file; over-subscription and the sessions involved; unmapped
running sessions; sessions paused or released this round; sessions awaiting
owner input or plan approval; and stale claims (see "Issue Selection"). In the
in-session loop, add the full "Ralph on the Board" block from
`.github/ralph-reference.md` at each periodic check-in and when Ralph stops.
Under `squad watch --execute`, the spawned session's final message is the
round report and always includes that block.

### Issue Selection

Work on every open, unblocked, unassigned issue labeled `squad` or `squad:{member}`.
Skip issues that are assigned, or that carry a blocking label: `status:blocked`,
`blocked`, `status:wontfix`, or `status:on-hold` (the last two are squad-cli's
blocking labels; this repo does not currently use them).

PrintFarmer rules:
- Scan open PRs (including drafts) first: route review feedback and CI failures
  to the PR author, and merge approved PRs per `.github/ralph-reference.md`.
  Route a PR only on a host that could work its issue: PR work for a
  `needs:xcode` issue, or that changes non-markdown files under `mobile/`, needs
  the `xcode` capability and an `xcode` slot. `squad watch --execute` runs this
  scan only when it also finds an eligible issue (see
  `.github/ralph-reference.md`), so PR recovery otherwise needs an in-app
  "Ralph, go".
- Unattended pickup needs the bare `squad` label: squad-cli lists issues with
  `--label squad`, so a `squad:{member}`-only issue is invisible to it. When you
  find one, add `squad`.
- Priority: `priority:p0` > `p1` > `p2` > `p3`; within a priority, children of open
  `priority:p0`/`p1` epics first, then oldest. `TEST ONLY` spikes go last.
- Skip issues labelled `go:no` or `status:needs-analysis` without `go:yes`. For
  `go:needs-research`, post findings as an issue comment, then remove
  `go:needs-research`; no PR is needed unless files change.
- **Claim before starting:** another machine may also run Ralph, and every host
  uses the same GitHub account, so the assignee cannot tell hosts apart. Skip
  any issue that already has an assignee or an open PR linked by `Closes #N`.
  To claim, assign the issue to yourself (`gh issue edit N --add-assignee @me`)
  and comment "Claimed by <machine>". Re-read the issue, its comments and its
  full timeline (`gh api --paginate repos/{owner}/{repo}/issues/N/timeline`).
  The winning claim is the "Claimed by" comment with the lowest comment ID
  posted after the issue's last `unassigned` event, counting only comments by
  the assignee's account. Start only if the issue is still assigned, has no
  linked PR, and the winning claim names this machine. Otherwise stop, and do
  not unassign: the assignee belongs to the winning host too. If you cannot
  read the full history, stop and report it. This narrows but does not close
  the race; GitHub has no atomic claim.
- **Stale claims:** a claim is stale when the issue is assigned, has no open PR
  linked by `Closes #N`, and has had no comment or branch push for 24 hours.
  Report it in your round report with the machine named by its winning claim,
  or "no marker". Do not release it yourself: a quiet build, or a
  `squad watch --execute` agent that `get_sessions_status` cannot see, looks
  the same as a dead claim. Releasing it is the owner's call.
  `squad watch --execute` never sees assigned issues, so only an in-app
  "Ralph, go" runs this check.
- Skip `needs:xcode` issues unless this machine's capabilities include `xcode`.
  Nothing mechanical serializes Xcode work: run at most one `needs:xcode`
  session per Mac, or fewer if "Host Limits" says so.
- Parallelize only independent work: issues that edit the same files go to one agent.

### Pull Requests

Branch `squad/{issue}-{slug}` from `development`. `squad-{issue}-{slug}`, the
form `rename_branch` produces (possibly after a prefix such as `jpapiez-`), is
the same convention: keep working on that branch and never create a second
one for the issue. Open a draft PR with the `squad`
label and `Closes #N`, then follow the review gate in `.github/copilot-instructions.md`
("Readiness and Merge Review Gate"). Merge only `squad`-labelled PRs with a
passing `squad/pre-pr-verdict`, using `gh pr merge --match-head-commit <sha>`.

### Post-Task Actions

After completing work on an issue, unassign yourself if no PR was opened, and
leave a one-line status comment.

### Escalation

If you are blocked on an issue, comment on it explaining why, add a `status:blocked`
label, unassign yourself, and move to the next actionable item.  Do not halt the loop.
