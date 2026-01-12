# Nginx Health Endpoint Fix

## Problem Summary

Health check endpoints were returning plain text "OK" instead of proper JSON responses when accessed through Nginx reverse proxy.

### Symptoms
```bash
# Expected (direct API access):
curl http://10.0.0.75:5245/healthz
{"status":"ok"}

# Actual (through Nginx):
curl http://10.0.0.75:8080/healthz
OK
```

## Root Causes

1. **Missing `/health` endpoint** - Not configured in Nginx at all
2. **Content transformation** - Nginx was transforming JSON to plain text for `/healthz`
3. **Missing proxy headers** - No `Accept: application/json` header sent to backend

## Solution

### 1. Added `/health` Endpoint Configuration

Both HTTP and HTTPS server blocks now include:

```nginx
location = /health {
  proxy_pass http://api:5245/health;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header Accept application/json;
}
```

### 2. Fixed `/healthz` Endpoint

Updated to include proper headers:

```nginx
location = /healthz {
  proxy_pass http://api:5245/healthz;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header Accept application/json;
}
```

### 3. Key Changes

**File**: `deploy/nginx/nginx.conf`

- Added `proxy_http_version 1.1` for both endpoints
- Added `proxy_set_header Host $host` 
- Added `proxy_set_header Accept application/json` (critical!)
- Applied to both HTTP (port 8080) and HTTPS (port 8443) server blocks

## Why This Matters

### Health Check Integration
The deploy script (`scripts/deploy-docker.sh`) validates deployment by:
1. Checking `/healthz` for basic availability (`{"status":"ok"}`)
2. Checking `/health` for detailed status (`{"status":"Healthy","results":{...}}`)

Without proper JSON responses, health checks fail even when services are running correctly.

### Monitoring & Observability
External monitoring tools (Prometheus, Grafana, etc.) expect structured JSON responses with:
- Status codes
- Detailed health metrics
- Dependency status

Plain text "OK" provides no actionable information.

## Testing

### Before Fix
```bash
curl -i http://10.0.0.75:8080/healthz
HTTP/1.1 200 OK
Content-Type: text/plain
Content-Length: 3

OK
```

### After Fix
```bash
curl -i http://10.0.0.75:8080/healthz
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"status":"ok"}

curl http://10.0.0.75:8080/health | jq
{
  "status": "Healthy",
  "results": {
    "comprehensive": {
      "status": "Healthy",
      "description": "All systems operational"
    },
    "signalr": {
      "status": "Healthy",
      "description": "SignalR fully operational"
    }
  }
}
```

## Deployment

### To Apply This Fix

1. **Pull latest changes**:
   ```bash
   git pull origin dev/jpapiez/logging-db-consolidation
   ```

2. **Rebuild frontend container** (contains Nginx config):
   ```bash
   # Monolith:
   docker compose build web
   docker compose up -d web
   
   # Microservices:
   docker compose -f docker-compose.microservices.yml build frontend
   docker compose -f docker-compose.microservices.yml up -d frontend
   ```

3. **Verify fix**:
   ```bash
   curl http://localhost:8080/healthz
   # Should return: {"status":"ok"}
   
   curl http://localhost:8080/health | jq
   # Should return JSON with status and results
   ```

## Related Files

- `deploy/nginx/nginx.conf` - Main Nginx configuration (fixed)
- `scripts/deploy-docker.sh` - Deployment script with health checks
- `src/api/Program.cs` - API health endpoint definitions

## Lessons Learned

1. **Always preserve content types** - Use `Accept` headers when proxying APIs
2. **Test through the proxy** - Don't just test direct API access
3. **Document proxy configuration** - Critical for troubleshooting
4. **Include all endpoints** - Missing `/health` caused silent failures

## Future Improvements

Consider adding:
- Health endpoint unit tests
- Automated health check verification in CI/CD
- Nginx config validation in deployment script
- Monitoring alerts for health endpoint failures
