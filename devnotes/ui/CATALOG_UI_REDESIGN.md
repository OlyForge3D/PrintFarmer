# Catalog UI Redesign Plan

## Overview

Redesign the Catalog page to support full CRUD management of hardware components (Hotends, Extruders, Toolheads, Nozzles) in addition to Printer Models. The new design uses a tabbed interface with contextual manufacturer filtering.

## Current State

- **CatalogPage**: Shows Manufacturers (left) → Printer Models (right)
- **Component Models** (Hotends, Extruders, Toolheads, Nozzles): Read-only API, seeded at DB init
- **No CRUD**: Cannot create/edit/delete component models via UI

## Goals

1. Add tabbed navigation for different catalog categories
2. Implement full CRUD for all component model types
3. Smart manufacturer filtering that doesn't block adding new items
4. Reusable components across all tabs

---

## Wireframes

### Main Catalog Layout with Tabs

```
┌─────────────────────────────────────────────────────────────────┐
│  CATALOG                                                        │
├─────────────────────────────────────────────────────────────────┤
│  [ Printers ] [ Hotends ] [ Extruders ] [ Toolheads ] [ Nozzles]│
├─────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐  ┌─────────────────────────────────────┐   │
│  │ Filter by Mfg   │  │  Hotends                            │   │
│  │                 │  │                                     │   │
│  │ ○ All (47)      │  │  [+ Add Hotend]  ← Opens modal      │   │
│  │ ● With Items(12)│  │                    with ALL mfgs    │   │
│  │                 │  │                                     │   │
│  │ ─────────────── │  │  ┌─────────────────────────────────┐ │   │
│  │ E3D (5)         │  │  │ Dragon HF - Phaetus             │ │   │
│  │ Slice Eng. (3)  │  │  │ Max: 500°C | High Flow          │ │   │
│  │ Phaetus (4)     │  │  └─────────────────────────────────┘ │   │
│  │                 │  │                                     │   │
│  │ [+ Add Mfg]     │  │                                     │   │
│  └─────────────────┘  └─────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

### Add/Edit Modal with Grouped Manufacturer Dropdown

```
┌─────────────────────────────────────────────────────────────┐
│  Add Hotend                                           [×]   │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  Manufacturer *                                             │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ ▼ Select manufacturer...                            │   │
│  ├─────────────────────────────────────────────────────┤   │
│  │ ── With Hotends ──                                  │   │
│  │   E3D                                               │   │
│  │   Slice Engineering                                 │   │
│  │   Phaetus                                           │   │
│  │ ── All Others ──                                    │   │
│  │   Bondtech                                          │   │
│  │   Prusa Research                                    │   │
│  │   Bambu Lab                                         │   │
│  │   ...                                               │   │
│  │ ── + Add New Manufacturer ──                        │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                             │
│  Name *                                                     │
│  ┌─────────────────────────────────────────────────────┐   │
│  │                                                     │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                             │
│  Max Temp (°C)          □ High Flow                        │
│  ┌───────────────┐                                         │
│  │ 500           │                                         │
│  └───────────────┘                                         │
│                                                             │
│  Description                                                │
│  ┌─────────────────────────────────────────────────────┐   │
│  │                                                     │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                             │
│  URL                                                        │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ https://...                                         │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                             │
│                              [Cancel]  [Create Hotend]      │
└─────────────────────────────────────────────────────────────┘
```

### Component Model Card Design

```
┌─────────────────────────────────────────────────────────────┐
│  Dragon HF                                    [Edit] [Del]  │
│  Phaetus                                                    │
├─────────────────────────────────────────────────────────────┤
│  Max Temp: 500°C  |  High Flow: ✓                          │
│  All-metal hotend with high flow capability                │
│  🔗 https://www.phaetus.com/dragon                         │
└─────────────────────────────────────────────────────────────┘
```

---

## API Design

### New Endpoints

| Resource | Endpoint | Method | Description |
|----------|----------|--------|-------------|
| **Manufacturers** | `/api/catalog/manufacturers/by-context?context={type}` | GET | Get manufacturers grouped by whether they have items |
| **Hotends** | `/api/catalog/hotends` | POST | Create hotend |
| | `/api/catalog/hotends/{id}` | PUT | Update hotend |
| | `/api/catalog/hotends/{id}` | DELETE | Delete hotend |
| **Extruders** | `/api/catalog/extruders` | POST | Create extruder |
| | `/api/catalog/extruders/{id}` | PUT | Update extruder |
| | `/api/catalog/extruders/{id}` | DELETE | Delete extruder |
| **Toolheads** | `/api/catalog/toolheads` | POST | Create toolhead model |
| | `/api/catalog/toolheads/{id}` | PUT | Update toolhead model |
| | `/api/catalog/toolheads/{id}` | DELETE | Delete toolhead model |
| **Nozzles** | `/api/catalog/nozzles` | POST | Create nozzle |
| | `/api/catalog/nozzles/{id}` | PUT | Update nozzle |
| | `/api/catalog/nozzles/{id}` | DELETE | Delete nozzle |

### Contextual Manufacturers Response

```json
GET /api/catalog/manufacturers/by-context?context=hotends

