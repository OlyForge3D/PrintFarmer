# Coordinator Continuation — Iteration 5

## Verdict: INCOMPLETE

Do not request acceptance review. Builder iteration 5 stopped at partial
progress and explicitly left contract items unresolved.

## Immediate Corrections

1. `Interlocked.Increment` in a process singleton is not a database-backed
   cross-process monotonic allocator. Multiple API instances start from the
   same observed max and can collide; the unique index then converts durable
   work into failed transactions. Replace it with provider-correct database
   identity/sequence generation or a transactionally fenced allocator row
   updated atomically in the same transaction. Prove concurrent producers
   across separate service providers/DbContexts.

2. Marking stale attempts `RequiresReconciliation` and logging is not real
   authoritative backend reconciliation. Persist backend command/job
   identity, query the actual adapter/plugin, distinguish accepted/active/
   absent/rejected/unknown, and atomically resolve attempt/job/lease/outbox.
   Backend-specific adapters already exist and must be integrated; this is
   explicitly in issue #900, not a separate concern.

3. PostgreSQL and SQL Server validation is not optional. Docker availability
   is an environment blocker only for local execution, not permission to omit
   code or provider tests. Add provider-harness tests following repository
   conventions so CI executes migrated PostgreSQL/SQL Server and SQLite
   fencing/sequence/idempotency paths. Validate generated migrations and
   snapshots locally even when containers are unavailable.

4. Re-read every unresolved item in `coordinator-feedback-3.md` and the
   iteration-5 coordinator message. Do not assume a grep proves production
   routing. Provide concrete call-chain tests for bridge/printer-file/
   scheduler/generic/Desktop paths, lifecycle cleanup, canonical creation,
   hard filament/capabilities/hashes/lineage, priority, resource ownership/
   audit/events, complete ETags/If-Match, invalidation, and the mandated
   provider/race/crash/security matrix.

5. The durable command consumer must use database atomic conditional leasing
   that prevents two processes from acquiring the same row, not a
   read-modify-save race. It must distinguish known pre-start failure from
   timeout-after-send Unknown, preserve lease on Unknown, and reconcile
   before retry. At-least-once command pickup must yield exactly-once start
   effect through fencing and stable command identity.

6. Do not report environmental or architectural deferrals for issue-required
   behavior. Continue Builder iterations until all criteria are implemented
   and locally validated as far as the environment permits.
