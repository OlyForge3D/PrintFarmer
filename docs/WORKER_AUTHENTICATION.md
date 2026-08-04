## Slicer worker authentication

PrintFarmer uses three credentials for separate slicer trust boundaries. These
credentials are API secrets and must only be sent over TLS outside a trusted
container network.

| Credential | Header | Scope |
|---|---|---|
| Bootstrap registration key | `X-Slicer-Api-Key` | Registry list and registration |
| Registry-issued service key | `X-Slicer-Service-Api-Key` | One registered service's lifecycle routes |
| Registry-issued worker service key | `X-Worker-Key` and `X-Worker-Id` | Worker-only job, model, and artifact routes |

Header names are exact contract names. HTTP header matching is
case-insensitive, but the hyphen placement must match.

### Configuration

The API, standalone slicer host, and OrcaSlicer worker read the bootstrap secret
only from `WorkerAuth:SharedKey`, normally supplied as
`WorkerAuth__SharedKey`. `WORKER_SHARED_API_KEY` is the deployment script's
secret input; Docker Compose maps it to the canonical .NET configuration path
for every participating service.

The bootstrap value is used only for registration. After registration, the
worker keeps the returned per-service key in memory and uses it for lifecycle
and worker-only requests.

Every worker-facing process refuses to start when `WorkerAuth:SharedKey` is
missing or blank, including `Development` and `Testing`. Startup logs identify
the resolved configuration path and provider type, but never log the key or a
key prefix. There is no environment bypass.

### Registration and service identity

Register a worker with the shared registry key:

```http
POST /api/slicers/register
X-Slicer-Api-Key: <shared-key>
Content-Type: application/json
```

Successful registration returns a service GUID and a generated per-service
key:

```json
{
  "id": "46df3648-8d62-4f70-b455-bb721db0c360",
  "apiKey": "<registry-issued-service-key>"
}
```

Every registration creates a fresh `SlicerService`, service key, and internal
`Worker` record atomically. A synchronization failure leaves neither record
persisted. Registration never returns an existing service key based on
caller-supplied metadata.

The worker keeps the returned GUID and key as its registered service identity.
They are sent as `X-Worker-Id` and `X-Worker-Key` on every worker-only request.
PrintFarmer resolves that GUID to the internal enabled worker record and
validates the key bound to that record. A key issued to one service cannot be
paired with another service's GUID.

`Worker:InstanceId` is optional diagnostic metadata, not proof of ownership or
a credential-recovery token. When it is unset, each worker process generates a
random GUID. Docker deployments leave it unset for both single and scaled
workers. Even if two callers present the same value and host, registration
issues separate service GUIDs and keys and preserves both worker records.

### Registry routes

The shared registry key protects:

- `GET /api/slicers`
- `POST /api/slicers/register`

The registry-issued service key protects the service identified by `{id}`:

- `GET /api/slicers/{id}`
- `POST /api/slicers/{id}/heartbeat`
- `POST /api/slicers/{id}/deregister`
- `POST /api/slicers/{id}/rotate-key`

For example:

```http
POST /api/slicers/46df3648-8d62-4f70-b455-bb721db0c360/heartbeat
X-Slicer-Service-Api-Key: <registry-issued-service-key>
Content-Type: application/json

{
  "status": "Online",
  "freeSlots": 1
}
```

The service key is matched to the route GUID. A key issued to one service
cannot operate on another service.

### Worker-only routes

Every worker-only request requires both headers:

```http
X-Worker-Key: <registry-issued-service-key>
X-Worker-Id: 46df3648-8d62-4f70-b455-bb721db0c360
```

The worker's `SlicerWorkerApi` named HTTP client uses an authentication handler
that reads the current registered identity for every request and replaces both
headers. Requests are rejected locally until registration has supplied a
service ID and key.

The protected routes are:

- `POST /api/slice/claim`
- `POST /api/slice/{jobId}/progress`
- `POST /api/slice/{jobId}/renew-lease`
- `GET /api/slice/{jobId}/model`
- `GET /api/slice/{jobId}/models/{modelIndex}`
- `POST /api/slice/{jobId}/artifacts`
- `POST /api/slice/{jobId}/complete`
- `POST /api/slice/{jobId}/fail`

Claim requests also carry the registered service GUID in `workerId`. The
header GUID, body GUID, and credential-bound service must agree. After a claim,
PrintFarmer stores the internal worker ID on the job. Model downloads, artifact
uploads, progress, lease renewal, completion, and failure are allowed only
while that job is processing and assigned to the resolved worker.

Each successful claim returns an opaque `claimToken`. The worker must send it
on every subsequent job operation:

```http
X-Claim-Token: <claim-token>
```

The token identifies one claim incarnation. It changes whenever a job is
claimed again, including when the same worker reclaims the same job. Requests
from an earlier claim are rejected even when their worker identity still
matches. Model downloads are rechecked after storage opens, artifact uploads
are fenced when their database record is inserted, and completion accepts only
artifacts produced under the active claim token.

Workers receive a same-origin model route rather than the model storage path.
They upload artifacts through the job-scoped multipart route. Completion only
accepts artifact IDs created by the same worker for the same job.

### User routes and artifacts

User-facing slice submission, status, cancellation, and artifact downloads do
not accept worker keys. They require the normal authenticated user principal,
the applicable `resource:action` permission, and owner/farm access. Farm
administrator bypasses are audited.

### Failure contract

Authentication failures use `application/problem+json`.

| Status | Code | Meaning |
|---|---|---|
| `401` | `authentication_required` | A required key or service identity is missing, malformed, disabled, or invalid |
| `503` | `authentication_unavailable` | A registry route cannot resolve its configured validator |
| `403` | `resource_forbidden` | Valid identity does not own the addressed resource |
| `404` | `resource_not_found` | Protected user-facing resource does not exist |

Responses do not echo presented credentials, worker process output, internal
storage paths, or raw slicer failure details.

### Rotation and operations

- Rotate the deployment bootstrap key by updating `WORKER_SHARED_API_KEY` for the
  API/slicer host and every worker, then restart the affected services
  together. This controls future registration; already registered workers use
  their per-service keys.
- Rotate a service key with
  `POST /api/slicers/{id}/rotate-key` and the current service key. Store the
  returned replacement before the next heartbeat or worker-only request.
- Never put credentials in source control, command output, URLs, or support
  bundles.
- Restrict direct access to worker and slicer-host ports. Expose them through
  the authenticated PrintFarmer deployment path only.
- Treat repeated `authentication_required` responses as a configuration or
  compromise signal; do not retry indefinitely with the same rejected key.

OpenAPI publishes separate `apiKey` schemes for registry, service, and worker
authentication so generated clients can distinguish the trust boundaries.
