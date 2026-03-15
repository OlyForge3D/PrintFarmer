# Orchestration Log — Lambert Camera Infrastructure Analysis

**Date:** 2026-03-15T01:46:46Z  
**Agent:** Lambert (claude-sonnet-4.5, background)  
**Task:** Analyze existing camera infrastructure in PrintFarmer; identify gaps to support Phase 1.5 camera control

## Outcome

✅ **Complete.** Published `.squad/decisions/inbox/lambert-camera-infrastructure.md`

## Summary

PrintFarmer has 80% of camera infrastructure already built, but scattered across two separate systems:

### Current State
- **Printer-attached cameras:** URLs stored on Printer entity, fetched via `ISupportsCamera` interface, discovered during network probe
- **Standalone cameras:** Full `Camera` entity with CRUD API, React UI, enable/disable toggle
- **URL rewriting:** `NetworkUrlRewriteService` rewrites camera URLs for Docker/native environments
- **SignalR integration:** Camera URLs included in printer status broadcasts
- **Frontend:** Direct connection to camera URLs (no API proxy), snapshot polling + stream support

### Critical Gap: No PrinterId FK
Standalone `Camera` entity has no association to `Printer`. Cannot express:
- "This external USB camera watches Printer X"
- "Enable/disable Printer X's camera"
- "Multi-camera per printer"
- Unified health monitoring across all camera types

### Minimal Viable Path (Effort: 11-16 hours)

**Phase 1 (4-6h):** Add `PrinterId` FK to Camera entity, create migration, data migration for existing printer cameras  
**Phase 2 (2-3h):** Extend API with camera-to-printer linking, query by printer  
**Phase 3 (3-4h):** Background health monitoring service + SignalR broadcast  
**Phase 4 (2-3h):** Update discovery probes to create Camera entities for discovered URLs  

## Recommendation

Start with **Phase 1 + 2** (6-9 hours) to unify the model and extend API. Delivers:
- External cameras linked to printers
- Enable/disable for ALL cameras (not just standalone)
- Foundation for multiple cameras per printer
- No breaking changes to existing API consumers

Phase 3 (health monitoring) and Phase 4 (discovery integration) can follow independently.

## Implementation Notes

**Patterns already in codebase:**
- `PrinterGroup` → `Printer` one-to-many relationship (use as template)
- `CamerasController` full CRUD (extend, don't replace)
- `MoonrakerSubscriptionService` background service pattern (reuse for health monitoring)
- `ServiceCollectionExtensions` DI registration pattern

**Existing assets:**
- Camera entity, DTO set, controller, service, React components
- URL rewriting and discovery probes already operational
- SignalR hub integration ready to broadcast health changes

---

**Full analysis:** `.squad/decisions/inbox/lambert-camera-infrastructure.md`
