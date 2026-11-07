# Print Failure Detection Providers

PrintFarmer can analyze printer camera frames with Obico ML API, PrintGuard, or
both. Each provider can be selected independently per printer; neither is
required when failure detection is not needed.

## Overview

The Obico ML API analyzes 3D printer camera images and detects print failures including:
- Spaghetti (failed prints with strands everywhere)
- First layer adhesion failures
- Layer shifts
- Support structure failures
- Other print anomalies

PrintFarmer supports two independently configured analysis providers. When
both are enabled and available for a printer, the monitor reports paired-frame
analysis only when both providers actually consume the same captured JPEG.
Obico's legacy URL-based snapshot fallback does not count as paired, even if
inference succeeds, because it may analyze a different frame. When only one
provider is available, it analyzes independently and the cycle is reported as
unpaired. A provider failure does not invalidate the other provider's result.

Detection is advisory only. PrintFarmer does not automatically pause, cancel,
or control printers based on these results. Operators remain responsible for
reviewing alerts and deciding whether to take action.

## Per-print Obico routing

Printer settings select **whether** Obico monitoring is wanted, not a permanent
server. On the first monitoring scan observing a starting or active print,
PrintFarmer reserves an enabled, reachable server with spare capacity. Selection
prefers the lowest fraction of occupied slots, then the lowest reservation count.
The existing `maxConcurrentAnalyses` API/configuration property now means
**simultaneous monitored prints**, not registered printers or instantaneous HTTP
requests. Warmup and operator pauses retain a slot.

The route is persisted with the print identity in `PrinterServiceState`, so API
restarts and concurrent starts do not redistribute ongoing prints or overbook the
shared database budget. Apply `AddObicoPrintRouting` for PostgreSQL, SQL Server,
and migrated SQLite installations before running the new API. Existing saved
pool entries and capacities are preserved; old permanent printer pins are cleared
and active prints are allocated under the new rules on the first scan.
Do not run old and new API versions against the same farm during cutover: old
instances do not understand print reservations.

Completion, cancellation, failure, a different print identity, or monitoring
opt-out releases the reservation on the next scan. All ended prints are released
before new prints are allocated. Starting managed jobs reserve before the image
warmup grace period expires. Externally started prints use the live printer
status and adopt the synthetic external job identity when that record appears.
Without a backend job identity, repeated same-file prints cannot be distinguished
unless an intervening non-printing state is observed.

Offline or missing telemetry is **not** proof that a print ended: its reservation
is retained until a terminal state or tracked job completion is observed.
This intentionally favors continuity over reclaiming uncertain capacity.
Monitoring details show **Reservation held** (and the selected pool server name
when known), not healthy monitoring, while activity is unknown/offline.
Unknown activity without a reservation shows **Activity unknown** instead.
An external print that never becomes observable again can hold capacity
indefinitely. Restore connectivity, physically verify the printer is idle, and
wait for the runtime to confirm the print has ended. Reconciliation releases
the reservation without disabling Obico. If it remains held, review connectivity
and any tracked job still starting; do not disable monitoring just to free
capacity. Before the next print, verify Obico remains enabled and confirm active
monitoring once the print starts.

Provider results are labeled **Last analysis**, with the recorded timestamp and
analysis mode when available; they are historical observations, not proof of
current coverage. A healthy result without a timestamp shows **No recorded
analysis observation**. Unknown provider activity shows **Current coverage
unconfirmed**, and summary badges show **Activity unknown**, never confirmed idle
or healthy monitoring.
Routing
is scan-based (startup delay and scan interval apply), not on the physical
printer-command path, and never delays a print start.

If no healthy server has capacity, monitoring reports unavailable and retries
allocation on later scans. If the selected server fails or is disabled, that
print retains its slot and reports unavailable monitoring while retrying the
**same** server; it does not silently fail over, submit to a global overflow
destination, or pause/cancel the printer. Other printers and PrintGuard continue
independently. A healthy connectivity probe is not proof of successful inference:
analysis errors remain visible in each provider's monitoring status.

The global Obico URL remains supported for installations with **no registered
pool entries**. This legacy mode has no configurable pool capacity limit.
Register a pool entry to enforce a budget, even for one server. Once a pool
exists, disabled, unreachable, or full pool entries never fall back to the
global URL; remove all pool entries to return to global-only routing. Global
prints already running when a pool is added retain their global route until
they finish. Changing that global URL makes those old routes unavailable rather
than silently substituting another destination.

