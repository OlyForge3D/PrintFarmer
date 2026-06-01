# Decision: Firmware 409 → PrinterBackendBusyException (#317)

**Author:** Brett (Researcher)  
**Date:** 2026-06-01  
**Status:** Implemented (all three plugins complete)

## Context

Issue #317 requested that the Moonraker, SDCP, and FlashForge backend plugins translate
firmware-level busy responses into `PrinterBackendBusyException` so the controller's
`MapControlOutcome` returns HTTP 502 BackendBusy instead of silently failing.

The `PrinterControlGate` / `GatePrinterControlAsync` (409 guard) is the primary defense.
Plugin-side propagation is the secondary defense for the race window between gate check
and backend I/O.

## Findings — Implementation Status Per Plugin

### Moonraker ✅ COMPLETE

**Detection logic:** `MoonrakerClient.SendGcodePrivateAsync` (lines ~831–898).

- HTTP 409 Conflict → always `PrinterBackendBusyException`.
- HTTP 503 with body matching an allowlist of unambiguous printing-job phrases
  (`"printer is printing"`, `"printer is busy"`, `"printer busy"`, `"sd busy"`) →
  `PrinterBackendBusyException`.
- HTTP 503 with Klippy-unavailable bodies (e.g. `"Klippy is not connected"`,
  `"Klippy is busy initializing"`) → returns `false` (transport error, not printer-busy).
  This narrowing was a Bishop review blocker from #318 to prevent false positives.

**Coverage:** `SetTempsAsync`, `MoveToAsync`, `MoveAsync`, `HomeXYAsync`, `HomeZAsync`,
and `SendGcodeAsync` all route through `SendGcodePrivateAsync`.

**Tests:** `src/tests/Farm.Web.Api.Tests/MoonrakerClientBusyTests.cs` — 9 unit tests
covering 409, 503-printing, 503-Klippy-various, 200-success paths.

### SDCP (Elegoo) ✅ COMPLETE (scope-limited by protocol)

**Detection logic:** `SdcpClient.StartPrintAsync` (line ~1168).

When the firmware rejects a StartPrint command (Ack=1), the client calls
`GetCurrentStatusArrayAsync` and checks `IsPrintingStatus(currentStatus)`. If the printer
reports code 1 (printing) or code 9 (starting), it throws `PrinterBackendBusyException`.

**Scope note:** SDCP is a resin-printer WebSocket protocol (Elegoo Mars/Saturn series).
It does not expose `ISupportsTemperatureControl` or `ISupportsMovement` — there are no
hotend/bed temp or movement commands to protect. `StartPrintAsync` is the only mutation
path requiring busy propagation.

A `TODO(#317-followup)` exists noting that if a future SDCP spec version introduces a
dedicated busy/printing response code, the round-trip status check can be eliminated.

**Tests:**
- `src/tests/Farm.Web.Api.Tests/SdcpClientBusyTests.cs` — unit tests for `IsPrintingStatus`.
- `src/tests/Farm.Web.Api.Tests/Backends/SdcpClientBusyTests.cs` — behavior tests with a
  real Kestrel WebSocket server (StartPrint rejection + CurrentStatus printing/idle).

### FlashForge ✅ COMPLETE (temperature mutations intentionally excluded)

**Detection logic:** `FlashForgeClient.StartPrintAsync` (lines ~295–316).

When the firmware rejects `~M23` (print start), the client sends `~M119` to check machine
status. If `IsBuildingStatus(m119Response)` returns true (`BUILDING_FROM_SD` or `BUILDING`),
it throws `PrinterBackendBusyException`. Otherwise returns `false`.

**Temperature mutations excluded by design:** `SetTemperaturesAsync` has a documented
`TODO(#317-followup)` noting that FlashForge firmware accepts `M104`/`M140` commands
during an active print (user-initiated temperature adjustments are normal operation).
No firmware-level busy signal is returned for temperature mutations. The
`GatePrinterControlAsync` controller gate is the primary defense for that path.

**Movement:** `IFlashForgeClient` does not implement `ISupportsMovement` — there is no
MoveTo/Move command surface to protect.

**Tests:**
- `src/tests/Farm.Web.Api.Tests/FlashForgeClientBusyTests.cs` — unit tests for
  `IsBuildingStatus` and `ParseMachineStatus` helpers.
- `src/tests/Farm.Web.Api.Tests/Backends/FlashForgeClientStartPrintBusyTests.cs` — behavior
  tests with a real TCP server (M23 rejection + M119 BUILDING/READY responses).

## Decision

All required work for #317 is present on `development`. The implementation was committed
piecemeal (alongside #314 and #318 work) without a formal issue-closing PR. This PR
closes #317 by documenting the finding and confirming test coverage.

**Follow-up items (already tracked via TODO comments in code):**
- SDCP: eliminate status round-trip if SDCP spec adds an explicit busy code.
- FlashForge: identify exact firmware error string for busy-start to avoid M119 round-trip.
- FlashForge `SetTemperaturesAsync`: revisit if firmware variants emerge that do reject
  temp changes during print.
