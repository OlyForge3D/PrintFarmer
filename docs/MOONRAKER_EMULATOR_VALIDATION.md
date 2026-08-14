# Moonraker Emulator Validation

This document covers the repository-built Moonraker protocol emulator strictly from
the deployment/validation side: what the container exposes, how it is wired into the
digest-pinned daily validation stack described in
[`DAILY_DEVELOPMENT_IMAGES.md`](./DAILY_DEVELOPMENT_IMAGES.md), and the supported vs.
unsupported boundary from that integration's point of view. The emulator's own
protocol/scenario implementation is documented alongside its source
(`src/moonraker-emulator/`); this page does not restate details this deployment
integration cannot verify.

## What the emulator container provides

| Property | Value |
|---|---|
| Image | `ghcr.io/olyforge3d/printfarmer-moonraker-emulator` |
| Build target | `moonraker-emulator-runtime` (`scripts/docker/dockerfiles/Dockerfile.multistage`) |
| Listen port | `7125` (root — no path prefix) |
| Health check path | `/healthz` |
| Control API toggle | `Emulator__EnableControlApi` (`true`/`false`) |
| Optional protocol authentication | `Emulator__ApiKey` (`X-Api-Key` header or `token` query parameter) |
| Instance identity | `Emulator__Scenario`, `Emulator__PrinterId`, `Emulator__PrinterName` |
| Runtime user | dedicated non-root `emulator` user |

The container has no Docker socket mount, no physical network scanning, and no
external network or service dependencies — it only serves deterministic,
printer-shaped HTTP/WebSocket state to the unchanged `Farm.Backend.Plugin.Moonraker`
client.

Local native runs bind to `127.0.0.1` by default. The container explicitly binds its
isolated listener for Compose networking. When `Emulator__ApiKey` is non-empty, every
Moonraker HTTP route and `/websocket` connection requires the configured key; the
validation-only `/__emulator/**` surface remains separately controlled by
`Emulator__EnableControlApi` and must not be enabled in production.

Seeded printer URLs must be plain `http://<host>:7125` origins, with no trailing
slash and no path prefix such as `/printers/{id}`: `Printer.BackendUrl` trims the
trailing slash and `Farm.Backend.Plugin.Moonraker` builds every request URI relative
to that origin, so a path-prefixed seed would double the path on every backend call.

## Topology: one image, four isolated instances (plus one intentionally absent)

The generated compose stack runs **four** separate instances of the same
digest-pinned emulator image, each its own compose service and container, each
configured to serve exactly one deterministic printer at its own root:

| Compose service | `Emulator__Scenario` | `Emulator__PrinterId` | `Emulator__PrinterName` |
|---|---|---|---|
| `moonraker-ready` | `Ready` | `ready` | `Moonraker Ready` |
| `moonraker-printing` | `Printing` | `printing` | `Moonraker Printing` |
| `moonraker-paused` | `Paused` | `paused` | `Moonraker Paused` |
| `moonraker-shutdown` | `Shutdown` | `shutdown` | `Moonraker Shutdown` |

This repository's "exactly one" requirement applies to the OrcaSlicer worker, **not**
to these emulator instances — the four are intentional, expected replicas of the
same image, addressed by Docker Compose's normal per-service DNS name. The ready
instance also has two discovery-only aliases described below.

`src/api/Services/Startup/MoonrakerEmulatorSeedSettings.cs` (config section
`MoonrakerEmulatorSeed`) seeds five real `Moonraker`-backend printer records by
default: Ready, Printing, Paused, and Shutdown point at their matching instance above
(`http://moonraker-ready:7125`, `http://moonraker-printing:7125`, etc.). The fifth,
**Offline**, is seeded as `http://moonraker-offline:7125` — a hostname with
**no compose service and no listener at all**. This is deliberate: it lets
`Farm.Backend.Plugin.Moonraker`'s unreachable/offline handling be exercised against a
real connection failure instead of a simulated one, so the seeded "Moonraker Offline"
printer is expected to report `isOnline: false`.

