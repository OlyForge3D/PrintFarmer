# Kane — HA Provider Revision (371 round-4)

**Commit:** `6785eae01`
**Branch:** `squad/371-home-assistant-provider`

## Changes Made

### `HomeAssistantSmartPlugProvider.cs` — `ParseStateResponse`

Replaced exact `== "kW"` check with a case-insensitive block covering all three HA
`device_class=power` units:

| unit_of_measurement | Action |
|---|---|
| `kW` / `kw` / `KW` | `watts *= 1000.0` (via `StringComparison.OrdinalIgnoreCase`) |
| `mW` / `mw` / `MW` | `watts *= 0.001` (new) |
| `W` (or absent) | no conversion (unchanged) |

### `HomeAssistantSmartPlugProviderTests.cs`

Added to the existing Blocker 1 kW test block (Brett's tests untouched):

- `[Theory] [InlineData("kw")] [InlineData("KW")]` — verifies case variants convert 2.0 → 2000 W
- `[Fact] GetCurrentReadingAsync_WhenStateInMilliwatts_ConvertsToWatts` — verifies 500 mW → 0.5 W

## Test Results

**20/20 HomeAssistantSmartPlugProvider tests pass** (17 Brett + 3 Kane).

## Hicks Blockers Resolved

1. ✅ Case-insensitive `kW` — `"kw"` and `"KW"` now convert correctly.
2. ✅ `mW` milliwatt support added per HA `device_class=power` spec.
