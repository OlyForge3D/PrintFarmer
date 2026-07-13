# Offline Write Replay (Idempotency-Key)

Backend for the operator "offline tolerance and write queue" feature (epic
[#705](https://github.com/OlyForge3D/PFarm/issues/705), feature
[#715](https://github.com/OlyForge3D/PFarm/issues/715)). Lets a client safely
retry a mutating request — after a dropped connection, an app relaunch, or an
offline period — without applying it twice, by tagging the request with a stable
`Idempotency-Key` header.

## How it works

A request to a gated write endpoint may carry an `Idempotency-Key` header. The
first time the server sees a given key it executes the mutation, captures the
response, and stores it. A later request with the **same key, same user, same
resolved path, and same body** replays the stored response instead of executing
the mutation again.

- **Filter:** `Farm.Web.Api.Infrastructure.Idempotency.IdempotencyFilter`
  (an `IAsyncResourceFilter` applied via `[Idempotent(routeKey)]`).
- **Store:** `Farm.Infrastructure.Services.Idempotency.IdempotencyStore`
  (EF Core; composite unique index on `(UserId, RouteKey, IdempotencyKey)`).
- **Cleanup:** `IdempotencyRecordCleanupService` prunes records past the
  retention window on a background sweep.

### Identity

The stored identity is **not** just the route template. The filter folds the
*resolved request path* into both the `RouteKey` column and the request hash:

```
EffectiveRouteKey = "{IdempotencyRouteKeys.<Constant>}|{HttpContext.Request.Path}"
```

This prevents one key from silently replaying across different `{id}`/`{sku}`/
`{toolheadIndex}` values (which, for empty-body actions such as task-complete,
would otherwise cause silent data loss). Keys are per-user: two users may reuse
the same key without colliding.

### Gated routes

| Route key constant | Endpoint |
|---|---|
| `PartsInventoryAdjust` | `POST /api/parts-inventory/{sku}/adjust` |
| `JobQueueHarvest` | `POST /api/job-queue/{id}/harvest` |
| `TaskComplete` | `POST /api/tasks/{id}/complete` |
| `PrinterToolheadSpoolBind` | `PUT /api/printers/{id}/toolheads/{toolheadIndex}/spool` |

### Response codes

| Situation | Result |
|---|---|
| First request for a key | Mutation executes; response captured; `Idempotent-Replay` header absent |
| Exact replay (same key + path + body) | Stored response replayed with `Idempotent-Replay: true` |
| Same key + path, different body | `409 Conflict` (`idempotencyKeyConflict`) |
| Prior request with the key still in-flight | `409 Conflict` (`idempotencyKeyInProgress`), with a `Retry-After: 5` header and a `retryAfterSeconds` hint so the client backs off |
| Malformed / multi-valued key header | `400 Bad Request` (`idempotencyKeyMalformed`) |
| Body larger than 1 MiB | `413 Payload Too Large` (`idempotencyRequestTooLarge`) |

Retention is **7 days** from a record's immutable `CreatedAt`. The boundary is
exclusive: a record whose age equals the window is still valid; expired records
are ignored on read and pruned in the background. A `Processing` record whose
owning request appears to have died (older than the configurable
`IdempotencyOptions.ProcessingStaleness`, default 5 minutes) is reclaimed so a
crashed request cannot wedge a key until it ages out.

## ON / OFF semantics

The feature is gated by the `offlineWriteReplayEnabled` operator flag (see
[OPERATOR_FEATURE_GATES.md](OPERATOR_FEATURE_GATES.md), default `true`).

- **ON:** the filter honours the `Idempotency-Key` header as described above.
- **OFF:** the filter is a deliberate no-op — the header is ignored, nothing is
  persisted, and the request executes directly online. Existing stored records
  are **not** deleted on disable; they simply age out under the retention window.

Because the disabled path is plain direct-online execution, disabling the
feature never queues, drops, or double-applies a write on its own.

## Flag-flip caveat

An `ON → OFF` or `OFF → ON` transition **with the same key in flight** is
inherent boundary behavior, not a bug:

- A key first seen while ON, then retried while OFF, executes online again (the
  OFF path does not consult the store).
- A key used while OFF, then retried while ON, is treated as new (no record was
  written while OFF).

This is safe because **direct-online writes carry their own durable domain
idempotency**, independent of this feature:

- **Adjust:** natural idempotency on `PartInventoryAdjustment.OperationKey`.
- **Harvest:** the permanent harvest guard — the atomic `PrintJob.HarvestedAt`
  claim plus `PartHarvestOutputSnapshot` uniqueness — prevents a job from being
  harvested twice **even after the idempotency record has expired and been
  pruned** (see `HarvestPermanenceIntegrationTests`).
- **Spool-bind:** re-binding the same spool to the same `(printer, toolhead)` slot
  is a no-op short-circuit — it returns the existing binding unchanged (`200 OK`)
  without churning `UpdatedAt`, writing a `FilamentSwapOverride` audit row, or
  broadcasting a coverage change. A replayed or re-delivered bind therefore cannot
  produce duplicate state even when the flag is OFF or mid-transition.

The `Idempotency-Key` layer is therefore a convenience/latency optimization over
the durable domain guards, not the sole line of defense against double-apply.

## Failure handling around the mutation boundary

The filter's abandon behavior is deliberately **asymmetric** around the mutation:

- **Before/at the mutation** — if the action pipeline throws, or surfaces a 5xx /
  handled exception, the mutation has not been recorded and controllers run their
  own transactions, so the `Processing` row is **abandoned** (deleted) and the
  client may retry the same key immediately.
- **After a successful mutation** — if flushing the buffered response or writing
  the completion record fails, the mutation has already been applied. Abandoning
  here would delete the `Processing` row and let a retry re-execute the
  already-applied write with no replay protection (a double-execution window). The
  row is therefore **left in place to expire naturally**: retries observe
  `409 InProgress` until the `ProcessingStaleness` reclaim frees the key, by which
  point the natural domain guards above plus a fresh execution close the loop. The
  original exception is rethrown so the failure still surfaces.

Under sustained contention, a caller that loses the insert race to a winner that
then vanishes (or is stale) retries the insert a bounded number of times and, if
it keeps losing, returns `409 InProgress` rather than executing unprotected.

## Known follow-ups (out of #715 scope)

- **Location/ETag replay fidelity:** replays currently reproduce the stored status
  code, content type, and body, but do not yet re-emit resource-specific
  `Location` / `ETag` headers on the replayed response. This is an intentional
  follow-up tracked beyond #715.
