## Brett revision — #371 / round-3 blockers

**Agent:** Brett (Researcher)
**Branch:** `squad/371-home-assistant-provider`
**Commit:** `45333917a`
**Date:** 2026-05-31T22:00:00-07:00
**Status:** Both blockers fixed. Build/test/format clean. Conflict scan empty.

---

## Blocker 1 — kW → watts conversion missing

**Hicks citation:** `src/api/Services/SmartPlug/HomeAssistantSmartPlugProvider.cs:201-207, 237`

**Root cause:** `ParseStateResponse` parsed the HA `state` string directly as watts
without inspecting `unit_of_measurement`. A kW entity reporting `state: "1.5"` with
`unit_of_measurement: "kW"` was stored as `1.5 W` instead of `1500 W`.

**Fix:** Inside the existing `if (root.TryGetProperty("attributes", ...))` block, added a
unit-aware conversion step before any other attribute extraction:

```csharp
// src/api/Services/SmartPlug/HomeAssistantSmartPlugProvider.cs  (~line 216)
if (attrs.TryGetProperty("unit_of_measurement", out JsonElement uomEl)
    && (uomEl.GetString() ?? string.Empty) == "kW")
{
    watts *= 1000.0;
}
```

Only `"kW"` triggers the multiply — `"W"` and absent units are left unchanged,
preserving existing behaviour for all watt-native sensors.

**Tests added:**

- `GetCurrentReadingAsync_WhenStateInKilowatts_ConvertsToWatts`
  — HA response `state: "1.5"`, `unit_of_measurement: "kW"` → `WattsNow == 1500`.
- `GetCurrentReadingAsync_WhenStateInWatts_DoesNotConvert`
  — HA response `state: "250.0"`, `unit_of_measurement: "W"` → `WattsNow == 250`.

**File:** `src/tests/Farm.Web.Api.Tests/Services/SmartPlug/HomeAssistantSmartPlugProviderTests.cs`

---

## Blocker 2 — Env var bypass of Enabled toggle

**Hicks citation:** `src/api/Services/SmartPlug/HomeAssistantSmartPlugProvider.cs:160-172`

**Root cause:** `ResolveConnectionParams` read `configuration["HomeAssistant:Token"]`
first and returned immediately if the config token was set — bypassing the
`settings.Enabled` check entirely. Deployments with `PFARM__HomeAssistant__Token`
configured therefore continued polling HA even after the admin set `Enabled = false`.

**Fix:** Moved the `!settings.Enabled` guard to execute **before** any token source is
consulted. The settings object is still resolved once from the scoped factory;
`Enabled=false` short-circuits immediately, returning `(settings.BaseUrl, null)`:

```csharp
// src/api/Services/SmartPlug/HomeAssistantSmartPlugProvider.cs  (~line 172)
// Enabled=false is checked first — before any token source.
if (!settings.Enabled)
{
    logger.LogDebug("HomeAssistant integration is disabled — skipping token resolution");
    return (settings.BaseUrl, null);
}

// Config-level token (env var) only reaches here when Enabled=true.
string? configToken = configuration["HomeAssistant:Token"];
if (!string.IsNullOrWhiteSpace(configToken))
{
    return (settings.BaseUrl, configToken);
}
```

**Test added:**

- `GetCurrentReadingAsync_WhenIntegrationDisabledAndEnvVarSet_ReturnsNullWithoutHttpCall`
  — `Enabled=false` + `HomeAssistant:Token = "env-override-token"` in config.
  A `MockBehavior.Strict` `HttpMessageHandler` is wired; any outbound HTTP call throws,
  proving the provider is completely inert. Assert: `reading == null`.

**File:** `src/tests/Farm.Web.Api.Tests/Services/SmartPlug/HomeAssistantSmartPlugProviderTests.cs`

---

## Validation

| Gate | Result |
|---|---|
| `dotnet build ./farm-web.sln -c Debug` | ✅ 0 errors, 8 warnings (all pre-existing) |
| `dotnet test ./farm-web.sln -c Debug --no-build` | ✅ 2224 passed, 1 failed (`MmuToolheadRetroSyncTests` — pre-existing, unrelated) |
| HomeAssistant tests (`--filter HomeAssistant`) | ✅ 30 passed, 0 failed |
| `dotnet format ./farm-web.sln --verify-no-changes` | ✅ exit 0 |
| Conflict marker scan (`<<<<<<<` / `>>>>>>>`) | ✅ 0 matches |

---

## Bishop / Vasquez compatibility

Both APPROVE the `b4680ba40`+`1487790fe` commits. These fixes are additive on top of those
commits and do not touch any of the surfaces they reviewed:

- `UnifiedSettingsController` blocklist — unchanged
- Token encryption/masking in `AdminHomeAssistantController` — unchanged
- `homeassistant.local` fallback removal — unchanged
- Error-path two-catch pattern — unchanged

The only modified files are `HomeAssistantSmartPlugProvider.cs` (`ResolveConnectionParams`
reordering + `ParseStateResponse` kW conversion) and the provider test file.
