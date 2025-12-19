# Code Consolidation Opportunities - Identified in Phase 2

## Overview
This document catalogs all consolidation opportunities identified during Phase 2 analysis, organized by priority and impact. These represent areas where similar functionality is duplicated across multiple locations and could benefit from centralization.

---

## 1. URL Normalization (COMPLETED ✅)

**Status:** COMPLETED - Utility created and applied to 4 services

**Files Already Updated:**
- SpoolmanService
- PrintersService (3 instances)
- ModelService
- GcodeFilesService

**Remaining Opportunities:**
- LocalSlicerFileStorage.cs (~1 instance)
- JobDispatcherService.cs (~1 instance)
- SlicersService.cs (~1 instance)
- GcodeFilesController.cs (~1 instance)
- NetworkUrlRewriteService.cs (uses different pattern, review separately)

**Impact:** Consolidating the remaining 4-5 instances would complete URL normalization consolidation across the codebase.

---

## 2. HTTP Client Registration Pattern (READY FOR IMPLEMENTATION)

**Status:** READY - Extension methods created, awaiting plugin refactoring

**Boilerplate Pattern Identified:** All backend plugins implement identical code:
```csharp
services.AddScoped<IClient>(provider =>
{
    var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient();
    httpClient.Timeout = TimeSpan.FromSeconds(10);
    // ... instantiate client
});
```

**Files That Could Use New Extension Methods:**
1. MoonrakerBackendPlugin.cs - `AddScoped<IMoonrakerClient>`
2. PrusaLinkBackendPlugin.cs - `AddScoped<IPrusaLinkClient>`
3. OctoPrintBackendPlugin.cs - `AddScoped<IOctoPrintClient>`
4. SdcpBackendPlugin.cs - `AddScoped<ISdcpClient>`

**Other Services with Similar Patterns:**
- HeartbeatBackgroundService.cs (uses HttpClient directly)
- PeriodicDiscoveryBackgroundService.cs (uses HttpClient directly)
- HttpJobPollerService.cs (uses HttpClientFactory with timeout)
- HttpProgressReporter.cs (uses HttpClient with configuration)

**Impact:** Refactoring 4 plugins would eliminate ~40 lines of boilerplate code and standardize HTTP client setup.

---

## 3. Repeated Capability Checking Patterns

**Status:** IDENTIFIED - Consolidation opportunity

**Pattern Identified:**
```csharp
if (client is ISupportsMovement movement)
{
    // use movement capability
}
```

**Locations with Multiple Instances (10+):**
- PrintersService.cs - 8+ instances of `if (client is ISupports...)` pattern
- GcodeHarvestService.cs - 2+ instances of capability checking
- Other services using capability interfaces

**Consolidation Approach:**
Could create helper methods in BackendCapabilityFactory or PrintersService:
```csharp
private bool TryGetCapability<T>(IBackendClient client, out T capability) where T : class
{
    if (client is T cap)
    {
        capability = cap;
        return true;
    }
    capability = null;
    return false;
}
```

**Impact:** Would reduce conditional logic and improve readability, but lower priority than other consolidations.

---

## 4. JSON Serialization Options

**Status:** IDENTIFIED - Pattern repeated in 10+ locations

**Pattern Identified:**
Multiple services create JsonSerializerOptions with identical settings:
- PropertyNamingPolicy = JsonNamingPolicy.CamelCase
- DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull

**Files with Repeated Pattern:**
- PrusaLinkApiClient.cs - Creates options for snake_case
- AppDbContext.cs - Multiple JsonSerializer.Serialize calls
- Various API clients throughout the codebase

**Consolidation Approach:**
Create centralized JsonSerializerOptions factory:
```csharp
public static class JsonSerializerOptionsFactory
{
    public static JsonSerializerOptions CamelCase { get; } = new JsonSerializerOptions { ... };
    public static JsonSerializerOptions SnakeCase { get; } = new JsonSerializerOptions { ... };
}
```

**Impact:** Single point of control for JSON serialization strategy, easier to update globally.

---

## 5. Logging Patterns

**Status:** IDENTIFIED - Similar try-catch-log patterns repeated

**Pattern Identified:**
Services consistently implement try-catch blocks with logging:
```csharp
try
{
    // operation
}
catch (Exception ex)
{
    _logger.LogError(ex, "Operation failed: {Message}", ex.Message);
    // return null, rethrow, or provide default
}
```

