# Bambuddy Live Demo — UI/UX Research Report

**Researcher:** Brett
**Date:** 2026-06-01
**Source:** https://demo.bambuddy.cool (live ephemeral demo session)
**Version observed:** v0.2.5b1

---

## Executive Summary

Bambuddy is the closest direct competitor to PrintFarmer in the Bambu Lab
ecosystem. Their live demo (spin-up, no login, 30-minute session) reveals a
polished, dark-first web app with a collapsible icon+label sidebar, rich card
grids, and a purpose-built `/system` page that shows CPU, memory, disk, app
version, uptime, and database health in one place. The `/stats` page features a
draggable widget dashboard. Settings uses tabbed horizontal navigation with
per-section H2 groups. Every page header uses an icon + H1 pattern with a
subtitle. These patterns are directly adoptable for PrintFarmer with low effort
and high polish payoff.

---

## Navigation Architecture

### Primary Sidebar (vertical, collapsible)

The sidebar is a `<aside>` (complementary landmark) with icon+label links. It
collapses to icon-only. Each nav item carries two `<img>` elements — one for
the inactive state, one for the active state (likely color-swapped SVG). The
sidebar is divided into two zones:

**Top zone (main navigation — 11 items):**

| Nav Item | Route |
|---|---|
| Printers | `/` |
| Filament | `/inventory` |
| Archives | `/archives` |
| Print Queue | `/queue` |
| Projects | `/projects` |
| File Manager | `/files` |
| MakerWorld | `/makerworld` |
| Profiles | `/profiles` |
| Maintenance | `/maintenance` |
| Statistics | `/stats` |
| Settings | `/settings` |

**Bottom zone (utilities, always visible):**

| Item | Notes |
|---|---|
| System | `/system` — not in main nav; only in sidebar footer |
| View on GitHub | External link |
| Keyboard shortcuts | Button with `?` tooltip |
| Light/Dark toggle | Per-session theme switch |
| Version badge | `v0.2.5b1` static text |

**Key behavior:** The "Print Queue" item shows a **live badge** with the count
of queued items (e.g., `3`). This is the only nav item with a live indicator.
The sidebar has a "Collapse sidebar" button with chevron icon.

---

## Page-by-Page Findings

### Printers Dashboard (`/`)

The main dashboard is a printer card grid with several controls:

**Page header pattern:**
```
[icon] Printers                [H1 with icon prefix]
[search box] [filter dropdowns] [sort controls] [size selector] [Select] [Add Printer]
```

**Filter bar elements:**
- Search input ("Search printers...")
- "All statuses" dropdown
- "All locations" dropdown
- "Hide offline" toggle button
- Sort by: Name, ascending/descending
- Card size selector: S / M / L / XL (4 options)
- Select mode button
- Add Printer button

**Printer card anatomy:**
Each card has:
- Printer model thumbnail image
- Printer name (H3) + model label
- Status badge: "Offline" with icon, or running state
- Action buttons: Chamber light toggle, Camera (new window), Plate check toggle,
  Files button
- Per-printer diagnostic: "Run diagnostic" and "OK" buttons

**Simulated printers in demo:**
- Garage P1S (P1S)
- Living Room A1 (A1)
- Office X2D (X2D)
- Workshop H2C (H2C)

All were offline in the demo. The model images are distinct per printer type.

---

### Statistics (`/stats`)

This is one of the most polished pages — a **drag-and-drop widget dashboard**.

**Page controls:**
- Reset Layout button
- Recalculate Costs button
- Export Stats button
- Time range dropdown ("All Time")

**Widget system:**
Each widget has:
- Drag handle ("Drag to reorder" button)
- Widget title (H3)
- Size cycle button ("Size: 1/4 — Click to cycle") — widgets snap between
  1/4, 1/2, and full-width sizes
- Hide widget button (eye icon)

**Available widgets (all observed):**

| Widget | Data shown |
|---|---|
| Quick Stats | Total Prints (18), Print Time (31.2h), Filament Used (677g), Filament Cost ($0.00), Energy Used (4.438 kWh), Energy Cost ($0.67) |
| Success Rate | Donut-style: 88% success, 14 successful / 2 failed / 2 cancelled |
| Time Accuracy | "No time accuracy data yet" placeholder |
| Failure Analysis | 12.5% failure rate, 2/18 prints, trend vs last week, top failure reasons |
| Print Activity | GitHub-style contribution heatmap by day/month |

The **Print Activity heatmap** is particularly striking — it uses the same
column-per-week, row-per-day grid as GitHub's contribution graph. Each cell
shows a tooltip like "11/30/2025: 0 prints".

---

### System Information (`/system`)

This is the page Jeff specifically asked about. It is the most directly relevant
to adding a system status card to PrintFarmer.

