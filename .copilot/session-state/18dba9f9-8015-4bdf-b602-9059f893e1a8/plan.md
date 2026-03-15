# Architecture Plan: Blocked & Deferred TODO Items

## Team Analysis Summary

Three Squad members analyzed the 5 blocked/deferred items in parallel:
- **🏗️ Dallas (Lead)** — Architecture decisions, interface design, implementation plans
- **🔧 Lambert (Backend)** — Deep codebase analysis, feasibility, risk factors
- **🔍 Brett (Researcher)** — Competitive analysis across 10+ print farm tools

## Consolidated Decisions

### ❌ ITEM 1: Camera Control → CLOSE (Won't Fix)

**Team consensus: Defer indefinitely / close.**

- **Lambert's finding**: Moonraker has NO enable/disable API. PrusaLink has camera *config* but no on/off toggle. The firmware concept doesn't exist.
- **Brett's finding**: Only SimplyPrint offers per-printer camera toggle. Most competitors treat cameras as always-on. Users want *smarter* cameras (AI failure detection), not on/off.
- **Dallas's design**: Provided full interface expansion (`ISupportsCameraControl`) but acknowledged firmware limitations.
- **Action**: Remove TODO stubs from `PrintersService.cs:2639,2663`. Add comment explaining firmware limitation.

### ✅ ITEM 2: Slicer Artifact Uploads → Phase 3E (Implement)

**Team consensus: Must-have, implement after job queue stabilizes.**

- **Lambert's critical finding**: `/api/artifacts` endpoint DOES NOT EXIST. The upload flow has a sender but no receiver.
- **Brett's finding**: Must-have for analytics. All competitors show thumbnails.
- **Dallas's design**: Full architecture — `Artifact` entity, `ArtifactsController`, storage strategy, metadata key conventions.

**Scope**: Entity + migration, controller, `SlicingArtifactKeys` constants, worker upload logic, frontend thumbnails.
**Assign**: Lambert (backend) + Ripley (frontend)
**Complexity**: L (Large)

### ✅ ITEM 3: OpenAPI Migration → CLOSE (Already Done!)

**Lambert's discovery: Already complete!** `Program.cs` already uses `.NET 10 native AddOpenApi()`. `ExampleSchemaFilter.cs` is 100% dead code.
**Action**: Delete dead code file.

### ⚠️ ITEM 4: Tag Support → DEFER (Projects are better)

**Brett strongly recommends SKIP**: "Projects win, free-form tags fail." No competing print farm tool has meaningful tag adoption.
**Action**: Close TODO. Projects feature provides better organization.

### ✅ ITEM 5: OrcaSlicer Types → Phase 3E (Implement)

**Team consensus: Straightforward when slicer integration stabilizes.**
**Scope**: `OrcaSlicerProfile`/`OrcaSlicerSettings` types, `manifest.json`, asset registry parsing, UI provider update.
**Assign**: Lambert
**Complexity**: M (Medium)

## Priority Order

| # | Item | Decision | Phase |
|---|------|----------|-------|
| 1 | OpenAPI Migration | ✅ Already done — delete dead code | Now |
| 2 | Camera Control | ❌ Won't fix — close TODOs | Now |
| 3 | Tag Support | ⚠️ Defer — projects are better | Now |
| 4 | Slicer Artifacts | ✅ Implement full artifact pipeline | 3E |
| 5 | OrcaSlicer Types | ✅ Implement profile/settings/manifest | 3E |

## Immediate Actions (Can do now)

- [ ] Delete `ExampleSchemaFilter.cs` (dead code)
- [ ] Close camera control TODOs with explanation
- [ ] Close tag support TODO with explanation

## Phase 3E Work Items (Future sprint)

- [ ] Artifact entity + EF Core migration
- [ ] ArtifactsController (upload/download)
- [ ] SlicingArtifactKeys constants
- [ ] HttpJobPollerService multi-artifact upload
- [ ] OrcaSlicerProfile + OrcaSlicerSettings types
- [ ] Manifest parsing in OrcaSlicerAssetRegistry
- [ ] OrcaSlicerUIProvider type references
- [ ] Frontend thumbnail display

## References

- `.squad/decisions/inbox/dallas-blocked-items-architecture.md`
- `.squad/decisions/inbox/lambert-codebase-analysis.md`
- `.squad/decisions/inbox/brett-competitor-research.md`
