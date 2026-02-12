# Copilot Processing

## Request
Add Filament Load, Unload, and Change GCode macros and buttons for Moonraker backends, accessible from the sidebar and detailed printer cards.

## Action Plan

### Phase 1: Research ✅
- Identified `ISupportsGcodeExecution` interface with `SendGcodeAsync(baseUrl, gcode, ct)`
- MoonrakerClient implements this interface
- PrintersController has control endpoints (`home`, `pause`, `cancel`, `emergency-stop`, `disable-motors`)
- PrintersService uses `ISupportsGcodeExecution` for DisableMotors (M84) and EmergencyStop (M112)
- Frontend `api.ts` has methods per control action
- Sidebar and DetailedPrinterCard both have `handleControlAction` switch statements

### Phase 2: Backend - Add SendGcodeAsync + endpoint
- [ ] Add `SendGcodeAsync(Guid id, string gcode)` to `IPrintersService`
- [ ] Implement in `PrintersService`
- [ ] Add `POST {id}/gcode` endpoint to `PrintersController`

### Phase 3: Frontend changes
- [ ] Add `sendGcode(printerId, gcode)` to `api.ts`
- [ ] Add filament buttons to sidebar (Moonraker only)
- [ ] Add filament buttons to printer card (Moonraker only)

### Phase 4: Build & Verify
- [ ] Build + test + lint — PFarm1-j4g: Split SpoolsPage into Filaments and Spools tabs

**Session**: Split SpoolsPage into tabbed Filaments + Spools view
**Date**: 2026-02-10

## Mismatches Found (Official Spec vs Our Code)

| Layer | Spec Field | Our Field | Impact |
|-------|-----------|-----------|--------|
| Cmd 320 response | `HistoryData` | `TaskIdList` | IDs not parsed |
| Cmd 321 request | `{ "Id": ["id1"] }` | `{ "TaskId": "id1" }` | Printer rejects request |
| Cmd 321 response | `HistoryDetailList` array | Single object | Details not parsed |
| Detail field | `TaskName` | `Filename` | Name not mapped |
| Detail field | `BeginTime` | `StartTime` | Start time null |
| Detail field | `TaskStatus` | `Status` | Status not mapped |
| Status values | 1=Complete,2=Error,3=Stopped | 0=Complete,1=Cancel,2=Error | Wrong statuses |

## Action Plan

### Phase 1: Fix DTOs
- [x] `SdcpHistoryIdsResult.TaskIdList` → `HistoryData`
- [x] New `SdcpHistoryDetailResult` wrapper with `Ack` + `HistoryDetailList`
- [x] `SdcpHistoryDetail` fields: `TaskName`, `BeginTime`, `TaskStatus`
- [x] Remove `Ack` from `SdcpHistoryDetail` (moved to wrapper)

### Phase 2: Fix client logic
- [x] Cmd 321 request: `{ Id = [taskId] }` not `{ TaskId = taskId }`
- [x] Cmd 321 response: Parse `HistoryDetailList` array
- [x] Status mapping: 1=completed, 2=error, 3=cancelled, 0=other

### Phase 3: Fix tests
- [x] Update test response builders and assertions

### Phase 4: Build & verify
- [ ] Build solution
- [ ] Run tests