Adding a new default scenario to `MoonrakerEmulatorSeedSettings` requires adding a
matching instance service to
`scripts/docker/compose-templates/docker-compose.moonraker-emulator.yml` in the same
change (unless the new default is intentionally offline-only), or the API seeder will
fail to reach it.

## Enabling it in the daily validation stack

```bash
./scripts/docker/compose-generator.sh \
  --architecture microservices \
  --db-provider postgres \
  --enable-orca-worker yes \
  --include-discovery \
  --include-moonraker-emulator \
  --exclude-monitoring \
  --exclude-telemetry \
  --output-dir "$STACK_DIR"
```

`docker-compose.daily-validation.yml` then:

- sets `MoonrakerEmulatorSeed__Enabled: "true"` on the `api` service — this one flag
  is sufficient (it is disabled by default in every other environment) and seeds the
  five built-in deterministic printers above with no further configuration: root URLs
  `http://moonraker-ready:7125`, `http://moonraker-printing:7125`,
  `http://moonraker-paused:7125`, `http://moonraker-shutdown:7125`, and the
  intentionally-unreachable `http://moonraker-offline:7125`, all backend `Moonraker`.
  Optional `MoonrakerEmulatorSeed__Printers__N__Name` /
  `MoonrakerEmulatorSeed__Printers__N__ServerUrl` /
  `MoonrakerEmulatorSeed__Printers__N__IsEnabled` env vars may override or add entries
  for local experimentation, but daily validation itself relies only on the built-in
  defaults — never a `BaseUrl` setting or a path-prefixed seed URL;
- sets `Discovery__DeterministicFixtures__Enabled: "true"` on the
  `printer-discovery` service so `discovery/add` exercises the real application path
  instead of scanning the physical network;
- publishes each instance's control API to its own loopback-only port —
  `127.0.0.1:${MOONRAKER_EMULATOR_PORT:-17125}` (ready),
  `127.0.0.1:${MOONRAKER_EMULATOR_PRINTING_PORT:-17126}` (printing),
  `127.0.0.1:${MOONRAKER_EMULATOR_PAUSED_PORT:-17127}` (paused), and
  `127.0.0.1:${MOONRAKER_EMULATOR_SHUTDOWN_PORT:-17128}` (shutdown) — via
  `Emulator__EnableControlApi: "true"`. The base template leaves every instance's
  control API disabled and unpublished, so none is reachable outside this isolated
  validation overlay.

No prior TestEmulator flags (`TestEmulator__Enabled`, `TestEmulator__MockDiscovery`,
`TestEmulator__MockSpoolman`, or the `TestEmulator__Printers__N__*` group) remain in
this overlay: every seeded printer in daily validation is backed by the real
`Moonraker` plugin talking to these emulator instances, not the in-process
TestEmulator plugin.

`DeterministicDiscoveryFixtureSettings`' two default discovery candidates —
`Discovered Voron V2.4` and `Discovered Prusa MK4S` — resolve to
`http://moonraker-discovery-voron:7125` and `http://moonraker-discovery-prusa:7125`.
Both hostnames are network aliases of the `moonraker-ready` instance (see Topology
above). Deterministic fixture discovery intentionally bypasses physical probing: the
injected fixture provider returns these candidate DTOs directly and does **not**
contact the aliases or perform any Moonraker handshake during the scan itself. What it
does exercise for real is the discovery controller/streaming service/broadcaster/
session-registry application path, with no LAN scan involved. The returned URLs
resolve via these aliases so that if a candidate is subsequently added as a printer,
the unchanged `Farm.Backend.Plugin.Moonraker` backend connects to the same running
instance for real — that add/connect step is covered by UI add/printer-card E2E
coverage, not by the discovery scan itself. Because the discovery URLs differ from every initial `MoonrakerEmulatorSeed` hostname,
both candidates are available in a fresh validation stack. After a candidate is added,
normal registered-URL filtering excludes it from later scans.

