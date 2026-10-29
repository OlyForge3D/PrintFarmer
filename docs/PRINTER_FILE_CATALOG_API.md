# Printer File Catalog API

This document provides detailed reference for the durable printer-file catalog
endpoints: metadata-only inventory, bounded background metadata/thumbnail
enrichment, explicit local Copy, staged Queue, and safe single-location Delete.

The legacy G-code harvest feature (the `/api/gcode-harvest/*` endpoints, the
`/hubs/harvest` SignalR hub, its operation, queue, and discovered-file tables,
and the `gcode_harvest` permission resource) was retired permanently in #3531.
Use **Files > On printers** (`/files?source=printers`, optionally
`&printer=<printerId>`) instead. Each Copy request imports exactly one file;
there is no server-side batch endpoint. The UI can copy several selected files
by sending one revision-bound request per file. Library files imported by earlier harvests
remain in the G-code library with their `Harvested` source, source printer, and
original printer path. Printed-parts inventory harvesting and mobile workflows
are unchanged.

## Development Notes
- AppDbContext schema is migration-managed for SQLite, PostgreSQL, and SQL Server.
  SQLite API startup uses `ProviderAwareMigrationRunner`; new catalog schema must
  be migrated on existing installations as well as new databases.

## Durable printer-file catalog

A remote location identifies one registered printer, storage root, and
backend-canonical relative path. It is not a library asset: `GcodeFile`
continues to represent owned local bytes. Nested equal filenames, distinct storage roots, and case-distinct paths
on case-sensitive backends remain separate locations regardless of database
collation.

Catalog provenance records the captured source observation separately from the
deduplicated local asset. Multiple origins may refer to one asset. Removing a
printer, remote location, or local asset does not erase the captured source
snapshot or cascade deletion into another copy. Queue-operation job IDs remain
recorded even if ordinary queue-history cleanup removes a job.

Inventory completeness is explicit. Only complete coverage of a root or subtree
can establish that an unseen file is missing. Partial, cancelled, unsupported,
malformed, and failed listings must not be interpreted as empty storage.
Size, modification time, and provider version evidence may be unknown; a local
digest identifies captured bytes but does not prove an unchanged remote source.

| Backend | Catalog coverage | Source-version assurance |
| --- | --- | --- |
| Moonraker | Native metadata-only `gcodes` listing | Reported metadata only; no conditional snapshot guarantee |
| OctoPrint | Recursive `local` listing, including nested children | Reported metadata only; no conditional snapshot guarantee |
| PrusaLink | Declared available roots, with bounded directory traversal | Reported metadata only; no conditional snapshot guarantee |
| SDCP | Unsupported until recursive scope and ACK completeness are proven | None |
| FlashForge / unknown | Unsupported | None |

Catalog support is independent of legacy file-list support, authenticated
streaming copy, and safe remote-delete support. A successful inventory does not
grant any of those mutation capabilities. A complete empty listing differs from
an HTTP/authentication/protocol failure; bounds or malformed responses never
produce invented complete emptiness. Inventory traversal and native-detail
requests share a 120-second deadline, 10,000-entry limit, depth limit of 16,
and 8 MiB per-response limit. There is no inventory HTTP request-count cap.
Inventory traversal never downloads G-code bodies, per-file G-code headers, or
thumbnails. A separate bounded enrichment slice can follow inventory under the
same confirmed-idle lease; catalog and preview GET requests never trigger it.

Native-detail enrichment is best effort: it stops at 75% of the scan deadline
(90 seconds by default), measured from the start of inventory traversal.
Individual detail failures are safely logged and skipped so later files can
still be enriched. Authentication failure stops further detail requests; neither
it nor an exhausted detail budget discards a completed listing. The catalog
service allows 15 seconds of teardown grace beyond the adapter's deadline to
receive its result; this does not extend the printer-request budget.

PrusaLink scans fetch native file details and store the full `display_name`,
reported size, normalized print metadata, and successful-detail-fetch timestamp
in the application's database. The raw short filename remains the location's
path and action identity; a long display name does not rename the file.
Scheduled scans reuse saved details only when listing evidence and configuration
still match. Force Refresh fetches the details again. These catalog values do
not depend on the thumbnail store and remain available while the printer is
offline or a preview cannot be loaded.

When a scan cannot fetch details, saved details survive only with unchanged
modification time, reported size (when present), provider version/hash,
configuration, mutation epoch, and availability evidence. Missing modification
time cannot prove reuse is safe. A successful response with no metadata replaces
the previous snapshot rather than preserving stale values.

Normalized metadata is stored as one bounded JSON display snapshot, not as
individually searchable database columns. Storage uses relaxed JSON escaping
and a 32,768-character serialized limit. A snapshot exceeding that limit is
logged and omitted without losing the file listing or successful-detail-fetch
marker. This encoding is confined to database storage, not HTML or API output.

### Discovery policy and metadata API

The `PrinterFileIndex` settings section is saved independently through the
existing settings shell. Its default cadence is 15 minutes with up to 20%
deterministic per-printer jitter. Failures back off to at most six hours,
including jitter. Entries become stale after two configured intervals.
Inventory traversal authentication failures pause that printer until
configuration changes or an authorized refresh is requested. Optional detail
authentication failures instead follow the best-effort behavior above.

Discovery requires fresh, explicitly idle backend telemetry. Normalized `Idle`
alone is insufficient because legacy normalizers also map unknown states to
idle. Offline, busy, stale, or unknown activity defers discovery without
refreshing last-seen timestamps. Disabling the global index or one printer's
index retains observations, origins, and owned local files; it does not disable
explicit Force refresh, Copy, or Queue actions.

