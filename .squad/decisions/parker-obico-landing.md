## Parker Landing Decision — Obico Self-Hosted Contract Fix

**Date:** 2026-03-25T11:55Z  
**Agent:** Parker (DevOps)  
**Commit:** `f6b07d42` on `development`  
**Scope:** Code + tests only; agent history mutations left in-flight

### Staging Strategy

**Why this approach:**
- **Code/tests:** Validated and ready by Dallas (architect), Kane (QA), Lambert (implementation)
- **Agent histories:** Still being mutated by other agents (Dallas, Kane, Lambert); left unstaged to avoid merge conflicts and respect each agent's authority over their own history
- **Squad decisions:** Inbound to inbox from multiple agents; scribe will process together when all are in sync

### Commit Contents

**Modified:**
- `src/api/Controllers/ObicoServerController.cs` (restored imports + GET contract validation)
- `src/infra/Services/FailureDetection/ObicoFailureDetectionService.cs` (upstream GET first + fallback)

**Created:**
- `src/tests/Farm.Web.Api.Tests/Controllers/ObicoServerControllerTests.cs`
- `src/tests/Farm.Web.Api.Tests/Services/FailureDetection/ObicoFailureDetectionServiceTests.cs`

### Pre-Push Verification

- Path casing: OK
- Dotnet format: OK
- Git status: Clean (only code/tests staged)
- Branch: `development`, up-to-date with origin before push

### Push Mechanics

```
git add src/api/Controllers/ObicoServerController.cs
git add src/infra/Services/FailureDetection/ObicoFailureDetectionService.cs
git add src/tests/Farm.Web.Api.Tests/Controllers/ObicoServerControllerTests.cs
git add src/tests/Farm.Web.Api.Tests/Services/FailureDetection/ObicoFailureDetectionServiceTests.cs
git reset .squad/agents/*  # Unstaged other agents' in-flight history mutations
git commit -m "fix: implement Obico self-hosted upstream contract adapter..."
git push origin development  # SUCCESS: 51bea8ba..f6b07d42
```

### Commit Message Format

Included:
- Clear reference to fix scope (upstream GET contract, legacy fallback)
- Issue reference: `[closes PFarm1-obico]`
- Required co-authored-by trailer: `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`

### Team Readiness Summary

**Approved by Dallas:** Contract fix is surgically correct; build passes; tests pass (4/4 Obico-specific)  
**Validated by Kane:** Two-layer coverage complete (service + controller); edge cases covered  
**Implemented by Lambert:** Upstream GET first, legacy fallback, imports restored  

### Observations

- Scribe is actively mutating squad files (.squad/decisions/inbox/, .squad/skills/) in parallel; this is expected and not blocking
- Each agent's history will be individually updated by that agent; Parker did not include them to avoid cross-agent conflicts
- The landed code is clean and decoupled from squad metadata churn
