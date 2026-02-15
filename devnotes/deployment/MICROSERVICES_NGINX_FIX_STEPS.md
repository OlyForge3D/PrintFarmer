# Quick Server Rebuild Steps

The Nginx configuration fix for microservices has been committed. Here's how to apply it:

## On Your Server (10.0.0.75)

```bash
# SSH to server
ssh pi@10.0.0.75
cd /home/pi/pfarm

# Pull latest changes (includes microservices Nginx fix)
git pull origin dev/jpapiez/logging-db-consolidation

# Rebuild just the frontend container
docker compose -f docker-compose.microservices.yml build frontend --no-cache

# Restart frontend with new config
docker compose -f docker-compose.microservices.yml up -d frontend

# Verify the fix
curl -i http://localhost:8080/healthz
# Should now show:
# Content-Type: application/json; charset=utf-8
# {"status":"ok"}
```

## What Was Fixed

**File**: `deploy/nginx/nginx-microservices.conf`

**Before**:
```nginx
location /health {
    access_log off;
    return 200 "OK\n";
    add_header Content-Type text/plain;
}
```

**After**:
```nginx
location = /healthz {
    proxy_pass http://api_backend/healthz;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header Accept application/json;
}

location = /health {
    proxy_pass http://api_backend/health;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header Accept application/json;
}
```

Now both endpoints proxy to the API and return proper JSON! 🎯
