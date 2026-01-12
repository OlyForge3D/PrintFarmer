# Worker Authentication

## Overview

PrintFarmer's distributed slicing system uses a shared API key mechanism to authenticate worker processes. This prevents unauthorized access to critical job management endpoints while allowing legitimate workers to claim, process, and complete slicing jobs.

## Architecture

### Protected Endpoints

The following endpoints require worker authentication:

- `POST /api/slice/claim` - Claim the next available job from the queue
- `POST /api/slice/{id}/progress` - Report progress updates for a job
- `POST /api/slice/{id}/complete` - Mark a job as complete and upload artifacts

### Authentication Mechanism

Workers authenticate by including a shared API key in the `X-Worker-Key` HTTP header:

```http
POST /api/slice/claim HTTP/1.1
Host: api.printfarmer.example
Content-Type: application/json
X-Worker-Key: your-secret-worker-key

{
  "workerName": "orcaslicer-worker-01",
  "capabilities": ["orcaslicer"]
}
```

### Authorization Flow

1. Worker sends request with `X-Worker-Key` header
2. `WorkerAuthService` validates the header using constant-time comparison
3. If valid: request proceeds to controller logic
4. If invalid or missing: returns `401 Unauthorized`

### Testing Environment Behavior

In the `Testing` environment (integration tests), if no shared key is configured, authentication checks are bypassed. This allows tests to run without explicit key configuration while ensuring production deployments enforce security.

## Configuration

### Environment Variables

Set the worker shared API key via environment variable:

```bash
export WORKER_SHARED_API_KEY="your-secure-random-key-here"
```

**Development Example:**
```bash
export WORKER_SHARED_API_KEY="dev-worker-key-not-for-production"
```

**Production Example:**
```bash
export WORKER_SHARED_API_KEY="$(openssl rand -base64 32)"
```

### appsettings.json (Alternative)

You can also configure the key in `appsettings.json`:

```json
{
  "WorkerAuth": {
    "SharedKey": "your-secure-random-key-here"
  }
}
```

**Priority:** Environment variables take precedence over `appsettings.json` values.

### Docker Deployment

In Docker Compose or Kubernetes environments, inject the key as an environment variable:

**docker-compose.yml:**
```yaml
services:
  api:
    image: printfarmer/api:latest
    environment:
      - WORKER_SHARED_API_KEY=${WORKER_SHARED_API_KEY}
  
  orcaslicer-worker:
    image: printfarmer/orcaslicer-worker:latest
    environment:
      - WORKER_API_KEY=${WORKER_SHARED_API_KEY}  # Worker reads from WORKER_API_KEY
      - Worker__ApiBaseUrl=http://api:5245
```

**Kubernetes Secret:**
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: worker-api-key
type: Opaque
stringData:
  key: "your-secure-random-key-here"
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: api
spec:
  template:
    spec:
      containers:
      - name: api
        env:
        - name: WORKER_SHARED_API_KEY
          valueFrom:
            secretKeyRef:
              name: worker-api-key
              key: key
```

## Worker Implementation

Workers must include the `X-Worker-Key` header in all protected requests:

**Example (C# HttpClient):**
```csharp
var client = new HttpClient();
client.DefaultRequestHeaders.Add("X-Worker-Key", workerApiKey);

var claimRequest = new ClaimJobRequest
{
    WorkerName = "orcaslicer-worker-01",
    Capabilities = new[] { "orcaslicer" }
};

var response = await client.PostAsJsonAsync(
    $"{apiBaseUrl}/api/slice/claim",
    claimRequest
);

if (response.StatusCode == HttpStatusCode.Unauthorized)
{
    _logger.LogError("Worker authentication failed - check API key");
    return;
}
```

**Example (curl):**
```bash
curl -X POST https://api.printfarmer.example/api/slice/claim \
  -H "Content-Type: application/json" \
  -H "X-Worker-Key: your-secret-worker-key" \
  -d '{
    "workerName": "orcaslicer-worker-01",
    "capabilities": ["orcaslicer"]
  }'