Admin server cards show **active print reservations / concurrent print capacity**.
The server list/detail API includes additive `activePrintCount`; legacy
`maxConcurrentAnalyses` names and saved values remain compatible. Capacity edits
do not evict ongoing prints. Servers with active reservations cannot be deleted;
wait for completion and reconciliation. Disabling a server prevents new
reservations and visibly degrades its current monitoring without controlling
its printers.

`POST /api/failure-detection/analyze/{printerId}` also uses the active print's
reserved route and requires access to that printer. It returns HTTP 503 without
inference when the route is unavailable or no print has reserved pool capacity;
manual requests do not create permanent printer pins or bypass a full pool.
Global-only installations retain explicit manual analysis for idle printers
when Obico is enabled and the legacy destination is available. Health probes,
not arbitrary camera inference, remain the idle pool diagnostics.

### Rollback safety

Rollback requires an idle maintenance window and a fresh, verified, matched
pre-upgrade backup of the whole database, artifact blobs, configuration, key
rings, and certificates. Restore and verify that backup in isolation first.
Running EF migration `Down` alone is not a rollback: it cannot recover cleared
legacy printer pins, and the old API misinterprets per-print routing state and
can use the global destination when the new API would refuse overflow.
Never run old and new APIs concurrently against the farm.

**Before starting any old API**, check the isolated restored Obico settings
with the rollback package's `Farm.Infrastructure.dll` and its SHA-256 from an
independently trusted artifact manifest. A hash calculated from the selected
DLL itself, or a locally rebuilt old-source DLL, is not proof that it is the
deployed rollback artifact. Keep its packaged dependencies beside the DLL.
From `src`, run the preflight without starting API or printer services:

```powershell
dotnet run --project ..\tools\ObicoRollbackPreflight -- sqlite <isolated-restored-db-path> <rollback-infrastructure-dll> <trusted-sha256>
```

For PostgreSQL or SQL Server, use `postgres` or `sqlserver` instead of `sqlite`
and `-` instead of the database path. Supply
`PFARM_ROLLBACK_CONNECTION_STRING` through the local process environment, with
least-privilege read-only access to an explicitly isolated restored database;
do not put credentials on the command line or point the tool at production.

The tool requires exactly one nonempty saved Obico row and invokes the selected
old binary's settings validator. Pool-only settings with Obico enabled and an
empty legacy URL are incompatible with the old validator. Missing, duplicate,
malformed, null, incompatible, or unverifiable settings block rollback; the
tool does not repair configuration, disable monitoring, or accept configuration
fallback silently. A successful check covers **Obico settings only**, not other
settings, schema compatibility, backup matching, or permission to activate.

Restoring a backup loses subsequent database writes. Operators must reconcile
jobs and reservations with physical printer state before activation; database
rollback does not rewind a printer. If compatibility cannot be proven, keep the
old API stopped and resolve the restore with the operator rather than weakening
validation or silently changing monitoring.

## Quick Start

### Select deployment providers

During Docker Compose generation, select either or both optional services:

```bash
./scripts/docker/compose-generator.sh --include-obico-ml --include-printguard --db-provider postgres
```

Omit either `--include-*` flag to leave that service out. The interactive
generator and deploy script also support independent selection; saved deployment
configuration retains those choices for later regeneration. PrintGuard uses
the publisher's
`ghcr.io/oliverbravery/printguard@sha256:f69c6bafb5673e94be26ca55b431f1bf42a9ae19a95be9fa34198f0c27a9d43e`
image. The service is available only on the internal Docker network and does
not publish a host port. Do not expose it directly: the publisher documents
that the service has no built-in authentication of its own.

The bundled Obico image does not support ARM64 runtime targets. Deployment
rejects the bundled service when the effective Docker runtime target is ARM64;
use an AMD64 Docker runtime target (including emulation where appropriate), or
disable the bundled Obico service and configure an external Obico API. The
deploy script checks `DOCKER_DEFAULT_PLATFORM` when set, otherwise it queries
the active Docker engine, which also handles remote Docker contexts. Configuration
validation and regeneration warn if they cannot determine the target; an actual
deployment with bundled Obico fails closed if the target cannot be determined.
The deploy script's `--platform` option selects the build platform and is not
by itself the Compose runtime target.

