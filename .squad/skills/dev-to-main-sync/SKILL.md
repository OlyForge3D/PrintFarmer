---
name: "dev-to-main-sync"
description: "Open a clean PR syncing development → main, stripping all .squad/ metadata. Use when development is ahead of main and you need to bring main current (Dependabot fixes, accumulated work, etc.)."
domain: "git, release, ci"
confidence: "medium"
source: "earned — proven once on 2026-05-29"
---

## Context

`main` is the public-facing default branch. `.squad/`, `.ai-team/`, `.ai-team-templates/`, `team-docs/`, and `docs/proposals/` **must never land on main** per repo policy (enforced by `squad-main-guard.yml`). `development` accumulates these files over time, so a naive merge would add hundreds of forbidden files.

This skill opens a **PR** (not a direct merge). The user reviews and merges manually.

## Prerequisites

- Clean working tree on `development`
- Authenticated as `jpapiez` — run `gh auth status` to confirm
- Token must have **`workflow` scope** — this is the most common blocker. Check with `gh auth status`; if `workflow` is missing, run `gh auth refresh --scopes workflow` before starting. Without it, the push will be rejected.

## Patterns

### Step 1 — Verify state

```bash
cd /Users/jpapiez/s/PFarm1
gh auth status                    # confirm authenticated, confirm 'workflow' scope present
git fetch origin
git log --oneline origin/main..origin/development | wc -l   # confirm expected commit count
git status                        # must be clean
```

### Step 2 — Create sync branch off main

```bash
git checkout origin/main -b sync/dev-to-main-YYYY-MM-DD
```

### Step 3 — Merge development (no-commit)

```bash
git merge --no-commit --no-ff origin/development
```

Expect many `CONFLICT (modify/delete)` entries for `.squad/*` — these are normal and will be stripped.

### Step 4 — Resolve conflicts

**`.squad/*` modify/delete conflicts:** Skip — handled by the strip step below.

**File-location conflicts for `.github/fact-checker-charter.md` etc.:** Git's directory-rename heuristic misfires because `.squad/templates/` looks like it was renamed to `.github/`. These entries are squad metadata and must be removed.

**Application code conflicts** (`.csproj`, `.gitignore`, scripts, workflows): Use development's version — it is the source of truth.

```bash
# For each real code conflict file:
git checkout --theirs <file>
git add <file>
```

### Step 5 — Strip forbidden paths

```bash
# From index
git rm -rf --cached --ignore-unmatch .squad/ .ai-team/ .ai-team-templates/ team-docs/ docs/proposals/

# Also remove any misrouted .github/ squad templates from index and disk
git rm --cached --ignore-unmatch .github/fact-checker-charter.md .github/loop.md .github/squad.agent.md.template
rm -f .github/fact-checker-charter.md .github/loop.md .github/squad.agent.md.template

# Remove from working tree
rm -rf .squad .ai-team .ai-team-templates team-docs docs/proposals
```

### Step 6 — Verify clean index

```bash
git diff --name-only --diff-filter=U | wc -l   # must be 0 (no unresolved conflicts)
git diff --cached --name-only | grep -E "^\.squad/|^\.ai-team/|^team-docs/|^docs/proposals/" | wc -l  # must be 0
```

### Step 7 — Commit

```bash
git commit -m "chore: sync development → main (Dependabot fixes + accumulated work)

{N} commits from development merged into main. Squad metadata
(.squad/, .ai-team/, team-docs/, docs/proposals/) explicitly excluded
per repo policy (enforced by squad-main-guard.yml).

Picks up Dependabot security fixes for the {N} vulnerabilities flagged
on the default branch.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Step 8 — Push and open PR

```bash
git push -u origin sync/dev-to-main-YYYY-MM-DD

gh pr create --base main --head sync/dev-to-main-YYYY-MM-DD \
  --title "chore: sync development → main (Dependabot + accumulated)" \
  --body "Brings main current with development ({N} commits). Picks up Dependabot security fixes for the {N} vulnerabilities flagged on the default branch.

Squad metadata (.squad/, .ai-team/, team-docs/, docs/proposals/) explicitly excluded per repo policy. The squad-main-guard.yml workflow will verify."
```

### Step 9 — Poll CI (up to 20 min)

```bash
gh pr view <PR#> --json statusCheckRollup,mergeable
```

`squad-main-guard.yml` **must pass**. If it fails, the index still contains forbidden paths — re-investigate with `git diff --cached --name-only | grep "^\.squad/"`.

Do **not** merge. Stop after PR is open and CI is green; user reviews and merges.

## Anti-Patterns

- **Don't skip the `workflow` scope check.** The push will succeed for all files except `.github/workflows/` without it — but workflow-touching merges (which all dev→main syncs are) will fail at the remote.
- **Don't `git add .` before stripping.** This would stage `.squad/` files into the commit. Always strip first, then verify.
- **Don't use `git checkout --ours` for code conflicts.** `ours` = main (the older, behind branch). `theirs` = development (the source of truth).
- **Don't merge directly to main.** PR → CI → user review is the required path.
- **Don't re-run the merge if the branch already has the merge commit locally.** If you hit a push blocker, just re-push after fixing the auth issue — the commit is already correct.

## Examples

Commit count check:
```bash
git log --oneline origin/main..origin/development | wc -l
# 536
```

Forbidden-path verification (must return 0):
```bash
git diff --cached --name-only | grep -E "^\.squad/|^\.ai-team/|^team-docs/|^docs/proposals/" | wc -l
# 0
```
