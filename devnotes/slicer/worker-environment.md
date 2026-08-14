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
| `Worker__InstanceId` | No | Auto-generated GUID (random per process) | Stable identity used to upsert the worker/service record on redeploy instead of creating a duplicate (issue #1528); always issued a fresh API key regardless of match. Only set for a single, non-scaled worker — scaled replicas must leave this unset/distinct so Compose doesn't collapse them into one record. |
| `Worker__QueueName` | Yes | (engine-specific initializer) | Redis list / stream / queue name from which jobs are consumed. Distinct per engine. |
| `Worker__WorkingDirectory` | No | `/app/temp` | Scratch space for slicing operations; periodically cleaned. |

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
Worker__InstanceId=<stable-id>  # only for a single, non-scaled worker
Logging__LogLevel__Default=Information
```

## Validation Checklist
- Redis reachable
- API storage endpoint resolves & returns 200 on health
- Working directory writable
- Engine binary executable (or auto-detect succeeds)

---
For engine-specific flags, see the corresponding engine worker doc.
