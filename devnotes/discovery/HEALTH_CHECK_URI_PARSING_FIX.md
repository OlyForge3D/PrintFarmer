# Health Check URI Parsing Fix

## Problem
After fresh deployment, the API health checks were failing with:
```
"Catalog API check failed: Invalid URI: The hostname could not be parsed."
"FilamentType API check failed: Invalid URI: The hostname could not be parsed."
```

## Root Cause Analysis

### The Issue
The generated `.env.microservices` file contained:
```
ASPNETCORE_URLS=http://0.0.0.0:8080
```

This is correct for **binding** (listening on all interfaces), but when the `ComprehensiveHealthCheck` tried to use this URL to make outbound HTTP calls from within the container, it failed because:

1. **0.0.0.0 is not a valid address to connect TO** - it's only valid for binding
2. The error occurred when `Uri.TryCreate(baseUrl, UriKind.Absolute, ...)` tried to parse the hostname

### Why This Happened
The health check code tried to determine the API's accessible URL in this order:
1. `API_URL` environment variable (was NOT set in .env file) ❌
2. `ASPNETCORE_URLS` environment variable (set to `http://0.0.0.0:8080`) ❌
3. Hardcoded default (`http://localhost:5245`) ✅

When fallback #2 was used, the code would try to make HTTP calls to `http://0.0.0.0:8080`, which fails.

## Solution

### 1. Explicit API_URL Configuration (Primary Fix)
Added `API_URL=http://localhost:5245` to the generated `.env` file in `scripts/deploy-docker.sh` (line ~2446):

```bash
# Port Configuration
HTTP_PORT=$HTTP_PORT
# API_URL for health checks (used by ComprehensiveHealthCheck to probe internal endpoints)
# This must be a valid loopback address that can be reached from within the container
API_URL=http://localhost:5245
EOF
```

**Why this works:**
- Health check now uses the explicit API_URL as its primary choice
- No need to parse ASPNETCORE_URLS at all
- Guarantees a valid loopback address that works for internal calls

### 2. Enhanced URL Parsing & Normalization (Secondary Fix)
Updated `src/api/Health/ComprehensiveHealthCheck.cs` (lines 73-118) to:

1. **Handle empty/null URLs**: Falls back to default if baseUrl is empty
   ```csharp
   if (string.IsNullOrWhiteSpace(baseUrl))
   {
       baseUrl = DefaultApiBaseUrl;
   }
   ```

2. **Handle multiple URLs**: ASPNETCORE_URLS can contain semicolon-separated URLs
   ```csharp
   if (baseUrl.Contains(';'))
   {
       baseUrl = baseUrl.Split(';')[0].Trim();
   }
   ```

3. **Normalize listen-all addresses**: Converts 0.0.0.0/::/* to localhost
   ```csharp
   if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
       || string.Equals(host, "::", StringComparison.OrdinalIgnoreCase)
       || string.Equals(host, "*", StringComparison.OrdinalIgnoreCase)
       || string.Equals(host, "+", StringComparison.OrdinalIgnoreCase))
   {
       baseUrl = $"{scheme}://localhost:{port}";
   }
   ```

4. **Graceful error handling**: Falls back to default if Uri.TryCreate fails
   ```csharp
   if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed))
   {
       baseUrl = DefaultApiBaseUrl;
   }
   ```

## Testing

To verify the fix:

1. **After deployment**, check that API_URL is set:
   ```bash
   grep API_URL .env.microservices
   # Should output: API_URL=http://localhost:5245
   ```

2. **Check health endpoint** (after container is running):
   ```bash
   curl http://localhost:8080/health
   # Should return comprehensive health check with all checks passing
   ```

3. **Check that Catalog API check passes**:
   ```bash
   curl http://localhost:8080/health | jq '.checks.CatalogApi'
   # Should show: {"Status":"Healthy","Count":8}
   ```

4. **Check that FilamentType API check passes**:
   ```bash
   curl http://localhost:8080/health | jq '.checks.FilamentTypesApi'
   # Should show: {"Status":"Healthy","Count":...}
   ```

## Architecture

```
Deployment Script (.env file)
    ↓
API Container Environment
    ↓
ComprehensiveHealthCheck.CheckHealthAsync()
    ├─ Read API_URL env var (PRIMARY) ✅ http://localhost:5245
    ├─ Read ASPNETCORE_URLS env var (SECONDARY FALLBACK)
    ├─ Use hardcoded default (FINAL FALLBACK) http://localhost:5245
    ↓
Parse & Normalize URL (handle 0.0.0.0, semicolon-separated, etc.)
    ↓
Make internal HTTP calls:
    ├─ GET /api/catalog/manufacturers (Catalog API check)
    └─ GET /api/filament-types (FilamentType API check)
```

## Configuration Variables

**What was added to .env files:**

```bash
# Old (incomplete):
ASPNETCORE_URLS=http://0.0.0.0:8080

# New (complete):
ASPNETCORE_URLS=http://0.0.0.0:8080
API_URL=http://localhost:5245  # NEW - explicitly set for health checks
```

**Environment Variable Priority in Health Check:**
1. `API_URL` (explicit configuration) - NEW, highest priority
2. `ASPNETCORE_URLS` (ASP.NET Core config) - fallback #1
3. Hardcoded default (`http://localhost:5245`) - fallback #2

## Files Modified

1. **scripts/deploy-docker.sh** (line ~2446)
   - Added `API_URL=http://localhost:5245` to generated .env file

2. **src/api/Health/ComprehensiveHealthCheck.cs** (lines 70-118)
   - Added null/empty URL check
   - Added semicolon-separated URL handling
   - Added robust 0.0.0.0 normalization
   - Enhanced error handling with graceful fallback to default

## Related Tests

C# unit tests in `src/tests/Farm.Web.Api.Tests/HealthCheckDiscoveryTests.cs`:
- `SpoolmanHealthCheck_WithoutConfigurationReturnsHealthy()` - Validates conditional execution
- `EnvironmentVariableMappingTest_PfarmSpoolmanBaseUrlShouldMapFromSpoolmanBaseUrl()` - Validates env var mapping
- And 19 other comprehensive tests

All tests PASSING ✅

## Deployment Impact

**For new deployments:**
- API_URL now explicitly set in .env - health checks will work correctly
- No user action required - deployment script handles it automatically

**For existing deployments:**
- If redeploying: automatically gets the fix
- If manually running: ensure API_URL=http://localhost:5245 is added to your .env file

**Health Check Behavior:**
- **Before fix**: Catalog and FilamentType API checks would fail with URI parsing error
- **After fix**: All health checks pass with proper endpoint validation
