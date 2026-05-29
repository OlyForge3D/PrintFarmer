# Status-Gated Mutation Endpoints

Printer control endpoints (`/temps`, `/move`, `/moveto`) are double-gated: once at the
controller level (status cache check) and once at the plugin/service layer (backend response).

## HTTP Response Codes

| Condition | HTTP | Source |
|---|---|---|
| Printer is printing (status cache) | 409 Conflict | `GatePrinterControlAsync` in `PrintersController` |
| Backend firmware refused (plugin throws `PrinterBackendBusyException`) | 409 Conflict | `MapControlOutcome(BackendBusy)` in `PrintersController` |
| Printer not found | 404 Not Found | `GatePrinterControlAsync` |
| Backend does not support the command | 502 Bad Gateway | `MapControlOutcome(BackendUnsupported)` |
| Backend unreachable / generic failure | 502 Bad Gateway | `MapControlOutcome(BackendUnreachable)` |

**Key invariant**: `PrinterControlOutcome.BackendBusy` maps to **409 Conflict** (not 502).
This was corrected in PR #318 — previously it incorrectly returned 502.

## Exception Flow (BackendBusy → 409)

```
Plugin client (e.g. MoonrakerClient, OctoPrintClient)
  └─ throws PrinterBackendBusyException
        ↓
PrintersService.SetTempsAsync / MoveAsync / MoveToAsync
  └─ catches PrinterBackendBusyException
  └─ returns PrinterControlOutcome.BackendBusy
        ↓
PrintersController.MapControlOutcome(BackendBusy)
  └─ returns Conflict(CommandResult) — HTTP 409
```

## Per-Plugin Busy Detection

| Plugin | Trigger |
|---|---|
| **OctoPrint** | HTTP 409 from printer API |
| **PrusaLink** | HTTP 409 from printer API |
| **Moonraker** | HTTP 409 (gcode queue conflict). HTTP 503 **only** when body contains printing/busy keywords (Klippy-unavailable 503 is NOT treated as busy — see below). |
| **FlashForge** | `~M23` rejection + `~M119` status shows `BUILDING_FROM_SD` or `BUILDING` |
| **SDCP** | StartPrint rejection + `CurrentStatus` is `printing` or `starting` |

## Moonraker 503 Narrowing (PR #318)

Moonraker 503 means **Klippy unavailable** (disconnected, shutdown, error state) — it does NOT
mean the printer is busy printing. Treating all 503s as "busy" was overly broad.

**Rule (implemented in `MoonrakerClient.SendGcodePrivateAsync`):**

- **409** → always `PrinterBackendBusyException`
- **503 + body matches printer-busy phrase allowlist** → `PrinterBackendBusyException`
- **503 + any other body (including empty)** → return `false` (backend unavailable, not busy)

**Phrase allowlist** (case-insensitive substring match after lowercasing body):

| Phrase | Source |
|---|---|
| `"printer is printing"` | Most common — gcode rejected while a job is active |
| `"printer is currently printing"` | Klipper firmware variant |
| `"printer is busy"` | Firmware variant |
| `"printer busy"` | Older firmware variant |
| `"sd busy"` | SD-card busy during active print |

**Intentionally excluded:** bare `"busy"` and bare `"printing"` — these over-match Klippy
startup states (e.g. `"Klippy is busy initializing"`) and unrelated error messages.
Prefer false negatives (return false → backend unavailable) over false positives (wrong 409).

This aligns Moonraker with the tighter OctoPrint/PrusaLink convention of 409-only busy detection.

## Testing

- `PrintersControllerControlGuardsTests` — controller unit tests covering status-cache gate (409
  when printing) and `BackendBusy` → 409 from service layer.
- `MoonrakerClientBusyTests` — covers 409 busy, 503+printing-body busy, 503+Klippy-body → false,
  503+`"Klippy is busy initializing"` → false, 503+`"SD busy"` → throws, uppercase phrase → throws.
- `OctoPrintClientTests`, `SdcpClientBusyTests`, `FlashForgeClientStartPrintBusyTests` — plugin-level busy propagation.
