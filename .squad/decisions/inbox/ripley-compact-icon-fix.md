# Decision: Frontend Type Alignment with Backend DTOs

**Date:** 2026-03-17  
**Agent:** Ripley (Frontend Dev)  
**Status:** Implemented

## Context

Compact printer cards showed disabled auto-dispatch icons for ALL printers, even those with auto-print enabled. Investigation revealed a type mismatch between backend DTOs and frontend TypeScript types.

## Problem

Backend `AutoPrintStatusDto` (C#):
```csharp
public class AutoPrintStatusDto {
    public Guid PrinterId { get; set; }
    public bool Enabled { get; set; }
    public int QueueDepth { get; set; }
    // ... other fields
}
```

Serializes to camelCase JSON:
```json
{
  "printerId": "...",
  "enabled": true,
  "queueDepth": 2
}
```

Frontend `AutoDispatchStatus` (TypeScript) had:
```typescript
interface AutoDispatchStatus {
  printerId: string;
  autoPrintEnabled: boolean;  // ❌ Wrong name
  queuedJobCount: number;     // ❌ Wrong name
}
```

Result: `autoDispatchStatus?.autoPrintEnabled` was always `undefined`, making all icons appear disabled.

## Decision

**Align frontend types exactly with backend DTO property names (after camelCase serialization):**

```typescript
export interface AutoDispatchStatus {
  printerId: string;
  enabled: boolean;              // ✅ Matches backend
  state: AutoDispatchState;
  queueDepth: number;            // ✅ Matches backend
  printerName?: string;
  isReady?: boolean;
  currentJobName?: string;
  lastActivity?: string;
  bedPreConfirmed?: boolean;     // Added for pre-clear feature
}
```

## Rationale

1. **Backend is the source of truth** — frontend types should mirror backend DTOs
2. **JSON serialization is camelCase** — ASP.NET Core serializes PascalCase C# properties to camelCase JSON
3. **Property names must match exactly** — TypeScript can't detect runtime mismatches at compile time
4. **Type safety requires alignment** — mismatched names result in `undefined` values at runtime

## Implementation

Updated 4 files:
- `src/types/api.ts` — Type definition
- `src/features/printers/components/CompactPrinterCard.tsx` — 5 references
- `src/features/printers/components/DetailedPrinterCard.tsx` — 5 references
- `src/features/printers/__tests__/BedClearBanner.test.tsx` — 5 test references

Also updated `BedClearBanner.tsx` to use `queueDepth` instead of `queuedJobCount`.

## Consequences

- ✅ Compact card icons now correctly reflect auto-dispatch state
- ✅ No TypeScript errors (types were already present, just wrong names)
- ✅ All 1471 tests passing
- ⚠️ Future changes to backend DTOs require corresponding frontend type updates

## Follow-Up

Consider automated type generation from backend DTOs (e.g., NSwag, TypeScript code generation) to prevent future mismatches.
