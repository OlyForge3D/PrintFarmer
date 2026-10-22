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
| Commands | `/printer/gcode/script`, `/printer/emergency_stop`; homing, absolute/relative movement, temperature targets, emergency stop, firmware restart, motor disable, filament commands, object exclusion, and Happy Hare/Qidibox/AFC MMU controls generated by the real client |
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

### Standard Klipper sensor fixtures

Every scenario includes these standard objects alongside the legacy extruder,
bed and part fan. They appear in REST and JSON-RPC object lists/queries and in
initial subscriptions. Subsequent `notify_status_update` messages contain only
changed, subscribed fields; unchanged readings do not generate notifications.

| Object | Fields and units |
|---|---|
| `temperature_sensor endstop` | `temperature`, `measured_min_temp`, `measured_max_temp`: Celsius numbers |
| `temperature_fan chamber_heater` | `temperature`, `target`: Celsius; `speed`: 0-1 number; `rpm`: rotations/minute number |
| `heater_generic chamber` | `temperature`, `target`: Celsius; `power`: 0-1 PWM duty |
| `heater_fan hotend` | `speed`: 0-1 number; `rpm`: null (no tachometer) |
| `controller_fan electronics` | `speed`: 0-1 number; `rpm`: null (no tachometer) |
| `fan_generic auxiliary` | `speed`: 0-1 number; `rpm`: null (no tachometer) |
| `filament_switch_sensor runout` | `enabled`, `filament_detected`: booleans, not 0/1 |
| `filament_motion_sensor encoder` | `enabled`, `filament_detected`: booleans |

`heaters` advertises heater/sensor inventory. The relevant subset of
`configfile.settings` exposes temperature-fan bounds (-255 to 256 Celsius) and
generic-heater bounds (0 to 100 Celsius), allowing the real backend to discover
writable targets. The automatic heater/controller fans remain read-only to the
backend. Test telemetry injection does not grant production control capability.

The supported command subset is `SET_FAN_SPEED FAN=auxiliary SPEED=0.5`,
`SET_HEATER_TEMPERATURE HEATER=chamber TARGET=50`,
`SET_TEMPERATURE_FAN_TARGET TEMPERATURE_FAN=chamber_heater TARGET=40`, and
`SET_FILAMENT_SENSOR SENSOR=runout ENABLE=0` (also `SENSOR=encoder`).
`SET_HEATER_TEMPERATURE` also updates the existing `extruder` and `heater_bed`
targets, just like their legacy `M104`/`M140` paths.
Snapmaker U1 mode additionally exposes independent `extruder1`, `extruder2` and
`extruder3` objects, with `T0`-`T3` selecting `toolhead.extruder`. Their heating
minimum is 10 Celsius, with maxima 290, 280 and 270 Celsius respectively; zero
is accepted separately as off. The primary `extruder` maximum is 300 Celsius
and the bed maximum is 120 Celsius. All limits appear in `configfile.settings`.
`TURN_OFF_HEATERS` clears every extruder, bed and generic heater target without
changing automatic or temperature-fan controls. Ordinary zero targets remain
local to their addressed heater.

Object queries support Moonraker's recommended `POST /printer/objects/query`
with an `objects` JSON body as well as GET query strings. Null field selections
return all fields; empty arrays return no fields; unknown objects/fields are
omitted. An empty POST object selection returns an empty status.

Names and values are required; optional upstream parameters, templates, PID,
thermal/tachometer physics, motion-sensor pulse timing and runout macros are not
simulated. A command changes its target/requested speed or enabled flag, not
the measured temperature, power or filament detection. These remain explicit
test inputs rather than invented physical completion.

With the control API enabled, inject readings through
`POST /__emulator/printer/sensor`:

```json
{
  "objectName": "temperature_fan chamber_heater",
  "fields": { "temperature": 35, "speed": 0.7, "rpm": 2100 }
}
```

Only existing fields with finite, appropriately ranged numbers or real booleans
are accepted; unknown objects/fields and invalid values return 400 without a
partial mutation. Temperature-sensor extrema update with readings and cannot be
injected directly. RPM remains numeric only on the configured tachometer fan,
and null-only on fans without a tachometer.
No new wall-clock work, network access or runtime dependency
is introduced. Readings stay fixed across virtual-time advances; notifications
use the existing virtual `eventtime`. Scenario/reset and firmware restart restore
the baseline. Separate printer aggregates never share sensor state.

