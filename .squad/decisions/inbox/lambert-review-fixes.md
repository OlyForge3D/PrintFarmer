# Decision: SSRF Validation Policy for Camera Health Monitor

**Date:** 2026-03-15
**Author:** Lambert (Backend Dev)
**Status:** Implemented

## Context
The camera health monitor probes user-supplied snapshot URLs. Since PrintFarmer is a local-network printer management app, cameras will commonly be on private IPs (192.168.x.x, 10.x.x.x, etc.).

## Decision
- **Block**: loopback addresses, link-local (169.254.x.x cloud metadata), non-HTTP(S) schemes
- **Allow**: RFC 1918 private IPs (10.x, 172.16-31.x, 192.168.x)
- Unsafe URLs are logged as warnings and the camera is marked unhealthy (not silently ignored)

## Rationale
Blocking private IPs would break the primary use case. Link-local blocking prevents cloud metadata SSRF (AWS 169.254.169.254). Loopback blocking prevents probing the PrintFarmer API itself.

---

# Decision: Per-Camera SaveChanges in Health Monitor

**Date:** 2026-03-15
**Author:** Lambert (Backend Dev)
**Status:** Implemented

## Decision
Save after each camera health probe instead of batching all saves at the end of the monitoring loop.

## Rationale
The health check loop can run for minutes (10s timeout × N cameras). Batching creates a race window where concurrent API updates (e.g., user toggling a camera) could be silently overwritten when the batch save fires. Per-camera saves keep the write window minimal.