**Page layout:** Single scrolling column of cards, each with an icon + H2
section header. Sections:

#### Application
- Version: `v0.2.5b1`
- Uptime: `42d 8h`
- Hostname: `571dad32a4a7`

Each stat is a 3-column row: icon | label | value. Very tight, no wasted space.

#### Support & Troubleshooting
- Debug Logging toggle (Enable / Disable button)
- Support Bundle download (disabled until debug logging is on)
- Numbered workflow instructions (1. Enable → 2. Reproduce → 3. Download → 4. Attach)
- Transparent "What's in the bundle?" / "NOT collected:" disclosure lists

#### Connection Diagnostic
- Per-printer diagnostic row: "Garage P1S 127.0.0.1" + "Run diagnostic" button
- Checks: port reachability, LAN developer mode, Docker network mode, credentials

#### System Health
- Log scanner that flags known issues
- "No known issues found in the last 102 log entries."
- "Re-scan" button

#### Database
Two-row layout:

| Stat | Value |
|---|---|
| Database Engine | SQLite |
| Version | SQLite 3.46.1 |
| Total Archives | 7 |
| Completed | 7 |
| Failed | 0 |
| Printing | 0 |
| Printers | 4 |
| Filaments | 0 |
| Projects | 2 |
| Smart Plugs | 4 |
| Total Print Time | 17h 50m |
| Total Filament Used | 0.61 kg / 605.6 g |

#### Connected Printers
- Count badge: "4 of 4 printers connected"
- List: printer name, model code, status (RUNNING / IDLE)

#### Storage
- Progress bar: "65.8 GB / 150.0 GB" with "84.2 GB free (56.1%)"
- Sub-items: Archive Storage (154.3 MB), Database Size (1.2 MB), File Manager
  (145.5 MB, 7 files, 2 folders)

#### Memory
- Progress bar: "16.3 GB / 62.7 GB" — "46.4 GB available"

#### CPU
- Cores: 12 (12 logical)
- Usage: 3.3%

#### System Details
- Operating System: Linux 6.17.13-3-pve
- Architecture: x86_64
- Python: 3.13.13
- Boot Time: Apr 20, 2026, 06:31 AM

**Visual pattern for all resource sections:**
```
[section icon] [H2 header]
[progress bar: "X GB / Y GB"]
[subtitle: "Z GB available"]
[icon] [label]  [value]
[icon] [label]  [value]
```

---

### Settings (`/settings`)

**Structure:** H1 header, then a **horizontal tab bar** across the top. Content
below is a single-column scroll of H2-grouped sections within the active tab.

**Tab list (12 tabs):**

| Tab | Badge |
|---|---|
| General | — |
| Smart Plugs | 4 (count badge) |
| Notifications | — |
| Workflow | — |
| Filament | — |
| Network | — |
| API Keys | — |
| Virtual Printer | — |
| SpoolBuddy | — |
| Failure Detection | — |
| Authentication | — |
| Backup | — |

**General tab sub-sections (H2 groups):**
- General — Default View, Date Format, Time Format, Default Printer, Reset button
- Appearance — Theme (Dark / Light / System buttons), Dark Mode customization
  (Background, Accent, Style), Light Mode customization
- Archive Settings — various archive retention settings

**Appearance settings detail:**
Dark Mode and Light Mode each have three customizable properties: Background,
Accent, Style. This is CSS variable theming exposed to users.

---

### Maintenance (`/maintenance`)

**Page controls:**
- Status tab / Settings tab toggle
- Subtitle: "All maintenance up to date"

**Per-printer accordion:**
Each printer has a row:
- Printer name (H2) + "All good" status with green check icon
- "Expand" button to show maintenance records
- "0 hours Total Print Time" button (links to print time detail)

Clean, minimal — only surfaces data when there's something to show.

---

### Print Queue (`/queue`)

**Summary stats bar (top, horizontal):**
```
[icon] 0 Printing | [icon] 3 Queued | [icon] 4h 54m Total Queue Time | [icon] 122g Total Queue Weight | [icon] 3 History
```

**Filter controls:**
- Printer dropdown (All / per-printer)
- Status dropdown (All / Pending / Printing / Completed / Failed / Skipped / Cancelled)
- Location dropdown (All / per-room)
- Clear History button

**View switchers:** List view / Timeline view

**Sort algorithm selector:** "SJF" (Shortest Job First) — the sort mode is
exposed as a named algorithm button, not just a sort field dropdown.

**Queue item anatomy:**
Each queued job shows:
- Job name (paragraph) + "View archive" link
- Assigned printer with location label
- Estimated time (e.g., 1h 57m)
- Filament weight (e.g., 29g)
- Schedule mode (ASAP or scheduled time)
- Status badge: Pending / Waiting / Staged
- Action buttons: Start Print, Edit, Cancel

