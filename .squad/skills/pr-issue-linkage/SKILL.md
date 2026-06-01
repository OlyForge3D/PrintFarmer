---
name: "PR Issue Linkage"
description: "GitHub auto-close syntax for PRs and issue linking"
domain: "pr-workflow"
confidence: "medium"
source: "earned from session 2026-05-31: 17 merged PRs with 0 auto-closes due to incorrect syntax"
---

## Context

When opening a pull request, builders must properly link GitHub issues so GitHub automatically closes them when the PR merges. The only syntax GitHub recognizes for auto-close is `Closes #N`, `Fixes #N`, or `Resolves #N` in the PR body.

## Patterns

**What Works (GitHub auto-closes):**
- `Closes #350` in PR body
- `Fixes #350` in PR body
- `Resolves #350` in PR body
- Multiple issues: one per line
  ```
  Closes #350
  Closes #351
  Fixes #352
  ```

**What Doesn't Work (No auto-close):**
- Parenthetical in title: `feat(x): thing (#350)` — GitHub sees this as a number, not a closing ref
- Bead-style footer: `[closes PFarm1-350]` — legacy bead syntax, GitHub ignores
- `relates to #350` — GitHub sees this as informational, not a close directive
- Closing ref in commit message instead of PR body
- Uppercase variants like `CLOSES #N` — GitHub is case-insensitive but some CI systems expect lowercase

**Verification:**
```bash
gh pr view <number> --json closingIssuesReferences
```

If the output includes the issue number(s), GitHub will auto-close them on merge. If empty, the link didn't register.

## Examples

**Correct builder flow:**
```bash
# Create feature branch and commit work
git checkout -b feat/printables-import
git commit -m "Implement printables import"

# Open PR WITH issue links in body
gh pr create \
  --title "feat(models): Printables 2-step import modal" \
  --body "## Summary
Adds two-step modal for importing printables.

## Linked issues
Closes #350
Closes #351

## Test plan
- Manual test in dev
- Run npm run test:run
"

# Verify auto-close will work
gh pr view <num> --json closingIssuesReferences
```

**Incorrect builder flow (will NOT auto-close):**
```bash
# ❌ Wrong: issue number only in title, not in body
gh pr create \
  --title "feat(models): Printables import (#350)" \
  --body "Adds printables import" \
  # GitHub won't auto-close because body has no Closes/Fixes/Resolves
```

## Anti-Patterns

- **Parenthetical refs in title only** — Builders assume `(#350)` in the title will trigger auto-close. It won't. Must be in body.
- **Bead-style legacy syntax** — `[closes PFarm1-350]` was used in legacy issue tracking. GitHub doesn't recognize this.
- **Missing issue links entirely** — Builders open PR without mentioning any issue, then expect reviewers to find and link manually.
- **Relates to vs Closes** — Using `relates to #350` instead of `Closes #350`. Different semantics; only Closes/Fixes/Resolves auto-close.

## Reviewer Gate

When reviewing a PR before merge, check:
```bash
gh pr view <num> --json closingIssuesReferences
```

If the PR links to an issue but this command returns empty, **REJECT the PR** and ask the builder to update the PR body with `Closes #N`.

**Rejection language:**
> PR body is missing `Closes #N` for issue #350. Without this, the issue won't auto-close when the PR merges. Update the PR body to include `Closes #350`, then I'll re-review.

## Recovery (Bulk Close After Fact)

If a PR merges without proper issue linkage and issues remain open, bulk-close via:
```bash
gh issue close 350 -c "Resolved by #<merged-PR-number>"
gh issue close 351 -c "Resolved by #<merged-PR-number>"
```

This adds a comment linking the issue to the PR and marks it closed.
