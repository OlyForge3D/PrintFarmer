# Decision: Add Wattage + MachineHourlyRate to Printer Modals

**Date:** 2026-03-27  
**Author:** Ripley (Frontend Dev)  
**Status:** IMPLEMENTED

## Context

Lambert added `Wattage` (nullable decimal) to `Printer` and `PrinterModel` entities and `MachineHourlyRate` was already on `Printer`. The Create/Update DTOs on both backend and TypeScript were updated, but the fields had no UI surface in the Add or Edit printer modals.

## Decision

Added a "Cost Settings" section to both `AddPrinterModal` and `EditPrinterModal` containing:

- **Wattage (W)**: `number` input, min 0, step 1. Helper: "Power consumption in watts. Leave blank to use model default or global setting."
- **Machine Hourly Rate ($)**: `number` input, min 0, step 0.01. Helper: "Hourly operating cost. Leave blank to use the global default."

Empty values submit as `undefined`/`null` — the backend cost calculation cascade (`printer.Wattage → model.DefaultWattage → settings.AveragePrinterWattage`) handles fallback.

## Changes

| File | Change |
|---|---|
| `src/infra/Dtos/PrinterDetailsDto.cs` | Added `Wattage` and `MachineHourlyRate` fields |
| `src/api/Controllers/PrintersController.cs` | Map `p.Wattage` and `p.MachineHourlyRate` into details DTO |
| `src/Web/ReactApp/src/types/api.ts` | Added `wattage?` and `machineHourlyRate?` to `PrinterDetails` |
| `src/Web/ReactApp/src/features/printers/components/AddPrinterModal.tsx` | Cost Settings section |
| `src/Web/ReactApp/src/features/printers/components/EditPrinterModal.tsx` | Cost Settings section + pre-population + change detection |
| `src/Web/ReactApp/src/features/catalog/components/PrinterModelsCatalog.tsx` | Show `defaultWattage` badge in Features column |
| `src/Web/ReactApp/src/features/printers/components/__tests__/PrinterCostFields.test.tsx` | 6 tests covering render, helper text, pre-population, and submit behavior |

## Validation

- ✅ 6/6 new cost field tests pass
- ✅ 5/5 existing EditPrinterModal tests pass
- ✅ 62/62 total printer test suite passes
- ✅ ESLint: 0 errors
- ✅ .NET build: 0 errors, 0 warnings
- ✅ React production build: success
