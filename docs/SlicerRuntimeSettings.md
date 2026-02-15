# Slicer Runtime Settings

This document describes runtime configuration options for slicer workers and the admin UI for modifying them.

## Overview

PrintFarmer supports a distributed slicer job queue where one or more workers perform slicing (PrusaSlicer, OrcaSlicer, etc.). Some runtime behavior (retry/backoff, jitter, per-engine executable paths and args) can be tuned at runtime via the Admin UI or persisted to the database.

## Admin UI

- Location: `Admin → Slicer Worker Settings` (`/admin/slicer` in the UI, protected to `farm_admin` role).
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

- Admin UI (preferred for operations): persisted to DB via `/api/slicer/settings` (POST)
- `appsettings.json` and environment variable `SlicerWorker:JitterPercent` are used for default initial values on startup

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

