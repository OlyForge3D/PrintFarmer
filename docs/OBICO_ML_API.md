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
both are enabled and available for a printer, the monitor sends the same
captured JPEG to each and reports the cycle as paired-frame analysis. When only
one provider is available, it analyzes that frame independently and the cycle
is reported as unpaired. A provider failure does not invalidate the other
provider's result.

Detection is advisory only. PrintFarmer does not automatically pause, cancel,
or control printers based on these results. Operators remain responsible for
reviewing alerts and deciding whether to take action.

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
# Pinned publisher image
OBICO_ML_IMAGE=ghcr.io/thespaghettidetective/obico-ml-api-cpu@sha256:e1b1c5b4cfc517ed884cad24e4a709d31b935ea49dab45ff60d31a62f4cb86ee

# Resource limits
OBICO_ML_CPU_LIMIT=2.0
OBICO_ML_MEMORY_LIMIT=2gb
OBICO_ML_CPU_REQUEST=0.5
OBICO_ML_MEMORY_REQUEST=512m

# Debug mode
OBICO_ML_DEBUG=False
```

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

PrintFarmer's `/health` includes an `obico` check. When failure detection is
disabled it reports Healthy without contacting Obico, and the Admin Control
Center hides its tile. When enabled it actively probes the global fallback
endpoint and all enabled pooled servers, even on an idle farm. Disabled or
missing server assignments fall back to the global URL, matching monitoring.

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