### Configure Obico ML API

**During Docker Compose generation:**
```bash
./scripts/docker/compose-generator.sh --include-obico-ml --db-provider postgres
docker compose up -d
```

**Check service health:**
```bash
docker compose ps obico-ml-api
docker compose logs obico-ml-api
curl http://localhost:3333/hc/
```

### Environment Variables

**Docker Compose (`.env` file):**
```bash
# Optional custom image; omit to use the pinned default (AMD64 runtime only)
OBICO_ML_IMAGE=registry.example/obico-ml-api:custom

# Resource limits
OBICO_ML_CPU_LIMIT=2.0
OBICO_ML_MEMORY_LIMIT=2gb
OBICO_ML_CPU_REQUEST=0.5
OBICO_ML_MEMORY_REQUEST=512m

# Debug mode
OBICO_ML_DEBUG=False
```

The deployment script preserves a custom `OBICO_ML_IMAGE` when regenerating
`.env`; it leaves the pinned default out so future default image updates are
not frozen by generated configuration. The pinned image is AMD64-only, so
bundled Obico is rejected when the effective Docker runtime target is not
AMD64. Set `OBICO_ML_IMAGE` to an image compatible with your runtime to use a
different architecture; compatibility of custom images is the operator's
responsibility. Custom values must be single-line Docker image references;
whitespace and shell metacharacters are rejected because the generated `.env`
file is sourced by the deployment script.

**PrintFarmer API:**
```bash
# ML API connection
OBICO_ML_API_URL=http://obico-ml-api:3333

# Detection settings
OBICO_ML_CONFIDENCE_THRESHOLD=0.7  # 0.0-1.0
OBICO_ML_SCAN_INTERVAL=30          # seconds
```

## Configure PrintGuard

PrintGuard is an independently selectable provider, not a shadow scorer.
PrintFarmer calls its classification API (`POST /api/v1/classify`); its
classification results and errors remain attributed to PrintGuard and do not
replace or suppress Obico results. When both providers are enabled, each
receives the same locally captured JPEG. If only one is selected and ready, it
can analyze the frame without the other provider.

Configure PrintGuard through the API process environment or another ASP.NET
configuration provider. The URL is the service root; PrintFarmer appends
`/api/v1/classify`.

The Docker deployment generator preserves these provider settings across
configuration regeneration. It supplies the bundled service URL only when the
corresponding bundled sidecar is selected; otherwise, configure an external
provider URL explicitly. Disabled providers do not require a URL.
When enabling a bundled provider after a disabled deployment, an empty saved
URL is replaced with its internal service URL. Nonempty saved URLs and explicit
process-environment URL overrides (including an explicit empty override) are
preserved; malformed nonempty configuration remains subject to application
validation rather than being silently replaced.

```bash
PFARM__PrintGuardShadow__Enabled=true
PFARM__PrintGuardShadow__ApiUrl=http://printguard:8000
PFARM__PrintGuardShadow__ApiToken=pg_your_read_scope_token
PFARM__PrintGuardShadow__RequestTimeoutSeconds=5
PFARM__PrintGuardShadow__CaptureTimeoutSeconds=5
```

`Enabled` defaults to `false`. `ApiToken` is optional only when the PrintGuard
instance permits unauthenticated read requests. When tokens are configured,
use a read-scoped token; never grant PrintFarmer PrintGuard's `control` scope.
Keep tokens in deployment configuration, not ordinary PrintFarmer settings.
Use HTTPS if sending them outside a trusted local network. Enabling this
provider sends printer camera frames to the configured endpoint; verify that
transfer is acceptable for the farm before enabling it.

PrintGuard's health endpoint is `GET /api/health`; it confirms service
readiness, not classification quality. A failed, timed-out, or unavailable
provider produces its own failure outcome while the other selected provider
continues independently. If local frame capture fails, image analysis is
unavailable for that cycle.