Busy or unknown sources receive a jittered next-attempt time without advancing
last-seen or complete-scan evidence, so they cannot monopolize the due batch.
Unexpected per-printer scan failures are isolated, logged with a safe failure
type, and backed off without changing file availability. A control write that
races scan ownership returns `409` with `scan_state_changed`; refresh state and
retry the deliberate Refresh or enable/disable action.

Database-backed, renewable ownership shares two aggregate I/O slots by default
(`PrinterFileIndex.MaxConcurrentIo`, range 1-16) and permits only one feature
operation per printer. Configuration, registration, policy, and ownership are
fenced again in the publication transaction. No database transaction spans
printer HTTP requests. A superseded scan cannot publish positive observations
or mark files missing.

Lease acquisition retries recognized serialization, deadlock, lock, and
concurrency conflicts up to eight attempts with jittered backoff. Every attempt
uses a fresh context and retries the entire serializable allocation transaction,
not just the failed statement. Available-slot selection starts at a randomized
slot to reduce collisions. Exhausted acquisition conflicts are logged and defer
the work; unexpected database failures are not treated as contention.

| Endpoint | Behavior |
| --- | --- |
| `GET /api/printer-files` | Metadata-only, paginated locations, authorized printer choices, scan status, and local-copy associations |
| `POST /api/printer-files/printers/{id}/refresh` | Default: request a coalesced background scan (`202`); does not bypass discovery disables or idle checks |
| `POST /api/printer-files/printers/{id}/refresh?force=true` | Run a bounded inventory now and mark enrichment due, bypassing discovery switches, schedule, and backoff while retaining idle and I/O safeguards |
| `PUT /api/printer-files/printers/{id}/index` | Set that printer's discovery control with `{ "enabled": true/false }` |

The default refresh response is `202` with `code: "refresh_requested"` (or
`"enrollment_pending"` before scan-state enrollment). It is not completion
evidence. With `force=true`, the request waits for the scan and returns `200`
with `{ "code": "refresh_completed", "files": N }` only after a complete listing
is published. Deferrals, interrupted/superseded scans, and incomplete inventories
return `409` with `code` and `reasonCode`; examples include
`waiting_for_confirmed_idle` and `refresh_incomplete`. Unavailable ownership,
including exhausted acquisition retries, returns `409` with
`code: "source_busy"` before a scan starts. A database state race returns `409`
with `code: "scan_state_changed"`. These failures must not be presented as
successful or scheduled force refreshes.

Forced scans use the same inventory bounds, exclusive per-printer ownership, and
deployment-wide I/O slots as background scans. They require confirmed-idle
telemetry at acquisition, lease renewal, and publication. They do not enable
automatic discovery or change its configured cadence.

The React client retries only forced requests rejected with `409 source_busy`,
using 1-1.5-second jitter within a 120-second capacity-wait window. Once a scan
starts, its own deadline applies; the client allows 150 seconds for each forced
HTTP request. Explicit non-idle responses, incomplete scans, state changes,
network failures, and timeouts are not replayed automatically. Those outcomes
require an operator to inspect current state before deliberately retrying.

Catalog responses include `maxConcurrentIo`, the configured deployment-wide
capacity. The bulk refresh control uses that value as its bounded parallel
request count, defaulting to two when talking to an older server. Per-printer
failures do not abort the remaining batch. Both icon-only refresh controls stay
inactive through a pending user refresh and its awaited catalog/preview cache
reload, including when switching between the catalog and printer settings.
Background I/O alone does not disable either control: `isScanning` includes
enrichment holding the exclusive per-printer lease, not just inventory scans.
A forced request can register interactive demand and use the bounded
`source_busy` retry above while background work releases ownership. The server
lease and I/O slots prevent overlapping work; a polled UI snapshot does not.
A completed listing does not mean metadata and thumbnail acquisition is done.

A published force refresh advances `observationRevision` even for unchanged
files, except unchanged locations pinned by unfinished file operations. The
React controls invalidate catalog and preview queries. Force refresh also marks
enrichment due, retaining an existing image only for the same source identity;
image acquisition happens later in the background, not in the GET or forced
inventory request. A completed refresh is not evidence that every image is ready.
Background scans retain revisions for unchanged files, and enrichment itself
never advances a revision. PrusaLink native details are still refreshed during
the forced inventory. Disabling automatic discovery also pauses background
enrichment; force refresh does not enable either switch.

Listing accepts `printerId`, `search` (at most 200 characters), `availability`
(`Available`, `Missing`, `Deleted`), `stale`, `page`, and `pageSize`
(1-200, default 50). Source authorization is applied before counts,
pagination, filtering, origins, and scan status. Responses contain canonical
root/path observations, not credentials or printer URLs.

Each location also includes `displayName`, optional `sizeBytes`, optional
`metadata`, and optional `detailsFetchedAtUtc`. The latter records a successful
native-detail fetch, including a valid response with no supported metadata.
Absent fields mean **unknown**, not zero or false. An empty saved result must
not be replaced by older preview-cache metadata. Existing installations acquire
details on their next eligible scan; migrations preserve their existing catalog.

