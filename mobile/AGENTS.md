# Agent Instructions

Use GitHub issues for all task tracking. Create and manage issues via:

```bash
gh issue create --title "Your issue title" --body "Details"
gh issue list
gh issue view <number>
```

For more information, see GitHub CLI documentation: https://cli.github.com/manual/gh_issue

## Session Completion

**When ending a work session**, ensure all work is pushed to remote:

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work via `Closes #N` in PR body
4. **PUSH TO REMOTE** - Commit and push your changes:
   ```bash
   git pull --rebase
   git push
   git status  # MUST show "up to date with origin"
   ```
5. **Verify** - All changes committed AND pushed to remote

**CRITICAL RULES:**
- Work is NOT complete until `git push` succeeds
- NEVER stop before pushing—that leaves work stranded locally
- If push fails, resolve and retry until it succeeds

