# Slicer Worker Shared Environment Variables

This reference lists environment variables common to all per-engine slicing workers (e.g., OrcaSlicer, PrusaSlicer). Engine‑specific docs should only document variables unique to that engine (like binary path overrides). Use this file as the authoritative source to avoid divergence.

## Core Hosting & Network
| Variable | Required | Default (Container) | Purpose |
|----------|----------|---------------------|---------|
| `ASPNETCORE_URLS` | No | `http://+:8080` | Internal Kestrel binding. All workers listen on container port 8080; external host port is mapped via compose / orchestration (e.g. 8081 -> 8080 for Orca, 8082 -> 8080 for Prusa). |

## Connectivity
| Variable | Required | Default | Purpose |
|----------|----------|---------|---------|
| `ConnectionStrings__Redis` | Yes (for distributed slicing) | (none) | Redis endpoint used for job queue operations and status pub/sub. |
| `Worker__StorageEndpoint` | Yes | `http://api:5245` (compose) | Base URL of API / storage service for artifact upload callbacks (e.g. G-code). |

## Worker Identity & Queueing
| Variable | Required | Default | Purpose |
|----------|----------|---------|---------|
| `Worker__InstanceId` | No | Auto-generated GUID (random per process) | Stable identity used to upsert the worker/service record on redeploy instead of creating a duplicate (issue #1528); always issued a fresh API key regardless of match. Must be distinct per worker — `deploy-docker.sh` sets it automatically (single deployments via `ORCA_WORKER_INSTANCE_ID`; scaled deployments get a literal value per generated service block, issue #1847). See "Redeploy identity" below for how the registry keeps this usable across a graceful shutdown. |
| `Worker__QueueName` | Yes | (engine-specific initializer) | Redis list / stream / queue name from which jobs are consumed. Distinct per engine. |
| `Worker__WorkingDirectory` | No | `/app/temp` | Scratch space for slicing operations; periodically cleaned. |

### Redeploy identity

The registry upserts on `Worker__InstanceId`, but it can only match a worker against a service
row that still exists. A worker recreated by `deploy-docker.sh` shuts down gracefully and calls
`POST /api/slicers/{id}/deregister` first, so deregistration must not destroy that row or the
replacement container registers as a brand-new worker — and, because `Workers` rows are keyed by
the service's Guid, leaves the previous `Workers` row stranded as
`Disabled: Slicer service deregistered`.

Deregistration therefore takes a `?retain=true` query parameter meaning "I will return under this
same instance ID". The row is kept as `Offline` with its credentials revoked, so the next
registration re-identifies it and updates both rows in place.

The worker sets `retain=true` only when `Worker__InstanceId` is explicitly configured. Every
worker `deploy-docker.sh` generates is, so they all take the retaining path. A worker started
without one — run by hand, from a local dev process, or by a bespoke host — generates a fresh
random identity every process start, and retaining rows for identities that never return would
strand one unreclaimable record per restart. Those deregistrations keep deleting the row.

Retention is never a credential-recovery mechanism: registration always issues a fresh API key,
and a retained row cannot authenticate because its service key is cleared and its `Workers` record
is left disabled and `Offline`.

Permanently removing a slicer is a separate administrative action
(`DELETE /api/admin/slicers/{id}`), which deletes both the service and its paired worker record.

## Engine Binary Path Pattern
Each engine exposes a binary path override following the pattern:

```
Worker__<EngineName>NoSpacesPath
```

Examples:
- `Worker__OrcaSlicerPath`
- `Worker__PrusaSlicerPath`

If unset, the worker attempts auto-detection at its conventional installation path.

## Logging & Diagnostics
| Variable | Purpose | Example |
|----------|---------|---------|
| `Logging__LogLevel__Default` | Global minimum log level | `Information` |
| `Logging__LogLevel__Slicing.*` | Fine-grained category tuning | `Debug` |

## Future (Planned) Shared Variables
| Variable (Proposed) | Purpose |
|---------------------|---------|
| `Worker__ShutdownGraceSeconds` | Grace period for in-flight job completion during SIGTERM |
| `Worker__MaxConcurrentJobs` | Concurrency cap (default 1 until engines are proven thread-safe) |

## Port Mapping Clarification
All workers listen *internally* on port 8080. External port exposure is controlled outside the container:

| Engine | Example Host Port | Container Port |
|--------|-------------------|----------------|
| OrcaSlicer | 8081 | 8080 |
| PrusaSlicer | 8082 | 8080 |

Change the host port by updating compose or deployment manifests—do not change the internal port unless you also update health check paths and service definitions.

## Minimal Required Set (Typical Deployment)
At minimum set:
```
ConnectionStrings__Redis=redis:6379
Worker__StorageEndpoint=http://api:5245
Worker__QueueName=<engine-queue-name>
```
Optional but recommended:
```
Worker__InstanceId=<stable-id>  # must be distinct per worker; deploy-docker.sh sets this automatically
Logging__LogLevel__Default=Information
```

## Validation Checklist
- Redis reachable
- API storage endpoint resolves & returns 200 on health
- Working directory writable
- Engine binary executable (or auto-detect succeeds)

---
For engine-specific flags, see the corresponding engine worker doc.