**"Staged" status** means the file has been pre-sent to the printer for ready
pickup.

---

### Archives (`/archives`)

**View modes:** Grid / List / Calendar / Print Log

**Filter bar:**
- Text search
- Date range filter
- Favorites toggle
- Hide Failed toggle
- Hide Duplicates toggle
- **Color filter** — circular color swatch buttons (hex colors): white, black,
  yellow, brown, red, grey, blue, green, beige, silver, sky blue, purple, red2.
  Filters archives by the filament color used.

The **color filter** is a particularly clever pattern — it lets you visually
filter print history by filament color swatches rather than typing color names.

**Archive card anatomy:**
- Thumbnail/3D preview button
- Archive number (#1, #2...)
- Print name with Edit button inline
- Printer name (location label)
- Color swatches for filament used
- "Sliced for [model]" tag

---

## Design Patterns Worth Adopting

### 1. System status card with progress bars

The `/system` page shows every resource in the same pattern:
```
[H2] Storage
  ▓▓▓▓▓▓▓░░░░░░░░░░░░░  65.8 GB / 150.0 GB
  84.2 GB free (56.1%)
  [icon] Archive Storage    154.3 MB
  [icon] Database Size      1.2 MB
```

This is immediately legible. The progress bar gives a visual at-a-glance sense
of saturation; the label rows give the detail. This should be directly adopted
for a PrintFarmer `/system` page.

### 2. Navigation badge for active counts

The Print Queue nav item shows a count bubble: `Print Queue [3]`. This is the
only nav item doing this, which makes it feel purposeful rather than cluttered.
PrintFarmer could add a similar badge to Print Queue (jobs pending) or Printers
(jobs running).

### 3. Page header = icon + H1 + subtitle

Every page uses this pattern:
```
[icon] Page Title
Descriptive subtitle sentence
```

PrintFarmer's pages currently vary. Adopting this consistently would give the
app a more finished feel at low cost.

### 4. Card size selector (S/M/L/XL)

The printer grid has four density modes. Small cards show just name + status;
large cards show full controls. Users with many printers default to a smaller
card; users with fewer prefer larger cards with more context. This is a quality-
of-life UX that scales from a single printer to a 40-printer farm.

### 5. Draggable widget statistics dashboard

The `/stats` page widgets are individually resizable (1/4, 1/2, full), hideable,
and drag-reorderable. This is a higher-effort feature but creates a feeling of
a professional, configurable dashboard rather than a fixed layout. At minimum,
PrintFarmer's stats page should adopt the "Quick Stats" row with icons for Total
Prints, Print Time, Filament Used, and Energy Cost.

### 6. GitHub-style print activity heatmap

The contribution-style calendar grid on `/stats` ("Print Activity") is a simple
but visually distinctive widget. It communicates farm utilization patterns at a
glance. Easy to implement with a CSS grid; the data model is just a `prints[]`
grouped by date.

### 7. Tabbed settings with count badges

Settings is a single page with a horizontal tab bar. Tabs that have configured
items show a count badge (Smart Plugs: 4). This is much better than a nested
sidebar — everything is visible in one scrollable page once a tab is selected.

### 8. Color swatch filter for archives

The hex-color swatch filter on `/archives` is a clever adaptation of the fact
that Bambu Lab's AMS system tracks filament colors. Filtering prints by color
is genuinely useful for filament tracking. PrintFarmer should consider this once
it has filament tracking in place.

### 9. System Health log scanner

The `/system` "System Health" section auto-scans recent logs for known issue
patterns and surfaces them as actionable items. This is a low-cost,
high-perceived-value feature — it makes the system feel "smart" because it
explains problems rather than just showing errors. PrintFarmer could implement
this by maintaining a list of known log patterns (e.g., OrcaSlicer crash
signatures, printer connection timeout messages) and surfacing them as health
alerts.

### 10. Support bundle with explicit privacy disclosure

The support bundle section shows two lists: "Collected" and "NOT collected."
This is a direct trust-building mechanism — users can verify their printer names,
passwords, and IPs won't be in the bundle. PrintFarmer should adopt this if/when
it adds a support bundle feature.

---

## How to Implement a System Status Card in PrintFarmer

Jeff's specific ask: "how could we implement a system status card showing CPU,
memory, disk, service versions."

### Recommended approach

**New route:** `/system` (or expose as a section of Settings admin).

**Backend:** A new `GET /api/system/info` endpoint returning:

```json
{
  "app": {
    "version": "1.x.x",
    "uptime": "3d 14h",
    "hostname": "printfarm-host"
  },
  "cpu": {
    "cores": 8,
    "usagePercent": 12.4
  },
  "memory": {
    "usedBytes": 4294967296,
    "totalBytes": 17179869184
  },
  "disk": {
    "usedBytes": 42949672960,
    "totalBytes": 107374182400,
    "archiveBytes": 161480704,
    "databaseBytes": 1258291
  },
  "services": {
    "api": "healthy",
    "orcaSlicer": "healthy",
    "signalR": "healthy"
  },
  "database": {
    "engine": "SQLite",
    "version": "3.x.x",
    "printerCount": 4,
    "archiveCount": 18
  }
}
```

**Frontend:** A `/system` React page with the same card-per-section pattern
Bambuddy uses. Use a linear progress bar component for storage and memory.
Poll every 30 seconds with a "Refresh" button for manual override.

**Sidebar:** Add "System" as a footer link in the sidebar (not in the main nav),
matching Bambuddy's pattern of keeping it accessible but not primary.

**CPU/memory data source on .NET:** `System.Diagnostics.Process.GetCurrentProcess()`
for app memory; `Environment.ProcessorCount` for CPU cores. For disk, use
`DriveInfo`. For actual CPU percent, use a background `PerformanceCounter`
(Windows) or read `/proc/stat` (Linux). The `psutil`-equivalent in .NET is
a lightweight wrapper around OS APIs.

---

## Polish Observations

**Dark-first:** Bambuddy defaults to dark mode with a warm dark background
(not pure `#000`). The active nav item uses a subtle lighter background with
an accent-colored icon. No harsh borders — they use background color contrast
between sidebar and main content.

**Icon language:** Every major heading, nav item, and stat uses an icon from
what appears to be a consistent custom or licensed icon set. The icons are not
Heroicons or Material icons — they look more like rounded Lucide/Phosphor style.
The double-image pattern per nav item (`img inactive` + `img active`) suggests
they swap the icon SVG on selection, likely changing fill color.

**Spacing:** Generous padding inside cards. Stats inside a section use a
2–4 column icon-label-value grid, not a flat list. The visual rhythm is
consistent across all pages.

**Animations:** Not directly observable from snapshots but the page transitions
on the demo were smooth — no hard cuts. The "Spin up" landing page showed a
session counter (`11 active, 39 of 50 free`) which is itself a quality detail.

**Typography:** H1 at the top of every page, H2 for section breaks, H3 for
card titles. Strict heading hierarchy. Sub-labels are muted (lower contrast),
values are full-contrast white. This creates a natural visual weight hierarchy.

**Error states:** "No known issues found" on system health and "No time
accuracy data yet" on stats widgets are both friendly empty states with soft
language rather than blank divs.

**"Report a Bug" floating button:** Fixed-position button in the bottom-right
corner of every page. Low-intrusion, always available. Useful for beta software.

---

## Recommendations for PrintFarmer (Priority Order)

| Priority | Feature | Effort | Impact |
|---|---|---|---|
| P0 | System status page (`/system`) with CPU, memory, disk, version, uptime | Medium | High |
| P0 | Consistent page header (icon + H1 + subtitle on every page) | Low | High |
| P1 | Print Queue live count badge in sidebar nav | Low | Medium |
| P1 | Card size selector (S/M/L) on Printers dashboard | Low | High (farm scalability) |
| P1 | Quick Stats widget on Statistics page | Low | High (polished feel) |
| P2 | Print Activity heatmap on Statistics | Medium | Medium |
| P2 | Draggable/resizable widget layout on Statistics | High | Medium |
| P2 | Color swatch filter on Archives (once filament tracking is ready) | Medium | Medium |
| P3 | System Health log scanner (known-pattern alerts) | Medium | High (perceived intelligence) |
| P3 | Support bundle with privacy disclosure | Medium | Medium (trust) |

**The single highest-ROI change is the `/system` page.** It directly answers
the question "is my server healthy?" that every farm operator asks when something
goes wrong. Bambuddy exposes this as a first-class page. PrintFarmer should too.

**The second-highest-ROI change is the consistent page header pattern.** Right
now pages feel like they were built independently. A shared `<PageHeader icon
title subtitle />` component applied everywhere would dramatically increase visual
cohesion.

---

## Screenshots Captured

The following screenshots were taken during the research session and saved to the
working directory:

- `bambuddy-homepage.png` — bambuddy.cool marketing page
- `bambuddy-demo-home.png` — demo spin-up page (session counter, Start Demo button)
- `bambuddy-printers-dashboard.png` — main printers grid view
- `bambuddy-statistics.png` — draggable widget stats dashboard
- `bambuddy-settings.png` — tabbed settings page
- `bambuddy-system.png` — system information page (CPU, memory, disk, version)
- `bambuddy-maintenance.png` — per-printer maintenance status accordion
- `bambuddy-queue.png` — print queue with summary stats bar
- `bambuddy-archives.png` — archives grid with color swatch filter
- `bambuddy-filament.png` — filament/inventory page
