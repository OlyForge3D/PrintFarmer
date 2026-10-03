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

### Detail implementation constraints

The mobile printer/current-job contracts currently contain no layer counters,
so Status reports **Layer unavailable** rather than inferring a layer from Z.
The mobile service also has no fan command; Control directs operators to
**Open in web** for fans. Heater, motion, Z-offset and physical filament
commands retain the registered-server safety preference and verified capability
gates. Starting the assigned queue head uses the existing dispatch endpoint
with that job's reviewed revision; it never reassigns the job or silently
retries a stale revision.

## Queue reorder endpoint

`PUT /api/job-queue/jobs/{id}/position` requires `Queue.Write`.

**Request**

- An `If-Match` header carrying the moved job's ETag, read with the controller's existing `ReadIfMatch()` helper.
- The body is either `{ beforeJobId, beforeJobETag }` or `{ afterJobId, afterJobETag }`. Exactly one neighbour must be set, and each neighbour ID needs its matching ETag. If neither or both neighbours are set, or a neighbour ID arrives without its ETag, the endpoint returns 400.

**Authoritative ordering**

Reordering changes the actual dispatch order, not just the list on screen. `QueuePosition` is **only comparable within a queue scope**: one assigned printer, or the unassigned scope. Values from two different scopes are independent counters and must never be compared.

- `QueueOrdering.OrderByPriorityDescending()` keeps its current cross-scope order: `Priority desc → QueuedAt asc → Id asc`. Lists that span scopes stay FIFO within a priority band. This covers the general job list (`EfPrintJobManagementRepository` default sort) and any all-printer view.
- A new `QueueOrdering.OrderWithinScope()`, in both its `IQueryable` and `IEnumerable` forms, sorts by `Priority desc → QueuePosition asc → QueuedAt asc → Id asc`. Callers must first filter to a single scope.
- Every query that is **already filtered to exactly one scope** switches to `OrderWithinScope()`:
  - the printer-assigned job lookups in `AutoDispatchService` that filter to a single `AssignedPrinterId`;
  - batch dispatch over the unassigned scope (`BatchDispatchService`);
  - skip, cancellation and bed-clear head selection;
  - the per-printer queue (`GetJobsByPrinterAsync`), applied only to its `Queued` rows. `Printing` rows are listed separately, ahead of the queued rows.
- **Mixed-scope selections are cross-scope consumers and never call `OrderWithinScope()` over the combined set.** This covers the auto-dispatch candidate selection that combines a printer's assigned jobs with unassigned jobs (`AutoDispatchService`, and `AutoDispatchBackgroundService`'s `AssignedPrinterId == null || AssignedPrinterId == printerId` query). These selections:
  1. order each scope separately with `OrderWithinScope()` and scan it until the first **eligible** candidate, applying the existing scoring, claim, elimination and recovery-block filters exactly as today. That candidate is the scope's head, so a reorder still decides which job leads its own scope, and an ineligible raw head never hides the next compatible job; then
  2. choose between those eligible heads with the unchanged `OrderByPriorityDescending()`. If neither scope yields an eligible head, the result is `NoCompatibleJob`, as today.

  Positions from the two scopes are never compared.
- The queue list API returns queued jobs grouped by scope, each group in `OrderWithinScope()` order. The UI and dispatch therefore agree inside every scope, which is the only place a reorder can apply.
- Existing rows already have a `QueuePosition` value from `QueuePositionAllocator`, so no migration is needed. Any ties are still broken by `QueuedAt` and then `Id`, so the current order is kept.
- The `QueueOrdering` class doc comment is updated to state both invariants.

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
- The indexed set for a scope is its `Status IN (0, 1)` rows: `Queued (Status = 0)` and `Assigned (Status = 1)`. `Printing (Status = 3)` and later statuses are outside the filter and play no part in reordering.
- **Assigned rows are held fixed.** They keep their values and never move.
- **Only the scope's Queued rows are permuted, and only among the values they already hold.** The service takes those rows in `OrderWithinScope()` order, applies the move, and hands the same sorted set of values back out in the new order. It does not invent new values.
  - The permuted values were already disjoint from the Assigned rows' values, so the final write cannot collide with an Assigned row.
  - No value exceeds the current maximum, so the `NextPosition` watermark stays ahead of every assigned position and needs no reconciliation. The next enqueue cannot collide.
- Writes are two-phase inside the transaction, so the unique index never sees a transient duplicate. Phase 1 moves each affected row to a negative temporary value (`-QueuePosition`) and saves. Phase 2 writes the final values and saves.
- Unassigned jobs are outside the filtered index, so nothing enforces their uniqueness. They use the same permutation, and ordering ties still fall back to `QueuedAt` and then `Id`.
- Tests cover:
  - a move in a printer scope where an untouched **Assigned** row holds a position between the moved job and its neighbour; the Assigned row keeps its value and the move saves without a unique-index violation;
  - a move in a scope that also has a Printing row, which is ignored;
  - a move in the unassigned scope;
  - an enqueue after a reorder that gets a fresh, non-colliding position.

**Status codes**

These follow the controller's existing `MapRevisionException` mapping.

| Status | When |
|---|---|
| 428 | The `If-Match` header is missing. This is the only cause of 428. |
| 412 | The moved job's or the neighbour's ETag is stale, or EF raises `DbUpdateConcurrencyException`. The response includes the current ETags. |
| 409 | A semantic or stale-ordering conflict: the moved job or the neighbour isn't `Queued`, the neighbour is the moved job, the neighbour is outside the moved job's queue scope, or **the neighbour no longer exists**. |
| 404 | The moved job doesn't exist. |
| 400 | The body is malformed: no neighbour, both neighbours, or a neighbour ID without its ETag. |

**Tests**

- `PriorityQueueOrderingTests`:
  - `OrderWithinScope()` uses `QueuePosition` to break ties within a priority band, and its two overloads agree.
  - Over a list that spans several printers, `OrderByPriorityDescending()` still orders by `QueuedAt` within a band, regardless of `QueuePosition`.
- Dispatch and ready-head tests: after a reorder, the next job dispatched matches the order shown in the UI.
- Mixed-scope auto-dispatch regression, in both `AutoDispatchService` and `AutoDispatchBackgroundService`:
  - a printer's assigned jobs and the unassigned jobs hold conflicting `QueuePosition` values (an unassigned job has a lower position than the printer's head);
  - selection still picks each scope's head by `OrderWithinScope()`, then chooses between heads by `Priority desc → QueuedAt → Id`;
  - no cross-scope position comparison changes the result;
  - when one scope's raw head is ineligible (eliminated, claimed, below the score threshold or recovery-blocked) but its second job is eligible, that second job becomes the scope's head and can still be dispatched.
- Controller and service tests: every status code above, plus two concurrent moves where the second gets 412.

**Mobile**

`.onMove` applies the move immediately and sends the ETags it saw in the list.

- Drag is limited to a single scope group: one printer, or Any printer. A job can't be dropped into a different group.
- On 412 or 409, including a neighbour that was deleted, it rolls back, refetches the queue and shows "Queue changed — refreshed".
- On 404 (the moved job is gone), it also refetches.
- On any other failure, it rolls back and shows an error.
- Unit tests cover the rollback-and-refetch path for a deleted neighbour (409), a stale ETag (412), and a generic failure.

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
