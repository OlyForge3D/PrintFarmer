# Decision: Pre-commit hook architecture

**Date:** 2025-07-26
**Author:** Lambert (Backend)
**Status:** Implemented

## Context

CI workflows (`ci-lint.yml`, `yamllint.yml`, `enforce-path-casing.yml`) catch lint issues on the server but feedback is slow (push → wait for CI). Developers need fast local feedback before committing.

## Decision

Created `.githooks/` with a portable pre-commit hook that mirrors CI checks on staged files only. Each check is independently skippable if its tool isn't installed, so the hook never blocks developers who haven't set up optional tooling.

## Checks implemented

| Check | Tool | CI mirror |
|-------|------|-----------|
| Shell lint | shellcheck | ci-lint.yml |
| YAML lint | yamllint | yamllint.yml |
| Path casing | node scripts/check-path-casing.js | enforce-path-casing.yml |
| TypeScript lint | npx eslint | React dev standards |
| C# format | dotnet format --verify-no-changes | .NET format standards |

## Key choices

- **`core.hooksPath` over symlinks** — modern git feature, no manual linking, works cross-platform
- **Opt-in activation** — developers run `.githooks/setup.sh` once; not forced on clone
- **CI stays in place** — hooks are fast feedback, CI is enforcement. Both coexist.
- **Staged-only scope** — only lint files being committed, not the whole repo
- **Graceful degradation** — missing tools produce warnings, not failures

## Noted issues

- Pre-existing path casing mismatch: `src/api/data/` in git vs `src/api/Data/` on disk. Hook correctly detects it. Separate fix needed.
