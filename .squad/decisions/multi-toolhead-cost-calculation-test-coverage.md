# Multi-Toolhead Cost Calculation Test Coverage

**Date:** 2026-04-01  
**Author:** Kane (QA Engineer)  
**Status:** ✅ Complete  
**Related Bead:** PFarm1-kk0v

---

## Context

PrintFarmer's cost calculation service (`JobCostCalculationService`) has two distinct code paths for calculating material costs:

1. **Multi-toolhead path** (lines 158-182): When a print job has `PrintJobToolheadUsage` records, it calculates cost per toolhead and aggregates them.
2. **Single-spool fallback** (lines 184-302): When no toolhead usages exist, it falls back to using the job's single `SpoolmanSpoolId`.

Previously, test coverage existed only for the single-spool path. The multi-toolhead path — which handles multi-material/MMU printing — had no automated tests covering per-extruder cost calculation, cost aggregation across toolheads, or edge cases like missing spool data or partial consumption.

---

## Decision

**Created comprehensive test suite for multi-toolhead cost calculation path.**

Added 11 unit/integration tests in `src/tests/Farm.Web.Api.Tests/Services/Cost/JobCostCalculationMultiToolheadTests.cs` covering:

### Core Functionality
- **Single toolhead calculation**: Per-extruder cost = (usageGrams / spoolWeightGrams) × pricePerKg
- **Multi-toolhead aggregation**: Total material cost = sum of per-toolhead costs (only toolheads with usage > 0 contribute)
- **Cost storage**: Per-toolhead costs stored in `PrintJobToolheadUsage.MaterialCostUsd`, total in `PrintJob.MaterialCostUsd`

### Edge Cases
- **Missing spool data**: Falls back to global default filament price ($30/kg from settings)
- **Partial consumption**: Some toolheads zero usage → excluded from total
- **Null filament usage**: Toolheads with null usage are skipped
- **All zero usage**: Returns null material cost when all toolheads have zero usage
- **Empty toolhead usages**: Falls back to single-spool path when collection is empty
- **Boundary case**: Exactly 1 toolhead still uses multi-toolhead path (not single-spool)

### Integration
- **Energy/machine/labor costs**: Multi-toolhead path correctly integrates with energy, machine hourly rate, and labor markup calculations
- **Very small usage**: Rounding behavior for sub-gram amounts ($0.01, $0.04)
- **Negative usage**: Skipped, results in null cost

---

## Key Technical Details

### Per-Toolhead Cost Calculation

Formula: `cost = (filamentUsageGrams / spoolWeightGrams) × pricePerKg`

Each toolhead's cost is **rounded to 2 decimal places individually**, then summed:

```csharp
// Example: 3 toolheads with 50g, 75g, 100g usage @ $25/kg (1000g spool)
T0: (50 / 1000) × 25 = 1.25 → $1.25
T1: (75 / 1000) × 25 = 1.875 → $1.88 (rounded)
T2: (100 / 1000) × 25 = 2.50 → $2.50

Total: $1.25 + $1.88 + $2.50 = $5.63
```

**Important:** The service rounds each toolhead cost before aggregation. This means the total may differ from rounding the aggregate usage first. In the example above:
- Per-toolhead rounding: $5.63
- Aggregate-then-round: (225g / 1000) × 25 = 5.625 → $5.62

The current implementation uses **per-toolhead rounding** (resulting in $5.63), which is the correct behavior since each toolhead's cost is persisted individually.

### Path Selection Logic

```csharp
// Line 156: Multi-toolhead path
if (job.ToolheadUsages != null && job.ToolheadUsages.Count > 0)
{
    // Iterate toolheads, calculate per-toolhead cost, aggregate
}
else
{
    // Single-spool fallback path
}
```

### Domain Model

- `PrintJobToolheadUsage`: Entity tracking per-toolhead filament consumption
  - `ToolheadIndex` (int): Zero-based extruder index (T0=0, T1=1, etc.)
  - `SpoolmanSpoolId` (string?): Optional reference to Spoolman spool
  - `FilamentUsageGrams` (double?): Grams consumed by this toolhead
  - `MaterialCostUsd` (decimal?): Calculated cost for this toolhead (populated by service)

- `PrintJob`: Aggregate root
  - `MaterialCostUsd` (decimal?): Total material cost (sum of all toolhead costs)
  - `ToolheadUsages` (ICollection<PrintJobToolheadUsage>): Per-toolhead consumption data

---

## Test Coverage Matrix

