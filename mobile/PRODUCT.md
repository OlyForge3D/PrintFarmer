# Product

<!-- impeccable:product-schema 1 -->

Scope: the native iOS client in `mobile/`. The PrintFarmer backend and React web
app share this product truth but keep their own design surfaces.

## Platform

ios

## Users

Two audiences of roughly equal weight, confirmed with the maintainer:

- **Floor operators** working the farm physically — walking between printers,
  phone in one hand, harvesting plates, loading filament, clearing failures,
  scanning bins and parts. Hands are busy, attention is split, the device is
  often held one-handed and sometimes with gloves.
- **Farm owners and managers** checking status remotely and making decisions —
  fleet health, dispatch, job history, uptime and reliability, maintenance
  planning, predictive insights, inventory levels.

Neither audience is primary. The app must serve floor work and oversight
without favoring either.

## Product Purpose

Monitor and manage 3D printer farms from iPhone and iPad, across one or more
registered PrintFarmer servers. Success is an operator resolving what needs
attention without hunting for the control, and an owner reading true fleet
state without opening a laptop.

## Positioning

PrintFarmer is a two-tier farm management system: the mobile client is a
first-class operator surface on the same API the web app uses, not a status
viewer bolted onto a dashboard. It is multi-server by design (one device, many
farms/backends), capability-gated per server, and built to keep working on a
shop-floor network that drops.

## Operating Context

- Shop floor and workshop environments: motion, noise, variable lighting, and
  physical tasks interleaved with the phone.
- Local networks, often self-hosted, sometimes behind self-signed HTTPS.
  Connectivity is unreliable; the app carries read caches, an offline action
  queue, and honest staleness banners.
- Real-time fleet events arrive over SignalR (printer status, discovery, slicer
  jobs); the UI is expected to reflect them live.
- Physical identifiers are part of the workflow: QR codes, barcodes, and NFC
  tags on spools, bins, parts, and printers.
- Servers advertise `operatorFeatures` capability flags. Features the server
  disables are omitted from navigation entirely rather than shown disabled, so
  the app's information architecture must survive destinations disappearing.

## Capabilities and Constraints

Confirmed feature surface in the current build:

- **Attention** — ranked feed of items needing operator action, with inline
  actions, camera snapshots, and confirmation dialogs.
- **Farm** — printer list with search, status filter, location filter chips,
  filament-coverage badges; printer detail; advanced controls (jog, preheat,
  home, z-offset, disable motors); predictive insights; auto-dispatch.
- **Tasks** — shift task plan and job list.
- **Scan** — barcode/QR/NFC intake, bin scan, part scan, harvest plate flow,
  printer lookup, offline queue review and retry.
- **Inventory** — filament spool inventory and printed-parts inventory, add
  spool, barcode intake, NFC write.
- **Oversight** — Dashboard, Dispatch dashboard, Maintenance, Maintenance
  analytics, Uptime & reliability, Job history, Job timeline, Locations.
- **Account and system** — Notifications, Settings (theme, push, NFC format,
  account, about), multi-server registry, server editor, connection check,
  certificate trust and pinning, demo mode, sign-out.

Constraints:

- iOS 17+, SwiftUI, Swift Concurrency, MVVM with a repository pattern.
  Xcode 26+.
- Consumes the shared `/api/*` contract: camelCase JSON, string enums. No
  mobile-only DTOs; extend the shared API instead.
- iPhone uses a tab-based compact layout; iPad uses `NavigationSplitView`. Both
  must be served by any structural change.
- Capability flags can remove any non-core destination at runtime.
- Credentials are per-server in the Keychain; server registrations live in
  UserDefaults on-device.

Known problem this record exists to address: features are hard to discover.
Oversight surfaces (Dashboard, Maintenance, Notifications, Settings) are hidden
behind a `⋯` menu that only exists on the Attention screen, the server switcher
sits top-leading on Attention but top-trailing on Farm, and roughly a dozen
destinations have no entry point in the primary navigation at all.

## Brand Commitments

- Name: **PrintFarmer**.
- Accent green `#10b981`, secondary blue `#1d4ed8`; light background `#ffffff`,
  dark background `#0b1020`. Shared with the web app.
- Status vocabulary: success green, warning amber `#d97706`, error red
  `#dc2626`, maintenance purple, assigned teal, homed blue, not-homed orange.
- Light and dark are both first-class; theme is user-selectable
  (system/light/dark).
- Licensed AGPL-3.0-only from PrintFarmer v0.2.3.

## Evidence on Hand

- Working app with the full feature surface listed above (`mobile/PrintFarmer/`).
- Real API contract, SignalR event stream, and capability flags.
- No customer names, testimonials, pricing, benchmarks, or case studies exist.
  Future work must not invent them. Farm names, printer names, and job data used
  in mockups are illustrative and must be labeled as such.

## Product Principles

1. **Two jobs, one app.** Floor work and oversight are equals; neither may be
   demoted to a submenu.
2. **Nothing important lives only in an overflow menu.** If a feature matters,
   it has a findable home.
3. **Honest state over optimistic state.** Stale, offline, degraded, and
   capability-disabled are shown as themselves, never hidden or faked.
4. **Survive absent features.** Any structure must stay coherent when the server
   disables destinations.
5. **Native before novel.** Platform navigation, controls, and gestures win;
   brand lives in tint, type, motion, and content.

## Accessibility & Inclusion

- 44×44 pt minimum touch targets; the codebase already enforces `minHeight: 44`
  on navigation controls.
- Dynamic Type via system text styles; no hard-coded point sizes.
- State is never carried by color alone — staleness and severity already pair
  color with text and accessibility labels.
- Every navigation control carries an accessibility label, hint, and identifier;
  UI tests depend on those identifiers.
- One-handed reach matters: the floor operator's dominant-hand thumb is the
  primary input.
