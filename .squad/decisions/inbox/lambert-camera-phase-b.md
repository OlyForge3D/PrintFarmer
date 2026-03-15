# Camera Health Monitor Design Decisions

**Date:** 2026-03-15  
**Agent:** Lambert  
**Status:** Proposed for review

## Context

Camera Phase B requires:
1. EF Core migrations to add Camera health tracking fields (PrinterId, Source, CameraType, HealthStatus, LastHealthCheck, HealthMessage, ConsecutiveFailures)
2. Background service to periodically probe camera snapshot URLs and update health status

## Decision 1: Background Service for Health Monitoring

**What:** Implemented `CameraHealthMonitorService` as an `IHostedService` that runs periodic HTTP health checks.

**Why:**
- Background services are the standard .NET pattern for long-running tasks
- Avoids blocking API requests with health check logic
- Automatically starts/stops with application lifecycle
- Integrates with `IHostedService` pipeline (uses existing patterns from `MoonrakerSubscriptionService`, `SystemLogCleanupService`)

**Alternatives considered:**
- Hangfire scheduled job — rejected (adds dependency, overkill for simple periodic task)
- SignalR client-triggered checks — rejected (unreliable, clients may not be connected)
- HTTP endpoint with external cron — rejected (requires external orchestration)

**Trade-offs:**
- Pro: Standard pattern, minimal overhead, automatic lifecycle management
- Pro: Can be disabled via `disableBackgroundServices` flag for testing/development
- Con: Fixed 5-minute interval (not configurable at runtime without restart)

## Decision 2: Health Check Interval — 5 Minutes

**What:** Service runs health checks every 5 minutes.

**Why:**
- Cameras are typically always-on devices (MJPEG streams from OctoPi, ESP32-CAM, etc.)
- 5 minutes provides reasonable fault detection without excessive network traffic
- Balances responsiveness with resource usage (HTTP GET per camera every 5 min is negligible)

**Alternatives considered:**
- 1 minute — rejected (too frequent for static camera hardware, network overhead for large fleets)
- 10 minutes — rejected (too slow for fault detection in production environments)
- 30 seconds — rejected (excessive polling, no benefit for static camera endpoints)

**Trade-offs:**
- Pro: Detects failures within 5-10 minutes (acceptable for non-critical monitoring)
- Pro: Low network overhead (~12 HTTP requests per hour per camera)
- Con: Transient failures may trigger false negatives (mitigated by 3-failure threshold)

## Decision 3: Failure Thresholds — Degraded (1-2), Unhealthy (3+)

**What:**
- 1-2 consecutive failures → Degraded
- 3+ consecutive failures → Unhealthy

**Why:**
- Transient network issues are common (WiFi interference, temporary AP overload)
- 3-failure threshold (15 minutes of failures) filters out noise
- Degraded state provides early warning before declaring camera dead
- Consecutive counter resets on success (any successful check recovers to Healthy)

**Alternatives considered:**
- Single-failure = Unhealthy — rejected (too sensitive, false positives)
- 5-failure threshold — rejected (25 minutes to detect, too slow for user-facing alerts)
- Sliding window average — rejected (adds complexity, unnecessary for camera health)

**Trade-offs:**
- Pro: Reduces false positives from transient network issues
- Pro: Provides graduated status (Degraded → Unhealthy) for alerting/UI display
- Con: 15 minutes to detect sustained failure (acceptable for camera monitoring)

## Decision 4: HTTP Timeout — 10 Seconds

**What:** HTTP GET requests timeout after 10 seconds.

**Why:**
- Cameras typically respond within 2-3 seconds for snapshot URLs
- 10 seconds allows for network variance, slow WiFi, or high camera load
- Prevents hanging requests from blocking the entire health check cycle

**Alternatives considered:**
- 5 seconds — rejected (too aggressive for slow networks or WiFi-connected cameras)
- 30 seconds — rejected (blocks next health check if camera is unresponsive)
- No timeout — rejected (indefinite hang risk)

**Trade-offs:**
- Pro: Gracefully handles slow networks without false negatives
- Pro: Ensures health check completes within reasonable time
- Con: Slow cameras may timeout even when functional (rare in practice)

