# Decision: Comprehensive Auto-Dispatch Documentation with Mermaid Diagrams

**Date:** 2026-01-15  
**Agent:** Ash (Documentation Specialist)  
**Status:** Completed  

## Context

The Auto-Dispatch system is a complex feature spanning 12+ source files across backend services, API controllers, and React frontend components. It includes:
- Channel-based event triggering
- Multi-factor weighted scoring (10 factors)
- Event-driven background service with concurrent task processing
- Ready Gate state machine for bed-clear safety
- Three operational modes (Manual, Suggest, Auto)
- Thread-safe job assignment with SemaphoreSlim locking

Without centralized documentation, developers and operators struggled to:
- Understand the complete system flow from trigger to dispatch
- Debug scoring decisions and understand why printers were eliminated
- Configure thresholds and understand their impact
- Integrate frontend UI components (toggle, Zap icon, banner)
- Troubleshoot race conditions and channel backpressure

## Decision

Created comprehensive documentation at `docs/AUTO_DISPATCH.md` (40KB, 1110 lines) with:

**Architecture Coverage:**
1. Component diagram showing all services, controllers, and data flows (Mermaid graph)
2. Three distinct concepts explained: Auto-Dispatch, Ready Gate, Auto-Print (future)
3. Detailed component documentation for 7 key services
4. Trigger flow sequence diagram (two paths: idle vs upload-and-print)
5. Dispatch cycle flowchart (15 decision points)
6. Ready Gate state machine (3 states, 6 transitions)

**Developer Reference:**
- 10-factor scoring system with weights and hard/soft requirement explanations
- Complete API endpoint reference (11 endpoints with request/response examples)
- SignalR event payloads (4 events: jobautodispatched, dispatchsuggestion, dispatchfailed, autoprintstatechanged)
- Thread safety mechanisms: SemaphoreSlim, channel backpressure, Interlocked operations
- Configuration options: system-level singleton + per-printer opt-in

**Operator Guidance:**
- Frontend UI components: Global toggle, per-printer Zap icon, Bed Clear Banner
- Three dispatch modes with use cases and behavior descriptions
- Configuration tuning guide (threshold values, idle delay, concurrent limit)
- Troubleshooting common scenarios

**Design Rationale:**
- Eight critical design decisions documented with:
  - Decision statement
  - Rationale (why this approach)
  - Alternative rejected (what wasn't chosen and why)
  - Future considerations

## Rationale

**Why comprehensive documentation over minimal reference:**
- System complexity requires both high-level overview (diagrams) and low-level detail (API specs)
- Multi-persona audience: backend developers, frontend developers, farm operators
- Debugging requires understanding complete flow: trigger → scoring → dispatch → ready gate
- Scoring algorithm is opaque without weight documentation and example calculations

**Why Mermaid diagrams:**
- GitHub native rendering (no external tools)
- Version-controlled alongside code (stays in sync)
- Visual clarity for complex state machines and sequence flows
- Accessible to non-technical operators (operators can read flowcharts)

**Why document design decisions:**
- Prevents future refactors from breaking implicit assumptions
- Explains non-obvious choices (immediate upload-and-print dispatch, SemaphoreSlim locking)
- Captures alternatives rejected (helps future contributors understand constraints)

## Consequences

**Positive:**
- Single source of truth for auto-dispatch system architecture
- Onboarding time reduced (new developers read docs before diving into code)
- Debugging accelerated (logs reference score breakdown from docs)
- Configuration decisions data-driven (operators understand threshold impact)
- Frontend integration clear (UI components documented with behavior)

**Negative:**
- Documentation maintenance burden (must update when system changes)
- 40KB file may be overwhelming for first-time readers (mitigated by table of contents)

**Mitigation:**
- Documentation updates ship in same commit as code changes (enforced by code review)
- Table of contents enables targeted reading (operators skip technical sections)
- Mermaid diagrams reduce cognitive load (visual > text for complex flows)

## Alternatives Considered

**1. Inline code comments only**
- **Rejected:** Doesn't provide system-level architecture view. Comments spread across 12 files.

**2. API spec (OpenAPI/Swagger) only**
- **Rejected:** Doesn't explain dispatch cycle, scoring algorithm, or design decisions.

**3. Separate docs for developers and operators**
- **Rejected:** Creates duplication risk. Single comprehensive doc with clear sections serves both audiences.

**4. Video walkthrough instead of written docs**
- **Rejected:** Not version-controlled, not searchable, high maintenance cost.

## Related Decisions

- **Sprint 1+2 API Documentation** (2026-03-21) — Established pattern of comprehensive API docs with examples
- **Design System Documentation** (2026-03-21) — Demonstrated value of thorough component documentation
- Location Hierarchy Architecture (decisions.md) — Adjacency list + materialized path design documented

## Follow-up Actions

- [ ] Link from README.md to Auto-Dispatch docs (add to "Key Features" section)
- [ ] Add auto-dispatch architecture diagram to ARCHITECTURE.md
- [ ] Update API.md with cross-references to AUTO_DISPATCH.md for endpoint details
- [ ] Create developer guide section in docs/ linking to all major feature docs

## Notes

**Documentation Sources:**
Read 12 source files totaling ~2,500 lines of code:
- Backend services: AutoDispatchTrigger, AutoDispatchBackgroundService, DispatchScorer, AutoPrintService, JobQueueService, JobDispatchService
- API controllers: AutoPrintController, DispatchController, DispatchSettingsController
- Frontend components: PrintQueueDashboardPage, CollapsedPrinterCard, BedClearBanner

**Mermaid Diagrams Created:**
1. Component architecture (graph TB) — 20+ nodes showing all services and data flows
2. Trigger flow (sequenceDiagram) — Two paths showing idle vs upload-and-print triggers
3. Dispatch cycle (flowchart TD) — 15 decision points from trigger to dispatch/failure
4. Ready Gate state machine (stateDiagram-v2) — 3 states, 6 transitions with conditions

**Key Insights Documented:**
- Channel uses BoundedChannelOptions(64) with DropOldest for backpressure
- Upload-and-print skips idle threshold via SkipIdleThreshold flag
- SemaphoreSlim prevents job-stealing race (two printers, one job)
- Hard requirements eliminate printers; soft requirements reduce score
- Ready Gate filament checks (material match, weight) before dispatch
- No compatible printer? File uploaded, NOT queued (prevents orphaned jobs)
