## 2026-05-29 — Bishop PR #316 merged

### What shipped

- Rebasing completed for PR #316 (`fix(api): gate /home endpoints (closes #314, completes #279)`) onto `development`.
- The `/home`, `/homexy`, and `/homez` endpoints still call `GatePrinterControlAsync` before backend commands and preserve the 409 `CommandResult` busy envelope.

### Conflict scope

- Conflict count: 1 file.
- Conflicting file: `src/tests/Farm.Web.Api.Tests/Controllers/PrintersControllerControlGuardsTests.cs`.
- Resolution: union merge keeping base `BackendBusy -> 409` regression tests plus the PR's six `/home` gate tests.

### Validation

- Local focused validation run from `src/`:
  - `dotnet test ./farm-web.sln -c Debug --filter "FullyQualifiedName~PrintersControllerControlGuardsTests"`
- Result on rebased head: `Passed!  - Failed: 0, Passed: 12, Skipped: 0, Total: 12`.
- Note: first attempt hit a recursive `src/api/bin/.../bin/Debug/net10.0` artifact-path failure; `dotnet clean ./farm-web.sln -c Debug` cleared the stale build output and the rerun passed.

### Merge result

- Merge method: squash.
- Merge SHA: `8becf256162ed2b4e14efe9df85cee2d18122426`.
- PR state after merge: closed / merged.
- #314 and #279 did not auto-close via the API merge path, so they were closed manually against the merge SHA.

### Review note

- Formal `APPROVE` review submission was blocked by GitHub's self-review rule because the PR author is `jpapiez`: `Review Can not approve your own pull request`.
