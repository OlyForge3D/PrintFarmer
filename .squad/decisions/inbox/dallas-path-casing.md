# Decision: Always grep for path string references when fixing casing

**Date:** 2025-07-18
**Author:** Dallas
**Status:** Accepted

## Context
When fixing the `src/api/data/` → `src/api/Data/` casing mismatch, the git index fix alone would have been insufficient. The `.csproj` Include globs and runtime `Path.Combine()` calls also used the old lowercase path, which would fail silently on Linux.

## Decision
When fixing directory casing mismatches, always search the entire codebase for string references to the old path (in `.csproj` files, C# source, config files, scripts, etc.) — not just the git index. A path casing fix is not complete until all references match the canonical casing.

## Consequences
- Slightly more investigation time per casing fix
- Prevents silent failures on case-sensitive CI/Docker environments
- Pre-commit hook (`enforce-path-casing.yml`) catches git index issues but not code-level path strings