{
  "withItems": [
    { "id": "...", "name": "E3D", "itemCount": 5 },
    { "id": "...", "name": "Phaetus", "itemCount": 4 },
    { "id": "...", "name": "Slice Engineering", "itemCount": 3 }
  ],
  "withoutItems": [
    { "id": "...", "name": "Bondtech", "itemCount": 0 },
    { "id": "...", "name": "Prusa Research", "itemCount": 0 },
    { "id": "...", "name": "Bambu Lab", "itemCount": 0 }
  ]
}
```

---

## DTOs

### Backend (C#)

```csharp
// === CREATE DTOs ===
public record CreateHotendModelDto(
    string Name, 
    Guid ManufacturerId, 
    int? MaxTemp, 
    bool IsHighFlow, 
    string? Description, 
    string? Url);

public record CreateExtruderModelDto(
    string Name, 
    Guid ManufacturerId, 
    string? GearRatio, 
    bool IsDirectDrive, 
    string? Description, 
    string? Url);

public record CreateToolheadModelDto(
    string Name, 
    Guid ManufacturerId, 
    string? Description, 
    string? Url);

public record CreateNozzleModelDto(
    string Name, 
    Guid ManufacturerId, 
    int? MaxTemp, 
    bool IsHardened, 
    string? Description, 
    string? Url);

// === UPDATE DTOs ===
public record UpdateHotendModelDto(
    string? Name, 
    int? MaxTemp, 
    bool? IsHighFlow, 
    string? Description, 
    string? Url);

public record UpdateExtruderModelDto(
    string? Name, 
    string? GearRatio, 
    bool? IsDirectDrive, 
    string? Description, 
    string? Url);

public record UpdateToolheadModelDto(
    string? Name, 
    string? Description, 
    string? Url);

public record UpdateNozzleModelDto(
    string? Name, 
    int? MaxTemp, 
    bool? IsHardened, 
    string? Description, 
    string? Url);

// === CONTEXTUAL MANUFACTURERS ===
public record ManufacturersByContextDto(
    IReadOnlyList<ManufacturerWithCountDto> WithItems,
    IReadOnlyList<ManufacturerWithCountDto> WithoutItems);

public record ManufacturerWithCountDto(
    Guid Id, 
    string Name, 
    int ItemCount);
```

### Frontend (TypeScript)

```typescript
// === CREATE DTOs ===
export interface CreateHotendModelDto {
  name: string;
  manufacturerId: string;
  maxTemp?: number;
  isHighFlow: boolean;
  description?: string;
  url?: string;
}

export interface CreateExtruderModelDto {
  name: string;
  manufacturerId: string;
  gearRatio?: string;
  isDirectDrive: boolean;
  description?: string;
  url?: string;
}

