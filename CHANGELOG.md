## [Unreleased]

### Features

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
