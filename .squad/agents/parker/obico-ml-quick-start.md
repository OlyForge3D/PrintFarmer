# Obico ML API Quick Start for PrintFarmer

## What is it?
AI-powered print failure detection service that analyzes camera images from 3D printers.

## Usage

### 1. Enable during deployment
```bash
./scripts/docker/compose-generator.sh --include-obico-ml --db-provider postgres
docker compose up -d
```

### 2. Verify it's running
```bash
docker compose ps obico-ml-api
docker compose logs obico-ml-api
```

### 3. Test the health endpoint
```bash
curl http://localhost:3333/hc/
# Response: {"status": "ok"}
```

## For Lambert (API Integration)

**Connection URL from API service:**
```bash
OBICO_ML_API_URL=http://obico-ml-api:3333
```

**Send detection request:**
```bash
POST http://obico-ml-api:3333/v1/detect
Content-Type: multipart/form-data

image: <JPEG binary>
settings: {"confidence_threshold": 0.7}
```

**Response format:**
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

## Configuration

**Resource limits (in .env):**
```bash
OBICO_ML_CPU_LIMIT=2.0          # Max 2 CPUs
OBICO_ML_MEMORY_LIMIT=2gb        # Max 2GB RAM
OBICO_ML_CPU_REQUEST=0.5        # Reserved 0.5 CPU
OBICO_ML_MEMORY_REQUEST=512m    # Reserved 512MB RAM
```

**Detection settings (for API service):**
```bash
OBICO_ML_CONFIDENCE_THRESHOLD=0.7   # 0.0-1.0 (70% confidence)
OBICO_ML_SCAN_INTERVAL=30           # Check every 30 seconds
```

## Key Points

✅ **Internal service** — Not exposed to host network  
✅ **CPU-only** — Works without GPU (1-3s inference time)  
✅ **Model cache** — Persistent volume for ML models (~500MB)  
✅ **Optional** — PrintFarmer works fine without it  
✅ **Privacy-first** — All inference happens locally  

## Troubleshooting

**High memory usage?**
```bash
# Reduce memory limit
export OBICO_ML_MEMORY_LIMIT=1gb
docker compose up -d obico-ml-api
```

**Slow inference?**
- CPU inference is 1-3s per image (normal)
- Increase scan interval to reduce load
- Or enable GPU acceleration (see docs)

**More details:** See `docs/OBICO_ML_API.md`

---
Created by Parker, 2026-03-13
