# Copilot Processing

User request: Fix issue #1179 so legacy SQLite pre-step schema-upgrade failures are fatal during startup, while post-migration seeding failures remain non-fatal. Add focused regression coverage, validate, commit/push, obtain Bishop/Hicks/Vasquez consensus, open and verify a PR, and report lifecycle details to Ralph.

## Action plan

- [done] Trace `ProgramHelpers.InitializeDatabaseAsync` and SQLite pre-step execution to identify the exception-classification boundary.
- [done] Implement the smallest root-cause fix so legacy SQLite pre-step failures surface as migration-contract failures.
- [done] Add regression coverage for fatal pre-step failures and preserve the existing non-fatal seeding behavior test.
- [done] Run focused backend build/test validation and fix any failures.
- [todo] Commit and push the completed change with the required co-author trailer.
- [todo] Obtain exact-head Bishop, Hicks, and Vasquez adversarial consensus.
- [todo] Open a non-draft PR to development with `Closes #1179`, verify issue linkage, and report lifecycle state to Ralph.

## Summary

Pending.
