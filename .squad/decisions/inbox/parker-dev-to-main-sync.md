## Decision Record: dev→main Sync PR — 2026-05-29

**Author:** Parker
**Date:** 2026-05-29
**Status:** ⚠️ PR ready locally, push blocked — needs `workflow` scope

### Summary

Prepared a clean sync of `development` → `main` to pick up 536 commits including Dependabot security fixes for 49 flagged vulnerabilities (2 critical, 15 high, 31 moderate, 1 low).

### What Was Accomplished

- **Branch created:** `sync/dev-to-main-2026-05-29` off `origin/main`
- **Commits merged:** 536 (all of development since the last main sync)
- **Commit SHA:** `d4d8b4a1e`
- **Forbidden paths stripped from index:** All `.squad/`, `.ai-team/`, `.ai-team-templates/`, `team-docs/`, `docs/proposals/` — confirmed 0 forbidden paths in staged index
- **Conflicts resolved (16):**
  - `.squad/*` modify/delete conflicts (≈60 files) — resolved by `git rm --cached`
  - `.github/fact-checker-charter.md`, `.github/loop.md`, `.github/squad.agent.md.template` — git directory-rename heuristic misfire; removed
  - `.gitignore`, 5 `.github/workflows/squad-*.yml`, `mobile/scripts/release-beta.sh`, `scripts/sync-monorepo-version.sh`, 5 `.csproj` files — resolved using development's version

### Blocker

Push rejected: `refusing to allow an OAuth App to create or update workflow ... without 'workflow' scope`.

**Resolution required:** Jeff must run `gh auth refresh --scopes workflow` (browser one-time code), then run:
```bash
cd /Users/jpapiez/s/PFarm1
git push -u origin sync/dev-to-main-2026-05-29
gh pr create --base main --head sync/dev-to-main-2026-05-29 \
  --title "chore: sync development → main (Dependabot + accumulated)" \
  --body "Brings main current with development (536 commits). Picks up Dependabot security fixes for the 49 vulnerabilities flagged on the default branch.

Squad metadata (.squad/, .ai-team/, team-docs/, docs/proposals/) explicitly excluded per repo policy. The squad-main-guard.yml workflow will verify."
```

The local branch `sync/dev-to-main-2026-05-29` is ready to push — no further merge or conflict resolution needed.

### CI Expectation

- `squad-main-guard.yml` — should PASS (0 forbidden paths in index, verified)
- All other checks (build, tests, compose validation) — expected green (same codebase as development which passed CI)
