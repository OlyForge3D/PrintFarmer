# Settings Page Redesign v2

**Issue:** #432  
**Author:** Newt (Industrial UI Designer)  
**Status:** Implementation Spec

## Overview

Redesign the Settings page from a horizontal tab strip to a **vertical sidebar navigation** with **horizontal sub-tabs** for multi-page sections. This improves discoverability, reduces cognitive load, and better accommodates the growing number of settings categories.

---

## Visual Layout

### Desktop Layout (≥768px)

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Settings                                           [🔍 Search...]      │
├──────────────┬──────────────────────────────────────────────────────────┤
│              │  ┌─────────┬─────────────┬───────────────┬─────────┐    │
│  ○ General   │  │ Cameras │ NFC Devices │   Locations   │ Custom  │    │
│  ○ Filament  │  └─────────┴─────────────┴───────────────┴─────────┘    │
│  ● Hardware ←│  ┌──────────────────────────────────────────────────┐   │
│  ○ Slicing   │  │                                                  │   │
│  ○ Notif...  │  │              [ Content Area ]                    │   │
│  ○ Integrat. │  │                                                  │   │
│  ○ Data      │  │         Currently: Cameras settings              │   │
│  ○ Users     │  │                                                  │   │
│              │  │                                                  │   │
│              │  └──────────────────────────────────────────────────┘   │
└──────────────┴──────────────────────────────────────────────────────────┘
   Sidebar          Sub-tabs (only for multi-page sections)
   (240px)                      Content Area
```

### Mobile Layout (<768px)

```
┌────────────────────────────────────┐
│  Settings           [🔍]           │
├────────────────────────────────────┤
│  [ Hardware               ▼ ]      │  ← Category dropdown
├────────────────────────────────────┤
│  ┌────────┬────────┬────────┐     │
│  │Cameras │  NFC   │Locations│     │  ← Sub-tabs (scrollable)
│  └────────┴────────┴────────┘     │
├────────────────────────────────────┤
│                                    │
│       [ Content Area ]             │
│                                    │
└────────────────────────────────────┘
```

---

## Component Hierarchy

```
SettingsShell
├── Header (title + SettingsSearch)
├── SettingsSidebar                    ← NEW: Vertical nav (desktop) / Dropdown (mobile)
│   ├── SidebarNavItem × 8
│   └── Responsive collapse logic
├── Content Area
│   ├── SettingsSubTabs               ← NEW: Horizontal sub-tabs (when section has 2+ pages)
│   │   └── SubTab × N
│   └── SettingsSection
│       └── Page Content (lazy-loaded)
```

### New Components to Create

| Component | Location | Description |
|-----------|----------|-------------|
| `SettingsSidebar` | `components/SettingsSidebar.tsx` | Vertical nav list with icons, active state, mobile dropdown |
| `SettingsSubTabs` | `components/SettingsSubTabs.tsx` | Horizontal tab bar for multi-page sections |

### Files to Update

| File | Changes |
|------|---------|
| `types.ts` | Add `subPages` array to each tab definition |
| `SettingsShell.tsx` | New layout using sidebar + sub-tabs instead of `SettingsTabStrip` |

---

## Sidebar Specifications

### Dimensions
- **Width (desktop):** 240px fixed
- **Width (mobile):** Full-width dropdown selector
- **Item height:** 44px (touch-friendly)
- **Item padding:** 12px horizontal, 10px vertical

### Styling

| State | Background | Text | Border |
|-------|------------|------|--------|
| Default | `transparent` | `text-pf-text-secondary` | none |
| Hover | `bg-pf-bg-1` | `text-pf-text-primary` | none |
| Active | `bg-pf-accent-bg` | `text-pf-text-primary` | `border-l-2 border-pf-accent` |
| Focus-visible | — | — | `ring-2 ring-pf-accent ring-inset` |

### Icons
Each sidebar item includes an icon from `@mdi/js`:

| Category | Icon | MDI Name |
|----------|------|----------|
| General | ⚙️ | `mdiCog` |
| Filament | 🧵 | `mdiPackageVariant` |
| Slicing | 🔪 | `mdiLayers` |
| Hardware | 🔧 | `mdiWrench` |
| Notifications | 🔔 | `mdiBell` |
| Integrations | 🔗 | `mdiNetwork` |
| Data | 💾 | `mdiDatabase` |
| Users | 👥 | `mdiAccountMultiple` |

---

## Sub-Tab Specifications

### Behavior
- **Render condition:** Only when section has ≥2 sub-pages
- **Single-page sections:** No sub-tab bar rendered (content shows directly)

### Dimensions
- **Tab height:** 36px
- **Tab padding:** 16px horizontal
- **Container:** Bottom border `border-b border-pf-border`

### Styling

| State | Background | Text | Border |
|-------|------------|------|--------|
| Default | `transparent` | `text-pf-text-secondary` | none |
| Hover | — | `text-pf-text-primary` | none |
| Active | — | `text-pf-text-primary` | `border-b-2 border-pf-accent -mb-px` |
| Focus-visible | — | — | `ring-2 ring-pf-accent ring-inset` |

---

## Sub-Page Inventory

| Category | Sub-Pages | Show Sub-Tabs? |
|----------|-----------|----------------|
| General | (single page) | ❌ No |
| Filament | (single page) | ❌ No |
| Slicing | Bed Types, Slicer Profiles | ✅ Yes |
| Hardware | Cameras, NFC Devices, Locations, Custom Fields | ✅ Yes |
| Notifications | (placeholder) | ❌ No |
| Integrations | Webhooks | ❌ No |
| Data | Tags, Quotas, Data Management | ✅ Yes |
| Users | User Accounts, API Keys, Login Audit | ✅ Yes |

---

## URL Routing Plan

### Current (Query Params)
```
/settings?tab=hardware
/settings?tab=slicing
```

### New (Path-Based)
```
/settings/general
/settings/filament
/settings/slicing/bed-types
/settings/slicing/profiles
/settings/hardware/cameras
/settings/hardware/nfc
/settings/hardware/locations
/settings/hardware/custom-fields
/settings/notifications
/settings/integrations/webhooks
/settings/data/tags
/settings/data/quotas
/settings/data/management
/settings/users/accounts
/settings/users/api-keys
/settings/users/audit
```

### Redirects (Backwards Compatibility)
Route legacy query params to new paths:

| Legacy | New Path |
|--------|----------|
| `?tab=general` | `/settings/general` |
| `?tab=hardware` | `/settings/hardware/cameras` (first sub-page) |
| `?tab=slicing` | `/settings/slicing/bed-types` |
| `?tab=data` | `/settings/data/tags` |
| `?tab=users` | `/settings/users/accounts` |

**Implementation note:** The redirect logic will be handled in `App.tsx` in a separate PR to avoid stale-branch issues.

---

## Responsive Breakpoints

| Breakpoint | Behavior |
|------------|----------|
| ≥768px | Sidebar visible on left, content on right |
| <768px | Sidebar collapsed to dropdown selector above content |

### Mobile Dropdown Behavior
- Dropdown shows current category name with chevron
- On click: opens dropdown menu listing all categories
- Category selection closes dropdown and navigates

---

## Search Integration

### Current Behavior
- Filters visible tabs in horizontal strip
- Shows only matching tabs

### New Behavior
- Highlights matching categories in sidebar
- Highlights matching sub-pages in sub-tab bar
- Non-matching items dimmed but still clickable
- Search clears: all items return to normal state

### Implementation
```typescript
// Filter returns matching category IDs and sub-page paths
const { matchingCategories, matchingSubPages } = filterSettings(query);

