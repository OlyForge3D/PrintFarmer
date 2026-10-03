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
- **`AppTab` cases:** overview, fleet, upkeep and reports. `OversightCatalog`'s exhaustive switches over them go with the catalog.
- **`AppDestination` cases:** maintenance, maintenanceAnalytics, uptimeReliability, filamentCoverage, predictiveInsights, dispatchDashboard, locations, jobHistory and jobTimeline.
- **Attention and Tasks:** `AttentionView`, `ShiftTasksView`, their view models and routes, and the client-side tab gating that shows them. Keep any data service the card badge still uses.
  - `SystemCapabilities.attentionEnabled` stays. It is decoded from the shared server capabilities contract; only the client tab gating that reads it is removed.
- **Supporting code:** the related deep links, `AppRouter` routes, `TaskActionRoute`, accessibility IDs, XCUI tests and snapshots. Remove view models, services and models only when nothing else references them.
  - **Exception:** `DeepLinkHandler.parse` keeps its `attention` case. The server still emits `printfarmer://attention/{id}` in native push payloads (`AttentionDeepLinks.cs`), so `DeepLinkDestination.attentionItem` must still parse and now routes to Farm with the "Needs attention" filter applied. Dropping the case would make a tapped notification do nothing.

## Printer card

- A 60pt thumbnail (or a printer glyph while idle), the name and a state pill.
  - The image does **not** come from `thumbnailUrl`. That field is `[JsonIgnore]` on `PrinterDto` and `PrinterStatusDto` because it is an internal-network URL, and `SensitiveSerializationTests` asserts it never reaches JSON.
  - #3230 adds an authenticated proxy, `GET /api/printers/{id}/current-job/thumbnail`, in the same style as the camera-snapshot route, and serializes only a relative `currentJobThumbnailUrl` with a cache-bust token. The card loads that URL with the session's auth.
  - #3232 therefore has a **hard** dependency on #3230.
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

- **Camera polling:** `PrinterDetailView` currently gates snapshot polling on `isOverviewPageForeground`, a #2522 review fix so that leaving Overview stops polling immediately. With four pages, that predicate is re-derived from the selected page so that **only Status polls**. Moving to Control, Filament or Queue stops polling, and returning to Status restarts it. A unit test covers each page transition.
- The homed-axes badges on Overview (`homedAxesBadges`, `resolvedHomedAxes`) and their tests are removed.
- Odometer, history tail, the Mainsail link, auto-dispatch and setup actions move behind "Open in web".

## Queue reorder endpoint

`PUT /api/job-queue/jobs/{id}/position` requires `Queue.Write`.

**Request**

- An `If-Match` header carrying the moved job's ETag, read with the controller's existing `ReadIfMatch()` helper.
- The body is either `{ beforeJobId, beforeJobETag }` or `{ afterJobId, afterJobETag }`. Exactly one neighbour must be set, and each neighbour ID needs its matching ETag. If neither or both are set, the endpoint returns 400.

**Authoritative ordering**

Reordering changes the actual dispatch order, not just the list on screen.

- `QueueOrdering.OrderByPriorityDescending()`, both its `IQueryable` and `IEnumerable` forms, becomes `Priority desc → QueuePosition asc → QueuedAt asc → Id asc`.
- Dispatch, ready-head, batch, skip, cancellation, bed-clear and the queue list already share that selector, so the queue list and dispatch can't disagree.
- Existing rows already have a `QueuePosition` value from `QueuePositionAllocator`, so no migration is needed. Any ties are still broken by `QueuedAt` and then `Id`, so the current order is kept.

**Mutation**

All of this happens in one transaction.

1. Check the moved job's ETag and the neighbour's ETag.
2. The moved job takes the neighbour's priority.
3. Reassign `QueuePosition` within the moved job's **queue scope**, then bump the row versions of every job whose position changed.
4. Broadcast the SignalR queue event.

**Position scope and collision safety**

`QueuePosition` is scoped per assigned printer, not per priority band.

