# Two-Pane Layout Proposal

**Author:** Newt (Designer)  
**Requested by:** Jeff Papiez  
**Date:** 2026-06-01  
**Status:** Design analysis, not implementation spec

## Executive Summary

PrintFarmer is already structurally close to a two-pane desktop layout. The
current `Layout.tsx` renders a persistent desktop sidebar and a main content
region inside a full-height shell. The main difference is that the app still has
a separate 48px top header above both panes, so the content pane does not truly
run from the top to the bottom of the viewport.

My recommendation is to treat app-wide two-pane navigation as a separate
initiative after the current reorganization issues (#435-#440). The current
reorg should keep reducing navigation entropy first. A true two-pane migration
is feasible, but it touches global layout, account controls, system status,
mobile behavior, and page-level assumptions.

## Feasibility Assessment

### What the current layout does

`src/Web/ReactApp/src/common/components/Layout.tsx` currently has this shape:

```text
Layout
├── full-height vertical shell
│   ├── top header, 48px tall
│   │   ├── mobile nav button
│   │   ├── PrintFarmer logo
│   │   ├── printer attention indicator
│   │   ├── SignalR connection status
│   │   ├── tasks badge
│   │   ├── notification bell
│   │   └── user/theme menu
│   └── horizontal body split
│       ├── mobile sidebar overlay below the header
│       ├── desktop sidebar, 224px expanded or 56px collapsed
│       └── scrollable main content pane
│           ├── email confirmation banner
│           ├── platform banner
│           ├── install banner
│           └── route outlet
```

The navigation model is already sectioned:

- Operations: Dashboard, Printers, Files, Projects, Slice, Print Queue,
  Auto-Dispatch.
- Hardware: Filament Inventory, NFC Bindings.
- Management: Maintenance, Statistics, Cost Analytics, Analytics, Scheduling,
  API Keys.
- Admin: Printer Groups, Catalog, Workers, System, Settings.
- Dev proposal links for farm admins.

The sidebar already supports:

- Permission, role, slicer, and platform-capability filtering.
- Desktop expanded width of `w-56`.
- Desktop collapsed icon-only width of `w-14`.
- Collapsed flyouts for items with children.
- Local-storage persistence for collapsed state.
- A mobile overlay opened from the top header.

### How close it is to two panes

It is close in structure, but not in final information architecture.

The current desktop body is already a left nav pane plus right content pane. The
app is not a true two-pane layout because the global header sits above both
panes. That header consumes vertical space and owns important global controls
that would need to move somewhere else.

So this is not a ground-up rewrite. It is a medium global-layout migration:

- The shell flex direction changes from "header above split body" to "left rail
  beside full-height content".
- Header-owned controls need a new home.
- Mobile navigation changes because the hamburger currently lives in the top
  header.
- Page templates and banners need spacing review once the content pane starts at
  viewport top.

## Design Proposal

### Design direction

Use an "industrial control rail" model: a calm, persistent command strip on the
left and a full-height operational canvas on the right. Operators should feel
like they are inside a printer farm control room, not moving through a generic
admin website.

### Desktop component structure

```text
AppShell
├── SkipLink
├── AppRail
│   ├── BrandBlock
│   ├── PrimaryNavigation
│   │   ├── NavSection: Operations
│   │   ├── NavSection: Hardware
│   │   ├── NavSection: Management
│   │   └── NavSection: Admin
│   ├── RailStatusCluster
│   │   ├── Printer attention count
│   │   ├── SignalR connection state
│   │   └── Optional system pulse
│   └── AccountCluster
│       ├── Notifications
│       ├── Tasks
│       ├── User menu
│       └── Theme switcher
└── ContentPane
    ├── GlobalBanners
    ├── RouteOutlet
    └── Optional page-local command bar inside PageTemplate
```

### Left rail

Recommended desktop widths:

- Expanded: 248px. The current 224px works, but 248px gives long labels like
  "Filament Inventory" and "Auto-Dispatch" more breathing room.
- Compact icon rail: 64px. The current 56px is efficient but tight for badge
  counts, focused states, and future system pulse affordances.
- Optional dense mode: 224px expanded and 56px compact can remain available for
  small laptops if the team wants to preserve the current footprint.

Recommended behavior:

- Keep the rail persistent at `lg` and above.
- Let operators collapse it to icon-only.
- Persist the collapse choice in local storage, reusing the existing
  `pf_navbar_collapsed` behavior.
- Replace hover-only collapsed flyouts with click/focus-triggered popovers so
  keyboard and touch users can reach child links reliably.
- Keep section labels visible in expanded mode. In icon-only mode, use thin
  separators plus accessible labels on each nav item.

Recommended sections after #435-#440:

- Operations: Dashboard, Printers, Files, Projects, Slice, Print Queue,
  Auto-Dispatch.
- Hardware: Filament Inventory.
- Management: Maintenance, Analytics, Scheduling.
- Admin: Catalog, Workers, System, Settings.

This follows Newt's approved reorganization direction: Analytics becomes one
entry, and configuration-heavy surfaces move into Settings instead of competing
for top-level rail space.

### Content pane

The right pane should own the entire vertical canvas:

- `main` starts at the top of the viewport.
- Page content scrolls independently from the left rail.
- Global banners remain at the top of the content pane.
- `PageTemplate` remains the page title and action surface.
- Page-level actions stay near the page title, not in a global header.

For nested navigation:

- Keep Settings as its own local IA: left category list plus horizontal sub-tabs
  for multi-page sections.
- Keep Analytics as one route with tabs for Statistics, Costs, and Insights.
- Avoid putting second-level app navigation into the global rail. The rail should
  answer "where am I in the app?", while page-local tabs answer "which view of
  this workspace am I in?"

This is especially important for Settings. A global rail plus a Settings
sidebar can work if the Settings sidebar is contained within the content pane.
It should not become a third persistent column across the whole app.

### Header considerations

A true two-pane layout should not keep the current full-width top header. It
would undermine the reason for migrating: reclaiming vertical space.

However, the app still needs the header's jobs:

- Brand and home affordance.
- Mobile menu trigger.
- Printer attention signal.
- Connection status.
- Tasks and notifications.
- User menu.
- Theme selector.

Recommended relocation:

- Brand goes at the top of the rail.
- Printer attention and connection status go in a compact rail status cluster.
- Tasks, notifications, user menu, and theme switcher go at the bottom of the
  rail in expanded mode.
- In collapsed mode, show icon buttons with visible badges and accessible names.
- Page search, if added later, should be page-local unless the team creates a
  true global command palette.

If the team wants a transitional version, use a slim content-pane command bar
only on pages that need it. Do not keep a permanent app-wide header.

### Mobile and responsive behavior

The desktop rail should not squeeze small screens. Recommended behavior:

- Below `lg`, hide the persistent rail.
- Use a top mobile app bar of about 48px with:
  - menu button,
  - PrintFarmer logo/name,
  - attention badge,
  - user or notification affordance.
- Open navigation as a full-height drawer from the left.
- The drawer should include the same section order as desktop.
- Closing behavior:
  - close on route selection,
  - close on Escape,
  - close on backdrop click,
  - trap focus only while the drawer is open.
- Provide a skip link to the main content before repeated navigation.

This means "no separate header bar" is a desktop goal. Mobile still needs a
small top bar because there is no persistent left pane.

## Pros and Cons

### Advantages of true two-pane layout

- More vertical space for monitoring pages, tables, camera-heavy layouts, and
  slicer workflows.
- Navigation becomes a stable instrument panel rather than a header/sidebar mix.
- Collapsed icon rail gives experienced operators fast movement without hiding
  the app structure.
- Status signals can live near navigation, which fits PrintFarmer's operational
  control-room feel.
- The current code already has most rail behaviors, so implementation risk is
  lower than a full navigation rewrite.
- Better fit for the settings reorganization because a calmer main rail makes
  Settings feel intentional, not like a dumping ground.

### Disadvantages and risks

- The current header holds real product controls, not decoration. Moving those
  controls requires careful design and accessibility review.
- Icon-only navigation can become cryptic if too many destinations remain in the
  main rail.
- Collapsed hover flyouts need improvement for keyboard, touch, and assistive
  technology users.
- Some pages may assume the old header spacing and need visual QA.
- Two persistent navigation layers can feel heavy inside Settings unless the
  Settings shell stays clearly page-local.
- Operators on tablets may need a tuned breakpoint strategy; a desktop rail at
  the wrong width could reduce usable printer-grid space.
- The migration is global, so regressions would affect every route.

### Compared with the current layout

| Area | Current layout | True two-pane layout |
|---|---|---|
| Vertical space | Loses 48px to global header | Content uses full desktop height |
| Navigation | Sidebar plus separate top header | One persistent desktop command rail |
| Status controls | Header cluster | Rail status/account clusters |
| Mobile | Header opens sidebar overlay | Mobile app bar opens drawer |
| Learnability | Familiar, already shipped | Slight relearning, cleaner after adoption |
| Implementation risk | Existing behavior | Medium global shell migration |
| Monitoring fit | Good | Better for dense real-time pages |
| Settings fit | Good after #432 | Good if nested nav stays page-local |

## Recommendation

Do not fold this into #435-#440. Those issues are already a navigation
reorganization with enough surface area: Analytics consolidation, Settings
sub-page moves, System status, redirects, and documentation.

Instead, make app-wide two-pane layout a separate initiative after #435-#440
lands. The right sequence is:

1. Finish the current information-architecture cleanup.
2. Measure the reduced main navigation after the cleanup.
3. Prototype the true two-pane shell behind a feature flag or dev route.
4. Validate the shell on Dashboard, Printers, Settings, Analytics, and a dense
   table page.
5. Run keyboard and screen reader checks on expanded rail, collapsed rail,
   popovers, and mobile drawer.
6. Then migrate `Layout.tsx`.

This lets the team separate two questions:

- "What belongs in the main navigation?"
- "What chrome should carry that navigation?"

Answering them separately reduces risk and will produce a cleaner final shell.

## Open Design Questions

- Should the desktop rail include a compact farm health pulse, or should that
  remain page-local until System status is implemented?
- Should collapsed rail child navigation open on click, focus, or both?
- Is 248px acceptable as the expanded rail width, or should PrintFarmer preserve
  the current 224px density?
- Should the account cluster stay in the rail, or should authenticated user
  controls live in a small floating content-pane corner?
- Should the left rail support pinned favorites for high-volume farms, or would
  that add too much complexity before the base IA stabilizes?

## Decision

Two-pane migration is feasible and strategically aligned with PrintFarmer, but
it should be tracked separately from #435-#440. The current reorganization should
stabilize the navigation model first; the two-pane shell should then refine how
that model is presented.