| Test Case | Purpose | Status |
|-----------|---------|--------|
| `CalculateAndStoreCostsAsync_WithSingleToolhead_CalculatesPerExtruderCost` | Single toolhead: 50g @ $25/kg = $1.25 | ✅ PASS |
| `CalculateAndStoreCostsAsync_WithMultipleToolheads_AggregatesCostAcrossToolheads` | 3 toolheads: T0=$1.25 + T1=$1.88 + T2=$2.50 = $5.63 | ✅ PASS |
| `CalculateAndStoreCostsAsync_WithMissingSpoolData_UsesGlobalDefaultPrice` | No spool → fallback to $30/kg default | ✅ PASS |
| `CalculateAndStoreCostsAsync_WithPartialConsumption_OnlyCountsNonZeroToolheads` | T0=50g, T1=0g, T2=0g → only T0 contributes | ✅ PASS |
| `CalculateAndStoreCostsAsync_WithNullFilamentUsage_SkipsNullToolheads` | T0=null, T1=50g → only T1 contributes | ✅ PASS |
| `CalculateAndStoreCostsAsync_WithAllZeroUsage_ReturnsNullMaterialCost` | All toolheads zero → null total cost | ✅ PASS |
| `CalculateAndStoreCostsAsync_WithMultiToolheadAndEnergyCosts_IncludesAllCostComponents` | Energy + machine + labor integrated correctly | ✅ PASS |
| `CalculateAndStoreCostsAsync_WithEmptyToolheadUsages_FallsBackToSingleSpoolPath` | Empty collection → single-spool fallback | ✅ PASS |
| `CalculateAndStoreCostsAsync_WithExactlyOneToolhead_UsesMultiToolheadPath` | 1 toolhead → multi-toolhead path (not fallback) | ✅ PASS |
| `CalculateAndStoreCostsAsync_WithVerySmallUsage_RoundsCorrectly` | 0.5g → $0.01, 1.5g → $0.04 (rounding edge) | ✅ PASS |
| `CalculateAndStoreCostsAsync_WithNegativeUsage_SkipsNegativeToolheads` | T0=-10g (invalid) → skipped, null result | ✅ PASS |

**Total:** 11/11 tests passing

---

## Validation Results

### Build Status
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Test Execution
```
Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 10 s
```

### Key Files
- Test suite: `src/tests/Farm.Web.Api.Tests/Services/Cost/JobCostCalculationMultiToolheadTests.cs`
- Implementation: `src/infra/Services/Cost/JobCostCalculationService.cs` (lines 156-386)
- Domain entity: `src/infra/Domain/PrintJobToolheadUsage.cs`
- Settings: `src/infra/Settings/CostTrackingSettings.cs`

---

## Consequences

### Positive
1. ✅ **Complete coverage** of multi-toolhead cost calculation path (previously untested)
2. ✅ **Edge case validation** for missing spool data, partial consumption, zero usage
3. ✅ **Regression protection** for MMU/multi-material printing cost accuracy
4. ✅ **Clear documentation** of per-toolhead rounding behavior via tests
5. ✅ **Integration validation** with energy/machine/labor cost components

### Negative
- None identified. Test suite is comprehensive without being brittle.

### Neutral
- Tests follow existing patterns from `JobCostCalculationTests.cs` (CustomWebApplicationFactory, helper methods, FluentAssertions)
- Per-toolhead rounding behavior is now explicitly validated (expected: $5.63, not $5.62)

---

## Follow-Up Actions

- [ ] Consider adding frontend tests for displaying per-toolhead costs in job detail UI
- [ ] Monitor production cost calculations to validate rounding behavior at scale
- [ ] Document cost calculation formula in user-facing documentation

---

## Related Files

```
src/tests/Farm.Web.Api.Tests/Services/Cost/
├── JobCostCalculationMultiToolheadTests.cs (NEW - 11 tests)
└── JobCostCalculationTests.cs (existing single-spool tests)

src/infra/Services/Cost/
└── JobCostCalculationService.cs (lines 156-386: multi-toolhead path)

src/infra/Domain/
├── PrintJobToolheadUsage.cs (per-toolhead cost storage)
└── PrintJob.cs (aggregate material cost)

src/infra/Settings/
└── CostTrackingSettings.cs (DefaultFilamentPricePerKg, etc.)
```

---

## References

- Bead: PFarm1-kk0v
- Service implementation: `JobCostCalculationService.CalculateMaterialCostAsync` (lines 156-182)
- Per-spool cost calculation: `JobCostCalculationService.CalculateSingleSpoolCostAsync` (lines 309-386)
- Test infrastructure: `CustomWebApplicationFactory` pattern from Farm.Web.Api.Tests