- `UX_PrintJobs_Printer_QueuePosition` is a unique index on `(AssignedPrinterId, QueuePosition)`, filtered to `AssignedPrinterId IS NOT NULL AND Status IN (0, 1)`. `QueuePositionAllocator` hands out monotonic values per printer from the `QueuePositionState.NextPosition` watermark, using `Guid.Empty` as the scope for unassigned jobs.
- The queue scope is the moved job's `AssignedPrinterId`, or the unassigned scope. The neighbour must be in the same scope; otherwise the request gets 409.
- The service **permutes the scope's existing position values**; it does not invent new ones. It takes the queued jobs in the scope in authoritative order, applies the move, and hands the same sorted set of position values back out in the new order.
  - Printing (`Status = 1`) rows are untouched, so they keep their values.
  - No value exceeds the current maximum, so the `NextPosition` watermark stays ahead of every assigned position and needs no reconciliation. The next enqueue cannot collide.
- Writes are two-phase inside the transaction, so the unique index never sees a transient duplicate. Phase 1 moves each affected row to a negative temporary value (`-QueuePosition`) and saves. Phase 2 writes the final values and saves.
- Unassigned jobs are outside the filtered index, so nothing enforces their uniqueness. They use the same permutation, and ordering ties still fall back to `QueuedAt` and then `Id`.
- Tests cover a move on an assigned printer that holds a Printing job, a move in the unassigned scope, and an enqueue after a reorder that gets a fresh, non-colliding position.

**Status codes**

These follow the controller's existing `MapRevisionException` mapping.

| Status | When |
|---|---|
| 428 | The `If-Match` header or a neighbour ETag is missing. |
| 412 | The moved job's or the neighbour's ETag is stale, or EF raises `DbUpdateConcurrencyException`. The response includes the current ETags. |
| 409 | A semantic conflict: the moved job or the neighbour isn't `Queued`, the neighbour is the moved job, or the neighbour is outside the moved job's queue scope. |
| 404 | The moved job or the neighbour doesn't exist. |
| 400 | The neighbour fields are malformed. |

**Tests**

- `PriorityQueueOrderingTests`: `QueuePosition` breaks ties within a priority band, and both overloads agree.
- Dispatch and ready-head tests: after a reorder, the next job dispatched matches the order shown in the UI.
- Controller and service tests: every status code above, plus two concurrent moves where the second gets 412.

**Mobile**

`.onMove` applies the move immediately and sends the ETags it saw in the list.

- On 412 or 409, it rolls back, refetches the queue and shows "Queue changed — refreshed".
- On any other failure, it rolls back and shows an error.
- Unit tests cover the rollback paths.

## Issues

| Issue | Scope | Depends on |
|---|---|---|
| [#3228](https://github.com/OlyForge3D/PrintFarmer/issues/3228) | A: API queue position endpoint | none |
| [#3229](https://github.com/OlyForge3D/PrintFarmer/issues/3229) | B: Web drag-to-reorder (follow-up, outside the epic) | #3228 |
| [#3230](https://github.com/OlyForge3D/PrintFarmer/issues/3230) | C: API current-job thumbnail | none |
| [#3231](https://github.com/OlyForge3D/PrintFarmer/issues/3231) | D1: iOS single shell, Scan button, PRODUCT.md | none |
| [#3232](https://github.com/OlyForge3D/PrintFarmer/issues/3232) | D2: iOS card redesign and homed-badge removal | #3230 |
| [#3233](https://github.com/OlyForge3D/PrintFarmer/issues/3233) | D3: iOS paged detail view | none |
| [#3234](https://github.com/OlyForge3D/PrintFarmer/issues/3234) | D4: iOS queue reorder | #3228 |
| [#3235](https://github.com/OlyForge3D/PrintFarmer/issues/3235) | D5: iOS orphan cleanup and XCUI refresh | #3231–#3234 |

## Validation

- **Mobile:** unit tests plus the affected XCUI suites, as described in `mobile/AGENTS.md`.
- **Backend:** targeted `dotnet test` runs for the changed services and controllers.
- **Review:** each implementation PR touches high-risk paths, so it gets the two-reviewer panel.