export interface CreateToolheadModelDto {
  name: string;
  manufacturerId: string;
  description?: string;
  url?: string;
}

export interface CreateNozzleModelDto {
  name: string;
  manufacturerId: string;
  maxTemp?: number;
  isHardened: boolean;
  description?: string;
  url?: string;
}

// === UPDATE DTOs ===
export interface UpdateHotendModelDto {
  name?: string;
  maxTemp?: number;
  isHighFlow?: boolean;
  description?: string;
  url?: string;
}
// Similar for Extruder, Toolhead, Nozzle...

// === CONTEXTUAL MANUFACTURERS ===
export interface ManufacturersByContextDto {
  withItems: ManufacturerWithCountDto[];
  withoutItems: ManufacturerWithCountDto[];
}

export interface ManufacturerWithCountDto {
  id: string;
  name: string;
  itemCount: number;
}

export type CatalogContext = 'printers' | 'hotends' | 'extruders' | 'toolheads' | 'nozzles';
```

---

## React Components

### New Components

| Component | Location | Purpose |
|-----------|----------|---------|
| `CatalogTabs` | `features/catalog/components/` | Tab navigation wrapper |
| `ManufacturerSelector` | `common/components/` | Grouped manufacturer dropdown with "Add New" |
| `ComponentModelCard` | `features/catalog/components/` | Reusable card for displaying any component |
| `HotendsCatalog` | `features/catalog/components/` | Hotends tab content |
| `ExtrudersCatalog` | `features/catalog/components/` | Extruders tab content |
| `ToolheadsCatalog` | `features/catalog/components/` | Toolheads tab content |
| `NozzlesCatalog` | `features/catalog/components/` | Nozzles tab content |
| `AddHotendModal` | `features/catalog/components/` | Create/Edit hotend modal |
| `AddExtruderModal` | `features/catalog/components/` | Create/Edit extruder modal |
| `AddToolheadModal` | `features/catalog/components/` | Create/Edit toolhead modal |
| `AddNozzleModal` | `features/catalog/components/` | Create/Edit nozzle modal |

### Refactored Components

| Component | Change |
|-----------|--------|
| `CatalogPage` | Wrap in `CatalogTabs`, extract current content to `PrinterModelsCatalog` |

---

## Implementation Checklist

### Phase 1: Backend DTOs & Service
- [x] Create component model DTOs in `src/infra/ComponentModelDtos.cs`
- [x] Add contextual manufacturers DTO (ManufacturersByContextDto)
- [x] Add CRUD methods to `ICatalogService`
- [x] Implement CRUD in `CatalogServiceAdapter`
- [x] Add repository CRUD methods to `ICatalogRepository`
- [x] Implement repository methods in `EfCatalogRepository`

### Phase 2: Backend Controller
- [x] Add contextual manufacturers endpoint (`/api/catalog/manufacturers/by-context/{context}`)
- [x] Add POST/PUT/DELETE for hotends
- [x] Add POST/PUT/DELETE for extruders
- [x] Add POST/PUT/DELETE for toolheads
- [x] Add POST/PUT/DELETE for nozzles

### Phase 3: Frontend Types & API
- [x] Add Create/Update DTOs to `api.ts`
- [x] Add contextual manufacturers types (ManufacturersByContext, CatalogContext)
- [x] Add CRUD methods to `apiClient`
- [x] Add mutation hooks to `useApi.ts`

### Phase 4: Frontend Components
- [x] Create `ManufacturerSelector` component
- [x] Create `CatalogTabs` wrapper (integrated into CatalogPage)
- [x] Extract `PrinterModelsCatalog` from current page
- [x] Create `ComponentModelCard` reusable component

### Phase 5: Component Tabs (one at a time)
- [x] Implement `HotendsCatalog` with Add/Edit/Delete modals
- [x] Implement `ExtrudersCatalog` with Add/Edit/Delete modals
- [x] Implement `ToolheadsCatalog` with Add/Edit/Delete modals
- [x] Implement `NozzlesCatalog` with Add/Edit/Delete modals

### Phase 6: Testing & Polish
- [x] Build verification (API & React build successfully)
- [x] API tests pass (1572/1572)
- [x] React tests pass (474/474)
- [ ] Manual testing of all CRUD operations
- [ ] Accessibility review

---

## Key Design Decisions

### 1. Manufacturer Handling ("Chicken and Egg" Problem)

**Problem**: If we filter manufacturers to "only those with items", users can't add the first item for a new manufacturer.

**Solution**: 
- Left panel filter shows manufacturers with items for browsing
- Add/Edit modals always show ALL manufacturers in dropdown
- Manufacturers grouped into "With Items" and "All Others" sections
- Inline "Add New Manufacturer" option in dropdown

### 2. Shared Components

The `ManufacturerSelector` component is reused across all Add/Edit modals:
- Consistent UX across all catalog tabs
- Single point of maintenance
- Supports context-aware grouping

### 3. Tab Persistence

- Selected tab stored in URL query param (`?tab=hotends`)
- Allows direct linking to specific tabs
- Browser back/forward works correctly

---

## Estimated Effort

| Phase | Estimated Time |
|-------|----------------|
| Backend DTOs & Service | 1-2 hours |
| Backend Controller | 1 hour |
| Frontend Types & API | 30 min |
| ManufacturerSelector | 1 hour |
| Tab Layout Component | 30 min |
| Each Component Tab (×4) | 1-2 hours each |
| Testing & Polish | 1-2 hours |
| **Total** | **10-14 hours** |

---

## Progress Log

| Date | Phase | Status | Notes |
|------|-------|--------|-------|
| 2026-01-19 | Planning | ✅ Complete | Created this design document |
| 2026-01-19 | Phase 1: Backend DTOs | ✅ Complete | Created ComponentModelDtos.cs, ICatalogService CRUD, EfCatalogRepository CRUD |
| 2026-01-19 | Phase 2: Backend Controller | ✅ Complete | Added all CRUD endpoints to CatalogController |
| 2026-01-19 | Phase 3: Frontend Types | ✅ Complete | Added types to api.ts, CRUD methods to apiClient, mutation hooks to useApi.ts |
| 2026-01-19 | Phase 4: Frontend Components | ✅ Complete | ManufacturerSelector, ComponentModelCard, PrinterModelsCatalog extracted |
| 2026-01-19 | Phase 5: Component Tabs | ✅ Complete | All 4 catalogs implemented with full CRUD |
| 2026-01-19 | Phase 6: Testing | 🔄 In Progress | Build passes, 474/474 React tests, manual testing pending |

---

## Implementation Notes

### Files Created (Phase 4-5)

| File | Purpose |
|------|---------|
| `ManufacturerSelector.tsx` | Grouped dropdown with "With Items" vs "All Others", inline Add New modal |
| `ComponentModelCard.tsx` | Generic card with type-specific badges (high flow, direct drive, hardened) |
| `PrinterModelsCatalog.tsx` | Extracted printer/manufacturer master-detail layout |
| `HotendsCatalog.tsx` | Full CRUD grid for hotend models |
| `ExtrudersCatalog.tsx` | Full CRUD grid for extruder models |
| `ToolheadsCatalog.tsx` | Full CRUD grid for toolhead models |
| `NozzlesCatalog.tsx` | Full CRUD grid for nozzle models |

### CatalogPage Refactored

The main CatalogPage now uses a tabbed interface with 5 tabs:
- **Printers** - Original master-detail layout (manufacturers → printer models)
- **Hotends** - Grid with manufacturer filtering, add/edit/delete modals
- **Extruders** - Grid with gear ratio and direct drive indicators
- **Toolheads** - Grid with manufacturer badges
- **Nozzles** - Grid with max temp and hardened indicators

### Backend Enhancement

All Update DTOs now include optional `ManufacturerId` field, allowing users to change the manufacturer when editing a component model (reduced friction per user feedback).
