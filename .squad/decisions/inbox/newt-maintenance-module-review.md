### 2026-03-14: Maintenance Module Design Review & Improvement Proposals

**By:** Newt (Designer — Industrial UI)  
**Requested by:** Jeff Papiez  
**Status:** PROPOSAL — awaiting approval

---

## 1. Current State Analysis

### Module Architecture

The maintenance module is a full-featured fleet maintenance command center organized as a top-level page (`MaintenanceDashboardPage.tsx`) with five tabs:

| Tab | Purpose | Components |
|-----|---------|------------|
| **Dashboard** | Fleet health at a glance — alerts, printer grid, low stock | `FleetMaintenanceOverview`, `MaintenancePriorityList`, `MaintenanceStatusGrid`, `LowStockAlert` |
| **Schedule** | Calendar + timeline of upcoming maintenance | `UpcomingMaintenanceCalendar`, `MaintenanceTimeline` |
| **Library** | Task Catalog + Maintenance Plans (nested sub-tabs) | `TaskCatalogTab`, `MaintenancePlansTabV2` |
| **Inventory** | Spare parts + replacement history | `PartsInventoryTab`, `ComponentReplacementHistory` |
| **Analytics** | Charts, stats, reports | `FleetStatisticsTable`, `MaintenanceTrendsChart`, `MaintenanceCostAnalysis`, etc. |

### Data Model (V3 Hierarchical Architecture)

```
Task (global catalog) ─────────┐
  └─ TaskComponent (parts)     │
                               ├── PlanTask (join entity with sort + overrides)
Plan (curated task bundle) ────┘
  └─ PrinterMaintenanceSchedule (deployment to a printer)
```

**Key entities:**
- **MaintenanceTask** — Global catalog entry. Has interval, priority, scope rules (12 boolean flags like `requiresEnclosure`, `requiresLinearRails`), parts linkage.
- **MaintenancePlan** — Groups tasks. Has scoping fields: `PrinterId`, `PrinterModelId`, `ManufacturerId`, `MotionType`. These exist on the **domain model and API** but are **NOT exposed in the UI form**.
- **PlanTask** — Join entity linking tasks to plans with `SortOrder` and optional interval overrides.
- **PrinterMaintenanceSchedule** — Deployment record binding a plan to a printer. Created via `/api/maintenance/schedules`.
- **MaintenanceComponent** — Physical part in inventory with stock tracking.

### What's Working Well

1. **Dashboard tab** — The ribbon stats bar, priority alert list, and printer status grid provide excellent fleet-level situational awareness. The layout is clean and information-dense.
2. **Calendar/Timeline schedule view** — Good dual-mode (calendar + timeline) for upcoming tasks. Day-click detail panel is intuitive.
3. **Task Catalog** — Category filtering, scope rules, parts linkage, import/export all present. Well-structured CRUD with search.
4. **Export/Import** — Present on both Plans and Inventory tabs. JSON envelope format enables backup/migration.
5. **Design token usage** — Post-sweep, the module is 100% on `pf-*` tokens. Dark theme works correctly throughout.

---

## 2. Pain Point #1: Plan ↔ Printer Association Is Invisible

### Problem

The `MaintenancePlan` domain model already has four scoping fields:
- `PrinterId` — Scope to a specific printer
- `PrinterModelId` — Scope to a printer model (e.g., all Prusa MK4S units)
- `ManufacturerId` — Scope to a manufacturer (e.g., all Prusa printers)
- `MotionType` — Scope to a motion type (e.g., all CoreXY printers)

**The API supports all four. The `CreateMaintenancePlanDto` and `UpdateMaintenancePlanDto` types include all four fields.** But the `PlanFormModal` component only exposes `name`, `description`, and `isActive`. The scoping fields are completely absent from the UI.

Additionally, the "deploy to printer" mechanism (`PrinterMaintenanceSchedule`) exists in the API service layer (`maintenancePlanService.deployPlan()`, `getScheduleDeployments()`) and has a dedicated hook (`useScheduleDeployments.ts`), but **no UI component renders deployment management**. Users cannot deploy a plan to a printer from the maintenance module.

