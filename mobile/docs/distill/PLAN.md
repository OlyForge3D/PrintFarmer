# iOS distill: Farm · Queue · Filament

Epic: [#3236](https://github.com/OlyForge3D/PrintFarmer/issues/3236). Visual reference: [mockups.html](mockups.html), opened locally in a browser.

## Problem

The iOS app has grown into a second web UI.

- It has three navigation shells (current, simple, and twoModes) and a Floor/Oversight split.
- Farm (`PrinterListView`) and Oversight "Fleet" both show the printer list.
- Web-grade analytics are reachable from the phone: Overview, Upkeep, Reports, Dispatch, Locations, History, Timeline and Predictive.
- Attention and Tasks are hubs of their own.
- The printer detail Overview page tries to show too much. It still has the homed-axes X/Y/Z "motion status" badges.

## Product stance

The phone is for the farm floor. Use it to see the farm, run the queue, handle filament, and scan. Anything analytical lives on the web.

This replaces the earlier principle that "floor work and oversight are equals" in `mobile/PRODUCT.md`.

## Information architecture

| Tab | Content |
|---|---|
| **Farm** | One printer list built from unified cards (Fleet is merged in). Printers that need attention show a badge, and a "Needs attention" filter chip narrows the list. |
| **Queue** | Three groups: Printing, then Queued (drag-to-reorder, requires `Queue.Write`), then Recent failures. |
| **Filament** | Spools, with Add spool and Assign to printer. |

- A floating **Scan** button sits above the tab bar on all three tabs. It reads printer and spool barcodes and NFC tags.
- Settings, Account and Servers sit behind the toolbar avatar.
- iPad uses a sidebar with the same three items. It has no Floor/Oversight groups.

## Cut list

Removed code is deleted, not hidden behind flags. Deep links to removed destinations open Farm.

- **Shells:** `NavigationShell`, `OversightMode`, `SidebarSection`, the shell picker and the Two-modes promo. Also the `pf_navigation_established_shell*` keys in `ServerRegistry`, with a one-time cleanup of stored values.
- **Oversight:** `OversightCatalog`, `OversightRowSubtitles`, `Views/Oversight/*` and the fleet root.
- **Destinations:** overview, fleet, upkeep (maintenance, maintenanceAnalytics, uptimeReliability), reports, filamentCoverage, predictiveInsights, dispatchDashboard, locations, jobHistory and jobTimeline.
- **Attention and Tasks:** `AttentionView`, `ShiftTasksView`, their view models and routes, and the gating that enables them. Keep any data service the card badge still uses.
- **Supporting code:** the related deep links, `AppRouter` routes, `TaskActionRoute`, accessibility IDs, XCUI tests and snapshots. Remove view models, services and models only when nothing else references them.

## Printer card

- A 60pt thumbnail (`thumbnailUrl`, or a printer glyph while idle), the name and a state pill.
- The job name, a progress bar, the %, and the time left with the done-at time.
- One compact line with nozzle and bed temperatures and the loaded filament's colour dot.
- An attention badge. An Obico failure shows in the state pill as "Failure suspected".
- **Removed from the card:** the location line, the coverage badge and the standalone Obico shield.
- `PrinterCardView` and `iPadPrinterCardView` merge into one adaptive card that uses `pf*` tokens instead of hard-coded hex values.
- Each card is a single VoiceOver element.

## Printer detail: swipeable pages

The detail view has a segmented header and a page indicator. The toolbar shows "‹ Farm" and "Open in web ↗".

1. **Status:** the camera or job image, state, job, progress, time left, layer, Pause/Resume/Cancel, and read-only temperatures.
2. **Control** (renamed from Controls): heat presets and targets, the jog pad with Home and a compact position readout, fans, Z-offset, and emergency stop. The "Not homed" note appears here only.
3. **Filament:** the loaded spool and the grams left compared with what the job needs. It offers Swap, Scan-to-assign and Unassign. Load and Unload are disabled while the printer is printing, and the page says why.
4. **Queue:** this printer's next job with "Start next job", then the jobs after it.

- The homed-axes badges on Overview (`homedAxesBadges`, `resolvedHomedAxes`) and their tests are removed.
- Odometer, history tail, the Mainsail link, auto-dispatch and setup actions move behind "Open in web".

## Queue reorder endpoint

`PUT /api/job-queue/jobs/{id}/position` with body `{ beforeJobId?, afterJobId? }`. It requires `Queue.Write`.

- The moved job takes its neighbour's priority. `QueuePosition` is renumbered within that priority band in a single transaction.
- Only queued jobs can be moved. The endpoint returns 409 when the neighbour is stale and 400 when the job isn't queued.
- It broadcasts the SignalR queue event. No migration is needed.
- On mobile, `.onMove` applies the change immediately and rolls it back if the request fails.

## Issues

| Issue | Scope | Depends on |
|---|---|---|
| [#3228](https://github.com/OlyForge3D/PrintFarmer/issues/3228) | A: API queue position endpoint | none |
| [#3229](https://github.com/OlyForge3D/PrintFarmer/issues/3229) | B: Web drag-to-reorder (follow-up, outside the epic) | #3228 |
| [#3230](https://github.com/OlyForge3D/PrintFarmer/issues/3230) | C: API current-job thumbnail | none |
| [#3231](https://github.com/OlyForge3D/PrintFarmer/issues/3231) | D1: iOS single shell, Scan button, PRODUCT.md | none |
| [#3232](https://github.com/OlyForge3D/PrintFarmer/issues/3232) | D2: iOS card redesign and homed-badge removal | soft dependency on #3230 |
| [#3233](https://github.com/OlyForge3D/PrintFarmer/issues/3233) | D3: iOS paged detail view | none |
| [#3234](https://github.com/OlyForge3D/PrintFarmer/issues/3234) | D4: iOS queue reorder | #3228 |
| [#3235](https://github.com/OlyForge3D/PrintFarmer/issues/3235) | D5: iOS orphan cleanup and XCUI refresh | #3231–#3234 |

## Validation

- **Mobile:** unit tests plus the affected XCUI suites, as described in `mobile/AGENTS.md`.
- **Backend:** targeted `dotnet test` runs for the changed services and controllers.
- **Review:** each implementation PR touches high-risk paths, so it gets the two-reviewer panel.
