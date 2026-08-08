# Slicer Runtime Settings

This document describes runtime configuration options for slicer workers and the admin UI for modifying them.

## Overview

PrintFarmer supports a distributed slicer job queue where one or more workers perform slicing (PrusaSlicer, OrcaSlicer, etc.). Some runtime behavior (retry/backoff, jitter, per-engine executable paths and args) can be tuned at runtime via the Admin UI or persisted to the database.

## Admin UI

- Location: **Admin Settings → Slicing → Defaults**
  (`/admin/settings?tab=slicing&sub=defaults`, protected to `farm_admin` role).
  The metadata-driven form renders the `Slicer` settings section from the
  slicer module.
- Settings surfaced:
  - Enabled: toggle the local worker on/off.
  - Per-engine executable path: absolute path or command for PrusaSlicer/OrcaSlicer/etc.
  - Per-engine args template: template string using placeholders `{input}` and `{output}` (and `{config}` for engines that support a generated config file).
  - Retry jitter percent (`Retry jitter percent`): a numeric percentage (0.0–100.0) applied as ± jitter to requeue backoff delays when transient failures occur.

Validation is applied on the client (number range) and server (0..100 enforced).

## Persistence

Settings are persisted to the `SlicerSettings` table in the application database. The schema defaults to a JitterPercent value of 15.0 when not set.

## How the worker uses jitter

When a worker decides to requeue a failed job, a base retry delay is computed (exponential backoff). The configured jitter percent is applied as a ± fraction to that delay to avoid thundering-herd retries across many workers:

  scheduledDelay = baseDelay ± (baseDelay * jitterPercent / 100)

The admin UI controls the global `JitterPercent` used by the worker. If the DB value is 0.0 the worker falls back to the static worker config default.

## Runtime configuration sources

- Admin UI (preferred for operations): persisted to DB via the metadata-driven
  `POST /api/settings/Slicer` endpoint, or the dedicated
  `PUT /api/admin/slicer/settings` endpoint (both `farm_admin`-only). Read back
  with `GET /api/settings/Slicer` or `GET /api/admin/slicer/settings`.
- `appsettings.json` and environment variable `SlicerWorker:JitterPercent` are used for default initial values on startup

## Queue metric semantics

Queue statistics are calculated separately for each slicer engine. Legacy jobs
without an exact canonical engine name are reported as OrcaSlicer jobs.

- `activeWorkers` counts registered workers that advertise the engine, are
  enabled, have a heartbeat inside the configured
  `StaleWorkerCleanup:StaleAfterMinutes` window, and are `Online` or `Busy`.
  Draining, offline, errored, disabled, missing-heartbeat, and stale workers are
  excluded.
- `averageProcessingTimeSeconds` is the arithmetic mean duration of completed
  jobs with both `startedAt` and `completedAt`, rounded to seconds.
  Missing or reversed timestamps and non-completed jobs are excluded. The value
  is `0` when no valid history exists.
- `estimatedWaitTime` estimates the completion time of queued jobs plus jobs
  that still have an unexpired, fenced lease on a live worker. The workload is
  divided into waves using the total slots of active workers, then multiplied
  by the average processing time.
- `estimatedWaitTime` is `null` when the engine has no dispatch capacity or no
  valid timing history. It is zero when capacity and history exist but there is
  no queued or actively leased work.

All engine and status counts remain SQL-side aggregates. Worker capacity,
active leases, and timing history are also aggregated in a fixed number of
queries, independent of engine, status, worker, or job counts.

## Migration notes

- Existing deployments upgrading from Alpha/Beta may need to ensure the `SlicerSettings` row exists in the DB.
- The application seeds a default `SlicerSettings` row at first access if missing.

## Example: set jitter from env

To set a default jitter at process startup without using the UI, set the environment variable:

```bash
export SlicerWorker__JitterPercent=12.5
# or in appsettings.json:
"SlicerWorker": {
  "JitterPercent": 12.5
}
```

This value will be used as the initial persisted value if DB is empty and remains editable via Admin UI.

## Operational guidance

- Start with conservative jitter (5–15%) and a moderate base retry to avoid rapid retries.
- Monitor queue pressure in the Admin > Slicer status pages and increase jitter when many concurrent retries are observed.
