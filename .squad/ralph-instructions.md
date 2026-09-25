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
    • Parallelism — Ralph always spawns agents for all actionable issues simultaneously
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

Read `~/.squad/machine-capabilities.json` before starting or resuming any issue.
If it has `maxConcurrent`, never exceed it:
- `maxConcurrent.xcode` — issue sessions for `needs:xcode` issues.
- `maxConcurrent.other` — issue sessions for every other issue.

Count every issue session you started on this host that is running, idle with
unmerged work, or interrupted, and that is not yet merged, blocked, or handed
off. Review sub-agents run inside their issue session and do not count
separately. When both slots are full, start nothing new: advance an existing
session instead, and leave other issues unassigned so another host can take them.
Resume interrupted sessions one slot at a time, highest priority first.

The Mac mini sets `{ "xcode": 1, "other": 1 }` because it runs out of memory
with more. When running `squad watch --execute` there, pass `--max-concurrent 2`.
A host without `maxConcurrent` is uncapped.

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