**Locations:**
- OctoPrintStatusClient.cs - ParseOctoPrintStatus (removed in Phase 1)
- OctoPrintWebSocketAdapter.cs - ParsePrinterState, ParseJobStatus (removed in Phase 1)
- GcodeHarvestService.cs - Multiple catch blocks
- PrintersService.cs - Multiple try-catch patterns
- Various API clients

**Consolidation Approach:**
Could create extension methods for safe operation execution:
```csharp
public static T SafeExecute<T>(Func<T> operation, T defaultValue, ILogger logger)
{
    try { return operation(); }
    catch (Exception ex) { logger.LogError(ex, ...); return defaultValue; }
}
```

**Impact:** Reduced code duplication, consistent error handling, easier to audit error cases.

---

## 6. Null/Empty String Validation

**Status:** IDENTIFIED - Repeated validation patterns

**Pattern Identified:**
Multiple services check for null/empty strings in similar ways:
- `string.IsNullOrEmpty()`
- `string.IsNullOrWhiteSpace()`
- Manual trim + check combinations

**Impact:** Low - This is already a .NET framework method, no consolidation needed.

---

## 7. HTTP Error Handling & Retries

**Status:** IDENTIFIED - Scattered retry logic

**Pattern Identified:**
Services implement their own retry logic with exponential backoff:
- OctoPrintClient - retry with exponential backoff
- Various HTTP clients - custom retry implementations
- Circuit breaker patterns used in PrintersService

**Consolidation Opportunity:**
Create standardized HttpClientRetryPolicy or use existing Polly framework integration.

**Impact:** Consistent retry behavior, easier to update retry strategy globally.

---

## 8. Configuration Reading Pattern

**Status:** IDENTIFIED - Repeated config resolution

**Pattern Identified:**
Multiple services read configuration the same way:
```csharp
string apiBaseUrl = config["Worker:ApiBaseUrl"] 
    ?? Environment.GetEnvironmentVariable("WORKER_API_BASE_URL")
    ?? DefaultApiBaseUrl;
```

**Locations:**
- HttpJobPollerService.cs
- HttpProgressReporter.cs
- SlicerRegistrationClient.cs
- PeriodicDiscoveryBackgroundService.cs

**Consolidation Approach:**
Create configuration helper methods:
```csharp
public static string GetConfigValue(
    IConfiguration config, 
    string configKey, 
    string envVarName, 
    string defaultValue)
```

**Impact:** Single point for configuration resolution, easier to update how config is read.

---

## Consolidation Priority Matrix

| # | Feature | Effort | Impact | Priority | Status |
|---|---------|--------|--------|----------|--------|
| 1 | URL Normalization Complete | Low | High | DONE | ✅ Completed |
| 2 | Backend HTTP Client Registration | Low | High | HIGH | Ready |
| 3 | Logging Utilities | Medium | Medium | MEDIUM | Identified |
| 4 | JSON Options Factory | Low | Medium | MEDIUM | Identified |
| 5 | Configuration Helper | Low | Medium | MEDIUM | Identified |
| 6 | Capability Checking Helpers | Medium | Low | LOW | Identified |
| 7 | HTTP Retry Policies | High | Medium | MEDIUM | Identified |
| 8 | Error Handling Utilities | Medium | Medium | MEDIUM | Identified |

---

## Estimated Effort Summary

**Quick Wins (1-2 hours total):**
- Complete URL Normalization consolidation (4-5 more files)
- Refactor backend plugins to use new extension methods
- Create JSON Options factory

**Medium Effort (2-4 hours):**
- Create logging/error handling utilities
- Create configuration helper methods
- Consolidate capability checking helpers

**Larger Refactoring (4+ hours):**
- Implement standardized HTTP retry policies
- Consolidate exception handling strategies
- Review and consolidate other architectural patterns

---

## Notes for Next Session

1. **Backend Plugin Refactoring** is the highest priority quick win - touches 4 files with identical boilerplate
2. **UrlNormalizer adoption completion** should be included in same session as plugin refactoring
3. **JSON Options factory** is trivial to implement and has good impact on maintainability
4. **Logging utilities** could be implemented incrementally as new error handling is added

The consolidations identified here form a roadmap for continued code quality improvements without breaking changes to functionality or API.