## Verifying the backend proof

`GET /api/printers` returns each printer's `backend` field as a string enum, and its
`isOnline` field reflects live backend reachability. Daily validation is expected to
report `backend == "Moonraker"` for the seeded printers (never `"TestEmulator"`), and
the "Moonraker Offline" printer specifically is expected to report
`isOnline == false`. The Compose-level smoke script
(`scripts/ci/smoke-daily-validation-stack.sh`) asserts exactly this, plus health for
all four running emulator instances, the deterministic discovery contract — a scan
(`autoRegister=false`, no live connection to the emulator) finding both
`Discovered Voron V2.4` and `Discovered Prusa MK4S` with `printerBackend == "moonraker"`
(`DiscoveryController.ScanAsync` maps `DiscoveredPrinterDto` into the local
`DiscoveryResult` type, serialized camelCase as `.hostname` and `.printerBackend`, with
the backend value explicitly lowercased via `ToLowerInvariant()`) — and exactly one
running `orcaslicer-worker` container:

```bash
scripts/ci/smoke-daily-validation-stack.sh
```

The script requires a reachable Docker daemon. If Docker is unavailable it prints an
explicit `SKIP:` message and exits `0`, so it can be included in local unit test runs
without failing Docker-less environments; once the stack is up, any failed assertion
is fatal and the script exits non-zero after printing the failing container's logs.
It always tears down its own isolated Compose project and generated directory on
exit, whether or not the assertions passed. Local image builds use the tracked
`scripts/docker/dockerfiles/Dockerfile.multistage` source and do not depend on an
ignored generated copy at the repository root.

## Protocol fidelity inventory

The emulator intentionally implements the Moonraker surface consumed by the current
`MoonrakerClient`, `MoonrakerSubscriptionService`, and printer-facing UI. It is not a
general-purpose replacement for every upstream Moonraker component.

### HTTP and REST

| Area | Supported routes and behavior |
|---|---|
| Identity and health | `/healthz`, `/printer/info`, `/server/info`, `/machine/system_info` |
| Printer objects | `/printer/objects/list`, `/printer/objects/query`; `webhooks`, `print_stats`, `display_status`, `toolhead`, `gcode_move`, `motion_report`, `extruder`, `heater_bed`, `fan`, `virtual_sdcard`, `idle_timeout`, `exclude_object`, Happy Hare, AFC, Qidibox, and Snapmaker U1 objects consumed by the backend |
| Commands | `/printer/gcode/script`; homing, absolute/relative movement, temperature targets, emergency stop, firmware restart, motor disable, filament commands, object exclusion, and Happy Hare/Qidibox/AFC MMU controls generated by the real client |
| Print lifecycle | `/printer/print/start`, `/printer/print/pause`, `/printer/print/resume`, `/printer/print/cancel`; realistic invalid-state, busy, unavailable, and missing-file errors |
| Files | `/server/files/roots`, `/server/files/list`, `/server/files/directory`, `/server/files/move`, `/server/files/copy`, `/server/files/metadata`, `/server/files/metascan`, `/server/files/thumbnails`, `/server/files/thumbs/*`, `/server/files/gcodes/*`, `/server/files/upload`; create/list/delete directories and upload/download/delete/start files |
| Cameras | `/server/webcams/list`, `/server/webcams/test`, `/webcams/{name}/snapshot`, `/webcams/{name}/stream`, and `/server/files/camera/monitor.jpg`; all media is local deterministic fixture data |
| History | `/server/history/list`, `/server/history/job`, `/server/history/totals`, `/server/history/reset_totals`; deterministic seed history plus lifecycle-generated entries |
| Spoolman through Moonraker | `/server/spoolman/status`, `/server/spoolman/spool_id`, and the consumed `/server/spoolman/proxy` spool lookup paths |

