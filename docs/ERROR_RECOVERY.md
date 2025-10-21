# Error Recovery — Slice Job Leases and Automatic Requeue

This document explains the error recovery system for the distributed slicer job queue. It covers how job leases work, how the server detects and recovers stuck jobs, configuration knobs, metrics to monitor, and operational testing steps.

## How it works (at a glance)

- Workers claim jobs via the pull API and receive a lease (duration in seconds). The claim sets `ClaimedAt` and `LeaseExpiresAt` on the `SliceJob` record.
- While a worker processes a job it periodically renews the lease by POSTing `/api/slice/{id}/renew` with a requested lease duration. The worker renew interval should be shorter than the lease (recommended: lease/3).
- `JobTimeoutScannerHostedService` runs on the server and periodically calls `GetStuckJobsAsync()` to find jobs where `LeaseExpiresAt` is in the past or processing exceeds a long-running threshold (default 15 minutes). For each stuck job it calls `IncrementRetryAndRequeueAsync()` which either:
  - increments `RetryCount` and sets job status back to `Queued` (and updates `QueuedAt`) if under `MaxAttempts`; or
  - marks the job `Failed` with an error message if the retry limit is reached.
- Metrics are recorded via `SliceJobMetrics` counters: `JobsTimedOutTotal` and `JobRetriesTotal`.

## Configuration

All knobs are configurable via `appsettings.json` or environment variables.

- Worker lease
  - `Worker:LeaseDurationSeconds` (int, default 300): Lease length granted to workers when they claim a job. Longer leases reduce reclaims but increase the time to detect true abandonment.

- Error recovery / retry policy
  - `JobDispatchRetry:MaxAttempts` (int, default 3): Maximum number of requeue attempts before marking a job Failed.
  - `JobDispatchRetry:BaseDelayMs` (int, default 250): Base backoff delay in milliseconds used for requeue timing.
  - `JobDispatchRetry:Multiplier` (double, default 2.0): Exponential multiplier applied to BaseDelayMs per retry.

- Scanner tuning
  - The scanner currently uses an internal scan interval of ~30s and treats jobs running longer than 15 minutes as long-running (configurable in code if desired). If your jobs typically take longer than 15m, increase the long-running threshold to avoid false positives.

## Metrics & Alerting

Export these metrics from `SliceJobMetrics` to your metrics backend (Prometheus/OpenTelemetry):

- `printfarmer.slicing.jobs_timed_out_total` — incremented when a stuck job is processed by the scanner.
- `printfarmer.slicing.job_retries_total` — incremented when the scanner increments a job's retry count and requeues it.

Suggested alerts:

- High rate or sustained increase in `jobs_timed_out_total` → alert on worker instability, network partitions, or slow pipelines.
- Rising `job_retries_total` combined with many `Failed` jobs → alert on systemic processing errors (data issues, environment misconfiguration) rather than transient worker flaps.

## Testing and validation

1. Unit tests: run `dotnet test ./tests/Farm.Web.Api.Tests` — there are focused unit tests for the repository helpers and scanner logic.

2. Integration test (manual):
   - Use `CustomWebApplicationFactory.CreateWithIsolatedDatabase()` to spin up a test host with an isolated in-memory DB.
   - Insert a `SliceJob` with status `Processing` and with `LeaseExpiresAt` set to the past.
   - Resolve `JobTimeoutScannerHostedService` from DI and call `ProcessStuckJobsOnceAsync()`.
   - Assert that the job has `Status` = `Queued` (or `Failed` if `RetryCount` exceeded) and that `RetryCount` was incremented.

3. Staging verification (recommended before production):
   - Deploy the scanner-enabled build to a staging cluster.
   - Create a job and deliberately stop the worker mid-processing (kill process). Wait for `LeaseDurationSeconds` + small buffer and ensure the scanner requeues the job.
   - Observe metrics and logs for the scanner and workers.

## Operational runbook

- If jobs are timing out frequently:
  1. Check worker host logs for connectivity or OOMs.
  2. Verify worker instances can reach the API renew endpoint (network/firewall rules).
  3. Temporarily increase `Worker:LeaseDurationSeconds` if false positives are caused by transient slowdowns.

- If many jobs exhaust retries and fail:
  1. Examine the `ErrorMessage` field on failed jobs and corresponding worker/log artifacts.
  2. Re-run or requeue affected jobs manually after addressing root cause.

- If scanner logs show errors:
  1. Inspect application logs for the exception details. The scanner logs errors when handling individual stuck jobs.
  2. Ensure the repository can perform required updates (DB connectivity/locks).

## Implementation notes for maintainers

- The scanner resolves `SliceJobMetrics` from the scope at scan time and does not dispose the singleton metrics instance (the DI container owns it).
- Avoid extremely short `Worker:LeaseDurationSeconds` (e.g., <60s) as network hiccups will cause frequent reclaims.

## Next improvements (future work)

- Add a testable configuration surface for the scanner's scan interval and long-running threshold (currently hard-coded defaults exist in code).
- Add a dedicated metrics scraping unit test harness that collects exported counter values for numeric assertions.
- Extend worker logic to abort processing quickly if renew failures indicate lease loss (to avoid duplicated work when a job was re-assigned).

---

Document created: October 21, 2025
