# Current Focus: Issue #708 Backend v3 Review + Lambert Revision Handoff

**Status:** Hicks completed REQUEST_CHANGES verdict; revision reassigned to Lambert

## Context

**Issue:** #708 Backend (APNs, JWT, rate-bucket, attention prefs, capabilities JSON serialization)  
**Reviewer:** Hicks (gpt-5.6-sol/max)  
**Verdict:** REQUEST_CHANGES (5 blockers)  
**Current Owner:** Lambert (revision)  
**Original Author:** Jeff Papiez (locked out for this cycle)

## Hicks Blockers (for this revision)

1. APNs token redaction — slash/query chars not fully masked
2. JWT invalidation regression test — second signing not proven to invalidate token
3. Rate-bucket prune race + hard-coded 5m expiry — concurrent scenarios unvetted
4. Attention preferences — partial reset + toggle mismatch
5. Capabilities JSON casing — non-production serializer options divergence

## Verified ✓

- B3 auth flow  
- Migrations  
- Build success  
- 75 focused tests passing  
- Full suite 3251/3253 (2 unrelated pre-existing failures)

## Next

Fix blockers above. Hicks will re-review after fixes applied.

---

## Previous: OrcaSlicer Sprint

**Status:** Wave 1 + Wave 2 complete; P3 frontend + P5 remaining (deferred for #708 focus)

Completed:
- P1: Slice Jobs Dashboard (SliceJobsPage.tsx) — Ripley
- P4: Job retry, pagination, settings CRUD, E2E tests — Lambert
- Profile parsing improvements + tests — Lambert
- P2: Real-time SignalR job progress hooks — Ripley
- P3 backend: Send-to-printer bridge endpoint — Lambert

Remaining (on hold):
- P3 frontend: "Send to Printer" button + printer selector — Ripley
- P5: First-slice onboarding polish — Ripley
