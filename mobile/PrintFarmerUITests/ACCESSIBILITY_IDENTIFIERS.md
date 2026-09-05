---
post_title: PrintFarmer UI Test Accessibility Identifier Map
author1: Kane
post_slug: print-farmer-ui-test-accessibility-identifiers
microsoft_alias: jpapiez
featured_image: ""
categories:
  - engineering
tags:
  - ios
  - accessibility
  - ui-testing
ai_note: AI-assisted and reviewed against the shipped Swift implementation.
summary: Canonical accessibility identifiers used by PrintFarmer shell UI tests.
post_date: 2026-09-04
---

## Selector Rules

- Select navigation by accessibility identifier, not visible title.
- Compact-width destinations use `tab.<case>`.
- Regular-width iPad destinations use `sidebar.<case>`.
- `shellDestinationButton(tabIdentifier:)` maps a compact tab identifier to
  its iPad sidebar counterpart.
- The helper queries the canonical `tab.*` identifier first. On SwiftUI
  runtimes that expose tab-bar buttons by label only, it falls back to the
  title mapped by this document while keeping identifier-based call sites and
  records an XCTest warning activity so identifier regressions are not silent.
- Screen-specific identifiers remain the same in Simple and Two Modes.
- Scanner identifiers describe the scanner flow, not its former navigation
  location.

## Simple Shell

The Simple shell is currently compact-width only.

The Simple shell opens Dashboard, Maintenance, and other management screens
from the Oversight hub using `oversight.destination.<case>`.

| Destination | iPhone |
| --- | --- |
| Attention | `tab.attention` |
| Farm | `tab.farm` |
| Inventory | `tab.inventory` |
| Oversight | `tab.oversight` |

## iPad Sidebar

Regular width currently preserves the shipping Floor sidebar while the compact
adaptive shells roll out.

| Destination | iPad |
| --- | --- |
| Attention | `sidebar.attention` |
| Farm | `sidebar.farm` |
| Tasks | `sidebar.tasks` |
| Inventory | `sidebar.inventory` |

## Two Modes Shell

The segmented mode picker is `navigation.modeControl`.
UI tests may launch directly in Oversight mode with
`--uitesting-two-modes --uitesting-oversight-mode`; this avoids depending on
gesture delivery to SwiftUI's duplicated mode-control accessibility nodes.

### Floor Mode

| Destination | iPhone |
| --- | --- |
| Attention | `tab.attention` |
| Farm | `tab.farm` |
| Tasks | `tab.tasks` |
| Inventory | `tab.inventory` |

### Oversight Mode

| Destination | iPhone |
| --- | --- |
| Overview | `tab.overview` |
| Fleet | `tab.fleet` |
| Jobs | `tab.jobs` |
| Upkeep | `tab.upkeep` |
| Reports | `tab.reports` |

Two Modes roots expose `oversight.root.<case>`. Their child rows use the same
`oversight.destination.<case>` identifiers as the Simple Oversight hub.

## Oversight Destinations

The shipped cases are:

- `oversight.destination.dashboard`
- `oversight.destination.dispatch`
- `oversight.destination.filamentCoverage`
- `oversight.destination.maintenance`
- `oversight.destination.maintenanceAnalytics`
- `oversight.destination.predictiveInsights`
- `oversight.destination.jobHistory`
- `oversight.destination.jobTimeline`
- `oversight.destination.locations`
- `oversight.destination.uptimeReliability`
- `oversight.destination.navigationSettings`

## Account And Navigation Settings

| Surface | Identifier |
| --- | --- |
| Account toolbar button | `navigation.account` |
| Account screen root | `account.root` |
| Notifications | `account.destination.notifications` |
| Settings | `account.destination.settings` |
| Manage Servers | `account.destination.manageServers` |
| Offline Queue | `account.destination.offlineQueue` |
| Navigation settings row | `settings.navigation` |
| Navigation settings root | `navigation.settings` |
| Automatic layout | `navigation.layout.automatic` |
| Simple layout | `navigation.layout.simple` |
| Two Modes layout | `navigation.layout.twoModes` |

## Relocated Scan And Lookup Flows

| Flow | Identifier |
| --- | --- |
| Inventory scan menu | `inventory.scan` |
| Inventory NFC action | `inventory.scan.nfc` |
| Continuous barcode intake | `inventory.scan.barcodeIntake` |
| Printed-part lookup | `inventory.partLookup` |
| Printed-part lookup row | `inventory.partLookup.row.<sku>` |
| Farm printer lookup | `farm.printerLookup` |
| Farm printer lookup row | `farm.printerLookup.row.<printer-uuid>` |
| Attention harvest bin scan | `attention.item.<attention-id>.action.scanBin` |
| Primary scanner action | `scan.primary` |
| Scanner NFC hint | `scan.nfc.hint` |