**Result:** Plans are created in a vacuum with no visible printer association — exactly Jeff's frustration.

### Proposed Solution: Plan Scoping + Deploy Flow

#### A. Add Scoping Fields to PlanFormModal

Expand the plan create/edit form with a "Scope" section below the existing name/description:

```
┌─────────────────────────────────────────────────────────────┐
│  New Maintenance Plan                                   ✕  │
├─────────────────────────────────────────────────────────────┤
│  Name *         [Prusa MK4S Preventive Maintenance      ]  │
│  Description    [Monthly checks + annual overhaul       ]  │
│                                                             │
│  ─── Scope ──────────────────────────────────────────────   │
│  Applies to:    [ All Printers          ▾ ]                 │
│                                                             │
│  When "Specific Printer" selected:                          │
│    Printer:     [ Prusa MK4S #01        ▾ ]                 │
│                                                             │
│  When "Printer Model" selected:                             │
│    Model:       [ Prusa MK4S            ▾ ]                 │
│                                                             │
│  When "Manufacturer" selected:                              │
│    Manufacturer: [ Prusa Research       ▾ ]                 │
│                                                             │
│  When "Motion Type" selected:                               │
│    Motion:      [ CoreXY               ▾ ]                  │
│                                                             │
│  When "Printer Group" selected (future):                    │
│    Group:       [ Production Bed 1     ▾ ]                  │
│                                                             │
│  [✓] Active                                                 │
│                                        [Cancel]  [Create]   │
└─────────────────────────────────────────────────────────────┘
```

**Implementation details:**
- Add a `Select` for scope type: "All Printers", "Specific Printer", "Printer Model", "Manufacturer", "Motion Type" (and later "Printer Group")
- Conditionally render the sub-selector based on scope type
- Populate selectors from existing API data: `apiClient.getPrinters()`, catalog manufacturers/models
- Map selection to the existing DTO fields (`printerId`, `printerModelId`, `manufacturerId`, `motionType`)

#### B. Show Scope Badge on PlanRow

In the plan list, display a `Badge` showing the scope:

```
▸ Prusa MK4S Preventive Maintenance              [Printer: MK4S #01] [Active]
  3 tasks (3 active) · Created Mar 14, 2026
```

Scope badge variants:
- 🖨️ `Printer: {name}` — specific printer
- 📦 `Model: {name}` — printer model
- 🏭 `Manufacturer: {name}` — manufacturer
- ⚙️ `Motion: {type}` — motion type
- 🌐 `All Printers` — universal plan

This uses the existing `printerName`, `printerModelName`, `manufacturerName` fields already on `MaintenancePlanDto`.

#### C. Add "Deploy to Printer" Action

Add a deployment flow accessible from two entry points:

1. **Plan row action button** — A "Deploy" icon button next to Edit/Delete on each plan row
2. **Printer-specific page** — On `PrinterMaintenancePage.tsx`, add a "Deploy Plan" button that opens a plan picker

**Deploy Modal wireframe:**
```
┌─────────────────────────────────────────────────────────────┐
│  Deploy Plan to Printer                                 ✕  │
├─────────────────────────────────────────────────────────────┤
│  Plan:    Prusa MK4S Preventive Maintenance                 │
│                                                             │
│  Target Printer:  [ Select printer...           ▾ ]         │
│  Notes:           [ Optional deployment notes       ]       │
│                                                             │
│  Already deployed to: 3 printers                            │
│    • Prusa MK4S #01 (active since Feb 10)                   │
│    • Prusa MK4S #02 (active since Feb 10)                   │
│    • Prusa MK4S #03 (active since Mar 1)                    │
│                                                             │
│                                        [Cancel]  [Deploy]   │
└─────────────────────────────────────────────────────────────┘
```

**Implementation:** Uses existing `useDeployPlan()` hook and `DeployMaintenancePlanDto`. The modal fetches `useScheduleDeployments(undefined, planId)` to show current deployments. The printer selector uses the existing printers list, filtered to exclude already-deployed ones.

