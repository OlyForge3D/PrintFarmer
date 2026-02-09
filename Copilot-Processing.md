# Copilot Processing: SDCP History Name Mismatches (Cmd 320/321)

**Session**: Fix SDCP history field name mismatches vs official CBD-Tech SDCP V3.0.0 spec
**Date**: 2026-02-09

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