The optional camelCase `metadata` object includes the 41 fields in PrusaLink's
[PrintFileMetadata schema](https://github.com/prusa3d/Prusa-Link-Web/blob/master/spec/openapi.yaml).
Moonraker, OctoPrint, and bounded G-code/BGCode parsing populate the same snapshot
with only validated source values. Native fields take precedence over fallback
fields; missing values are not guessed:

| Fields | Type and units |
| --- | --- |
| `printerModel`, `profile`, `materialName`, `filamentType`, `fillDensity`, `supportMaterial` | String; provider text is preserved |
| `estimatedPrintTimeNormalMode` | Provider's textual duration |
| `printTimeSeconds`, `estimatedPrintTimeSeconds` | Number, seconds |
| `layerHeightMm`, `maxLayerZMm`, `brimWidthMm`, `nozzleDiameterMm` | Number, millimeters |
| `bedTemperatureC`, `nozzleTemperatureC` | Number, degrees Celsius |
| `bedTemperaturePerToolC`, `nozzleTemperaturePerToolC` | Number array, degrees Celsius |
| `nozzleDiameterPerToolMm`, `filamentUsedPerToolMm` | Number array, millimeters |
| `filamentTypePerTool` | String array |
| `filamentUsedMm`, `filamentUsedG`, `filamentUsedCm3` | Number, millimeters / grams / cubic centimeters respectively |
| `filamentUsedPerToolG`, `filamentUsedPerToolCm3` | Number array, grams / cubic centimeters respectively |
| `filamentCost`, `filamentCostPerTool` | Number / number array; currency unspecified by the provider |
| `requiredResinMl` | Number, milliliters |
| `fadedLayers`, `ironing` | Number; provider value is preserved |
| `exposureTimeSeconds`, `initialExposureTimeSeconds`, `minExposureTimeSeconds`, `maxExposureTimeSeconds`, `minInitialExposureTimeSeconds`, `maxInitialExposureTimeSeconds` | Number, seconds |
| `normalPercentPresent`, `normalLeftPresent`, `quietPercentPresent`, `quietLeftPresent`, `layerInfoPresent` | Boolean |
| `layerCount` | Source-reported integer; never inferred from height |
| `infillPercentage`, `infillPattern` | Number, 0-100 percent / string |
| `supportEnabled`, `supportType`, `supportStyle` | Nullable boolean / strings; explicit false means disabled |
| `slicerName`, `slicerVersion` | Source-reported strings |
| `tools` | Array of identified tools: `index` (zero-based), optional `sourceId`, nullable `color`, `material`, and `filamentUsedG` |
| `filamentUsedGIsDerived` | True only when total grams were summed from a complete set of tool weights |

Legacy tool arrays retain provider order. The richer `tools` records retain
explicit source slots: missing color, material, or weight remains null in that
slot rather than shifting another tool into it. Colors must be literal
`#RRGGBB` or `#RRGGBBAA`; they are not inferred from names. Zero and false remain
valid where the source field permits them. These are source-reported
observations, not independently verified print parameters or evidence of remote
byte equality. Acquisition time, extractor version, independent metadata/image
outcomes, and source identity are persisted with enrichment.

Search ignores case on SQLite, PostgreSQL, and SQL Server. It lowers the stored
path and the search text explicitly instead of relying on provider collation,
and an exact-case match is always accepted. SQLite's built-in `lower()` folds
ASCII only, so `ä` does not match `Ä` there; folding non-ASCII letters the same
way on every provider would need a normalized column and a migration, which is
not implemented. Location identity remains strictly ordinal.

Current authority is evaluated for every enabled printer, including printer
choices outside the current file page, and for local-copy associations on the
page, using a bounded number of database queries that does not grow with the
printer count. Each request reads the actor's current permissions and group
access; nothing is cached across requests, so revocation takes effect on the
next request. Large farms should still measure catalog polling load before
rollout; no throughput guarantee is claimed.

Read access requires current `printer_files:read` and source-printer View.
Refresh and per-printer control additionally require source Manage. Copy
requires `printer_files:copy` plus current `gcode_library:read` and
`gcode_library:create` grants. Standard farm users
receive read and copy, not remote-delete permission. Deferred actions recheck
the actor's current database authority; expired administrator assignments do
not remain valid. Metadata uses the existing `ModelRead` desktop scope gate;
durable mutations initially require an interactive session and do not accept
Desktop API-key exchange tokens.

Local-copy associations also require current local-library read and local asset
View access. Queue requires local asset Submit access, including after
deduplication or permission changes. Printer access alone never exposes a
restricted local asset ID or authorizes reuse of its bytes.

### Explicit local Copy and staged Queue

The canonical `/files` page separates **Local library** from **On printers**.
Both toolbars start with **Search**. Local library matches display names
and stored filenames; On printers matches relative paths, including filenames
and folder segments. Both use case-insensitive substring matching, subject to
database-provider Unicode casing differences. On printers also offers a Printer
filter (default **All**), a view toggle, Refresh, and single-location actions.
Both sources use matching 40px-high toolbar inputs,
dropdowns, buttons, and view toggles, including expanded local folder controls.
Catalog browsing reads only local state. Automatic
indexing can acquire metadata and thumbnails in a bounded background enrichment
slice; browsing, opening details, and loading images never contact a printer.
Captured local versions are links, not a claim that current remote bytes match.
Each Copy request imports one file and there is no batch endpoint. Selecting
several files makes the UI send one request per file, each bound to its own
observation revision and intent key; only failed files are retried with their
original key, and stale files must be refreshed first. Part/stock harvesting
and immediate printer Print controls are unchanged.

### Background enrichment, local previews, and browser Download

**Copy Local** retains the durable import flow described below. **Download**
streams a file to the browser without importing it, creating an asset, or
creating a queue job. Neither preview nor Download weakens existing Copy,
Queue, Delete, revision, or snapshot-confirmation checks.

All three content endpoints require an interactive authenticated session and
the `ModelRead` policy. The `observationRevision` query parameter is required
and must equal the selected catalog location's current positive revision.
No endpoint accepts a printer URL or arbitrary remote path.

| Method and endpoint | Behavior |
| --- | --- |
| `GET /api/printer-files/locations/{id}/preview?observationRevision={n}` | Local database metadata, image reference, and enrichment status; never acquires a printer-I/O lease or contacts the printer |
| `GET /api/printer-files/locations/{id}/thumbnail?observationRevision={n}&version={v}` | Authorized local raster image; `404` if absent or the supplied artifact version no longer matches; never contacts the printer |
| `GET /api/printer-files/locations/{id}/download?observationRevision={n}` | Explicit bounded browser attachment stream, not a local import |

Preview JSON uses this camelCase contract:

```json
{
  "locationId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "observationRevision": 7,
  "state": "Available",
  "reasonCode": null,
  "capturedAtUtc": "2026-10-26T18:00:00Z",
  "hasImage": true,
  "thumbnailUrl": "/api/printer-files/locations/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/thumbnail?observationRevision=7&version=11111111-2222-3333-4444-555555555555",
  "thumbnailVersion": "11111111-2222-3333-4444-555555555555",
  "metadataState": "Ready",
  "thumbnailState": "Ready",
  "metadataReason": null,
  "thumbnailReason": null,
  "extractorVersion": 1,
  "nextAttemptAtUtc": "2026-10-27T18:00:00Z",
  "isStale": false,
  "sizeBytes": 123456,
  "printTimeSeconds": 3600,
  "layerHeightMm": 0.2,
  "metadata": {
    "printerModel": "COREONE",
    "printTimeSeconds": 3600,
    "layerHeightMm": 0.2
  }
}
```

- `Available`: an image, metadata, or a known measurement (including size) is present.
- `Unavailable`: no display value is available and acquisition is not pending.
- `Unsupported`: the backend does not support enrichment.
- `Deferred`: no value is available and background acquisition is pending or deferred.
- A missing or null metadata field means unknown, not zero or an inferred
  value. `capturedAtUtc` is the saved acquisition time, not the GET time or a
  source-version guarantee.

`metadataState` and `thumbnailState` independently report `Pending`, `Ready`,
`Partial`, `Absent`, `Unsupported`, `Deferred`, or `Failed`. An image failure
does not discard successful metadata. Safe reason codes distinguish, for
example, `cache_full`, `storage_unavailable`, `capture_paused`,
`range_unsupported`, `region_limit`, and `invalid_image`. `isStale` identifies
a retained same-source image while revalidation is due or its last image
outcome is not ready. Clients should not confuse a known file size with complete
print settings, or a missing image with failed metadata.

Catalog `printers[]` includes `supportsPreview`, `canPreview`,
`supportsDownload`, and `canDownload`. These are capability/authority flags,
not a promise that the printer is currently idle. Existing
`supportsCopy && canCopy` also identifies eligible browser downloads for the
currently certified adapters.

| Backend | Background enrichment | Browser Download |
| --- | --- | --- |
| Moonraker | Native per-file metadata and thumbnail first; bounded reads of the exact `gcodes` file fill unknown fields or embedded images | Certified authenticated `gcodes` stream |
| OctoPrint | Native analysis time, filament length/volume, and highest Z first; validated native thumbnail or bounded exact-file reads where supported | Certified authenticated `local` stream |
| PrusaLink | Native print metadata and thumbnail first; bounded exact-file reads fill unknown fields or embedded images | Unsupported until native streaming is certified |
| FlashForge, SDCP, others | Unsupported; no G-code fallback | Unsupported until native streaming is certified |

Acquisition runs only with fresh confirmed-idle telemetry under the renewable,
exclusive per-printer lease and deployment I/O budget. Native facts win, and
native thumbnails are acquired before optional metadata fallback so a slow
G-code read cannot prevent an already-available preview from being captured.
Fallback reads only bounded regions, never imports the file, and never uses the
explicit Copy/Download path. Each file has at most eight wire attempts (including
authentication retries), 8 MiB of responses, and 15 seconds. A slice handles at
most eight files and 16 MiB. Metadata responses are limited to 256 KiB, text
regions to 1 MiB each, binary prefixes and decoded data to 4 MiB each, and images
to 2 MiB, 4096 pixels per edge, and 4,194,304 total pixels. Unsupported ranges,
compression, image formats, or parser bounds yield explicit partial/unsupported
outcomes, not an unbounded download.

Acquired metadata and images retain independent outcomes if optional fallback
exhausts the adapter's deadline or request/byte budget. The service safety
timeout allows 15 seconds of grace beyond the adapter's 15-second deadline to
return those partial results; it does not extend the printer I/O budget.
Caller cancellation, lease loss, or a detected source change still discards
the acquisition rather than publishing stale results.

Image validation accepts supported PNG/JPEG/WebP payloads, not SVG or QOI.
Redirects, off-origin references, ambiguous/encoded traversal paths, and
oversized or truncated bodies fail closed. Publication rechecks ownership,
idle state, registration, configuration, source identity, and current row
eligibility without holding a database transaction over network I/O.

Never-attempted files run first, followed by the oldest due attempt. Transient
failures retry after 1, 5, 15, then 60 minutes with up to 20% subtractive
deterministic jitter. Settled outcomes revalidate after 24 hours; this is a
revalidation interval, not image expiry. Old extractor versions and force
refresh also make work due. Successful metadata remains useful when image
capture cannot proceed, and local-capacity retries do not repeatedly read
already-settled metadata.

#### Fallback telemetry

The `PrintFarmer.PrinterFileEnrichment` meter measures the shared acquisition
pipeline for Moonraker, OctoPrint, and PrusaLink. It uses the API's existing
OpenTelemetry exporters: scrape `/metrics` with Prometheus or configure
`OpenTelemetry:OTLP:Endpoint`. `DISABLE_TELEMETRY=true` and the `Testing`
environment disable export. Deploy the instrumentation and collect samples
before drawing conclusions about live fallback frequency or latency.

| OpenTelemetry instrument | Type / unit | Labels | Meaning |
| --- | --- | --- | --- |
| `printer_file.enrichment.started` | Counter / acquisitions | `backend` | Acquisitions started, including native-only attempts |
| `printer_file.enrichment.fallback.started` | Counter / fallbacks | `backend`, `trigger` | Actual bounded G-code/BGCode fallback attempts |
| `printer_file.enrichment.fallback.skipped` | Counter / fallbacks | `backend`, `trigger`, `reason` | Needed fallback not started because the budget was exhausted or the source was empty |
| `printer_file.enrichment.fallback.duration` | Histogram / ms | `backend`, `trigger`, `outcome` | Elapsed fallback reads and parsing, including unsuccessful attempts |

Labels are bounded: `backend` is `moonraker`, `octoprint`, `prusalink`, or
`unknown`; `trigger` is `metadata`, `thumbnail`, or `metadata_and_thumbnail`.
Skip reasons are `deadline`, `request_limit`, `response_limit`, or `empty_source`.
Duration outcomes are `completed`, `partial`, `timeout`, `failed`, `cancelled`,
or `error`. File paths, printer/user IDs, URLs, credentials and exception text
are not metric labels.

One acquisition starts at most one fallback; head/tail reads and reuse of its
parsed thumbnail count together. Retries and revalidation are new acquisitions,
not unique files. Metadata gaps can trigger fallback even when a native image
is ready. Time covers the bounded reads and parsing only, excluding native
metadata/images, final source verification, database and thumbnail publication.
`completed` means the bounded parser finished conclusively, not that it found
every field or a valid image, or that publication succeeded. `partial` preserves
some fallback data despite an inconclusive read; `timeout` identifies the
per-file deadline, `cancelled` caller cancellation, and `error` an unexpected
exception that still propagates.

Timeout samples measure time spent before interruption, not the time a full
read would have needed. They consume the **remaining** shared 15-second budget
after native work. Do not discard them when assessing cost: doing so biases
latency toward successful reads. Histogram boundaries are 10, 50, 100, 250,
500, 1000, 2500, 5000, 10000, 15000 and 30000 ms, plus infinity.

Prometheus translates the names below. Fallback-start percentage by backend,
including zero fallback when acquisition samples exist:

```promql
100 * (
  sum by (backend) (rate(printer_file_enrichment_fallback_started_total[1h]))
  or on (backend)
  (0 * sum by (backend) (rate(printer_file_enrichment_started_total[1h])))
) / sum by (backend) (rate(printer_file_enrichment_started_total[1h]))
```

Approximate p95 and mean fallback time in milliseconds, across all outcomes:

```promql
histogram_quantile(0.95,
  sum by (le, backend) (
    rate(printer_file_enrichment_fallback_duration_milliseconds_bucket[1h])
  )
)

sum by (backend) (rate(printer_file_enrichment_fallback_duration_milliseconds_sum[1h]))
/
sum by (backend) (rate(printer_file_enrichment_fallback_duration_milliseconds_count[1h]))
```

Completed attempt counts by outcome (including timeouts) and skipped attempts
by reason over the same window:

```promql
sum by (backend, outcome) (
  increase(printer_file_enrichment_fallback_duration_milliseconds_count[1h])
)

sum by (backend, reason) (
  increase(printer_file_enrichment_fallback_skipped_total[1h])
)
```

No series can mean no recorded event or no working exporter/scrape; it is not
proof of zero cost. A zero denominator means no observed acquisition activity,
not a measured 0% fallback rate. Windowed rates need multiple scrapes and include
in-flight work; histogram counts appear when an attempt ends, whereas start
counters appear immediately. These measurements can refute a fallback-cost
hypothesis, but cannot by themselves attribute latency to printer hardware,
networking, or parsing. Misreading them could lead operators to change budgets
or concurrency unnecessarily, delaying previews or increasing printer load.

#### Shared thumbnail storage and upgrade requirements

Apply the `AddPrinterFileEnrichment` AppDbContext migration for the configured
provider (SQLite, PostgreSQL, or SQL Server). Existing catalog rows are enriched
on their next eligible background pass; there is no whole-catalog download.
No slicer-context migration is required.

The private store is `<Gcode storage directory>/.printer-file-thumbnails`,
outside the web root and library. The configured Gcode root must already exist;
an unavailable root reports `storage_unavailable`, not a replica-local fallback.
**Every API replica must use the same database and the same persistent Gcode
volume**, with working exclusive file creation and same-directory atomic
rename. Matching path strings on different disks do not constitute shared
storage. Provision and verify the mount before enabling acquisition on replicas.

`PrinterFileIndex.thumbnailCacheMaxBytes` defaults to **512 MiB** and accepts
2 MiB through 1 TiB. Database-coordinated reservations include in-flight,
published, and retired bytes. Healthy images are never evicted to admit another
file. At capacity, image acquisition reports `cache_full` while metadata
continues. Increase capacity or allow obsolete artifacts to be reclaimed rather
than deleting healthy previews to make room. Retired images have a one-hour
grace period; crashed writes and failed deletions remain accounted for until
cleanup succeeds.

Images are immutable, SHA-256-identified artifacts with an opaque
`thumbnailVersion`. Identical same-source bytes reuse the artifact; changed
bytes receive a new version. Identity includes printer, root, ordinal path,
configuration generation, size, raw modification value, and provider
version/digest, separately from action revisions. A detected source change,
missing/deleted file, or configuration change stops serving the old association.
Without strong provider identity, a same-size/same-time overwrite may remain
undetected until revalidation; a local digest cannot prove remote equality.

Every GET checks current permissions, printer View ACL, registration,
availability, revision, and configuration. Cached bytes can be served while
busy or offline because this causes no printer I/O. Missing or corrupt local
bytes return `404` and mark the image for background reacquisition; the request
does not download a replacement. Image responses include a strong SHA-256 ETag,
`Cache-Control: private, no-store`, and `X-Content-Type-Options: nosniff`.
Clients must authenticate even when reusing a previously returned URL.

Download additionally requires `ModelWrite` and the exact live Copy authority:
`printer_files:read`, `printer_files:copy`, printer View access, and library
read/create permissions. It requires fresh, explicit idle evidence and the
existing exclusive file-I/O lease. Pre/post native inventory checks must match
the selected observation. The lease remains held throughout streaming;
activity is checked on each output chunk and live authority at least once per
second while writing. Lease loss and request cancellation propagate upstream.
No persistent download copy is written. Limits are the observed size (when
known), an absolute 200,000,000 bytes, and the configured file-download timeout.
The attachment uses a sanitized, length-bounded leaf filename, never a path.
Post-start failures abort the response rather than reporting a successful file.

Missing/invalid revisions return `400`; stale observations/configuration and
busy downloads return `409`; inaccessible locations return `404`; size-limit
violations return `413`. Error bodies are ProblemDetails with non-sensitive
`code` and `reasonCode` extensions. Unsupported preview is a normal `200`
`Unsupported` response. Upstream transport/invalid-content failures return
safe `409`/`503` errors, never printer URLs, credentials, or response bodies.
The frontend loads local preview records and images only for visible items,
not the whole catalog, and avoids aggressive polling while enrichment is pending.

### Durable Copy Local and staged Queue endpoints

| Endpoint | Behavior |
| --- | --- |
| `POST /api/printer-file-index/{locationId}/copy` | Durable explicit copy; body contains `observationRevision` and optional `virtualDirectory` |
| `POST /api/printer-file-index/{locationId}/queue` | Durable staging for any destination; body contains `observationRevision`, `destinationPrinterId`, and `options` |
| `GET /api/printer-file-index/operations` | Up to 100 recent actor-authorized Copy/Queue operations for reconnect recovery |
| `GET /api/printer-file-index/operations/{operationId}` | Authorized progress, local asset/content binding, and committed job IDs |
| `POST /api/printer-file-index/operations/{operationId}/confirm-snapshot` | Explicit accept/decline bound to the captured content and enqueue intent |
| `POST /api/printer-file-index/operations/{operationId}/enqueue` | Retry admission of the same local capture without another download |
| `POST /api/printer-file-index/operations/{operationId}/retry` | Retry an unbound failed/blocked transfer, retaining the original source observation |
| `POST /api/printer-file-index/operations/{operationId}/cancel` | Cancel before admission; a committed job is returned rather than falsely cancelled |

Copy and Queue creation require an `Idempotency-Key` header. The durable
actor/key binding survives ordinary replay-cache expiry. Identical commands
return the same operation and intended jobs; changed options under that key
conflict. A new key represents a new deliberate intent. UI retries retain the
key after a lost response and recent operations can be resumed without
reissuing a mutation.

Copy currently supports authenticated native Moonraker `gcodes` and OctoPrint
`local` streaming. PrusaLink inventory is listing-only until authenticated
streaming is certified; unsupported capability is not inferred from legacy
downloads. Redirects and encoded traversal are refused. Actual content is
bounded to 200,000,000 bytes and the configured download deadline (default
900 seconds), with persisted upload policy, daily actor quota reservations,
and free-storage checks. Successful publication consumes the actual newly
owned bytes; verified hash-deduplication consumes no additional byte charge.
Existing uploads keep their established accounting.

Copy checks the actual SHA-256, byte count, strict UTF-8 text format and metadata
before publishing owned content. It does not authorize execution or manufacture
calibration/promotion lineage. Queue additionally validates the captured program
against the actual destination's safety envelope and normal compatibility
requirements. Safety failures cannot be waived by snapshot consent.

Queue accepts explicit standard heater shutdown (`M104 S0` and `M140 S0`)
as well as `TURN_OFF_HEATERS`, and explicit motor shutdown (`M18` or `M84`).
Later heating, motor enabling, or movement invalidates the corresponding
shutdown evidence. Copy alone does not require an executable shutdown sequence.

Every catalog Queue stages locally, including Queue to the source printer.
After validation, an unverified source enters `AwaitingSourceConfirmation`.
The confirmation body is:

```json
{
  "gcodeFileId": "the-captured-local-asset-id",
  "contentSha256": "the-actual-captured-sha256",
  "sizeBytes": 123456,
  "payloadSha256": "the-operation-enqueue-payload-sha256",
  "accept": true
}
```

This acknowledges that pre/post listing metadata cannot prove a version-bound
remote read. It authorizes only this actor's exact operation, captured bytes and
enqueue payload, never remote deletion. Detected source changes still block.
Declining creates no job and retains the local copy. Restart/refetch never
auto-confirms. A verified source skips only this warning, not integrity,
destination compatibility, or Ready/dispatch gates.

Admission uses the existing queue pipeline inside the same database transaction
as the durable operation-to-job link. A job pins its local asset, actual digest
and size. Multiple copies retain the ordinary queue semantics: one job with
`copies`, not one independent job per copy. Local safety/byte verification and
authorization run again before admission; no printer network call occurs in
that transaction. Committed jobs remain independent of later remote changes.
An admission failure can retain its local asset and valid confirmation for
retry; it never silently substitutes another capture.

Copy publication retries a database race at most three times using the same
capture, then exposes `Failed` / `publication_failed` and releases its quota
reservation rather than automatically downloading forever. Queue storage
conflicts expose `Blocked` / `queue_storage_conflict`, retain the captured bytes
and valid consent, and require explicit admission retry. Pinned catalog jobs
are rechecked at claim and their actual opened upload stream is rehashed and
size-checked before either upload path sends any bytes.

### Safe single-location Delete

Delete is a separate interactive flow, not a Copy/Queue confirmation or a local
library deletion. It requires current `printer_files:read`,
`printer_files:delete`, source View, and source **Manage** authorization.
Queue write, Submit, and local-library delete do not authorize it. Every intent
is scoped to its authenticated actor and one registered catalog location.
Requests cannot supply a URL, credentials, root, or path.

Certified current-path deletion supports Moonraker `gcodes` and OctoPrint
`local`. PrusaLink and SDCP are explicitly unsupported by this safe capability;
their legacy delete methods are not fallbacks. No adapter currently claims
native atomic version-conditional deletion. The separate `NativeConditional`
contract is available only to a certified implementation.

| Endpoint | Request | Response |
| --- | --- | --- |
| `POST /api/printer-file-index/{locationId}/delete` | `Idempotency-Key` header and `{ "observationRevision": 3 }` | `202`, Delete operation and status URL in `Location`; creates an intent without deleting |
| `GET /api/printer-file-index/delete-operations/{operationId}` | No body | `200`, actor-authorized Delete operation |
| `POST /api/printer-file-index/delete-operations/{operationId}/confirm` | Confirmation body below | `200`, durable operation; may perform the single authorized delete |
| `POST /api/printer-file-index/delete-operations/{operationId}/reconcile` | No body | `200`, durable operation after read-only inventory observation; never resends delete |

The confirmation body is:

```json
{
  "confirmationBinding": "opaque-binding-returned-by-this-delete-intent",
  "acceptIrreversibleDelete": true,
  "acceptCurrentPathRace": true
}
```

Both booleans are explicit user choices, never preselected or automatically
restored after reconnect. Irreversible consent is always required. For
`CurrentExactPath`, race consent is separately mandatory: other printer clients
may replace or start a file after metadata/idle checks. These checks are not
atomic delete-if-unchanged. A Queue snapshot confirmation never authorizes
Delete. No automatic backup is created.

Confirmation is validated only after actor/source authorization and never grants
that authority. After acquiring the exclusive file-operation lease, confirmation
reads a fresh tracked operation from storage; either a changed state or a
persisted send timestamp independently prevents another delete.

The binding includes actor, operation, printer, configuration generation,
case-sensitive root/path, observation/mutation revisions, captured metadata,
and guarantee. UI confirmation must display the exact printer/root/path and
observation revision plus an irreversible data-loss warning. A detected change
blocks the old intent and requires a refreshed observation, new intent/key,
and new explicit consent.

MVC serializes properties in camel case, enums as the strings shown below,
GUIDs as strings, and nullable UTC timestamps as ISO-8601 strings or `null`.
The exact Delete response shape is:

```typescript
interface PrinterFileDeleteOperation {
  id: string;                         // Guid
  revision: number;                   // Int64
  state: 'AwaitingDeleteConfirmation' | 'SendingDelete' |
    'ReconcilingDelete' | 'Deleted' | 'Blocked';
  locationId: string;                 // Guid
  sourcePrinterId: string;            // Guid
  storageRoot: string;
  relativePath: string;
  observationRevision: number;        // Int64, immutable intent observation
  configurationGeneration: number;    // Int64
  guarantee: 'Unsupported' | 'CurrentExactPath' | 'NativeConditional';
  confirmationBinding: string;        // Opaque; do not reconstruct
  currentPathRaceAccepted: boolean;
  deleteSentAtUtc: string | null;     // Persisted possible-send boundary, not proof of send
  outcome: 'AwaitingConfirmation' | 'Acknowledged' | 'OutcomeUnknown' |
    'ObservedAbsent' | 'Blocked';
  reasonCode: string | null;
}
```

`Unsupported` intents are rejected rather than created. `state` uses the shared
`PrinterFileOperationState` enum; the five states above are those emitted by
the Delete service. Clients should fail closed on unrecognized future states.
The C# service contract is `IPrinterFileDeleteService`: `CreateAsync(Guid actorId,
string idempotencyKey, PrinterFileDeleteCommand command, CancellationToken ct)`,
`GetAsync(Guid actorId, Guid operationId, CancellationToken ct)`,
`ConfirmAsync(Guid actorId, Guid operationId, PrinterFileDeleteConfirmation
confirmation, CancellationToken ct)`, and `ReconcileAsync(Guid actorId,
Guid operationId, CancellationToken ct)`. Create returns
`Task<PrinterFileDeleteOperation>`; the other methods return
`Task<PrinterFileDeleteOperation?>`. `PrinterFileDeleteCommand` contains
`Guid LocationId` and `long ObservationRevision`; the HTTP creation body
contains only `long ObservationRevision` because the location is in the route.
The confirmation record contains `string ConfirmationBinding`,
`bool AcceptIrreversibleDelete`, and `bool AcceptCurrentPathRace`.

**Only `outcome: "Acknowledged"` with `state: "Deleted"` reports a committed
successful deletion.** This tombstones only the selected remote location;
owned bytes, other locations, origins, model/SKU associations, and accepted
local jobs are not deleted. A complete later scan may identify a recreated
path as a new observation; it never inherits old Delete consent.

Fresh authoritative file inventory and successful explicit online/idle status
are checked while holding the shared printer-file lease and existing physical
actuation barrier. A reported current job/file path, active dispatch/start,
unknown/busy/offline status, lost ownership, changed source metadata or revoked
authority blocks mutation. Lock order is printer-file lease, physical barrier,
then short publication transaction; no printer network call runs in a database
transaction. The intent, consent, and possible-send boundary are durable before
mutation. Adapter mutation requests are not automatically retried.

Same actor/key/payload creation replays durable status, not deletion; a changed
payload under that key conflicts. Repeating confirmation after the send
boundary also cannot resend. Poll the returned Delete status URL after losing
a response. Do not use the Copy/Queue retry endpoint for Delete.

`SendingDelete` and `ReconcilingDelete` both expose `OutcomeUnknown`, including
after a process restart. `observed_absent_no_send` and
`observed_absent_after_send` expose `ObservedAbsent`, not historic success.
Read-only reconciliation retains `OutcomeUnknown` and records either
`outcome_unknown_observed_absent` or `outcome_unknown_observed_present`.
Presence advances the catalog observation for a new deliberate intent;
it does not authorize that intent or release the physical barrier.

Command rejections use `{ "code": "safe_reason_code" }`: invalid keys return
`400`; stale/changed payloads, missing consent, unsupported capability, busy
sources and fence conflicts generally return `409`. Missing/unauthorized
actor-owned operation IDs return `404`; controller authentication/scope
failures may return `401`/`403`. Confirmation can also return `200` with
`outcome: "Blocked"` and a `reasonCode`; HTTP success alone is not deletion
success. Typical blocked reasons include `source_changed_reconfirm`,
`fresh_confirmed_idle_required`, `source_path_in_use`,
`physical_fence_conflict`, `physical_fence_lost`, `source_ownership_lost`,
`source_observation_unavailable`, and `delete_rejected`. A lost publication
fence after possible send can return an error while durable status remains
unknown; always consult status rather than interpreting that error as no-send.

#### Unknown-outcome quarantine and operator action

The current safe recovery policy is **indefinite physical-actuation quarantine
for an unresolved possibly sent delete**, not an elapsed-time retry policy.
The physical barrier is retained across HTTP failures and restarts; read-only
Delete reconciliation does not clear it. Existing dispatch-attempt
reconciliation is not a supported resolver for a direct Delete barrier.
Consequently even a new intent with new observation/consent may remain blocked
on that source printer. There is no Delete API to force-release this quarantine.

The 120-second request cancellation deadline and renewable printer-file lease
are resource/coordination bounds, **not proof that a destructive sender or
printer command has stopped**. Cancellation cannot recall a request already
accepted by the printer. A suspended sender may resume; an upstream server may
retain an accepted request after a client disconnect. A newer lease, cached or
fresh idle observation, current path absence/presence, credential change, or
application restart alone does not prove all such work has drained. Releasing
the barrier after an arbitrary timeout could allow a delayed old delete to
erase a newly recreated file. The certified weak backends provide neither
atomic version conditions nor a server-enforced expiring mutation token.
Therefore no finite automatic recovery timeout is claimed by this release.

UI should say: **"Deletion outcome unknown. This printer is quarantined from
further PrintFarmer actuation. Check status without retrying deletion and
contact a farm administrator with this operation ID."** Offer status refresh
and the read-only reconcile action, not Retry, Resend, Force delete, or a
success toast when absence is observed.

Administrators must preserve the operation ID and quarantine, investigate
sender/transport/backend evidence, and prevent path reuse while delayed
mutation remains possible. Do not clear database fence fields, delete the
operation, re-register the printer, or change idempotency keys to bypass the
barrier. Do not assume a restart or an idle/file-list response proves safety.
If old senders and already accepted remote commands cannot be proven stopped,
leave the quarantine in place and escalate for a reviewed recovery procedure.
Any future administrative release mechanism requires independent evidence
that every old sender is fenced/stopped **and** remote accepted work has
completed or been cancelled, followed by fresh source/idle/observation checks
and audited release of only the matching physical command barrier. Releasing
coordination must still preserve the historic `OutcomeUnknown`; it must never
replay the old delete or manufacture a successful outcome. Such a release
mechanism is not implemented by this flow.

Legacy `DELETE /api/printers/{id}/files` now requires Delete permission and
source Manage, then returns `409` with `safe_delete_intent_required` in its
`CommandResult` message without calling the backend. Filename-only callers
must use the catalog intent/confirmation flow. Existing immediate Print
behavior is unchanged except that, as with other physical controls, an
unresolved physical barrier blocks actuation.

### Migration, rollout and verification limits

Deploy the additive AppDbContext migrations through normal startup migration
handling before enabling discovery. Maintained SQLite, PostgreSQL and SQL Server
each include `AddPrinterFileCatalog`; SlicerDbContext has no catalog changes.
Do not run a destructive down-migration as a feature rollback. Keep a compatible
application/schema pair and use global or per-printer discovery disable to stop
scanning without discarding provenance, owned bytes or accepted queue jobs.
This is not a guarantee that arbitrary old and new writers can run together.

The `RetireGcodeHarvest` AppDbContext migration (SQLite, PostgreSQL and SQL
Server, #3531) permanently drops the four legacy harvest tables
(`GcodeHarvestQueueItems`, `HarvestFileGcodeFileMappings`,
`HarvestDiscoveredFiles`, `GcodeHarvestOperations`) and deletes only the
`RolePermissions` rows for the resource named exactly `gcode_harvest`, then
that resource. Nothing is archived. G-code library files, their `Harvested`
provenance, printer-file catalog data and printed-parts harvest fields are
retained. The migration is irreversible: its `Down` throws, so rollback means
restoring a backup taken before the upgrade. A populated SQLite upgrade test
verifies the retained rows and exact grant scope; PostgreSQL and SQL Server are
covered only by generated-migration and model-drift checks, not by execution
against a live database.

Validation for this implementation uses fake backends and isolated test
databases, never production printer mutations. A populated maintained SQLite
migration test verifies ordinal path identity and preservation after printer
removal. All five production/application model-drift checks pass, including
application SQLite, but that is not proof of executing PostgreSQL or SQL Server
migrations against a running database.

The Windows validation host has no configured `PFARM_TEST_POSTGRES_CONN` or
`PFARM_TEST_SQLSERVER_CONN`. Six existing infrastructure provider tests fail
explicitly for that reason; live provider migration/concurrency evidence remains
a deployment prerequisite. One existing PrintQueue test reaches its assertions
but fails deleting a pooled SQLite database during Windows cleanup. These
limitations must not be reported as an all-green backend run.

The relevant module, backend adapter and API regression suites, full React
coverage/build/lint, and solution formatting are recorded on the epic's
acceptance issue. A failed authorization, replay, content-pinning, coverage or
fencing assertion falsifies the safety claims above. A defect can disclose
restricted filenames, duplicate production, waste material or irreversibly
delete the only remote copy; scan disable is reversible, physical effects are
not. Review and live-provider evidence remain separate release gates.