The publisher's repository identifies PrintGuard as GPL-2.0 (GPL version 2);
do not infer the `GPL-2.0-only` SPDX variant from that alone. The add-on uses
the publisher-provided container image and documented HTTP API, rather than
vendoring code or weights or building or repackaging the service. This
architectural boundary is not a categorical legal conclusion that the programs
are separate works. Confirm distribution plans with qualified counsel. The
published benchmark is author-reported and has not been independently
replicated.

## API Usage

### PrintFarmer system health

Obico administration and system health share an egress-validated prediction-route
probe. For the bundled upstream API, the expected HTTP 400 response must contain
an empty `detections` array and a `Failed to get image` message identifying the
exact synthetic probe URL. That response proves route compatibility, not image
fetching or classification. Unrelated HTTP 400 responses, authorization failures,
server errors, and unreachable endpoints still fail validation. Legacy servers
are checked separately through their supported upload contract.

PrintFarmer's `/health` also includes a `printguard` check. When the global
PrintGuard integration is disabled, it does not contact the provider and its
Admin Control Center and detailed System Health cards are hidden. When enabled,
invalid URLs, timeouts, or token formatting report Degraded with a configuration
diagnostic. The check validates the publisher's `GET /api/health` contract
(`ok: true` and a nonempty version), then calls `GET /api/v1/monitors` with the
configured bearer token to verify read-scope access. The health endpoint alone
is authentication-exempt and cannot prove the application may use the read API.
Neither request submits images or invokes printer controls.

Both requests share a total budget of at most six seconds (or the configured
request timeout, if shorter). Response bodies are limited to 64 KB. Concurrent
requests share a probe and completed results are cached for 45 seconds, including
failures; changing configuration or disabling takes effect on the next request.
Safe diagnostics distinguish connection, timeout, HTTP, authorization, and
response-contract failures without exposing endpoint URLs, tokens, upstream
bodies, or exceptions. Healthy means hub readiness and read-API access, **not**
classification/model accuracy or proof that a printer has usable camera frames.

PrintFarmer's `/health` includes an `obico` check. When failure detection is
disabled it reports Healthy without contacting Obico, and the Admin Control
Center hides its tile. When enabled it actively probes all enabled pooled
servers, even on an idle farm. The global endpoint is probed only in global-only
mode or while a pre-existing global print is finishing after a pool was added.
A disabled pool is unavailable, not permission to bypass its capacity budget.

The check shares the administrative server connectivity probe: guarded,
address-pinned `GET /p/` with the existing legacy `POST /p/` compatibility
fallback. Identical endpoint/credential combinations are probed once; different
credentials at the same endpoint remain separate so a broken route cannot be
masked by a working one. All targets reachable means Healthy; partial or total
outage, or no usable targets, means Degraded. Degraded remains HTTP 200 on
`/health`; `/healthz` remains independent liveness.

Probes run at most four at a time within a six-second batch budget. Concurrent
health requests share the same batch, and completed results are cached for
45 seconds, including failures. Caller cancellation does not cancel that batch.
Endpoint/credential changes may take one cache interval to appear; disabling
takes effect on the next health request. Anonymous health output exposes only
generic descriptions, the enabled flag and target/reachable counts, never
endpoint URLs, server names, credentials or upstream errors.

Healthy means backend reachability, **not** ML accuracy, usable camera snapshots,
or proof that every printer's monitoring loop is protecting an active print.

### Health Check
```bash
GET http://obico-ml-api:3333/hc/
```

**Response:**
```json
{"status": "ok"}
```

### Detect Print Failures

**Request:**
```bash
POST http://obico-ml-api:3333/v1/detect
Content-Type: multipart/form-data

image: <binary JPEG data>
settings: {"confidence_threshold": 0.7}
```

**Response:**
```json
{
  "detections": [
    {
      "confidence": 0.85,
      "label": "spaghetti",
      "bbox": [100, 150, 400, 500]
    }
  ],
  "is_failure": true,
  "inference_time_ms": 1250
}
```

## Architecture

```
┌─────────────────────┐
│ Printer Camera      │
│ (Moonraker/Prusa)   │
└──────────┬──────────┘
           │ JPEG snapshot
           ↓
┌─────────────────────┐
│ PrintFarmer API     │
│ Failure Detection  │
└──────────┬──────────┘
           ├── HTTP ──► Obico ML API
           └── HTTP ──► PrintGuard
```

**Network Communication:**
- Optional services are selected independently and communicate over the
  internal Docker network