// Sidebar items get `isMatch` prop
<SidebarNavItem isMatch={matchingCategories.includes(category.id)} />

// Sub-tabs get `isMatch` prop
<SubTab isMatch={matchingSubPages.includes(page.path)} />
```

---

## Accessibility Requirements

### Keyboard Navigation

| Key | Sidebar | Sub-Tabs |
|-----|---------|----------|
| `Tab` | Enter/exit sidebar | Enter/exit tab list |
| `↑/↓` | Navigate between categories | — |
| `←/→` | — | Navigate between sub-tabs |
| `Enter` | Activate category | Activate sub-tab |
| `Home` | Jump to first item | Jump to first tab |
| `End` | Jump to last item | Jump to last tab |

### ARIA Attributes

**Sidebar:**
```html
<nav aria-label="Settings categories">
  <ul role="list">
    <li>
      <a 
        href="/settings/hardware"
        aria-current="page"      <!-- active item only -->
        aria-expanded="true"     <!-- if has sub-pages -->
      >
        Hardware
      </a>
    </li>
  </ul>
</nav>
```

**Sub-Tabs:**
```html
<div role="tablist" aria-label="Hardware settings">
  <button 
    role="tab" 
    aria-selected="true"
    aria-controls="panel-cameras"
    id="tab-cameras"
  >
    Cameras
  </button>
</div>
<div 
  role="tabpanel" 
  id="panel-cameras"
  aria-labelledby="tab-cameras"
>
  <!-- content -->
</div>
```

### Focus Management
- On category change: focus moves to first sub-tab (if any) or content heading
- On sub-tab change: focus moves to content area
- Visible focus indicator at all times (`focus-visible:ring-2 ring-pf-accent`)

---

## Transition Plan

### Phase 1 (This PR)
1. Create `SettingsSidebar` component
2. Create `SettingsSubTabs` component
3. Update `SettingsShell` layout
4. Update `types.ts` with sub-page definitions
5. Internal navigation works via component state

### Phase 2 (Separate PR)
1. Update `App.tsx` routes to path-based
2. Add redirect logic for legacy `?tab=X` params
3. Update any deep links in app

---

## Design Tokens Used

All styling uses existing `pf-` prefixed tokens from `index.css`:

```css
/* Backgrounds */
bg-pf-bg-0      /* content area */
bg-pf-bg-1      /* hover states */
bg-pf-accent-bg /* active sidebar item */

/* Text */
text-pf-text-primary
text-pf-text-secondary

/* Borders */
border-pf-border
border-pf-accent

/* Focus */
ring-pf-accent
```

---

## Files Changed

```
src/Web/ReactApp/src/features/settings/
├── components/
│   ├── SettingsSidebar.tsx      ← NEW
│   ├── SettingsSubTabs.tsx      ← NEW
│   ├── SettingsTabStrip.tsx     (keep for reference, unused after migration)
│   └── SettingsSearch.tsx       (unchanged)
├── pages/
│   └── SettingsShell.tsx        ← MODIFIED
└── types.ts                     ← MODIFIED

docs/design/
└── settings-redesign-v2.md      ← NEW (this file)
```
