# Spike: SignalR `printerupdated` broadcast volume (issue #1242)

**Type:** Measurement spike — no production code changed. This document is the deliverable.
**Branch:** `dev/jpapiez/issue-1242-signalr-broadcast-volume-spike` (audit trail only, no PR).

## Correction to the issue's stated cost model

Issue #1242 states the fan-out uses `Clients.All`. That is no longer true in the current
codebase. Both `PrusaLinkPollingService.cs` (line ~302) and `TestEmulatorPollingService.cs`
(line ~180) broadcast via:

```csharp
await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId))
    .SendAsync("printerupdated", signalRUpdate, ct);
```

`git log -p` shows this group-scoped change predates this issue. The real worst-case volume is
`Σ(printers × subscribers-to-that-printer)`, not `printers × all-connected-clients`. In practice
a farm-overview dashboard page subscribes to every visible printer, so for that page the
worst case still approaches `printers × viewers-of-that-page`; a client only viewing one
printer's detail page gets ~1/40th the volume in a 40-printer farm. `PrinterHub.SubscribeToPrinterAsync`
also does a one-time `Clients.Caller` unicast on subscribe, separate from the steady-state
broadcast rate.

The dead `hasChanges` flag and the unconditional client callback fan-out described in the issue
are both still accurate (confirmed below).

## Method

- **Environment:** local API (`dotnet run --project ./api/Farm.Web.Api.csproj`), SQLite dev DB,
  `TestEmulator` backend enabled and seeded with **40 emulated printers** via
  `appsettings.Development.json` (not committed — reverted after the spike).
- **Emulator poll interval:** `TestEmulatorPollingService` polls every **2 seconds** — faster than
  the 5-10s intervals the issue cites for real backends (PrusaLink/SDCP). All rates below are
  reported as measured at 2s, with a linear scaling estimate to 5s/10s where relevant, since the
  emulator's rate is not representative of production polling cadence.
- **Client simulation:** a throwaway C# console harness (`Microsoft.AspNetCore.SignalR.Client`,
  kept outside the repo in session artifacts, never committed) opened **6 concurrent authenticated
  connections**, each subscribed to all 40 printer groups — modeling 6 concurrent users all with
  a farm-overview dashboard open (the worst case for this topology). Every `printerupdated`
  message was logged with a timestamp, connection index, printer id, and raw JSON body to CSV.
- **Two 60-second capture windows:**
  1. **Idle farm** — all 40 printers freshly seeded in `Idle` state (no active jobs).
  2. **Active farm** — all 40 printers seeded directly into `Printing` state with randomized
     progress (1-95%) and print duration (180-600s) so temperatures were mid-ramp and progress
     was actively advancing on every poll (realistic jitter, not synchronized/frozen state).
- **Analysis:** a throwaway Python script parsed both CSVs to compute message/byte rates and a
  suppression ratio (consecutive same-printer messages with byte-identical JSON body — the exact
  condition a payload-complete equality gate would need to match to skip a `SendAsync`).
- **Server serialization cost:** a throwaway C# microbenchmark serialized one representative
  `PrinterStatusUpdate` 500,000 times with `System.Text.Json` using the same camelCase policy as
  the hub, measuring wall time and `GC.GetAllocatedBytesForCurrentThread` deltas.
- **Client cost:** assessed via static code reading of `printer-signalr.ts`
  (`applyStatusUpdate`), not a browser profiling run — out of scope for an "S"-sized spike. This
  limitation is stated explicitly rather than hidden.

## 1. Baseline message rate

40 printers, 6 clients each subscribed to all 40 printers, 60s capture, TestEmulator's 2s poll
interval:

| State  | Server-side (total sends across 6 subscribers) | Per-client (one dashboard viewer) |
|--------|---:|---:|
| Idle   | 125.8 msg/s, 12.9 KB/s | 21.0 msg/s, 2.15 KB/s |
| Active (printing) | 124.5 msg/s, 26.6 KB/s | 20.8 msg/s, 4.43 KB/s |

Message *rate* is essentially identical between idle and active (both ≈ printers/poll-interval ×
subscribers, i.e. `40 printers / 2s × 6 ≈ 120/s` — matches). Only payload *size* changes
materially (temps/progress/jobName fields populate), which is why active-farm bytes/s is ~2x
idle despite the same message count.

**Scaling to realistic production poll intervals** (5-10s, per the issue, vs. the emulator's 2s):
linearly scaling down by interval ratio, a 40-printer farm with 6 dashboard viewers would produce
roughly **8.4–4.2 msg/s per client** (5s/10s interval) instead of the measured 21 msg/s — the
emulator's faster polling means the numbers above should be read as an upper bound relative to
real backend cadence, not a 1:1 production estimate.

## 2. Suppression ratio (what a payload-complete equality gate would catch)

Computed per-printer over consecutive messages on a single client's stream (1,200 consecutive
pairs across 40 printers, 60s):

| State | Identical consecutive pairs | Suppression ratio |
|-------|---:|---:|
| Idle | 1,200 / 1,200 | **100.0%** |
| Active (printing) | 119 / 1,200 | **9.9%** |