All REST responses use Moonraker-style `result` or `error` envelopes where the real
client expects them. Unknown routes and unmodeled Spoolman proxy paths fail explicitly;
they do not return success-shaped placeholders.

### WebSocket JSON-RPC

`/websocket` supports:

- `server.connection.identify`;
- `server.info` (including the production subscription service's heartbeat);
- `printer.objects.list`;
- `printer.objects.subscribe`, including per-object field filtering and an initial
  snapshot;
- `printer.objects.query`;
- `server.files.get_directory`;
- `camera.start_monitor` and `camera.stop_monitor`;
- `notify_status_update`, `notify_klippy_ready`,
  `notify_klippy_disconnected`, and `notify_klippy_shutdown`.

The endpoint accepts fragmented client messages, preserves JSON-RPC request IDs,
returns standard parse/invalid-request/method-not-found errors, serializes concurrent
subscriber writes, and broadcasts mutations to every applicable subscription.

## Scenarios, virtual time, and fault controls

| Scenario | Initial observable state |
|---|---|
| Ready | Klippy ready, idle, homed axes, ambient temperatures, seeded file/history/camera/spool data |
| Printing | Klippy ready, `benchy.gcode` active, heated tool/bed, object list, deterministic progress |
| Paused | Klippy ready, `benchy.gcode` paused at 20 percent with resumable state |
| Shutdown | Moonraker reachable, Klippy shutdown, print error state, firmware restart available |
| Offline | No emulator process or listener; the real backend receives a connection failure |

`Emulator__TimeScale=0` is the deterministic default. Tests advance virtual time
explicitly; a positive value enables accelerated demo progress.

The validation-only control API is available only when
`Emulator__EnableControlApi=true`:

| Route | Purpose |
|---|---|
| `GET /__emulator/printers` | Read the current instance state as a one-element array |
| `POST /__emulator/reset` | Restore the complete configured fixture baseline: scenario, virtual time, files, history/totals, Spoolman, MMU, and faults |
| `POST /__emulator/printer/scenario` | Switch to `Ready`, `Printing`, `Paused`, or `Shutdown` |
| `GET/POST /__emulator/printer/mmu` | Read or select `None`, `HappyHare`, `Afc`, `Qidibox`, or `SnapmakerU1` |
| `GET /__emulator/time` | Read virtual time |
| `POST /__emulator/time/advance` | Advance virtual time by `{ "seconds": number }` |
| `POST /__emulator/time/reset` | Reset virtual time |
| `GET/POST /__emulator/rules` | List or add fault rules |
| `DELETE /__emulator/rules/{id}` | Remove one fault rule |
| `POST /__emulator/rules/clear` | Remove all fault rules |

Fault rules can target HTTP paths/methods or WebSocket JSON-RPC methods. Supported
effects are latency, explicit HTTP status/body, JSON-RPC error, malformed JSON,
WebSocket disconnect, stale-notification suppression, and Klippy unavailable. Rules
can be one-shot with a remaining-use count or repeating. Control paths themselves are
never affected by fault rules, so a broad injected fault cannot prevent cleanup.

The daily validation API also enables `POST
/api/test/moonraker-emulator/reset`. This application-level reset requires an
authenticated principal holding the `diagnostics:admin` permission (including
`farm_admin`), returns `404` unless the API is running in Development with both
`MoonrakerEmulatorSeed__Enabled=true` and
`MoonrakerEmulatorSeed__EnableControlApi=true`, and is never enabled by the production
templates. It restores the seeded printing/paused queue rows and dispatch ownership,
cancels transient active jobs, clears physical-control and acknowledgement state, and
removes printers added from the deterministic `moonraker-discovery-*` fixtures. The
Playwright fixture calls it before every emulator test. The application reset and
each emulator instance's complete fixture reset are both required because neither
boundary owns the other's PostgreSQL or process-local state.

Example deterministic progress and one-shot fault:

```bash
curl -X POST http://127.0.0.1:17126/__emulator/time/advance \
  -H "Content-Type: application/json" \
  -d '{"seconds":330}'

curl -X POST http://127.0.0.1:17125/__emulator/rules \
  -H "Content-Type: application/json" \
  -d '{
    "target":"Http",
    "effect":"HttpStatus",
    "pathContains":"/printer/info",
    "httpStatusCode":503,
    "remainingUses":1,
    "repeating":false
  }'
```

## Running printer-facing E2E coverage

After the daily validation stack is healthy, run the strict Moonraker printer suite
from `src/Web/ReactApp`:

```bash
API_BASE_URL=http://127.0.0.1:15245 \
BASE_URL=http://127.0.0.1:18080 \
npm run test:e2e:moonraker -- --project=chromium
```

The suite defaults the four control URLs to loopback ports `17125` through `17128`.
Override any instance independently with `MOONRAKER_EMULATOR_URL_READY`,
`MOONRAKER_EMULATOR_URL_PRINTING`, `MOONRAKER_EMULATOR_URL_PAUSED`, or
`MOONRAKER_EMULATOR_URL_SHUTDOWN`.

The daily stack no longer uses TestEmulator flags. The TestEmulator plugin remains in
the repository for its existing focused tests and non-daily workflows; this change
does not remove or alter production backend selection.

## Unsupported Moonraker APIs

The following upstream surfaces are intentionally unsupported because current
PrintFarmer printer flows do not consume them:

- MQTT and agent/event bridge administration;
- CAN bus, USB, serial, and peripheral inventory;
- Moonraker database, user, API-key, authorization, and login administration;
- announcements, extensions, and update-manager mutation;
- sudo/password, service/package management, and host reboot/shutdown;
- OctoPrint compatibility endpoints and arbitrary third-party component APIs;
- power-device discovery and mutation (`/machine/device_power/*`); PrintFarmer defines
  infrastructure DTOs for this upstream area but has no production Moonraker call site
  or printer-facing UI consuming it;
- unmodeled Spoolman proxy operations beyond the consumed spool lookups.

All four filament changer/toolhead variants consumed by the current Moonraker
subscription service are supported: Happy Hare, AFC, Qidibox (including its
`officiall_filas_list.cfg` lookup), and Snapmaker U1.

Requests to unsupported paths return an explicit `404` JSON error. Add a route only
when the real backend or a printer-facing feature consumes it, and add protocol plus
real-client integration coverage in the same change.

## Deployment smoke boundary

This integration only requires and only verifies:

- the `/healthz` endpoint on each of the four running emulator instances;
- each running instance answering as `Moonraker` for its own configured printer so
  `Farm.Backend.Plugin.Moonraker` can report it online with a backend identity of
  `Moonraker`;
- that the fifth seeded printer ("Moonraker Offline"), which points at a hostname
  with no listener, reports `isOnline == false`;
- that a `printer-discovery` scan (`autoRegister=false`) satisfies the deterministic
  discovery contract — finding the Voron and Prusa fixture entries with the expected
  hostname/backend fields — **without** the scan itself contacting the
  `moonraker-ready` instance's discovery-only network aliases or performing any
  Moonraker handshake; a subsequently added candidate connecting for real is covered
  by UI add/printer-card E2E coverage, not by this scan;
- the optional control API toggle (`Emulator__EnableControlApi`), which this
  integration only ever enables inside the isolated daily validation network.

It makes no claim about, and does not exercise, the emulator's coverage of the wider
upstream Moonraker API surface (for example MQTT, CAN bus, Moonraker's own
database/user/auth administration, announcements, sudo, USB/serial inventory,
OctoPrint compatibility, or update-manager mutation APIs). Whether those are
implemented, partially implemented, or intentionally out of scope is defined by the
emulator's own source and tests, not by this deployment integration — do not infer
protocol coverage from the presence of a compose service or a passing health check.
