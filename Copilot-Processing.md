# Request

Address issue #2757 automatic scheduling and production host-update adapter wiring without bypassing manual readiness/recovery gates.

## Plan

- [done] Inspect current host-update contracts, policy services, scheduler registration, adapters, and focused tests.
- [done] Identify the smallest safe scheduling/adapter gap and implement explicit prerequisite enforcement.
- [done] Add or update focused backend tests for scheduling and adapter policy behavior.
- [done] Run focused validation, commit changes, and push the branch.
- [done] Record final summary and dependencies for coordinating agents.
- [done] Revise scheduler lifecycle, replay commit timing, cancellation reachability, policy TOCTOU validation, and status diagnostics after unanimous review.
- [done] Re-run focused infrastructure and system-info integration validation.
- [done] Correct canonical policy fingerprint revalidation, route snapshot coverage, hosted-service gating, cancellation latching, adapter naming, and production jitter after second review.
- [done] Validate the final route snapshot, host-update tests, API contract tests, and infrastructure build.
- [done] Make the automation policy repository mandatory for executor construction and cover both matching-admit and drift-reject paths.
- [done] Add generation-keyed pre-arm cancellation, bounded scheduler disposal, installation-seeded jitter, advisory replay documentation, response metadata, and administration constructor coverage.
- [done] Add adapter pre-arm and installation-jitter regression tests; focused infrastructure, administration, API startup, and solution-build validation passed.
- [done] Complete the post-merge full Farm.Web.Api.Tests suite with bounded hang diagnostics.

## Summary

Made the scheduler singleton-backed so poll/backoff state persists across hosted ticks, while each
execution resolves and disposes its scoped production adapter before the cadence delay. Replay now
reserves before execution and commits only after success. Added a singleton safe-checkpoint
cancellation bridge and admin cancellation endpoint, plus policy revision/fingerprint validation
after the execution lock is acquired. Status projection preserves concrete fail-closed reasons and
the existing system-info contract. Focused infrastructure tests passed (414), the system-info
integration test passed, and the API build passed with zero warnings/errors.

Final review revision also aligns executor policy fingerprints with the scheduler projection,
updates the cancellation route snapshot, gates hosted scheduling on host-state availability,
preserves status during early polls, uses installation-seeded production jitter, and removes
agent-cast names from the adapter. Host-update tests passed (402), route/system-info API
contract tests passed (22), and the infrastructure test build passed with zero warnings/errors.
The full infrastructure suite remains blocked only by six provider tests requiring unavailable
PostgreSQL/SQL Server connection strings.

Follow-up hardening made the executor policy repository mandatory rather than optional, so
tests cannot silently bypass the production policy gate. Matching-policy execution and
fingerprint drift rejection are now covered explicitly. The API build completed with zero
warnings/errors, 403 host-update tests passed, and the 22 route/SystemInfo API contract tests
passed.

Round-4 hardening adds generation-keyed cancellation pre-arming so a cancellation arriving
between active-generation publication and adapter registration is delivered at the first safe
checkpoint. Repeated cancellation remains idempotent, scheduler disposal bounds its gate wait,
and production jitter includes a stable installation seed. The replay Reserve path now documents
its advisory semantics and durable exclusion dependencies, the cancellation route advertises 409,
and unprovisioned startup uses a fallback jitter seed without requiring HostStatePath.
Validation: solution build passed with 0 warnings/errors; 405 host-update infrastructure tests,
157 administration tests, and 7 API startup/DI tests passed. The complete Farm.Web.Api.Tests run
completed after the merged development tree and passed 3,648 tests with 0 failures or skips.

Round-5 fixes move installation identity to a top-level persisted helper, make adapter pre-arm
state lifecycle-locked and explicitly capped, and bound adapter disposal draining with warning
logging. Scheduler cancellation wiring now uses PreArmCancellation for both bridge and direct
executor paths. The merged #2787 host-executable fail-closed assertions remain present.
Validation: solution build passed with 0 warnings/errors; 423 host-update infrastructure tests,
157 administration tests, and the full Farm.Web.Api.Tests suite passed (3,648 passed, 0 failed,
0 skipped). The round-5 cancellation regression test is
`CancellationBridge_UsesPreArmForActiveSchedulerGeneration`.
