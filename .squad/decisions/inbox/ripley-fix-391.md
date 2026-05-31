# Decision: Fix captive dependency in PowerMonitorPollingService

**Date:** 2025-07-14
**Author:** Ripley (frontend, acting on backend fix per lockout rule)
**PR:** #391
**Bead:** #347

## Context

`PowerMonitorPollingService` is a singleton `BackgroundService` that previously accepted
`IEnumerable<ISmartPlugProvider>` as a direct constructor dependency. PR #393 (HA integration)
registers `HomeAssistantSmartPlugProvider` as **scoped** (it depends on per-request HTTP clients
and HA session tokens).

When both PRs merge, this creates a **captive dependency** — a singleton holding a reference to a
scoped service. With `ValidateScopes=true` (ASP.NET Core Development mode), this causes a startup
crash. In production (without validation), the scoped provider silently becomes a de-facto
singleton, leaking state across requests.

## Decision

Replace the direct `IEnumerable<ISmartPlugProvider>` constructor injection with per-iteration
scope resolution:

1. Remove `IEnumerable<ISmartPlugProvider>` from the constructor parameters.
2. In each poll iteration, resolve `IEnumerable<ISmartPlugProvider>` from the already-existing
   `AsyncServiceScope` via `scope.ServiceProvider.GetServices<ISmartPlugProvider>()`.
3. Pass the resolved providers to `PollMonitorsAsync` as a parameter.

## Validation

- Integration test `PowerMonitorPollingServiceScopeTests` verifies:
  - Startup succeeds with `ValidateScopes = true` and a scoped provider registered.
  - Each scope resolves a distinct provider instance (no captive reference).
- Full solution build: 0 errors.
- All tests pass.

## Consequences

- Any `ISmartPlugProvider` can now be registered with any DI lifetime (singleton, scoped, transient).
- Zero behavioral change for existing singleton providers.
- PR #393 can merge without modification.