#### D. Future: Printer Group Integration

Once `PrinterGroup` is fully mature, add `printerGroupId` as a plan scoping option. This would let users say "Apply this plan to all printers in Production Line A" and auto-deploy the schedule to all group members. The `PrinterGroup` entity already exists (`src/Web/ReactApp/src/types/api.ts` lines 2994-3042).

---

## 3. Pain Point #2: Parts Inventory Inline Editing

### Current Flow

The edit flow for a parts inventory item is:
1. Find the item in the list (scrolling through card-style rows)
2. Click the small pencil (Edit) icon button on the right side of the row
3. A full `ComponentFormModal` opens (XL-sized modal with 8 fields)
4. Make the change (e.g., update stock count from 3 to 5)
5. Click "Save Changes"
6. Modal closes, list refreshes

**For quick stock updates, this is 5 interactions for a 1-field change.** The modal is appropriate for full CRUD (adding a new part with all fields), but grossly disproportionate for incrementing a stock count or correcting a price.

### Proposed Solution: Hybrid Card + Inline Table Mode

#### A. Quick-Edit Inline Stock Controls

Add +/- buttons directly on the inventory card row for the most common operation (stock adjustment):

```
┌──────────────────────────────────────────────────────────────────┐
│  LM8UU Linear Bearing            [Bearings]  [Low Stock]        │
│  SKU: LM8UU-01 · Amazon · $2.50/ea                              │
│                                                                  │
│  Stock: [−]  3  [+]  (min: 5)              [✎ Edit]  [🗑 Delete] │
└──────────────────────────────────────────────────────────────────┘
```

- The `[−]` and `[+]` buttons call `useUpdateComponent` with just the `inStock` field changed
- Debounce rapid clicks (300ms) to batch into a single API call
- Optimistic UI update — show new count immediately, rollback on error
- Toast on error only (no success toast for +/- — too noisy)

#### B. Full Table View Mode (Toggle)

