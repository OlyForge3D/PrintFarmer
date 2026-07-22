# History

## Core Context

PrintFarmer is a .NET 10 and React 19 application for 3D-printer farm management. The current work is requested by jpapiez.

## Learnings

- Issue #842 (thumbnail replacement) completed via PR #856 (commit `32659d2db`).
- Kaylee implemented atomic PUT endpoint with immutable PNG candidates, owner-or-admin auth, optional If-Match ETag, migrations (PG+SQL Server), and full test matrix.
- Zoe approved after verifying endpoint, auth, ETag, atomicity, rollback, cleanup, migrations (schema consistent), routing (split nginx assertions accurate), and test coverage.
- Pre-existing MySQL deployment assertion failure is unrelated to #842 (no deploy/ or generator changes, cosmetic trailer name variant).
- Stack dependencies: PR #850 → #854 → #856 (all OPEN; #841 deferred base).