#### Reference evidence and limits

Inspected and live-captured on 2026-10-18:

- [virtual-klipper-printer at e272bcd](https://github.com/mainsail-crew/virtual-klipper-printer/tree/e272bcd1060dad6cee1598d6debdaa5d5afae3c7):
  `temp_sensors.cfg` supplies the temperature sensor and temperature fan;
  `miscellaneous.cfg` supplies heater/controller fans and a filament-motion
  sensor. Its Dockerfile uses stock Klipper, not a vendor fork.
- [Klipper at 9cb9975](https://github.com/Klipper3d/klipper/tree/9cb99754c035b90206ef0a02660f674fc9517e9b):
  `klippy/extras/temperature_sensor.py`, `temperature_fan.py`, `fan.py`,
  `fan_generic.py`, `heater_generic.py` and `filament_switch_sensor.py`,
  plus the [official status reference](https://www.klipper3d.org/Status_Reference.html).
  These establish the field names/types, generic-heater contract and switch
  sensor omitted from the reference container's default configuration.
  In particular, fan status includes `rpm: null` without a tachometer.

The reference also ran in an isolated **WSLC** container, without a Docker daemon.
For local container-based testing, prefer `wslc` over a Docker daemon when it is
available; the emulator's fast tests still run natively without either.
The captured image was
`ghcr.io/mainsail-crew/virtual-klipper-printer@sha256:7144f96705d83514191f6f8653751c84600ae38c1b7318d1e66593c43c7d743a`
(image revision `e272bcd1060dad6cee1598d6debdaa5d5afae3c7`, version
`v0.13.0-787`). Its actual checked-out runtime revisions were
[Klipper e2b99a9](https://github.com/Klipper3d/klipper/tree/e2b99a9159f992169c1ba53029dec020e850cc99)
and [Moonraker 9e676eb](https://github.com/Arksine/moonraker/tree/9e676eba6b02661a4dfa3ec6e7ac3f3504498e6d);
these differ from the separately inspected Klipper revision above.
The reference and Klipper sources are GPL-3.0; repository licensing policy was
checked and no upstream source, scripts or configuration files were vendored.
Only protocol facts and original fixture data/implementation are included.

#### Live reference capture

The temporary container used loopback port 17139, two CPUs, a 2 GiB memory limit,
a private `/tmp`, and **no host mounts**. Verify that the chosen local port and
container name are unused before running:

```powershell
wslc pull --quiet ghcr.io/mainsail-crew/virtual-klipper-printer@sha256:7144f96705d83514191f6f8653751c84600ae38c1b7318d1e66593c43c7d743a
wslc run -d --name pf3434-reference --cpus 2 --memory 2G -p 127.0.0.1:17139:7125 --tmpfs /tmp ghcr.io/mainsail-crew/virtual-klipper-printer@sha256:7144f96705d83514191f6f8653751c84600ae38c1b7318d1e66593c43c7d743a
```

Inside that disposable container only, enable `addons/temp_sensors.cfg` in
`printer.cfg` and disable `addons/heater_bed.cfg`: they both use PA6. Keep the
default extruder and miscellaneous objects. Add an original private configuration
include for the three missing object types:

```ini
[heater_generic chamber]
heater_pin: PD4
sensor_type: Generic 3950
sensor_pin: PA0
control: watermark
min_temp: -255
max_temp: 100
gcode_id: CH

[fan_generic auxiliary]
pin: PA2
kick_start_time: 0

[filament_switch_sensor runout]
switch_pin: ^PA3
pause_on_runout: False
```

Restart the whole container with `wslc restart --timeout 15 pf3434-reference`,
then wait for `/server/info` to report `klippy_state: ready`.
`FIRMWARE_RESTART` alone failed to reset this simulated MCU during the capture;
the full container restart succeeded. Do not run the image's startup script
directly in a shared WSL distribution: it replaces `/bin/systemctl` inside its
container environment.

The REST query requested exactly the eight objects below. This is its captured
result, retaining actual names, values and event time; unrelated printer/system
objects were not requested:

```json
{
  "result": {
    "eventtime": 173.07767437,
    "status": {
      "temperature_sensor endstop": {
        "temperature": 0.0,
        "measured_min_temp": 99999999.0,
        "measured_max_temp": 0.0
      },
      "temperature_fan chamber_heater": {
        "speed": 0.0, "rpm": 0.0, "temperature": 0.0, "target": 0.0
      },
      "heater_generic chamber": { "temperature": 0.0, "target": 0.0, "power": 0.0 },
      "heater_fan heater_fan": { "speed": 0.0, "rpm": null },
      "controller_fan controller_fan": { "speed": 0.0, "rpm": null },
      "fan_generic auxiliary": { "speed": 0.0, "rpm": null },
      "filament_switch_sensor runout": { "filament_detected": false, "enabled": true },
      "filament_motion_sensor runout_sensor": { "filament_detected": false, "enabled": true }
    }
  }
}
```

A JSON-RPC `printer.objects.subscribe` request with ID `3434` and
`params.objects` mapping each of those eight names to `null` returned the same
status field/value snapshot, with `result.eventtime: 173.328411139`.
Each of these subsequent HTTP `/printer/gcode/script` commands returned
`{"result":"ok"}`:

```gcode
SET_FAN_SPEED FAN=auxiliary SPEED=0.5
SET_TEMPERATURE_FAN_TARGET TEMPERATURE_FAN=chamber_heater TARGET=40
SET_HEATER_TEMPERATURE HEATER=chamber TARGET=50
SET_FILAMENT_SENSOR SENSOR=runout ENABLE=0
SET_FILAMENT_SENSOR SENSOR=runout_sensor ENABLE=0
```

The subscribed WebSocket then delivered this actual change-only notification:

```json
{
  "jsonrpc": "2.0",
  "method": "notify_status_update",
  "params": [
    {
      "temperature_fan chamber_heater": { "target": 40.0 },
      "heater_generic chamber": { "target": 50.0 },
      "fan_generic auxiliary": { "speed": 0.5 },
      "filament_switch_sensor runout": { "enabled": false },
      "filament_motion_sensor runout_sensor": {
        "filament_detected": true, "enabled": false
      }
    },
    173.578527187
  ]
}
```

A temporary native-emulator comparison matched **all eight exact field sets and
JSON value kinds**, plus all five requested target/speed/enabled results. Only
three configured instance names were mapped explicitly:
`heater_fan heater_fan` to `heater_fan hotend`,
`controller_fan controller_fan` to `controller_fan electronics`, and
`filament_motion_sensor runout_sensor` to `filament_motion_sensor encoder`.
No field names, types, units or captured values were rewritten.

This establishes a bounded wire-contract comparison, not full firmware
conformance. Zero temperatures and the `99999999` minimum are the live
simulator's startup readings/sentinel, not physical ambient measurements.
The motion sensor's concurrent detection change is not evidence that disabling
a sensor causes insertion; pulse timing is not modeled by the emulator.
Live monotonic event times and readings are deliberately not equated with the
emulator's deterministic clock and chosen fixture readings.
Turn off temporary heater/fan targets, then remove only your reference container:

```powershell
wslc stop --time 15 pf3434-reference
wslc rm pf3434-reference
```

The capture's reference container and native comparison process were stopped,
and the reference container was removed. No reference service remains running.

#### Deterministic emulator examples

The following chosen readings are **source-derived emulator fixtures**, not the
live capture above. `KlipperSensorTests` exercises these shapes and notifications
against the emulator.

Representative emulator object-query result:

```json
{
  "result": {
    "eventtime": 1704067200,
    "status": {
      "temperature_sensor endstop": {
        "temperature": 23.4,
        "measured_min_temp": 23.4,
        "measured_max_temp": 23.4
      },
      "temperature_fan chamber_heater": {
        "temperature": 24, "target": 0, "speed": 0, "rpm": 0
      },
      "heater_generic chamber": { "temperature": 24, "target": 0, "power": 0 },
      "heater_fan hotend": { "speed": 0, "rpm": null },
      "controller_fan electronics": { "speed": 0, "rpm": null },
      "fan_generic auxiliary": { "speed": 0, "rpm": null },
      "filament_switch_sensor runout": { "enabled": true, "filament_detected": true },
      "filament_motion_sensor encoder": { "enabled": true, "filament_detected": true }
    }
  }
}
```

After advancing virtual time by five seconds and injecting the temperature-fan
readings above, a subscriber to that object receives:

```json
{
  "jsonrpc": "2.0",
  "method": "notify_status_update",
  "params": [
    { "temperature_fan chamber_heater": { "temperature": 35, "speed": 0.7, "rpm": 2100 } },
    1704067205
  ]
}
```

This coverage does **not** establish vendor-fork object support. Humidity,
pressure and gas are not fabricated on the stock thermistor
`temperature_sensor` object; environmental sensor drivers and vendor extensions
need their own evidence/fixtures. Existing MMU/vendor fixtures are separate and
are not validated by this reference.

Focused native validation (from `src`, no Docker required):

```powershell
dotnet build .\tests\Farm.Moonraker.Emulator.Tests\Farm.Moonraker.Emulator.Tests.csproj -c Debug
dotnet test .\tests\Farm.Moonraker.Emulator.Tests\Farm.Moonraker.Emulator.Tests.csproj -c Debug --no-build --settings .\vstest.runsettings --blame-hang --blame-hang-timeout 2m --blame-hang-dump-type mini
```

The focused project covers all eight initial/query/notification shapes,
field filters, change-only deltas, invalid/atomic mutations, reset/isolation,
virtual time and existing fault rules. Its real subscription-service integration
also proves a sensor-only temperature delta reaches both cache and lowercase
`printerupdated` broadcasts with Celsius units and bounded writable descriptors.

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
| `POST /__emulator/printer/sensor` | Inject validated standard Klipper readings and broadcast changed subscribed fields |
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

The [daily UI runner](DAILY_UI_VALIDATION_RUNNER.md) automates this setup with
run-specific admin credentials, ports and external-target Playwright configuration.
It gates both browser phases on healthy, authenticated emulator fixtures and
preserves immutable-image provenance without starting a local Vite server.

After the daily validation stack is healthy, run the strict Moonraker printer suite
from `src/Web/ReactApp`:

```bash
API_BASE_URL=http://127.0.0.1:5245 \
BASE_URL=http://127.0.0.1:3000 \
npm run test:e2e:moonraker -- --project=chromium
```

The suite defaults the four control URLs to loopback ports `17125` through `17128`.
Override any instance independently with `MOONRAKER_EMULATOR_URL_READY`,
`MOONRAKER_EMULATOR_URL_PRINTING`, `MOONRAKER_EMULATOR_URL_PAUSED`, or
`MOONRAKER_EMULATOR_URL_SHUTDOWN`.

### Authenticating against an already-provisioned admin (issue #1586)

The `emulatorReady` Playwright fixture (`e2e/fixtures/emulator-setup.ts`) authenticates
as an admin before every test. On a pristine database (`/api/setup/status` reports
`needsSetup: true`) it self-provisions its own hardcoded `e2e-admin` account exactly as
before — no configuration is needed for a fresh local run.

Daily immutable-image validation, however, first runs
`scripts/ci/smoke-daily-validation-stack.sh`, which completes initial setup by creating
its own validation administrator. By the time Playwright runs, `needsSetup` is already
`false`, and the fixture cannot create its hardcoded account. Set both
`E2E_ADMIN_USERNAME` and `E2E_ADMIN_PASSWORD` (and optionally `E2E_ADMIN_EMAIL`) to the
credentials of that already-provisioned admin so the fixture authenticates as it
instead:

```bash
API_BASE_URL=http://127.0.0.1:5245 \
BASE_URL=http://127.0.0.1:3000 \
E2E_ADMIN_USERNAME=daily-smoke-admin \
E2E_ADMIN_PASSWORD="$smoke_admin_password" \
npm run test:e2e:moonraker -- --project=chromium
```

Both env vars must be set together — the fixture falls back to its hardcoded defaults
(warning, but never logging the values) if only one is present. Neither value is ever
printed to logs.

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