```

## Security Considerations

### Current Implementation: Shared Key

The current implementation uses a **single shared key** for all workers. This has security implications:

**Advantages:**
- Simple to deploy and configure
- No database or key management infrastructure required
- Suitable for trusted internal networks

**Limitations:**
- All workers share the same credential
- Compromised key requires updating all workers simultaneously
- No per-worker audit trail or revocation capability
- Key rotation requires coordinated restart of all services

### Best Practices

1. **Generate Strong Keys:**
   ```bash
   # Generate a cryptographically secure random key
   openssl rand -base64 32
   ```

2. **Keep Keys Secret:**
   - Never commit keys to source control
   - Use environment variables or secret management systems
   - Rotate keys regularly (quarterly minimum)

3. **Network Isolation:**
   - Deploy workers in isolated networks
   - Use firewall rules to restrict API access to worker IPs
   - Consider mutual TLS for additional transport security

4. **Monitoring:**
   - Monitor for 401 responses (potential auth issues or attacks)
   - Alert on unexpected worker names or capabilities
   - Track worker activity for anomaly detection

### Future: Per-Worker Keys (Planned)

A more robust authentication model is planned for production deployments:

- **Database-backed keys:** Each worker has a unique API key stored in the database
- **Key rotation:** Workers can refresh keys without service interruption
- **Revocation:** Individual workers can be disabled without affecting others
- **Audit trail:** Track which worker performed which actions
- **Expiration:** Keys automatically expire after a configured lifetime

**Migration Path:**
The shared key mechanism will remain supported for backward compatibility. New deployments can opt into per-worker keys by setting `WorkerAuth:UsePerWorkerKeys=true`.

## Troubleshooting

### 401 Unauthorized Errors

**Symptom:** Worker requests return `401 Unauthorized`

**Diagnostic Steps:**

1. **Verify key is set:**
   ```bash
   # On API server
   echo $WORKER_SHARED_API_KEY
   
   # Should output non-empty value
   ```

2. **Check worker configuration:**
   ```bash
   # On worker machine
   echo $WORKER_API_KEY
   
   # Should match API server's WORKER_SHARED_API_KEY
   ```

3. **Inspect request headers:**
   ```bash
   # Enable verbose logging on worker
   export WORKER_LOGGING_LEVEL=Debug
   
   # Look for log entries showing header inclusion
   ```

4. **Test with curl:**
   ```bash
   curl -v -X POST http://api:5245/api/slice/claim \
     -H "Content-Type: application/json" \
     -H "X-Worker-Key: test-key" \
     -d '{"workerName":"test","capabilities":["orcaslicer"]}'
   
   # Check response status and headers
   ```

### Key Mismatch

**Symptom:** Worker key doesn't match API server key

**Solution:**
```bash
# Regenerate and synchronize key across all services
NEW_KEY=$(openssl rand -base64 32)

# Update API server
export WORKER_SHARED_API_KEY="$NEW_KEY"

# Update all workers
export WORKER_API_KEY="$NEW_KEY"

# Restart services
docker-compose restart api orcaslicer-worker prusaslicer-worker
```

### Testing Environment Bypass

**Symptom:** Tests pass without key but production fails

**Explanation:** The `Testing` environment bypasses auth when no key is configured. Always set `WORKER_SHARED_API_KEY` in `TestWebApplicationFactory` for accurate testing:

```csharp
// In test setup
Environment.SetEnvironmentVariable("WORKER_SHARED_API_KEY", "test-worker-key");
```

## Related Documentation

- [Distributed Slicing Architecture](./DISTRIBUTED_SLICING.md)
- [Worker Development Guide](./WORKER_DEVELOPMENT.md)
- [Security Best Practices](../SECURITY.md)
- [Deployment Configuration](./DEPLOYMENT_CONFIG_PERSISTENCE.md)

## API Reference

### WorkerAuthService

**Namespace:** `Farm.Web.Api.Services.Workers`

**Interface:**
```csharp
public interface IWorkerAuthService
{
    bool IsAuthorized(HttpContext context);
}
```

**Implementation:**
```csharp
public sealed class WorkerAuthService : IWorkerAuthService
{
    public bool IsAuthorized(HttpContext context)
    {
        // Extracts X-Worker-Key header and validates against configured key
        // Uses constant-time comparison to prevent timing attacks
        // Returns true if authorized, false otherwise
    }
}
```

### WorkerAuthSettings

**Namespace:** `Farm.Web.Api.Services.Workers`

**Configuration POCO:**
```csharp
public sealed class WorkerAuthSettings
{
    public string SharedKey { get; set; } = string.Empty;
}
```

**Binding:** Reads from `WorkerAuth:SharedKey` in configuration or `WORKER_SHARED_API_KEY` environment variable.

## Changelog

- **2025-10-21:** Initial shared key authentication implementation
- **2025-10-21:** Added negative auth tests and documentation
- **Future:** Per-worker key model design and implementation (planned)
