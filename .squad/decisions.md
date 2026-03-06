# Squad Decisions

## Active Decisions

### 1. Hierarchical Location System (Approved)

**Author:** Dallas (Lead/Architect)  
**Date:** 2026-03-07  
**Status:** APPROVED — Phase 1 ready for implementation

#### Problem
PrintFarmer's flat Location entity doesn't scale. Need to support "Warehouse 1 > Room A > Rack 3" organizational hierarchies and user-defined location types.

#### Solution
**Approach C: Adjacency List + Cached Path (Hybrid)**
- Self-referential `ParentId` for structural integrity
- Computed `Path` column for fast queries and breadcrumbs
- LocationType entity for user-defined organizational vocabulary (Building, Floor, Room, Rack, etc.)
- Materialized path cache enables breadcrumb rendering and descendant queries without recursion

#### Key Design Decisions
1. **Arbitrary depth** — not limited to fixed levels (unlike 3DPrinterOS)
2. **User-defined types** — customers define their own organizational vocabulary
3. **Cached path** — single table, fast queries, low maintenance overhead
4. **Printer assignment** — printers can attach to any level (leaf or intermediate)
5. **TotalPrinterCount** — denormalized for reporting (updated on assignment/removal)

#### Entities
- **Location:** ParentId (FK), Path (cached), Depth, SortOrder, LocationTypeId, PrinterCount, TotalPrinterCount
- **LocationType:** Name, Icon (MDI), Color, IsSystem flag (7 seeded types: Building, Floor, Room, Zone, Rack, Shelf, Workstation)
- **Printer:** Unchanged. Still points to Location via LocationId (nullable)

#### Competitive Advantage
- Only competitor with true hierarchy is 3DPrinterOS (3-level, rigid)
- No competitor offers user-defined location types
- This is a market differentiator

#### Phase 1 Scope
- Tree CRUD infrastructure
- Path materialization on create/move
- Tree API: `GET /api/locations/tree`, `POST /api/locations/{id}/children`, `PUT /api/locations/{id}/move`
- Breadcrumb generation
- LocationType management

#### Phase 2 Scope (Future)
- Dispatch scoring integration (location proximity weighting)
- Bulk operations (move subtree, delete subtree)
- Advanced UI (collapse/expand, reorder, visual tree)
- Printer grouping by location (PrinterGroup entity)

#### Dependencies
- None. This is foundational. Dispatch will build on it in Phase 2.

#### Risks & Mitigation
- **Migration complexity:** New columns are nullable; old flat data migrates as root-level nodes with Depth=0, Path="/LocationName"
- **Path cache consistency:** Maintain via service layer; never update Path directly in controller
- **Querying descendants:** Use `Path LIKE '/Warehouse%'` with indexed cache
- **Performance:** Denormalized TotalPrinterCount avoids recursive counts; indexed on Depth and ParentId for fast tree traversals

#### Reference
Full design document: `.squad/decisions/inbox/dallas-location-hierarchy-design.md` (ready for merge on approval)

---

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
