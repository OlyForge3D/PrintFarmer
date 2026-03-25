# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

### 2025-01-21: Architecture for 5 Blocked/Deferred Items

**Task:** Design implementation plans for 5 TODO items blocking backend/slicer features.

**Investigation:**
- **Camera Control (Item 1):** `ISupportsCamera` interface only has read methods (stream/snapshot URLs). Backend plugins (Moonraker, PrusaLink, OctoPrint, SDCP, FlashForge) need enable/disable/status methods. `PrintersService.cs` stubs return false.
- **Slicer Artifacts (Item 2):** `HttpJobPollerService` uploads only G-code. `SlicingResult.Metadata` is unstructured `Dictionary<string, string>`. Need conventions for thumbnails (small/medium/large), logs, configs, and multi-artifact upload.
- **OpenAPI Migration (Item 3):** `ExampleSchemaFilter.cs` has 19 TODOs with commented OpenAPI v2 code. Need migration to ASP.NET Core 10 native OpenAPI (`Microsoft.AspNetCore.OpenApi`) with document/operation transformers.
- **Tag Support (Item 4):** `Tag` entity exists but no `PrintJobTag` junction table or repository methods. `PrintJobManagementService` logs "not implemented" on tag updates. Need migration, service layer, API endpoints.
- **OrcaSlicer Types (Item 5):** `OrcaSlicerAssetRegistry` manifest parsing is TODO. `OrcaSlicerUIProvider` has placeholder `typeof(object)` for profile/settings types. Need OrcaSlicer-specific types and manifest schema.

**Key Patterns:**
- **Capability Interface Pattern:** Backend plugins use marker interfaces (`ISupportsCamera`, `ISupportsFileUpload`) discovered via reflection. Adding methods requires updating all 6 plugins.
- **Multi-Artifact Upload:** Need standardized metadata keys (`thumbnail_small`, `slicer_log`) and loop-based upload logic after primary G-code.
- **OpenAPI Transformers:** ASP.NET Core 10 uses `AddOpenApi()` with document/operation transformers instead of Swashbuckle filters.
- **Tag Junction Table:** Many-to-many via `PrintJobTag` entity, not direct navigation property. Standard EF Core pattern.
- **Embedded Resources:** OrcaSlicer assets (bed models, textures) are embedded resources, manifest must be JSON-deserialized at init.

**Architecture Decisions:**
1. **Camera Control:** Extend `ISupportsCamera` with 3 methods (`Enable`, `Disable`, `IsEnabled`). Research Moonraker/PrusaLink APIs first. SDCP/FlashForge return false gracefully.
2. **Slicer Artifacts:** Define `SlicingArtifactKeys` constants, implement multi-artifact upload with thumbnail extraction from G-code comments (PNG base64 or file paths).
3. **OpenAPI Migration:** Replace Swashbuckle with native OpenAPI, use transformers in `Program.cs`, delete `ExampleSchemaFilter.cs`.
4. **Tag Support:** Create `PrintJobTag` junction table, implement `PrintJobTagService`, migrate database (all 4 providers), add API endpoints.
5. **OrcaSlicer Types:** Define `OrcaSlicerProfile` and `OrcaSlicerSettings` types (reverse engineer from samples), implement manifest JSON parsing with embedded resources.

**Complexity Estimates:**
- Camera Control: M (2-3 days) — Research + 6 plugin implementations
- Slicer Artifacts: L (4-5 days) — Metadata conventions + G-code parsing + upload logic
- OpenAPI Migration: M (2-3 days) — Straightforward refactor, testing Swagger UI
- Tag Support: M (3-4 days) — Standard CRUD + migrations + UI
- OrcaSlicer Types: M (2-3 days) — Reverse engineering + JSON parsing

**Recommended Owners:**
- Camera Control: Taylor (backend plugins)
- Slicer Artifacts: Taylor + Morgan (backend + slicer parsing)
- OpenAPI Migration: Jordan or Taylor (API refactor)
- Tag Support: Taylor + Jordan (backend + UI)
- OrcaSlicer Types: Morgan or Jordan (slicer domain knowledge)

**Files Written:**
- `.squad/decisions/inbox/dallas-blocked-items-architecture.md` — Full architecture document with problem statements, proposed solutions, implementation plans, dependencies, and complexity estimates for all 5 items.
## Core Context

This section summarizes foundational knowledge and recurring patterns across Dallas's leadership sessions.

### Role & Responsibility

**Dallas** serves as the Lead decision reviewer for PrintFarmer. Responsibilities include:
- Reviewing architectural decisions and trade-offs from other agents
- Providing quality-first feedback (rejecting incomplete implementations)
- Ensuring 3-layer consistency (service → API → UI) for critical changes
- Guiding team alignment on product direction and design patterns
- Approving or rejecting feature implementations based on regression risk and pattern compliance

