# G-code Harvesting API

This document provides detailed reference for the G-code Harvesting endpoints.

The legacy backend endpoints and stored history remain supported. The React
G-code harvest UI and client transport are retired; use **Files > On printers**
(`/files?source=printers`, optionally `&printer=<printerId>`) instead. Its
explicit Copy action handles one file at a time; it does not provide bulk
import. Printed-parts inventory harvesting and mobile workflows are unchanged.

## Overview
The harvesting system scans a printer's G-code storage, filters candidate files, records discovered files, and optionally imports them into the central library. Operations are asynchronous and progress is observable via the REST API or real-time SignalR events on `/hubs/printers`.

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/gcode-harvest/start` | Start a harvest operation |
| GET | `/api/gcode-harvest/operations/{operationId}` | Operation status/details |
| GET | `/api/gcode-harvest/operations/{operationId}/files` | Discovered files list |
| POST | `/api/gcode-harvest/import` | Import selected discovered files |
| POST | `/api/gcode-harvest/operations/{operationId}/cancel` | Cancel running operation |
| GET | `/api/gcode-harvest/printers/{printerId}/active` | Active operation for printer |
| GET | `/api/gcode-harvest/printers/{printerId}/recent?count=10` | Recent operations for printer |
| GET | `/api/gcode-harvest/active` | All active operations |
| GET | `/api/gcode-harvest/queue?printerId={printerId}&status=Pending` | Queue summaries with optional printer and status filters |

## Harvest Queue

`GET /api/gcode-harvest/queue` returns an array of queue summaries, ordered by
priority descending and then queued time ascending. Both `printerId` and `status`
are optional, combined filters applied in the database. Reads do not track
entities and select only summary fields, including the current printer name.
The endpoint remains unpaginated and requires `gcode_harvest:admin`.

Each summary contains `id`, `printerId`, `printerName`, `queuedAt`,
`processingStartedAt`, `completedAt`, `status`, `priority`, `errorMessage`,
`filesFound`, and `filesAdded`. Status values are strings (`Pending`,
`Processing`, `Completed`, `Failed`, or `Cancelled`).

## Start Harvest
`POST /api/gcode-harvest/start`

Request body (StartGcodeHarvestDto):
```json
{
  "printerId": "11111111-1111-1111-1111-111111111111",
  "includeSubdirectories": true,
  "fileExtensions": ["gcode", "gco"],
  "minFileSizeBytes": 1024,
  "maxFileSizeBytes": 104857600,
  "modifiedAfter": "2025-09-01T00:00:00Z",
  "duplicateHandling": "skip"
}
```

Field notes:
- `fileExtensions`: null/empty => no extension filtering.
- `minFileSizeBytes` / `maxFileSizeBytes`: inclusive bounds (null removes bound).
- `modifiedAfter`: strict greater-than filter.
- `duplicateHandling`:
  - `skip`: ignore existing (increments FilesSkipped)
  - `overwrite`: replace existing entry (increments FilesAdded)
  - `rename`: import as new (adds suffix `-copy`, `-copy2`, ...)

Response (GcodeHarvestResultDto):
```json
{
  "operationId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "success": true,
  "message": "Harvest started",
  "discoveredFiles": 0,
  "importedFiles": 0,
  "errors": null
}
```

## Operation Object
`GcodeHarvestOperationDto` (summary):
```json
{
  "id": "...",
  "printerId": "...",
  "status": "Running|Completed|Failed|Cancelled",
  "startedAt": "2025-09-06T07:30:00Z",
  "completedAt": null,
  "filesFound": 0,
  "filesAdded": 0,
  "filesSkipped": 0,
  "filesErrored": 0,
  "totalBytesProcessed": 0,
  "includeSubdirectories": true,
  "maxFileSizeBytes": 104857600,
  "modifiedAfter": null,
  "fileExtensions": ["gcode","gco"],
  "minFileSizeBytes": 1024,
  "duplicateHandling": "skip"
}
```

## Discovered File Object
`DiscoveredGcodeFileDto` (summary):
```json
{
  "id": "...",
  "harvestOperationId": "...",
  "printerPath": "/path/to/file.gcode",
  "fileName": "file.gcode",
  "fileSizeBytes": 123456,
  "modifiedAt": "2025-09-05T14:11:00Z",
  "fileHash": null,
  "isSelected": false,
  "alreadyInLibrary": false,
  "existingLibraryFileId": null,
  "processingFailed": false,
  "errorMessage": null,
  "extractedSlicerName": null,
  "extractedSlicerVersion": null,
  "extractedPrintTime": null,
  "extractedFilamentLength": null,
  "extractedNozzleDiameter": null,
  "extractedMaterial": null,
  "extractedLayerHeight": null,
  "extractedInfill": null
}
```

## Import Selected Files
`POST /api/gcode-harvest/import`
```json
{
  "harvestOperationId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "selectedFileIds": ["f1f1...", "f2f2..."],
  "addToLibraryOnly": true,
  "autoDetectCapabilities": true,
  "defaultTags": ["harvested"]
}
```

## Cancellation
Cancellation attempts return HTTP 200 with `true` if successful, `400` if the operation can no longer be cancelled.

## Real-time Updates
Operation progress & completion events are broadcast through the existing printers SignalR hub. The client merges updates into local state.

## Development Notes
- AppDbContext schema is migration-managed for SQLite, PostgreSQL, and SQL Server.
  SQLite API startup uses `ProviderAwareMigrationRunner`; new catalog schema must
  be migrated on existing installations as well as new databases.
- Duplicate handling is enforced during worker processing when a file hash / name indicates an existing library entry.

## Durable printer-file catalog

The catalog is independent of harvest-operation history. A remote location
identifies one registered printer, storage root, and backend-canonical relative
path. It is not a library asset: `GcodeFile` continues to represent owned local
bytes. Nested equal filenames, distinct storage roots, and case-distinct paths
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
produce invented complete emptiness. Inventory traversal is capped at 100
requests, 10,000 entries, depth 16, and 120 seconds, without G-code body,
per-file header, or thumbnail requests.

### Discovery policy and metadata API

The `PrinterFileIndex` settings section is saved independently through the
existing settings shell. Its default cadence is 15 minutes with up to 20%
deterministic per-printer jitter. Failures back off to at most six hours,
including jitter. Entries become stale after two configured intervals.
Authentication failures pause that printer until configuration changes or an
authorized refresh is requested.

Discovery requires fresh, explicitly idle backend telemetry. Normalized `Idle`
alone is insufficient because legacy normalizers also map unknown states to
idle. Offline, busy, stale, or unknown activity defers discovery without
refreshing last-seen timestamps. Disabling the global index or one printer's
index retains observations, origins, and owned local files; it does not disable
explicit Copy or Queue actions.

Busy or unknown sources receive a jittered next-attempt time without advancing
last-seen or complete-scan evidence, so they cannot monopolize the due batch.
Unexpected per-printer scan failures are isolated, logged with a safe failure
type, and backed off without changing file availability. A control write that
races scan ownership returns `409` with `scan_state_changed`; refresh state and
retry the deliberate Refresh or enable/disable action.

Database-backed, renewable ownership shares two aggregate I/O slots by default
and permits only one feature operation per printer. Configuration, registration,
policy, and ownership are fenced again in the publication transaction. No
database transaction spans printer HTTP requests. A superseded scan cannot
publish positive observations or mark files missing.

| Endpoint | Behavior |
| --- | --- |
| `GET /api/printer-files` | Metadata-only, paginated locations, authorized printer choices, scan status, and local-copy associations |
| `POST /api/printer-files/printers/{id}/refresh` | Request a coalesced idle scan; does not bypass discovery disables or idle checks |
| `PUT /api/printer-files/printers/{id}/index` | Set that printer's discovery control with `{ "enabled": true/false }` |

Listing accepts `printerId`, `search` (at most 200 characters), `availability`
(`Available`, `Missing`, `Deleted`), `stale`, `page`, and `pageSize`
(1-200, default 50). Source authorization is applied before counts,
pagination, filtering, origins, and scan status. Responses contain canonical
root/path observations, not credentials or printer URLs.

Search follows the database provider's collation and can have different case
matching across deployments; location identity remains strictly ordinal.
Current-authority checks scale with the number of enabled printers, including
printer choices outside the current file page. Large farms should measure
catalog polling load before rollout; no constant-time authorization or
large-farm throughput guarantee is claimed.

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
The latter offers metadata search, availability/freshness filters, scan controls,
and single-location actions. It never downloads remote previews or thumbnails.
Captured local versions are links, not a claim that current remote bytes match.
Legacy harvest remains available through the backend, not the React UI. Copy
handles one file at a time; bulk import is unavailable. Part/stock harvesting
and immediate printer Print controls are unchanged.

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

## Future Enhancements (Roadmap)
- Pagination & server-side filtering for discovered files
- Retry queue for transient download errors
- Hash pre-check via printer metadata where available
- Optional auto-import on completion
