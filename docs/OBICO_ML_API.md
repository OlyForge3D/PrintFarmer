# Obico ML API Integration

AI-powered print failure detection for PrintFarmer using the open-source Obico ML API.

## Overview

The Obico ML API analyzes 3D printer camera images and detects print failures including:
- Spaghetti (failed prints with strands everywhere)
- First layer adhesion failures
- Layer shifts
- Support structure failures
- Other print anomalies

**Key Features:**
- Local inference (no cloud dependencies)
- CPU-only default (GPU optional)
- Configurable confidence thresholds
- REST API for easy integration

## Quick Start

### Enable Obico ML API

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
# Image and version
OBICO_ML_IMAGE=thespaghettidetective/ml_api:base-1.4

# Resource limits
OBICO_ML_CPU_LIMIT=2.0
OBICO_ML_MEMORY_LIMIT=2gb
OBICO_ML_CPU_REQUEST=0.5
OBICO_ML_MEMORY_REQUEST=512m

# Debug mode
OBICO_ML_DEBUG=False
```

**PrintFarmer API (for Lambert's integration):**
```bash
# ML API connection
OBICO_ML_API_URL=http://obico-ml-api:3333

# Detection settings
OBICO_ML_CONFIDENCE_THRESHOLD=0.7  # 0.0-1.0
OBICO_ML_SCAN_INTERVAL=30          # seconds
```

## API Usage

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
│ (Feature #1 Logic)  │
└──────────┬──────────┘
           │ HTTP POST /v1/detect
           ↓
┌─────────────────────┐
│ Obico ML API        │◄─── Model Cache Volume
│ (Flask + ONNX/DNN)  │     (obico-ml-model-cache)
└─────────────────────┘
```

**Network Communication:**
- Internal Docker network only
- PrintFarmer API → `http://obico-ml-api:3333`
- No host port exposure (secure by default)

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
docker pull thespaghettidetective/ml_api:base-1.4

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

**Update to latest model:**
```bash
# Pull latest image
docker compose pull obico-ml-api

# Restart service
docker compose up -d obico-ml-api
```

**Pin to specific version:**
```bash
# In .env or compose file
OBICO_ML_IMAGE=thespaghettidetective/ml_api:base-1.4
```

## References

- **Obico GitHub:** https://github.com/TheSpaghettiDetective/obico-server
- **Docker Hub:** https://hub.docker.com/r/thespaghettidetective/ml_api
- **Obico Docs:** https://www.obico.io/docs/
- **PrintFarmer Feature #1:** (AI Print Failure Detection)

## Status

✅ **Docker infrastructure:** Complete (Parker, 2026-03-13)  
⬜ **API integration:** Pending (Lambert, Feature #1)  
⬜ **Frontend UI:** Pending (Quinn, Feature #1)  

---

**Need help?** Check `.squad/decisions/inbox/parker-obico-docker.md` for detailed decision rationale.