This is the central finding of the spike. **The suppression hypothesis holds almost perfectly at
idle and almost completely fails during active printing.** While a job is running, the emulator's
ramped temperatures and advancing progress mean nearly every poll produces a genuinely different
payload — a payload-complete gate suppresses fewer than 1 in 10 messages in that state. Real
backends would show similar behavior for progress and nozzle/bed temperature fields, which
fluctuate continuously during a print (this is standard 3D-printer telemetry jitter, not an
emulator artifact — the issue's own premise anticipates this risk, and the data confirms it).

## 3. Cost attribution

**Server-side serialization** (`System.Text.Json`, camelCase, `PrinterStatusUpdate` sample with
representative field values):

| Metric | Value |
|---|---|
| Time per serialization | ~1.07 µs |
| Allocation per serialization | ~336 bytes |
| Wire size per message | ~311 bytes |
| Single-core throughput | ~930,000 serializations/sec |

SignalR serializes a group broadcast **once per distinct broadcast event**, not once per
subscriber (same-protocol clients in a group share the serialized payload). The distinct
broadcast-event rate in this farm is ≈ printers/poll-interval = 40/2s ≈ **20.75 events/sec**,
independent of subscriber count. At that rate, serialization costs roughly **22 µs of CPU and
7 KB of allocation per second** — immaterial at this farm size regardless of state or subscriber
count.

What *does* scale with subscriber count is the per-connection transport write: 124.5 sends/sec
measured server-side vs. 20.75 distinct events/sec × 6 subscribers = 124.5 — confirms fan-out
cost is dominated by socket writes, not re-serialization. At 40 printers × 6 clients this is still
a trivial load for a single instance; it would only become material at farm/viewer counts an
order of magnitude larger than tested here (not measured in this spike).

**Client-side cost** (static analysis, no browser profiling): `printer-signalr.ts`
`applyStatusUpdate` unconditionally does `lastStatuses.set(...)` and invokes **every** registered
`printerStatusCallbacks` entry on every message, with no equality check. For a farm-overview
viewer at the measured per-client rate (~21 msg/s idle, ~21 msg/s active), that means ~21
store-writes-plus-callback-fan-outs per second regardless of whether anything changed. At idle,
100% of that churn is provably redundant (per §2). This is the strongest evidence-backed argument
for a gate — but it is a claim about wasted client work, not measured render time or frame drops,
which would require an actual browser profiling pass (explicitly out of scope here).

## 4. Recommendation

**A payload-complete change-detection gate is worth implementing, but its value is concentrated
almost entirely in the idle state, not the active-printing state, and it will not meaningfully
reduce server CPU/bandwidth at farms of this size.**

Reasoning, backed by the numbers above:

- Idle suppression is ~100% — a real farm spends most of its time with most printers idle between
  jobs, so a gate would eliminate the large majority of `printerupdated` traffic and client
  churn during that (dominant) time.
- Active-print suppression is only ~9.9% — the gate provides almost no benefit while a printer is
  actually running, because progress/temperature fields genuinely change on nearly every poll.
  Any follow-up work must not be sold as "cuts broadcast volume during printing" — it doesn't.
- Server-side serialization/allocation cost is already negligible (µs/KB-per-second range) at 40
  printers × 6 clients; a gate would not produce a measurable server CPU or memory win at this
  scale. The socket-write cost that does scale with subscriber count is unaffected by payload
  content and would only be reduced by the fraction of messages actually suppressed (i.e., mostly
  during idle periods).
- The main win is client-side: eliminating ~100% of no-op re-renders while a farm sits idle,
  which is real farm operators' most common viewing state (a dashboard left open while printers
  wait for the next job).

**Proposed follow-up issue** (concrete, scoped):

> Implement the `hasChanges` check that already exists as a dead flag in
> `PrusaLinkPollingService.cs` and `SdcpPollingService.cs` (and add the equivalent to
> `TestEmulatorPollingService.cs`): before calling `Clients.Group(...).SendAsync("printerupdated",
> ...)`, compare the new `PrinterStatusUpdate` against the last-sent value for that printer
> (excluding volatile-but-meaningless noise, if any is identified) and skip the send when
> unchanged.
>
> **Target:** ~90-100% reduction in idle-state `printerupdated` volume (matches measured
> suppression ratio); ~5-15% reduction in active-print-state volume (matches measured ratio — do
> not overstate this). No material server CPU/allocation reduction is expected or promised at
> current farm/viewer scales; the benefit is client-side render-churn reduction and reduced idle
> socket-write volume.
>
> **Not in scope for the follow-up:** browser-side render profiling to quantify actual frame-time
> savings — recommend a lightweight before/after check (React DevTools profiler on the
> farm-overview page, idle vs. gated-idle) as a fast validation step once the gate lands, rather
> than a separate spike.

Issue #1242 is left **open** with this recommendation recorded as a comment; spikes do not
self-close.

## Reproduction notes

- 40 printers via `TestEmulator.Printers` config array in `appsettings.Development.json`
  (temporary, not committed).
- 6-connection SignalR capture harness and the serialization microbenchmark were both throwaway
  console apps kept outside the repository (session-scoped artifacts), per the spike's
  instructions not to merge instrumentation code.
- Raw CSV captures (idle/active) and the analysis script are not part of this PR/branch; the
  aggregated numbers above are the durable record.
