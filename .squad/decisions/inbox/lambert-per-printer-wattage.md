# Per-Printer Wattage with Catalog Defaults

**Author:** Lambert (Backend Dev)
**Date:** 2026-03-26
**Status:** Implemented

## Decision

Added per-printer wattage override (`Printer.Wattage`) and catalog-level default (`PrinterModel.DefaultWattage`) with a three-level cascade for energy cost calculation.

## Cascade Rule

```
printer.Wattage ?? printer.Model?.DefaultWattage ?? settings.AveragePrinterWattage
```

## Changes Made

### Domain
- `PrinterModel.DefaultWattage` (decimal?) — catalog default for model
- `Printer.Wattage` (decimal?) — per-printer override

### DTOs
- `UpdatePrinterDto`: Added `Wattage` and `MachineHourlyRate`
- `CreatePrinterFromDiscoveryDto`: Added `Wattage` and `MachineHourlyRate`
- `PrinterModelDto`: Added `DefaultWattage`
- `PrinterModelSeedDto`: Added `DefaultWattage`

### Cost Calculation
- `JobCostCalculationService.CalculateEnergyCost`: Uses cascade instead of flat settings value
- Both `.Include(j => j.AssignedPrinter).ThenInclude(p => p.Model)` added to job queries

### Seed Data
- `printer-models.yaml`: 37 models populated with `defaultWattage` (120W–500W based on known specs)

### Controller/Service
- `PrintersController` update endpoint maps `Wattage` and `MachineHourlyRate` from DTO
- `PrintersService.CreatePrinterFromDtoAsync` maps both fields on creation

### Tests
- 4 new cascade tests (override, model default, full cascade, settings fallback)
- Test helper creates isolated models to prevent seeded DefaultWattage from leaking

### Migrations
- `AddWattageToEntities` for both PostgreSQL and SQL Server

## Ripley Impact

`Wattage` and `MachineHourlyRate` are now available on the Add/Edit printer DTOs for frontend modals.
