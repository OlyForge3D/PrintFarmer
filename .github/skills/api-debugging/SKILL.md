```skill
---
name: api-debugging
description: Debug PrintFarmer API errors by authenticating against endpoints, querying Docker containers, and inspecting logs. Use when investigating 500 errors, auth failures, or API endpoint issues in both local dev and Docker deployments.
---

# PrintFarmer API Debugging Skill

Use this skill when debugging API errors (500s, 401s, connection failures) in either local development or Docker deployment.

## Architecture Overview

PrintFarmer runs two API processes in microservices mode:

| Service | Port | Routes |
|---|---|---|
| **Main API** (`printfarmer-api`) | 5245 | Most `/api/*` endpoints |
| **Slicer Host** (`printfarmer-slicer-host`) | 5246 | `/api/workers`, `/api/slicers`, `/api/slicer`, `/api/slice`, `/api/3d-models`, `/api/artifacts` |
| **Nginx Proxy** (`printfarmer-nginx-proxy`) | 80 | Routes to the above based on path |

When the user reports a 500 at `http://localhost/...`, that's through nginx. Determine which backend handles the route, then check that container's logs.

## Step 1: Identify Which Backend Handles the Route

Slicer routes (forwarded to slicer-host:5246):
- `/api/workers/`
- `/api/slicers/`
- `/api/slicer/`
- `/api/slice/`
- `/api/3d-models/`
- `/api/artifacts/`
- `/api/admin/slicer/`
- `/hubs/slicer`

Everything else goes to the main API on port 5245.

## Step 2: Check Container Logs

```bash
# Main API logs
docker logs printfarmer-api --tail 50 2>&1

# Slicer host logs
docker logs printfarmer-slicer-host --tail 50 2>&1

# Filter for errors
docker logs printfarmer-api --tail 100 2>&1 | grep -iE "error|exception|fail" | tail -20

# Trigger the error then capture logs
curl -s http://localhost/api/workers/ && sleep 1 && docker logs printfarmer-slicer-host --tail 30 2>&1
```

## Step 3: Hit the Endpoint Directly (Bypassing Nginx)

Test from inside the container to rule out nginx issues:

```bash
# Main API (port 5245)
docker exec printfarmer-api curl -s -w "\nHTTP_CODE: %{http_code}\n" http://localhost:5245/api/printers 2>&1

# Slicer host (port 5246)
docker exec printfarmer-slicer-host curl -s -w "\nHTTP_CODE: %{http_code}\n" http://localhost:5246/api/workers/ 2>&1
```

## Step 4: Authenticate (When Endpoints Require Auth)

### Obtaining Credentials

Before generating a token, determine which credentials to use:

1. **Check `.deploy-config`** — The deploy script generates this file in the **repo root** during deployment. It contains all deployment settings including admin credentials:

   ```bash
   # Read admin credentials from .deploy-config
   grep -E "^AUTO_ADMIN" .deploy-config
   ```

   The relevant lines look like:
   ```
   AUTO_ADMIN=true
   AUTO_ADMIN_USERNAME=admin
   AUTO_ADMIN_PASSWORD=<password>
   AUTO_ADMIN_EMAIL=admin@example.com
   ```

   Extract the username and password from `AUTO_ADMIN_USERNAME` and `AUTO_ADMIN_PASSWORD`.

2. **If `.deploy-config` doesn't exist** — Ask the user for the username and password to use for authentication.

### Main API — JWT Bearer Token

The main API uses JWT authentication. To get a token:

```bash
# 1. Check if setup is needed (no admin user yet)
curl -s http://localhost:5245/api/setup/status
# Returns: {"needsSetup": true} or {"needsSetup": false}

# 2a. If needsSetup is true — create initial admin (use credentials from config or user)
curl -s -X POST http://localhost:5245/api/setup/initial-admin \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","email":"admin@local.dev","password":"TestPass123!"}'

# 2b. If admin already exists — login
curl -s -X POST http://localhost:5245/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"usernameOrEmail":"admin","password":"TestPass123!"}'

# Response contains: {"success":true,"token":"eyJ...","expiresAt":"...","user":{...}}

# 3. Use the token
TOKEN="eyJ..."  # paste token from login response
curl -s -H "Authorization: Bearer $TOKEN" http://localhost:5245/api/printers

# One-liner to login and extract token (requires jq):
TOKEN=$(curl -s -X POST http://localhost:5245/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"usernameOrEmail":"admin","password":"TestPass123!"}' | jq -r '.token')
```

**JWT configuration**: Key is in `Jwt__Key` env var (32+ chars), issuer/audience default to "PrintFarmer".

### Slicer Host — No Auth Required (Standalone Mode)

The slicer-host uses `StandaloneAuthHandler` which allows all requests. No token needed when hitting it directly on port 5246. Auth failures at `/api/workers/` through nginx are NOT auth issues — they're application errors.

### Endpoints That Don't Require Auth

These use `[AllowAnonymous]` and work without a token:
- `GET /healthz` and `GET /health`
- `GET /api/setup/status`
- `POST /api/setup/initial-admin` (only when no admin exists)
- `POST /api/auth/login` and `POST /api/auth/register`
- `GET /api/catalog/*` (most catalog read endpoints)
- `GET /api/tags/*` (most tag read endpoints)
- `GET /api/locations/*` (read endpoints)
- `GET /api/settings/public`
- `GET /api/filament-types/*` (read endpoints)

## Step 5: Check the Database

```bash
# List tables in main schema
docker exec printfarmer-database-postgres psql -U postgres -d printfarmer -c "\dt"

# List tables in slicer schema
docker exec printfarmer-database-postgres psql -U postgres -d printfarmer -c "\dt slicer.*"

# Query a specific table
docker exec printfarmer-database-postgres psql -U postgres -d printfarmer \
  -c 'SELECT "Id", "ServiceId", "Status" FROM slicer."Workers";'

# Check database credentials
docker exec printfarmer-database-postgres env | grep POSTGRES
```

## Step 6: Check Environment and DI Issues

```bash
# Check container environment
docker exec printfarmer-api env | grep -iE "DB_|JWT|DEPLOYMENT|SLICER" 2>&1
docker exec printfarmer-slicer-host env | grep -iE "DB_|CONNECTION|SLICER" 2>&1

# Check if slicer module is loaded (monolithic mode)
curl -s http://localhost:5245/api/workers/
# "SLICER_DISABLED" = module not loaded (expected in microservices mode)
```

## Common Error Patterns

| Symptom | Likely Cause | Fix |
|---|---|---|
| 500 from nginx, 404 "SLICER_DISABLED" from API | Route should go to slicer-host, not main API | Check nginx config |
| 500 with `DbUpdateException` | Column constraint violation (e.g., varchar overflow) | Check entity configuration max lengths |
| 500 with `JsonException` | Deserialization mismatch (e.g., object vs array) | Check the JSON shape in the database |
| 500 with `No service for type 'X'` | Missing DI registration in slicer-host | Add to `SharedInfrastructureRegistrations.cs` |
| 401 Unauthorized | Missing or expired JWT token | Login to get fresh token |
| Empty response (HTTP 000) | Wrong port inside container | Check `ASPNETCORE_URLS` env var |

## Nginx Config Location

```bash
docker exec printfarmer-nginx-proxy cat /etc/nginx/nginx.conf
```

The nginx config routes slicer paths to `slicer-host:5246` and everything else to `api:5245`.
```
