# Orchestration Log: Commit Message Scrub Directive Hardening

**Date:** 2026-05-31T16:42Z  
**Coordinator:** Brady  
**Action:** Scribe merged pending commit-message-scrub directive, updated charter with pre-commit gate, and applied gate to this session's commit.

## Summary

Three historical commits on `origin/development` contained "external-reference-app" in their subjects:
- `52174133c`
- `3fe3ed503`
- `96dbcd4aa`

The prior no-external-refs directive (2026-05-31T09:14) was interpreted as covering only issues/PRs/source/comments. Brady clarified that commit messages (subjects, bodies, trailers) are shipping artifacts and must be scrubbed.

## Directive Scope

Forbidden strings (case-insensitive, all branches, all future commits):
- `external-reference-app`
- `external-author`
- `external reference app` (any variant)
- URLs to `[external reference repo]`

Acceptable alternatives:
- "adoption plan" / "Phase N work breakdown"
- "external 3D-printer-management reference adoption"
- Standalone feature description: "g-code preview", "quick-slice UX", "notifications"

**Out of scope (exempt from scrub):** `.squad/` internal memory files (decisions.md, agents/*/history.md, log/, orchestration-log/, decisions-archive). These remain team-private.

**Historical remediation:** The 3 dirty commits on `development` remain as-is; force-push risk exceeds cleanup benefit on a shared integration branch.

## Implementation

- Scribe charter updated with strict pre-commit procedure:
  1. Compose message into temp file
  2. Run grep scrub: `grep -iE 'external-reference-app|external-author|external reference app|github\.com/external-author'`
  3. If found: rewrite before committing
  4. Log gate trigger to orchestration log

- This session's commit passed the gate (0 matches)

## Next Steps

All team members must apply the same pre-commit gate before committing. Coordinator will spot-check PRs for compliance.
