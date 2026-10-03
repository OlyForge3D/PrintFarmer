# Product

<!-- impeccable:product-schema 1 -->

Scope: the native iOS client in `mobile/`. The backend and React app share
the API, but the phone and web serve different jobs.

## Platform

ios

## Users and Purpose

**The phone is for the farm floor. The web is the full console.**

Floor operators walk between printers, harvest plates, load filament and
clear failures with one hand and divided attention. Success means seeing the
farm, running its queue, handling filament or scanning a physical identifier
without hunting through an analytical dashboard.

Owners and managers use the web for reporting, maintenance planning,
reliability, predictive insights, dispatch analysis, locations and history.
The mobile app is not a second web console.

## Navigation

- **Farm**: one printer list and printer detail. Notification attention links
  open Farm with its Needs attention filter applied.
- **Queue**: print jobs and their actionable detail.
- **Filament**: spool inventory, add spool and assignment. Capability-enabled
  printed-part stock and quantity adjustments remain a secondary toolbar entry,
  not a fourth tab.
- **Scan**: a floating button above the tab bar on every tab. The shared
  scanner reads barcode/QR and NFC printer and spool tags. Existing barcode
  intake and tagged-spool creation flows are reused.
- **Account avatar**: notifications, Settings, Servers and offline activity.

iPhone has exactly three tabs: Farm, Queue and Filament. Regular-width iPad
has the same three sidebar items. There are no shell preferences,
Floor/Oversight groups, duplicate Fleet destination, Attention or Tasks tabs,
or Two-modes promotion. Retired analytical deep links open Farm.

## Operating Context

- Shop floors have movement, noise, variable lighting and intermittent networks.
- Read caches and the offline action queue must expose honest staleness.
- SignalR supplies real-time printer and job events.
- Physical labels use QR, barcode or NFC.
- One device supports multiple registered servers; credentials are per-server
  in Keychain and registrations are on-device.
- Server capability flags still govern actions and data, not the three core tabs.

## Constraints

- iOS 17+, SwiftUI, Swift Concurrency, MVVM and repository services; Xcode 26+.
- Shared `/api/*` contract: camelCase JSON and string enums.
- No mobile-only DTOs without a genuine contract need.
- Native navigation and minimum 44-point targets on iPhone and iPad.
- Analytical work belongs on the web, not another phone navigation group.

## Brand and Accessibility

- PrintFarmer accent green `#10b981`, secondary blue `#1d4ed8`; light background
  `#ffffff`, dark background `#0b1020`.
- Light, dark and system themes are first-class.
- Status pairs color with text; no motion-status or homed-axes card badges.
- Dynamic Type, VoiceOver labels/hints and stable navigation identifiers.
- One-handed reach is the primary interaction constraint.
- Licensed AGPL-3.0-only from PrintFarmer v0.2.3.

## Product Principles

1. **Floor work first.** See printers, run the queue, handle filament, scan.
2. **One home per job.** No duplicated Farm/Fleet or competing navigation shells.
3. **Honest state.** Offline, stale and unavailable are not success.
4. **Native before novel.** Platform controls and gestures carry the workflow.
5. **Web for depth.** Analytics and administration stay in the full console.

## Evidence

The app consumes the real shared API, SignalR and capability flags.
[The distill plan](docs/distill/PLAN.md) and
[illustrative mockups](docs/distill/mockups.html) define the target surface.
Mock farm names and job data are illustrative; no customer or performance
claims should be invented.
