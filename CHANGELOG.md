## [Unreleased]

### Features

- Add rich printer-file settings and durable thumbnails through one bounded,
  confirmed-idle background pipeline for Moonraker, OctoPrint, and PrusaLink
  (#3570, #3571). Native metadata takes precedence over bounded G-code/BGCode
  fallback; tool colors, materials, and weights retain their source slots and
  unknown values. Browsing and preview/image GETs perform no printer I/O.
  The shared store defaults to 512 MiB and never evicts healthy previews to
  admit another file. Upgrade with the `AddPrinterFileEnrichment` migration
  for SQLite, PostgreSQL, or SQL Server; API replicas must share the database
  and persistent Gcode volume.

- Measure bounded printer-file fallback frequency and elapsed time by backend
  through the existing OpenTelemetry/Prometheus pipeline (#3571). Acquisition,
  fallback-start and skipped counters plus a duration histogram distinguish
  metadata/image triggers, timeouts, cancellations and failures without
  exporting file paths, printer IDs or credentials.

- Persist PrusaLink native file details in the database, including full display
  names, sizes, and all 41 supported print-metadata fields. Show saved details
  independently of thumbnail availability; reuse unchanged observations during
  scheduled scans and refetch on Force Refresh. Remove the inventory HTTP
  request-count cap while retaining time, entry, depth, and response bounds.
  Slow or failed optional detail reads no longer discard completed listings;
  saved details remain available when source evidence is unchanged.

- Add independent Moonraker runtime speed/flow API controls with bounded
  percentages, semantic capabilities and nullable authoritative status/SignalR
  readback. Other backends explicitly reject unsupported writes (#3474).

- Make cameras a first-class feature at `/cameras` with all, by-location and
  by-printer views. Cameras link to many printers (one primary per printer),
  are auto-discovered from printer backends on connect, create and every
  `CameraSync.intervalMinutes` (default 15), and are managed from Add/Edit
  Printer. Existing printers are backfilled on the first sync after upgrade.
  The admin settings Cameras entry is removed (#3335).

- Add dry-run-first Mac mini Ralph setup with private unverified staging and
  explicit native-app cutover instructions, discovered policy approval and
  receipt reuse/renewal without manually copying IDs or commit pins (#2935).
- Stage a sole mini coordinator with centralized triage and native Mac/Windows
  consumers using an explicitly supplied private GitHub control repository.
  Durable reserve-before-publish transitions, role gates, opaque receipts and
  local native-session journals retain capacity through uncertainty. Setup
  never initializes the queue, enables schedules or migrates live authority.

### Breaking changes

- Interactive printer controls now defer motion, homing, and temperature
  readiness to native firmware/macros instead of PrintFarmer preflight.
  Filament actions no longer automatically heat or wait. Relative jogging
  reflects backend transport support independently of absolute movement;
  missing telemetry does not revoke native API control support.
  Permissions, typed inputs, no-replay handling and automated/calibration
  protections remain (#3512).

- Ralph shared host profiles no longer pin deployment workflow UUIDs. IDs remain
  in private configuration; filesystem preflight is non-authorizing and every
  round requires fresh current-execution native identity reconciliation. Existing
  deployments need an owner-approved merged policy pin and updated bootstrap;
  no live workflow, schedule or host configuration is changed automatically.
- Ralph macOS eligibility now includes all issues with hard 1-mobile + 4-general
  work quotas (5 total); Windows permits only 5 general. New Windows mobile SSH
  dispatch is disabled while legacy reconciliation remains available. Mac
  general admission remains blocked on the legacy dispatcher. The new native
  role path requires approved policy, attested cutover and pinned private queue
  genesis before its four general slots become operational.

### Fixes

- Align the Prometheus exporter with the OpenTelemetry 1.18 SDK so `/metrics`
  exports samples instead of returning an empty HTTP 200 response after a
  serializer type-load failure (#3571).

- Preserve acquired printer-file metadata and thumbnails when optional G-code
  fallback times out. Native thumbnail acquisition now precedes fallback, and
  the service lets the adapter report partial results before its safety timeout
  (#3571).

- Retry printer-file lease allocation conflicts as whole transactions instead
  of failing scans on wrapped PostgreSQL serialization errors. Bulk Force
  Refresh now uses the configured I/O capacity rather than scanning printers
  serially, with bounded retries only when no scan started. Both refresh
  controls are icon-only and remain inactive through refresh, cache reload,
  and observed server scans (#3571).

- Update System.Security.Cryptography.Xml and its transitive Pkcs dependency
  from 10.0.10 to 10.0.14 across .NET projects, resolving high- and
  moderate-severity NuGet security advisories (#3568).

- iOS: remove the Heat **Go** button. Nozzle, Bed, Chamber, Heater Box and
  other settable heaters now share one row: name, current reading, a tappable
  target and ±. Steppers apply that heater after a short pause; typed targets
  apply on **Set** and **Cancel** reverts. Pending commands no longer show
  caption text on heater rows. Presets now sit above the rows, cool down is a
  ❄ button, and queued targets that can no longer be sent (for example once a
  print starts) are dropped with a Heat notice (#3547).

- Align the detailed printer card and sidebar with working **Materials** and
  **Tuning** disclosures. Group filament slots and single-spool details under
  Materials and retain pending control state while collapsed (#3489).

- Replace web speed/flow inputs with print-only **Tuning** sliders that send once
  when an adjustment ends, without Apply buttons or redundant help text. Hide
  all filament sensor rows and empty sensor messages while retaining live
  readback and authentication no-replay safeguards (#3487).

- Remove generated Sensor/Heater type subtitles from web sensor names and show
  HeaterBox humidity as an indented child with a water-percent glyph and its
  reading in the Current column (#3467).

- Correct Moonraker active-tool temperature identity, individually bounded
  extruder/bed targets, safe-off zero values and explicit all-heater cooldown.
  Preserve sensor telemetry through authenticated fallback polling, clear invalid
  readings without refreshing unrelated safety evidence, and expose filament
  monitoring switches and gas resistance. Keep measured temperature extrema in
  the API without cluttering the web sensor rows (#3462).

- Fix the slicer 3D text tool producing empty geometry (and empty STLs) for
  every font family since #2901, and upgrade `three` to 0.186.1. The Droid
  serif, sans and mono typefaces that three no longer publishes are now bundled
  under Apache-2.0 with their upstream NOTICE (supersedes #3178).

- Update the frontend test environment to jsdom 30 and exercise filament-slot
  focus restoration through a visible rail. Frontend development now requires
  Node.js 24.15+ within 24.x, or Node.js 26+.

- Align EF Core, Microsoft.Data.Sqlite, and EF health checks on 10.0.12,
  including coupled logging and dependency injection pins in backend plugins,
  discovery, shared workers, and the profile parser tool to prevent NuGet
  downgrades and dependency-submission failures (#3357).

- Honor **OctoPrint/Slicer API → Require API key** for slicer uploads: allow
  anonymous uploads and queue submission when disabled, while preserving
  authenticated users' queue and printer-group permissions (#2779).

## [0.2.3]

### Features

- Release publication emits a signed complete immutable release set.

### Fixes

- Request the owner-approved `workflows: write` permission explicitly for the
  canonical reusable publisher while retaining its existing pinned action and
  least-privilege read/write permissions. The consumed tag diagnostic workflow
  file is removed from `development`, retiring future dispatches from that ref;
  no GitHub API operation disables or deletes the remote workflow record, and
  existing run history is unaltered.
- Record the source-only assessment for the unsigned sequence-1 reservation:
  the permanent tag is not a release or recovery, the original discarded HTTP
  422 remains unknown, and no safe recovery operation fits the current
  signed-authority invariants without a new owner decision.
- Release GitHub failures preserve bounded, sanitized validation and permission
  diagnostics without changing request or recovery behavior (#2734).
- Publication keeps the release draft until its signed manifest is verified.
- Automatic release qualification runs after read-only admission, with genuine
  same-transaction checks and exact-tree reviewed-PR evidence before publication.
- Release review verification reads individual commit statuses so genuine
  GitHub Actions creator provenance is retained through collection and authorization.

### Breaking changes

- None.
