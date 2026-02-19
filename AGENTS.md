# Agent Instructions

This project uses **bd** (beads) for issue tracking. Run `bd onboard` to get started.

## Installation

If `bd` is not installed:

```bash
curl -fsSL https://raw.githubusercontent.com/steveyegge/beads/main/scripts/install.sh | bash
```

Then ensure it's on your PATH (add to `~/.bashrc`):

```bash
export PATH="$PATH:$HOME/.local/bin"
```

## Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --status in_progress  # Claim work
bd close <id>         # Complete work
bd sync               # Sync with git
```

## Landing the Plane (Session Completion)

**When ending a work session**, you MUST complete ALL steps below. Work is NOT complete until `git push` succeeds.

**MANDATORY WORKFLOW:**

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **PUSH TO REMOTE** - This is MANDATORY:
   ```bash
   bd sync                  # writes bead closures to .beads/issues.jsonl
   git add .beads/issues.jsonl
   git commit -m "chore: sync beads" --allow-empty  # if no code changes pending
   git pull --rebase        # incorporate any remote changes
   git push
   git status               # MUST show "up to date with origin"
   ```
5. **Clean up** - Clear stashes, prune remote branches
6. **Verify** - All changes committed AND pushed
7. **Hand off** - Provide context for next session

**CRITICAL RULES:**
- Work is NOT complete until `git push` succeeds
- NEVER stop before pushing - that leaves work stranded locally
- NEVER say "ready to push when you are" - YOU must push
- If push fails, resolve and retry until it succeeds
- **Bead closure and code changes MUST land in the same commit.** Always close the bead (`bd close <id>`), run `bd sync`, then stage both the changed code files AND `.beads/issues.jsonl` together before committing. Commit message must reference the bead ID (e.g. `[closes PFarm1-xxx]`). Never commit code changes without including the updated `.beads/issues.jsonl`.