Add a view mode toggle in the toolbar (similar to Printers page's collapsed/detailed/table toggle):

```
Toolbar: [Search...] [Category ▾] [+ Add Part] [Export] [Import] [Cards 📇 | Table 📊]
```

**Table view columns:**

| Name | Category | SKU | Supplier | Cost | Stock | Min | Actions |
|------|----------|-----|----------|------|-------|-----|---------|
| LM8UU Linear Bearing | Bearings | LM8UU-01 | Amazon | $2.50 | `[3]` | `[5]` | ✎ 📋 🗑 |

**Inline editing rules:**
- **Editable cells** (click to edit): Stock, Min Stock, Unit Cost, Name, SKU, Supplier
- **Non-editable** (click opens modal): Description, Category, URL
- **Cell edit interaction**: Click cell → transforms to `Input` → type new value → blur or Enter saves → Escape cancels
- **Row-level save**: Changes are saved per-cell on blur (no row-level save button needed — each field is independent)
- **Keyboard navigation**: Tab moves to next editable cell in the row, Shift+Tab moves back. Enter commits and moves down. Escape cancels edit.
- **Visual feedback**: Editing cell gets a `border-pf-accent` focus ring. Saving shows a brief checkmark flash. Error shows red border + tooltip.
- **Pending state**: While saving, show a subtle spinner in the cell. Disable navigation away from that cell until save completes.

**Implementation approach:**
- Create an `EditableCell` component wrapping `Input` with blur-save, keyboard nav, and optimistic update logic
- The table view renders `EditableCell` for editable columns, static text for non-editable
- Each cell's blur handler calls the existing `useUpdateComponent` mutation with the full updated DTO
- Use `onKeyDown` for Enter/Escape/Tab handling

#### C. Bulk Stock Adjustment

Add a "Bulk Update" toolbar button that shows when multiple items are selected (add row checkboxes in table view):

```
[✓ 4 selected]  [Adjust Stock +/-]  [Set Category]  [Delete Selected]
```

This is a Phase 2 enhancement but worth noting in the architecture.

---

## 4. Pain Point #3: Clone/Duplicate Items

### Where Clone Buttons Should Appear

| Entity | Location | Button Placement |
|--------|----------|-----------------|
| **Parts (Components)** | Inventory card row + table row | Icon button `📋` between Edit and Delete |
| **Tasks (Catalog)** | Task card row in Task Catalog tab | Icon button `📋` between Edit and Delete |
| **Plans** | Plan row in Maintenance Plans tab | Icon button `📋` between Edit and Delete |

### Clone Flow

**Parts Clone:**
1. User clicks `📋` on "V6 Brass Nozzle 0.4mm"
2. System creates: `{ ...original, name: "V6 Brass Nozzle 0.4mm (Copy)", id: newGuid }`
3. The `ComponentFormModal` opens pre-filled with cloned data, name highlighted for renaming
4. User edits name to "V6 Hardened Steel Nozzle 0.4mm", adjusts fields
5. Clicks "Save" → creates new component
6. Toast: "Part cloned successfully"

**Task Clone:**
1. User clicks `📋` on "Replace hotend nozzle"
2. `TaskFormModal` opens pre-filled with cloned data, name "Replace hotend nozzle (Copy)"
3. User renames, adjusts scope rules or interval
4. Saves → new catalog task created
5. **Parts associations are preserved** — cloned task keeps the same component links

**Plan Clone:**
1. User clicks `📋` on "Prusa MK4S Preventive Maintenance"
2. `PlanFormModal` opens pre-filled: "Prusa MK4S Preventive Maintenance (Copy)"
3. User renames, adjusts scope
4. Saves → new plan created **with all PlanTask links duplicated** (same tasks, same sort order)
5. Deployments are NOT cloned (deployments are printer-specific runtime state)

### Naming Convention for Clones

- Append ` (Copy)` to the original name
- If ` (Copy)` already exists, append ` (Copy 2)`, ` (Copy 3)`, etc.
- Pre-select the name text in the modal so the user can immediately type a replacement
- Truncate if the resulting name would exceed `maxLength` (200 chars for most fields)

### Implementation

For Parts and Tasks, the clone is purely client-side — pre-fill the existing create modal with data from the source item. No new API endpoint needed.

For Plans, duplicating the PlanTask links requires either:
- **Option A (simple):** Clone the plan via the existing create endpoint, then loop through original PlanTasks and call create-task for each. Multiple API calls but works today.
- **Option B (ideal):** Add a `POST /api/maintenance/plans/{id}/clone` endpoint that deep-copies the plan + PlanTask links server-side in a single transaction. Returns the new plan.

**Recommendation:** Start with Option A (no backend changes), add the clone endpoint in a follow-up if the N+1 API calls cause noticeable latency.

---

## 5. Additional Observations

### 5a. PlanFormModal Is Missing All Scoping Fields

As noted in Pain Point #1, the `PlanFormModal` only shows Name, Description, and Active toggle. The domain model's `PrinterId`, `PrinterModelId`, `ManufacturerId`, and `MotionType` fields are not rendered. This is the most impactful single fix — it addresses Jeff's #1 pain point.

### 5b. No "Deploy Plan" UI Anywhere

The `useScheduleDeployments` hook exists, the `useDeployPlan` mutation exists, the service methods exist, but no component renders a deploy button or a deployment list. The Schedule Deployments API is fully implemented but has zero frontend exposure. This makes the entire Plan → Printer connection invisible.

### 5c. Library Tab Default Sub-Tab Is Backwards

The Library tab defaults to the "tasks" sub-tab (`defaultTab="tasks"`) but shows the "Maintenance Plans" tab as the first visual tab. This means clicking "Library" shows the Task Catalog content behind the Plans tab header. Confusing. Should either:
- Default to `"plans"` (since it's visually first), or
- Swap the tab order so Tasks is visually first

### 5d. Inventory Card Layout Wastes Horizontal Space

The current inventory card layout uses a vertical card per item. In a table view (proposed in Pain Point #2), the same information would be scannable ~3x faster. Power users managing 50+ parts need the density of a table.

### 5e. Task Catalog Lacks Clone

Jeff specifically mentioned wanting to "split out the types of thermistors or hotends into multiple, distinct products." The Task Catalog has identical CRUD patterns to Parts Inventory but no clone action. Adding clone here would let users rapidly create variant tasks (e.g., "Replace V6 nozzle" → clone → "Replace Volcano nozzle").

### 5f. PrinterMaintenancePage Doesn't Show Deployed Plans

The printer-specific maintenance page (`PrinterMaintenancePage.tsx`) fetches `scheduleDeployments` but only lists them in a basic format. It should show which plans are deployed, with the ability to undeploy or deploy additional plans. This would close the loop from Printer → Plan visibility.

### 5g. No Keyboard Shortcut for Common Actions

For an operator workflow, keyboard shortcuts would accelerate common actions:
- `N` — New item (context-sensitive: plan, task, or part depending on active tab)
- `E` — Edit selected item
- `D` or `Delete` — Delete selected item (with confirmation)
- `/` — Focus search
- `Ctrl+E` — Export

This is a Phase 2 enhancement but worth designing for.

---

## 6. Wireframe Descriptions for Implementation

### Wireframe 1: Enhanced PlanFormModal with Scope Section

**Location:** `MaintenancePlansTabV2.tsx` → `PlanFormModal`

**Layout (top to bottom):**
1. **Header:** "New Maintenance Plan" / "Edit Plan"
2. **Name field** — `Input`, required, max 200 chars (existing)
3. **Description field** — `Textarea`, 3 rows, max 1000 chars (existing)
4. **Horizontal rule separator** with label "Scope"
5. **Scope type selector** — `Select` with options:
   - "All Printers (Universal)" (default, sets all scope fields to null)
   - "Specific Printer" → shows printer `Select` populated from `apiClient.getPrinters()`
   - "Printer Model" → shows model `Select` populated from catalog
   - "Manufacturer" → shows manufacturer `Select` populated from catalog
   - "Motion Type" → shows motion type `Select` (Cartesian, CoreXY, Delta, Polar)
6. **Conditional sub-selector** — appears below scope type, context-dependent
7. **Active checkbox** — existing `Checkbox` component
8. **Footer buttons** — Cancel (secondary) + Create/Save (primary)

**State management:** Add `scopeType` state (enum), plus `selectedPrinterId`, `selectedModelId`, `selectedManufacturerId`, `selectedMotionType` states. On submit, map scope type to DTO fields.

### Wireframe 2: Deploy Plan Modal

**Location:** New component `DeployPlanModal.tsx` in maintenance/components/

**Trigger:** "Deploy" icon button (🚀) on each `PlanRow`, OR "Deploy Plan" button on `PrinterMaintenancePage`

**Layout:**
1. **Header:** "Deploy Plan to Printer"
2. **Plan info** — Read-only display: plan name, scope badge, task count
3. **Printer selector** — `Select` filtered to exclude already-deployed printers
4. **Notes field** — `Input`, optional
5. **Current deployments section** — List of `PrinterMaintenanceScheduleDto` records for this plan, each showing printer name, deploy date, and a "Remove" (❌) button
6. **Footer:** Cancel + Deploy (primary)

### Wireframe 3: Parts Inventory Table View

**Location:** `PartsInventoryTab.tsx` — add `viewMode` state ('cards' | 'table')

**Toolbar addition:** View mode toggle buttons (Cards icon / Table icon) at end of toolbar

**Table columns (left to right):**
1. **Checkbox** — row selection (for future bulk actions)
2. **Name** — `EditableCell`, left-aligned, truncate with tooltip
3. **Category** — `Badge`, non-editable (click to filter by this category)
4. **SKU** — `EditableCell`, monospace font
5. **Supplier** — `EditableCell`, left-aligned
6. **Unit Cost** — `EditableCell`, right-aligned, `$` prefix, number input
7. **Stock** — `EditableCell` with `[−]` / `[+]` stepper buttons flanking the number, warning color if below min
8. **Min Stock** — `EditableCell`, right-aligned
9. **Actions** — Edit (full modal), Clone (📋), Delete (🗑)

**Row styling:**
- Default: `bg-pf-bg-2`, `border-pf-border`
- Low stock: `border-pf-warning/40`, stock cell text in `text-pf-warning`
- Hover: `border-pf-accent/30`
- Editing cell: Focus ring `ring-pf-accent`, slightly expanded padding

### Wireframe 4: Clone Button Placement

**Parts Inventory (both card and table views):**
```
Actions column: [✎ Edit] [📋 Clone] [🗑 Delete]
```
Clone button uses `CopyIcon` from MdiIcons, variant `"subtle"`, size `"sm"`.

**Task Catalog:**
```
Task row actions: [✎ Edit] [📋 Clone] [🗑 Delete]
```
Same icon and sizing.

**Maintenance Plans:**
```
Plan row header actions: [✎ Edit] [📋 Clone] [🚀 Deploy] [🗑 Delete]
```
Clone and Deploy are new buttons. Deploy uses a rocket/upload icon.

### Wireframe 5: Plan Row with Scope Badge

**Current plan row:**
```
▸ Prusa MK4S Preventive Maintenance              [Inactive]  [Default]
  3 tasks (3 active) · Created Mar 14, 2026
```

**Proposed plan row:**
```
▸ Prusa MK4S Preventive Maintenance  [Model: Prusa MK4S] [Active] [Default]
  3 tasks (3 active) · Deployed to 3 printers · Created Mar 14, 2026
                                                   [✎] [📋] [🚀] [🗑]
```

The scope badge uses the existing `Badge` component with `variant="default"`. The "Deployed to N printers" text is derived from a lightweight count query (or included in the plan DTO response).

---

## 7. Implementation Priority

| Priority | Item | Effort | Impact |
|----------|------|--------|--------|
| **P0** | Add scope fields to PlanFormModal | Small (form fields + state) | **Critical** — directly answers Jeff's #1 complaint |
| **P0** | Show scope badge on PlanRow | Tiny (conditional Badge render) | Visibility of plan scope |
| **P1** | Build DeployPlanModal | Medium (new component + hook wiring) | Completes the Plan→Printer connection |
| **P1** | Add Clone button to Parts Inventory | Small (pre-fill existing modal) | Directly answers Jeff's #4 request |
| **P1** | Add Clone button to Task Catalog | Small (same pattern) | Directly answers Jeff's #4 request |
| **P1** | Add Clone button to Plans | Small-Medium (need PlanTask deep copy) | Answers Jeff's #4 request |
| **P2** | Add inline +/- stock controls on cards | Small (stepper buttons + mutation) | Quick win for Jeff's #2 complaint |
| **P2** | Add table view mode to Parts Inventory | Medium-Large (EditableCell component + table layout) | Full answer to Jeff's #3 request |
| **P2** | Fix Library tab default sub-tab | Tiny (change `defaultTab` prop) | Polish |
| **P3** | Keyboard shortcuts | Medium (global shortcut handler) | Operator workflow speed |
| **P3** | Bulk stock adjustment | Medium (multi-select + batch mutation) | Power user feature |
| **P3** | Plan clone API endpoint | Small (backend) | Eliminates N+1 API calls for plan clone |

---

## 8. Summary of Recommendations

1. **Expose the existing scope fields** — The hardest part (backend + API) is already done. The PlanFormModal just needs the UI wired up.
2. **Build the Deploy flow** — The hooks and service methods exist. A single modal component connects plans to printers visibly.
3. **Clone is a pre-fill pattern** — No new API needed for Parts/Tasks. Open the create modal pre-filled with source data.
4. **Table view is a significant effort** — The `EditableCell` component needs careful keyboard handling. Deliver inline +/- stepper buttons on cards first (P2a), then table mode (P2b).
5. **Fix the Library tab ordering** — A 1-line change that reduces confusion.
