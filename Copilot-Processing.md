# Copilot Processing — Issue #1310

## Request

Implement a fix for GitHub issue #1310 ("bug: squad verdict gate is structurally
unsatisfiable - non-author admin does not exist") in OlyForge3D/PrintFarmer, touching
`.github/workflows/squad-review-verdict.yml`, `scripts/ci/verify-squad-verdict.mjs`, and
`.github/copilot-instructions.md` / `CLAUDE.md` as needed. Explicit escape hatch: if the fix
genuinely requires an architecture decision beyond a Parker-level DevOps change (e.g. GitHub
permissions/identities Parker cannot grant), stop, do not open a PR, and report back instead of
forcing an inadequate fix. Explicit negative constraint: must not let the PR author record their
own verdict, and must not weaken exact-head SHA pinning.

## Plan

1. Read `.github/workflows/squad-review-verdict.yml` and `scripts/ci/verify-squad-verdict.mjs`
   in full; confirm exactly which check is unsatisfiable and why.
2. Enumerate every technical mechanism that could supply a "recorder identity distinct from the
   PR author" without a second human, and stress-test each for whether it adds *real*
   independence or is a relabeling trick that still lets the sole human self-approve.
3. Decide whether a Parker-executable fix exists that satisfies the negative constraint, or
   whether this is a genuine owner/architecture decision.
4. If a real fix exists: implement workflow + verifier + docs changes, update/add tests, run
   validation, go through the 3-way review gate, open PR with `Closes #1310`.
5. If not: stop, document the reasoning, do not open a PR, report back clearly.

## Analysis

### Confirmed root cause

`squad-review-verdict.yml` requires, for the `workflow_dispatch` actor (`context.actor`, i.e.
whoever ran `gh workflow run` / clicked "Run workflow" — the human dispatcher):
- `pull.user.login.toLowerCase() !== actor.toLowerCase()` (line 81-84): actor must differ from
  the PR author.
- `getCollaboratorPermissionLevel(actor) === 'admin'` (line 86-95): actor must be a repo admin.

`verify-squad-verdict.mjs` independently re-derives the same dispatcher identity from
`run.display_title` (which embeds `github.actor` at dispatch time) and re-checks
`title.actor.toLowerCase() === author.toLowerCase()` (line 112-114) — so even if the workflow's
own guard were bypassed, the verifier enforces the same non-author-dispatcher rule from the
outside.

`jpapiez` is the only collaborator (`admin=true`) on this repo
(`gh api repos/OlyForge3D/PrintFarmer/collaborators`) and is the author of essentially every PR.
No GitHub account can simultaneously be "the PR author" and "not the PR author" — the guard is
therefore unsatisfiable for any PR jpapiez opens, which is all of them. Confirmed: 0/12 recent
merged PRs have `squad/pre-pr-verdict` evidence; the workflow's only two dispatch attempts both
failed on exactly this guard.

### Why bot/token tricks do not fix this (and would be a forbidden self-approval loophole)

The obvious-looking fix is "make the recorder a bot/App identity instead of a second human" —
this is the issue's own Option A. I traced it through in detail:

- `status.creator.login` is **already** `github-actions[bot]` today, because the commit-status
  write already runs through the default `GITHUB_TOKEN` inside `actions/github-script`. That
  part of the "distinct identity" is already satisfied and needs no new credential.
- The actual blocking checks (above) key on `context.actor`/`github.actor` **at dispatch time**
  — i.e. who ran `gh workflow run` / clicked the button — not on which credential wrote the
  status. `github.actor` for a `workflow_dispatch` is set to whoever/whatever performed the
  dispatch API call.
- A personal fine-grained PAT belonging to jpapiez, used to dispatch, still resolves
  `github.actor` to `jpapiez` — a PAT does not change account identity.
- Chaining two workflows so that workflow A (dispatched by jpapiez) calls
  `actions.createWorkflowDispatch` on workflow B using the default `GITHUB_TOKEN` would very
  likely make workflow B's `github.actor` resolve to `github-actions[bot]`, which would
  trivially satisfy the string-inequality checks. **I am not implementing this.** It supplies
  zero real independent judgment — it is still jpapiez, alone, single-handedly producing the
  APPROVE signal, merely laundered through an extra bot hop so the metadata says someone else
  did it. That is substantively "the PR author recording their own approval" via a technical
  proxy, which the task explicitly forbids me from proposing, and it would make the audit trail
  actively misleading (attributing the decision to `github-actions[bot]` when no independent
  entity actually reviewed anything).
- A dedicated bot/service **account** (a real second GitHub identity, e.g. added as a
  collaborator with its own PAT stored as a secret) would technically satisfy the
  string-inequality checks for real, but it still doesn't supply independent *judgment* unless
  a genuinely separate party controls that identity. If jpapiez alone controls the bot
  account's credential, dispatching-as-bot is exactly as self-attested as dispatching-as-jpapiez
  — just with different words in the login field. It only becomes a real control if a second,
  actual person (or a real automated adjudicator with its own non-bypassable logic) is the one
  who causes that identity to act.
- Genuinely automating the trio review itself as CI logic that autonomously evaluates the diff
  (issue's Option B) *would* supply real independent judgment without a second human — but this
  requires moving Bishop/Hicks/Vasquez from Copilot-CLI-driven sub-agent sessions into
  GitHub-Actions-hosted logic with its own model-API credentials, which is a substantial
  architecture change (new secrets, redesigned review pipeline, rewrites of the squad docs that
  describe them as `task`-tool-dispatched agents) — not a scoped workflow/script fix.

### Conclusion

Every fix that satisfies "the recorder must be a real, independent, non-self-controlled party"
requires either:
- **A genuine second human** with independent judgment and admin rights (issue's Option C) — a
  real organizational/staffing decision, not something Parker can create.
- **A newly provisioned, independently-controlled machine identity** (bot account or GitHub
  App) whose credential is not solely held by jpapiez — creating a new GitHub account/App
  requires interactive signup/registration I cannot perform via CLI/API, and even if created,
  someone other than jpapiez would need to actually control when it acts for the check to mean
  anything.
- **A genuinely automated independent adjudicator** running in CI (issue's Option B done for
  real, not as a relay) — a large architecture change (new LLM API credentials, redesigned
  review pipeline) outside a scoped DevOps fix.

Every option I *can* execute alone collapses to relabeling the same single human's self-report,
which the task explicitly forbids me from shipping as "the fix." Per the task's own escape
hatch, I am stopping here rather than forcing through an inadequate technical fix. No code
changes are being committed for the workflow/verifier/docs; this file documents the
investigation.

## Status

Investigation complete. No PR opened. Reporting back to the requester with the above analysis
and the concrete decision needed (provision a real second human admin, or fund/scope a genuine
automated-adjudicator architecture change) before any further implementation is possible.