- PrintFarmer API → `http://obico-ml-api:3333`
- PrintFarmer API → `http://printguard:8000`
- Neither service publishes a host port by default

## Resource Requirements

**Default Configuration:**
- **Memory:** 512MB-2GB (configurable)
- **CPU:** 0.5-2 cores (configurable)
- **Disk:** ~500MB (includes ML models)
- **Inference time:** 1-3 seconds per image (CPU)

**GPU Acceleration (Optional):**
Uncomment GPU configuration in `docker-compose.obico-ml.yml`:
```yaml
deploy:
  resources:
    reservations:
      devices:
        - driver: nvidia
          count: 1
          capabilities: [gpu]
```

**Requires:**
- NVIDIA GPU
- NVIDIA Container Toolkit
- Docker with `--gpus` support

## Troubleshooting

### Service won't start
```bash
# Check logs
docker compose logs obico-ml-api

# Verify image pull
docker pull ghcr.io/thespaghettidetective/obico-ml-api-cpu@sha256:e1b1c5b4cfc517ed884cad24e4a709d31b935ea49dab45ff60d31a62f4cb86ee

# Check disk space (models are large)
df -h
```

### High memory usage
```bash
# Check current memory
docker stats obico-ml-api

# Reduce memory limit
export OBICO_ML_MEMORY_LIMIT=1gb
docker compose up -d obico-ml-api
```

### Slow inference
```bash
# CPU-bound inference is normal (1-3s per image)
# Solutions:
# 1. Enable GPU acceleration (see above)
# 2. Increase scan interval (reduce API calls)
# 3. Add more CPU resources
export OBICO_ML_CPU_LIMIT=4.0
docker compose up -d obico-ml-api
```

### Model download failures
```bash
# Models download on first start
# Check logs for download progress
docker compose logs obico-ml-api | grep -i model

# Manual model cache clear (re-downloads on restart)
docker volume rm printfarmer_obico-ml-model-cache
docker compose up -d obico-ml-api
```

## Security Considerations

Snapshot downloads performed by PrintFarmer's legacy multipart fallback use
the shared egress guard. Destinations are checked after DNS resolution and
connections are pinned to vetted addresses. For HTTPS, the original Host
header preserves SNI and certificate hostname validation ([.NET transport
contract](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-sni)).
Redirects are not followed.
Loopback, link-local (including metadata services), and other forbidden
destinations are denied by default; administrator-configured egress exceptions
still apply. Private LAN addresses remain permitted for printer cameras.
This is the farm's existing safe-fetch policy, not a public-internet-only
allowlist. A blocked snapshot fails detection rather than uploading image data.

✅ **Enabled by default:**
- Internal network only (no host exposure)
- Minimal Linux capabilities (`cap_drop: ALL`)
- tmpfs for temporary files
- Read-only container filesystem (where possible)

⚠️ **Optional authentication:**
```yaml
environment:
  - ML_API_TOKEN=your-secret-token
```

Then include token in API requests:
```bash
curl -H "Authorization: Bearer your-secret-token" \
     -X POST http://obico-ml-api:3333/v1/detect ...
```

## Upgrading

**Refresh the pinned image:**
```bash
# Pull the configured digest
docker compose pull obico-ml-api

# Restart service
docker compose up -d obico-ml-api
```

**Pin to the verified publisher image:**
```bash
# In .env or compose file
OBICO_ML_IMAGE=ghcr.io/thespaghettidetective/obico-ml-api-cpu@sha256:e1b1c5b4cfc517ed884cad24e4a709d31b935ea49dab45ff60d31a62f4cb86ee
```

## References

- **Obico GitHub:** https://github.com/TheSpaghettiDetective/obico-server
- **Docker Hub:** https://hub.docker.com/r/thespaghettidetective/ml_api
- **Obico Docs:** https://www.obico.io/docs/
- **PrintFarmer Feature #1:** (AI Print Failure Detection)

## Status

✅ **Docker infrastructure:** Obico ML API and optional PrintGuard add-on
✅ **API integration:** Independent provider analysis and failure reporting
✅ **Frontend UI:** Provider selection and failure-detection status

---

**Need help?** Check `.squad/decisions/inbox/parker-obico-docker.md` for detailed decision rationale.
