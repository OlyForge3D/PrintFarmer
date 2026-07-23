# Revision Decision — Issue #371: Home Assistant Smart Plug Provider

**Agent:** Dallas (Tech Lead)  
**Branch:** `squad/371-home-assistant-provider`  
**Revision commit:** `b4680ba40` (on top of Lambert's `f03fdb538`)  
**Status:** All 6 trio blockers addressed. Ready for re-review.

---

## Blocker 1 — Security: HA token leaks via UnifiedSettingsController

**Reviewers:** Bishop (BLOCK), Hicks  
**File:** `src/api/Controllers/UnifiedSettingsController.cs`

Added `_settingsBlocklist` (`HashSet<string>` with `OrdinalIgnoreCase`) containing `HomeAssistantSettings.SectionName` (`"HomeAssistant"`).

- `Get()` (AllowAnonymous GET /settings): filters blocked keys from response dict before returning
- `GetSettingsByKeyName()` (AllowAnonymous GET /settings/{key}): returns `NotFound` for blocked keys
- `Update()` (POST /settings bulk): skips blocked entries with a `LogWarning`, continues remainder
- `UpdateSettingsByKeyNameAsync()` (POST /settings/{key}): returns `NotFound` for blocked keys

Also removed two sensitive log statements that fired before the blocklist loop:
- `LogInformation("{@SettingsSections}", rawDict)` — full raw payload before filtering
- `LogInformation("{@TypedSettings}", deserializedObj)` — full typed object including `EncryptedToken`

---

## Blocker 2 — Enabled toggle ignored

**Reviewer:** Hicks  
**Files:** `src/api/Controllers/Admin/AdminHomeAssistantController.cs`, `src/api/Services/SmartPlug/HomeAssistantSmartPlugProvider.cs`

**Controller** (`AdminHomeAssistantController`):
- `TestConnectionAsync` (line ~86): early return `{ Success=false, Message="integration is disabled" }` when `!settings.Enabled`
- `DiscoverEntitiesAsync` (line ~144): early return `BadRequest("Home Assistant integration is disabled")` when `!settings.Enabled`

**Provider** (`HomeAssistantSmartPlugProvider.ResolveConnectionParams`):
- New method replaces old `ResolveToken()`. Reads `HomeAssistantSettings` once per call.
- If `Enabled == false` and no `HomeAssistant:Token` config override → returns `(null, null)` → `GetCurrentReadingAsync` returns `null` (no reading recorded).
- Config key override (`PFARM__HomeAssistant__Token`) intentionally bypasses the toggle for dev/admin use.

---

## Blocker 3 — Discovery returns non-watt entities

**Reviewer:** Hicks  
**File:** `src/api/Controllers/Admin/AdminHomeAssistantController.cs`, method `IsPowerCapableEntity` (~line 279)

Narrowed matching criteria to **instantaneous power only**:
- `device_class == "power"` (sensor reports watts)  
- OR `unit_of_measurement` in `{"W", "kW"}`

Removed: `energy`, `current`, `voltage` from `device_class`; `kWh`, `Wh`, `A`, `V`, `mA` from units. These are energy/current/voltage sensors, not power sensors. The provider stores readings as watts; recording kWh/V/A there corrupts farm power data.

Side effect on tests: `PowerEntityCount` expectation in `TestConnectionAsync_WhenHaResponds_ReturnsVersionAndEntityCount` dropped from 2 → 1 (the `sensor.plug_energy` kWh entity is correctly excluded now).

---

## Blocker 4 — Error handling loses root cause

**Reviewer:** Hicks  
**Files:** `src/api/Controllers/Admin/AdminHomeAssistantController.cs`, `src/api/Services/SmartPlug/HomeAssistantSmartPlugProvider.cs`

**Root cause of original bug:** `catch (Exception ex) when (ex is not OperationCanceledException)` silently swallowed `TaskCanceledException` (which inherits `OperationCanceledException`) — HTTP timeouts were never caught, propagating unhandled.

**Fix — two-catch pattern** (applied in `TestConnectionAsync`, `DiscoverEntitiesAsync`, and provider `GetCurrentReadingAsync`):

```csharp
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    throw; // real user cancellation — bubble up
}
catch (Exception ex)
{
    message = ex switch
    {
        TaskCanceledException => "Request timed out...",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
            => "Authentication failed. Check the long-lived access token.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound }
            => "Home Assistant API not found at the configured URL.",
        _ => ex.Message
    };
}
```

`HttpRequestException.StatusCode` is populated by `EnsureSuccessStatusCode()` (available .NET 5+, repo targets `net10.0`).

---

## Blocker 5 — Hardcoded `homeassistant.local` fallback

**Reviewer:** Bishop  
**File:** `src/api/Services/SmartPlug/HomeAssistantSmartPlugProvider.cs`

`ParseDeviceAddress` now returns `(string? BaseUrl, string EntityId)`:
- Pipe-separated `"http://ha.local:8123|sensor.power"` → `BaseUrl = "http://ha.local:8123"`, `EntityId = "sensor.power"`
- Entity-only `"sensor.power"` → `BaseUrl = null`, `EntityId = "sensor.power"`

Removed the `"http://homeassistant.local:8123"` literal entirely.

`GetCurrentReadingAsync` resolves base URL as `parsedBaseUrl ?? configuredBaseUrl`. If both are null/empty, logs a warning and returns `null` — no silent best-effort request to an unknown host.

---

## Blocker 6 — Missing error-path test coverage

**Reviewers:** Bishop, Vasquez  
**Files:** `src/tests/Farm.Web.Api.Tests/Controllers/AdminHomeAssistantControllerTests.cs`, `src/tests/Farm.Web.Api.Tests/Services/SmartPlug/HomeAssistantSmartPlugProviderTests.cs`

**New provider tests** (4 added):
- `GetCurrentReadingAsync_WhenIntegrationDisabled_ReturnsNull`
- `GetCurrentReadingAsync_WhenHaReturns401_ReturnsNull`
- `GetCurrentReadingAsync_WhenHaReturns404_ReturnsNull`
- `GetCurrentReadingAsync_WhenHaTimesOut_ReturnsNull`

**New controller tests** (3 added):
- `TestConnectionAsync_WhenIntegrationDisabled_ReturnsDisabledMessage`
- `TestConnectionAsync_WhenHaReturns401_ReturnsTokenErrorMessage`
- `TestConnectionAsync_WhenHaTimesOut_ReturnsTimeoutMessage`

**Existing tests updated** (5):
- `TestConnectionAsync_WhenBaseUrlMissing_ReturnsFailure` — added `Enabled = true`
- `TestConnectionAsync_WhenTokenMissing_ReturnsFailure` — added `Enabled = true`
- `TestConnectionAsync_WhenHaResponds_ReturnsVersionAndEntityCount` — added `Enabled = true`, `PowerEntityCount` 2→1
- `DiscoverEntitiesAsync_WhenBaseUrlMissing_ReturnsBadRequest` — added `Enabled = true`
- `DiscoverEntitiesAsync_WhenHaResponds_ReturnsPowerEntitiesOnly` — added `Enabled = true`

Also: `CreateProvider` test helper updated to set `Enabled = true` by default; `GetCurrentReadingAsync_WithLegacyAddressFormat_UsesDefaultBaseUrl` replaced with `UsesConfiguredBaseUrl` (uses `settingsBaseUrl: "http://ha.custom.local:8123"`) and `WhenBaseUrlNotConfigured_ReturnsNull`.

---

## Validation

```
Build:  succeeded, 0 errors, 7 warnings (all pre-existing)
Tests:  2221 passed, 1 failed (MmuToolheadRetroSyncTests — pre-existing, unrelated to HA)
Format: --verify-no-changes exit 0
Conflicts: none
```