### Approved Patterns & Conventions

**Compact Status Detail Modal (`compact-status-detail-modal` skill):**
- Compact surface should be glanceable (status + icon)
- Surface should be clickable to launch detailed modal
- Modal provides full context (why, next steps, timestamps, snapshots)
- Used for: failure detection, startup state, dispatch eligibility

**Monitoring Lifecycle Badges (`monitoring-lifecycle-badges` skill):**
- Show active monitoring state, not raw error states
- One consistent signal across all surfaces
- Avoid redundant overlays or duplicated displays
- Failure detection shield belongs in header, not overlaid on camera

**Quality First Principle:**
- Incomplete fixes are rejected; request revision
- 3-layer contract required for visibility regressions
- Regression coverage > quick fixes
- Integration seam testing validates stale-state issues

**Startup as UI Boundary:**
- Respect optimistic state transitions during startup
- UI can override stale backend state early in boot
- Documented in attention contract framework

### Team Alignment

- All agents aligned on product direction (as of 2026-03-25)
- Quality standards enforced across all code changes
- Trade-offs documented for future tuning

### Active Decisions (Recent)

| Decision | Date | Status | Impact |
|----------|------|--------|--------|
| Failure Detection Badge Placement | 2026-03-25 | Recommendation (ready for approval) | Remove camera overlay; keep header badge only |
| Startup as UI Boundary | Earlier | Approved | Respects optimistic state; documented in attention contract |
| Failure Detection Warmup Gate (30s) | Earlier | Approved | Prevents false positives during startup |
| Auto-Print Ready-Gate Dispatch | Earlier | Approved | Dispatch eligibility tied to confirmation state |
| 3-Layer PendingReady Visibility | Earlier | Approved | Service → API → UI regression coverage |

### Architecture Deep Dives Completed

1. **Blocked Items Analysis** (2026-01-21)
   - Assessed 5 TODO items: camera control, slicer artifacts, OpenAPI migration, tag support, OrcaSlicer types
   - Proposed solutions and complexity estimates for each
   - Identified capability pattern for backend plugins

2. **Auto-Print Scaling** (2026-03-06)
   - Analyzed architecture to 100 printers
   - State management: 3 states (None, PendingReady, Ready)
   - Background service concurrency: SemaphoreSlim serialization, MaxConcurrentDispatches limit
   - Query pattern: Batch optimization via AsSplitQuery()

3. **Help System Lifecycle** (Earlier)
   - Designed guided tours as first phase
   - Expansion path to contextual help
   - Video tutorials + written docs for future

### Recurring Questions

- **UI Redundancy:** Should duplicate information appear in multiple surfaces? → No; single source of truth in header
- **Regression Coverage:** What counts as sufficient testing? → 3-layer contract (service → API → UI)
- **Discoverability:** How do operators find hidden features? → Clickable affordances + modal pattern
- **Scalability:** Does architecture support 100+ printers? → Verify batch queries and background task concurrency

---
## Session: Failure Detection Badge Placement Review (2026-03-25)

**Role:** Lead decision reviewer  
**Status:** Recommendation formulated; ready for team approval

### Work Completed
- Analyzed operator workflow: card view (collapsed) vs. camera feed (expanded)
- Assessed UI redundancy: header badge vs. camera overlay (identical information)
- Evaluated visual noise and cognitive load impact
- Reviewed discoverability via modal pattern
- Consolidated findings with Ripley (Frontend Dev)

### Recommendation
**Keep header badge only; remove camera overlay.**

**Reasoning:**
1. Single source of truth eliminates confusion and sync issues
2. Camera overlay is redundant; operator sees header state before camera opens
3. Reduces visual distraction during video inspection
4. Header badge → modal flow provides full detail access
5. Follows PrintFarmer conventions (secondary status lives in header, not overlays)

### Decision Document
- Status: Recommendation ready for team decision
- File: `.squad/decisions/decisions.md` → merged from inbox
- Implementation path clear; backend-agnostic (UI only)

### Implementation Checklist
- [ ] Remove `FailureDetectionMonitoringOverlay` import from CompactPrinterCard.tsx (line 18)
- [ ] Remove overlay prop from PrinterCameraPreview call (lines 230–236)
- [ ] Optionally deprecate overlay component if unused elsewhere
- [ ] Validate pattern compliance (compact-status-detail-modal, monitoring-lifecycle-badges)

### Related Skills
- `compact-status-detail-modal`: Status affordances should be glanceable + launch modal for detail
- `monitoring-lifecycle-badges`: Monitoring status badges reflect active lifecycle, not raw errors

---