## Decision 5: Per-Camera Exception Handling

**What:** Exception in one camera's health check does not stop the loop for other cameras.

**Why:**
- One bad camera (malformed URL, DNS failure, network partition) should not block health checks for 100 other cameras
- Logged exceptions provide visibility for debugging
- Allows graceful degradation of monitoring service

**Alternatives considered:**
- Fail-fast on exception — rejected (stops all monitoring, terrible UX)
- Retry per camera — rejected (adds complexity, health check runs every 5 min anyway)

**Trade-offs:**
- Pro: Robust to individual camera failures
- Pro: Monitoring continues even with configuration errors
- Con: Exceptions are logged but do not block health check completion

## Decision 6: IServiceScopeFactory for DbContext Access

**What:** Background service (singleton lifetime) uses `IServiceScopeFactory` to create scoped `AppDbContext` instances.

**Why:**
- Background services are singletons by design (live for application lifetime)
- DbContext must be scoped (per-operation lifetime) to avoid EF Core threading issues
- `IServiceScopeFactory` is the standard pattern for accessing scoped services from singletons

**Alternatives considered:**
- Inject DbContext directly — rejected (violates lifetime rules, causes EF Core errors)
- Use DbContextFactory — considered but overkill (ScopeFactory is simpler, existing pattern in repo)

**Trade-offs:**
- Pro: Follows .NET best practices for background service + DbContext
- Pro: Matches existing pattern in `MoonrakerSubscriptionService`, `SystemLogCleanupService`
- Con: Slightly more verbose than direct injection (acceptable for correctness)

## Decision 7: Initial 30-Second Delay Before First Check

**What:** Service waits 30 seconds after application startup before running first health check.

**Why:**
- Ensures database initialization completes (`DatabaseInitializer` runs on startup)
- Avoids race condition where health check queries Cameras table before it's created/migrated
- Standard pattern for background services that depend on database (see `SystemLogCleanupService`)

**Alternatives considered:**
- No delay — rejected (race condition with database initialization)
- 10-second delay — rejected (too short, db initialization can take 20+ seconds on slow hardware)
- 60-second delay — rejected (too long, delays first health check unnecessarily)

**Trade-offs:**
- Pro: Avoids startup race conditions
- Pro: Matches existing background service patterns
- Con: First health check delayed by 30 seconds (acceptable, cameras won't change status immediately)

## Decision 8: Enum Storage as Strings

**What:** CameraSource, CameraType, HealthStatus enums stored as strings in database.

**Why:**
- Database-readable values (easier to query/debug raw SQL)
- Migration-friendly (adding enum values doesn't break existing data)
- Follows existing pattern in repo (PrinterBackend, JobStatus, etc. all stored as strings)

**Alternatives considered:**
- Store as integers — rejected (existing repo convention is string storage)

**Trade-offs:**
- Pro: Human-readable in database
- Pro: Avoids enum reordering issues
- Con: Slightly larger storage (negligible for enum values)

## Implementation Notes

- Migration names: `AddCameraPrinterRelationship` (consistent with existing migration naming)
- Service registration: `RegisterBackgroundServices()` in `ServiceCollectionExtensions.cs`
- Test fix: Randomized ServerUrl in `CameraManagementTests.CreateTestPrinterAsync()` to prevent UNIQUE constraint violations
- Build warnings fixed: Replaced `defaultValue: ""` with `string.Empty` in migrations

## Validation

- Build: 0 errors, 9 pre-existing warnings (obsolete camera properties, unrelated)
- Tests: 1615/1616 pass (1 pre-existing failure in PrinterImportFacadeIntegrationTests, unrelated)
- Migrations: PostgreSQL + SqlServer both generated cleanly, only Camera table changes

## Next Steps

1. Frontend UI to display camera health status (badge/icon in Camera View)
2. AlertingService integration (trigger alert when camera goes Unhealthy)
3. Manual health check trigger endpoint (for admin diagnostics)
4. Configurable health check intervals (DB setting or environment variable)
