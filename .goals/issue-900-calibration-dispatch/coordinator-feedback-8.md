# Final Acceptance Audit — Iteration 8

## Verdict: FAIL

Most previously reported defects are fixed. Four blockers remain.

1. **Normal terminal completion/failure does not release the queue lease.**
   `PrintJobCompletionService.MarkCurrentJobAsCompletedAsync`,
   `MarkCurrentJobAsFailedAsync`, and orphan-sync terminal paths update job
   status without clearing the matching `PrinterDispatchState.ActiveJobId` /
   `ActiveDispatchAttemptId`. After the first normal print, every later claim
   is permanently `printer_busy_active`. Release only the matching lease and
   close the attempt/ack/audit/outbox in the same terminal transaction. Test
   claim -> backend accepted -> completed/failed -> next claim succeeds.

2. **Ad-hoc lease does not block queue claims.**
   `AcquireAdHocClaimAsync` sets only `ActiveDispatchAttemptId`, while queue
   gate checks that field only for ad-hoc jobs. During bridge/printer-file
   upload a queue job can claim the same printer. Check active attempt
   unconditionally or use the same exclusive lease field. Add ad-hoc-vs-queue
   and queue-vs-ad-hoc concurrent tests.

3. **Ad-hoc claim is weaker and unknown outcomes never reconcile.**
   Missing/stale telemetry currently passes ad-hoc claim. Apply the same
   fail-closed freshness/online/idle gate as queue claims. Reconciler filters
   out `PrintJobId == null`, so unknown ad-hoc attempts pin the printer
   forever. Persist enough identity and reconcile/release/accept ad-hoc
   attempts through adapter state. Test missing/stale telemetry and unknown
   ad-hoc recovery.

4. **Provider and production-call-chain tests do not run in CI.**
   Current CI excludes `DbHeavy` and `Docker`; no workflow provisions
   PostgreSQL/SQL Server or sets provider connection strings. Add a CI job
   with PostgreSQL and SQL Server services (or repository-standard provider
   harness), set the environment variables, apply migrations, and run the
   DbHeavy/Docker provider and production-call-chain tests. The job must fail
   rather than silently return when providers are unavailable. Keep the
   existing fast gate.

Fix all four, run the new CI-equivalent commands locally where available,
push, and keep PR #979 draft against `development`.
