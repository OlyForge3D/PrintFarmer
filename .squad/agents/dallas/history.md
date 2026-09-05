# Dallas History


## Core Context

Dallas is the project lead & product architect. Key contributions:
- Feature prioritization & architecture oversight
- Location hierarchy system design (phase 1 approved)
- Auto-dispatch phase 1 & 2 architecture
- Competitive analysis & market differentiation
- Team coordination & decision governance
- Failure detection & UI polish sessions (2026-03-25)
- Auto-dispatch naming cleanup & consistency (2026-03-25)

Early entries (pre-2026-03-25) summarized for maintainability. See decisions-archive.md for historical context.

---

## Learnings — 2026-07-13 Hicks v5 Independent Remediation of PR #750 / issue #708

**Scope:** Reviewer Rejection Protocol reassigned the v5 revision of the F3 native push backend to Dallas. Landed one descendant commit `45474b8b9` on top of `4227f5141` (v5 baseline) that fixes both Hicks blockers.

### Blockers resolved

- **H1-v5 (High) — global push opt-out bypass.** `NativePushDispatcher` was only consulting per-attention-kind push toggles, so a preserved `PushOnPrinterFailure=true` could sneak past a user who had set `EnablePushNotifications=false`. Added a master gate that increments `SkippedCategoryOptOut` and skips dispatch when the persisted row exists AND its master push flag is false. A missing row still falls back to the CLR default (true) so pre-#708 opt-in behaviour is preserved for users who never touched the preference UI.
- **H1-v5 (High) — write projection.** The controller was OR-ing only the four legacy job rows into `EnablePushNotifications`, which silently reset the master flag to `false` when a user disabled every job row even though attention rows were still active. Relocated derivation to `NotificationService.ApplyMasterFlagsFromMatrix` (OR of all nine event×channel rows) and made it run inside the same tracked read/write as the row itself. Result is mirrored back to the caller DTO via `MirrorAttentionAndMasterFlags` so the controller response body reflects reality.
- **H2-v5 (Medium) — stale legacy snapshot race.** Controller was doing an `AsNoTracking` pre-read then handing the transient DTO to the service, which did its own tracked read/write. A concurrent newer-client attention update between the two reads could be overwritten by the legacy PUT's stale snapshot. Moved attention-row preservation into `NotificationService.UpdatePreferencesAsync` behind a new `preserveAttentionFields` parameter; controller now signals whether the incoming matrix addressed any attention row and stops touching the persisted row up-front.

### Key discoveries

- The interface signature change is safe because the only non-controller caller (`NotificationServiceDeliveryTests.cs:519`) doesn't set the parameter and receives the default `false`, preserving pre-fix semantics for modern requests.
- Master-flag derivation is now the service's single source of truth. This is a small but real architectural shift — no other consumer computes those flags any more. Worth flagging in decisions.md if it comes up in future reviews.
- EF Core in-memory (`UseInMemoryDatabase(name)`) shares state across DbContext instances sharing the same name, which is exactly what the two-context concurrency regression test needs — no external server required.
- The two full-suite failures (`FilamentCoverageControllerTests.GetFleet_LargeFleet_CompletesWithinReasonableBudget` — 15s budget missed at 29.9s; `PrintersServiceSwapBindingTests.GuidedConcurrentFirstGateBinds…` — SQLite file locked by another process during teardown) are demonstrably environmental/pre-existing and unrelated to notification code paths.

### Gates

- `dotnet build ./farm-web.sln -c Debug` → 0 errors, 0 warnings
- Focused notification tests (NativePush | NotificationPreferences | NotificationService) → 139 / 139 pass
- Full `Farm.Web.Api.Tests` suite → 3282 pass, 2 pre-existing environmental failures unrelated to notifications
- `dotnet format --verify-no-changes` flagged a pre-existing CHARSET encoding issue on `NativePushDispatcherTests.cs` (confirmed present at baseline `4227f5141`); other four edited files are format-clean
- Push confirmed both anchors ancestors of remote head `45474b8b9`: `4227f5141` (exit 0) and `6ce67c89e` (exit 0)

### Coordinator notes

- PR #750 remains draft; trio review is coordinator's next step per protocol
- Contract untouched: capabilities endpoint, nine PascalCase enum tokens, camelCase DTO properties, unknown-token → 400, nine rows materialized with the expected attention defaults

## 2026-09-04 — #2364 DateFormatter spike: analysis gate closed (no implementation)

Ran the analysis gate for mobile perf spike #2364 after six rounds blocked on farm
workload. Made the call on available evidence rather than filing a seventh BLOCKED
report — the standing gate required physically starting a print, which no analysis
session can do.

Verdict: **implementation NOT warranted.** Closed #2364 as measured/not-warranted, no
child issue. The decisive findings were workload-independent, so a further Instruments
run could not have changed the outcome:

- `etaFormatted` has **zero call sites** in the whole iOS target — 2 of the 4 flagged
  constructions are unreachable dead code.
- `shortTimeFormatted` has 1 doubly-conditional call site; its symbol family logged
  0 samples across all 192.6 s of measurement.
- `relativeFormatted` (9 of 10 call sites, the only measured cost at 0.0101 %) **cannot
  be hoisted as the issue proposed** — proved by compiling against Swift 6.0: a
  nonisolated global `RelativeDateTimeFormatter` is inferred `@MainActor` and rejected
  from `extension Date`. This corrected a prior round's answer that would have sent an
  implementer into a wall.

Set a numeric, two-sided threshold (T1 ≥ 1.0 % main-thread CPU; T2 any hitch or
>250 ms microhang) so the decision is falsifiable and re-openable, and recorded both
"Risk" correctness questions as decided.

Lesson worth keeping: before commissioning a perf fix, check that the code is
*reachable* and that the proposed fix *compiles*. Two greps and one `swiftc -typecheck`
settled what six measurement rounds could not.

Decision logged: `.squad/decisions/inbox/dallas-2364-dateformatter-gate.md`
Artifact: https://github.com/OlyForge3D/PrintFarmer/issues/2364#issuecomment-5547770003
