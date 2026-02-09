# Copilot Processing: SDCP File List Parsing (Cmd 258)

**Session**: PFarm1-97.1 - Replace stubbed file list with real SDCP Cmd 258 parsing
**Date**: 2026-02-09

## Action Plan

### Phase 1: Add SDCP File List Response DTOs
- [x] Add SdcpFileListAckResponse, SdcpFileListAckData, SdcpFileListResult, SdcpFileEntry models

### Phase 2: Fix Request Payload
- [x] Send `{ Url = "/local" }` instead of `{}` per SDCP spec

### Phase 3: Implement Response Parsing
- [x] Replace placeholder return with actual JSON deserialization
- [x] Handle Ack != 0 error responses
- [x] Handle folder vs file types

### Phase 4: Update ISupportsFileList Implementation
- [x] Map usedSize to PrinterFileInfo.Size
- [x] Remove TODO/placeholder comments

### Phase 5: Build & Test
- [ ] Build solution
- [ ] Run tests

### Phase 6: Commit & Push
- [ ] Commit, close bead, push
