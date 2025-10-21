# Worker Sandboxing & Resource Limiting

## Overview

This document explains the hardening and resource constraints applied to slicer worker containers (`orcaslicer-worker`, `prusaslicer-worker`). The goal is to reduce attack surface, enforce predictable resource usage, and isolate potentially risky native slicer binaries executed by the workers.

## Objectives

| Objective | Mechanism |
|-----------|-----------|
| Prevent privilege escalation | Non-root user (`sliceruser`), `no-new-privileges:true`, `cap_drop: [ALL]` |
| Limit filesystem write scope | `read_only: true` with explicit writable app/temp, logs, cache directories |
| Constrain process count | `pids_limit: 128` |
| Constrain open files | `ulimits: nofile: 1024` |
| Constrain memory & CPU | Compose `deploy.resources.limits` (Swarm) + soft reservations |
| Reduce runtime introspection | `DOTNET_EnableDiagnostics=0`, `COMPlus_EnableDiagnostics=0` |
| Prevent execution in /tmp abuse | `tmpfs /tmp` with `rw,noexec,nosuid,size=64m` |
| Limit concurrency inside worker | `Worker__MaxConcurrentJobs=1` (overrideable) |
| Explicit memory ceiling for internal logic | `Worker__MaxMemoryMb=1024` (advisory) |

## Dockerfile Hardening

Changes applied in `Dockerfile.orcaslicer` and `Dockerfile.prusaslicer`:

- Added security labels (`security.sandbox=true`)
- Disabled .NET diagnostics & profiler attach (`DOTNET_EnableDiagnostics=0`, `COMPlus_EnableDiagnostics=0`)
- Ensured published files are copied with ownership (`--chown=sliceruser:sliceruser`)
- Created locked-down directories (`chmod 700` on `/app/temp`, `/app/logs`, `/app/cache`)
- Added advisory environment values for internal concurrency/memory guards

## Runtime Compose Hardening

Applied to both worker services in `docker-compose.yml` and `docker-compose.microservices.yml`:

```yaml
read_only: true
cap_drop:
  - ALL
security_opt:
  - no-new-privileges:true
tmpfs:
  - /tmp:rw,noexec,nosuid,size=64m
ulimits:
  nofile: 1024
pids_limit: 128
deploy:
  resources:
    limits:
      cpus: "1.0"
      memory: 1024M
    reservations:
      cpus: "0.25"
      memory: 256M
```

> NOTE: `deploy:` resource limits only take effect under Docker Swarm / ECS / Kubernetes. For standalone Docker Engine, also use runtime flags (`--cpus`, `--memory`), or translate into orchestrator manifests.

## Overriding Defaults

You can override concurrency and memory advisory limits globally via application settings or per-worker using environment variables.

### Global Settings (Preferred)

Configure via `appsettings.json` or the Settings UI (Slicer section):

```json
{
  "Slicer": {
    "MaxConcurrentJobs": 2,
    "MaxMemoryMb": 2048
  }
}
```

These settings are hot-reloadable and apply across all workers. The orchestrator enforces these as upper bounds during worker registration.

### Per-Worker Environment Variables

Override limits for individual worker containers:

```bash
Worker__MaxConcurrentJobs=2
Worker__MaxMemoryMb=2048
```

**Note**: The orchestrator will enforce the lower of (worker-requested, global-setting) to prevent resource abuse.

To relax sandboxing (not recommended), remove or adjust:

- `read_only: true`
- `cap_drop: [ALL]`
- Add selective capabilities only if required (e.g., `SYS_PTRACE` for native debugging)

## Extending Hardening

| Enhancement | Description | Status |
|-------------|-------------|--------|
| Seccomp profile | Provide a custom seccomp profile to block risky syscalls. Example: `security_opt: ["seccomp:./seccomp-slicer.json"]` | Planned |
| AppArmor profile | Constrain FS & kernel interactions further: `security_opt: ["apparmor:printfarmer-slicer"]` | Planned |
| Distroless runtime | Replace base image with distroless .NET + required GTK libs (future optimization) | Planned |
| Image scanning | Integrate Trivy / Grype scanning in CI for vulnerability detection | ✅ **Implemented** - See [SLICER_WORKER_CI_SECURITY.md](./SLICER_WORKER_CI_SECURITY.md) |
| Image efficiency | Dive layer analysis to detect wasted space and optimize images | ✅ **Implemented** - See [SLICER_WORKER_CI_SECURITY.md](./SLICER_WORKER_CI_SECURITY.md) |
| Integrity verification | Pin and checksum AppImage / Flatpak assets before extraction | Planned |

## Operational Monitoring

Combine new metrics (see `SLICER_SERVICE_METRICS.md`) with container stats:

- Alert if memory usage > 85% of limit
- Alert if OOM kills (`docker events` or cgroup metrics)
- Track job failure reasons and correlate with resource exhaustion

## Troubleshooting

| Symptom | Likely Cause | Remediation |
|---------|--------------|------------|
| Worker exits immediately | Missing binary / stub exit code 2 | Verify asset download; disable `ALLOW_STUB` for strict mode |
| Slicer crashes with permission error | Read-only root FS blocking writes | Ensure app writes only to `/app/temp`, `/app/logs`, or add dedicated volume |
| Extraction fails for new architecture | Unsupported AppImage layout | Provide preseeded asset via build secret or update extraction logic |
| Heartbeat latency spikes | CPU throttling due to limits | Raise `cpus` limit or reduce `MaxConcurrentJobs` |

## Future Roadmap

1. Seccomp + AppArmor profile generation
2. Distroless base variant
3. Automatic checksum verification of slicer binaries
4. Container image SBOM + vulnerability gates in CI
5. Adaptive resource tuning based on historical utilization

## References

- **CI Security Checks**: [SLICER_WORKER_CI_SECURITY.md](./SLICER_WORKER_CI_SECURITY.md) - Automated Trivy scanning and Dive efficiency analysis
- **Docker Security Best Practices**: https://docs.docker.com/develop/security-best-practices/
- **.NET Container Hardening**: https://learn.microsoft.com/dotnet/core/docker/building-net-docker-images
- **OCI Image Labels**: https://github.com/opencontainers/image-spec/blob/main/annotations.md
- **Trivy Scanner**: https://aquasecurity.github.io/trivy/
- **Dive Efficiency Tool**: https://github.com/wagoodman/dive

---
**Status:** Initial sandboxing complete. Review periodically as slicer features expand.
