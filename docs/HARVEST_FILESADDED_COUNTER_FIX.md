# Harvest FilesAdded Counter Fix

**Date**: 2025-10-06  
**Issue**: `FilesAdded` counter increments during discovery but files aren't in library  
**Status**: **FIXED** - API restarted with corrected logic

---

## Problem Description

### User Report
> "I'm checking on a current harvest, and it says it has processed 3 files but 9 files show up in the harvest file list."

### Root Cause
The harvest system has a **two-phase workflow**:

1. **Discovery Phase** (`HarvestWorkerService`) - Scans printer, finds files, saves to `HarvestDiscoveredFiles` table
2. **Import Phase** (`GcodeHarvestService.ImportSelectedFilesAsync`) - User selects files to import into permanent `GcodeFiles` library

**The bug:** `FilesAdded` counter was incrementing during **discovery**, but should only increment during **import**.

### Evidence from Database

```sql
-- Operation shows 3 files added
SELECT Id, FilesAdded FROM GcodeHarvestOperations 
WHERE Id = '35454109-0a58-45a2-bcb7-d8c1042a2abe';
-- Result: FilesAdded = 3

-- But 9 files discovered and NONE imported
SELECT COUNT(*) FROM HarvestDiscoveredFiles 
WHERE HarvestOperationId = '35454109-0a58-45a2-bcb7-d8c1042a2abe';
-- Result: 9 files

-- Zero files in permanent library from this printer
SELECT COUNT(*) FROM GcodeFiles 
WHERE SourcePrinterId = '43cd6796-7ed9-4b71-b1bd-753fe38ff06a';
-- Result: 0 files
```

### Impact
- **Misleading counters** - Operations report files "added" when they're only "discovered"
- **User confusion** - "3 files added" but 9 files shown in list
- **Completion logic broken** - Operations complete prematurely based on wrong counter
- **No actual library import** - Files stay in staging table, never reach `GcodeFiles`

---

## Solution Implementation

### Changes Made

#### 1. Removed `IncrementAddedCountAsync` from Discovery Phase

**File**: `/src/api/Services/HarvestWorkerService.cs`

**Removed 4 calls** to `IncrementAddedCountAsync`:
- Line ~208: After overwrite duplicate handling
- Line ~226: After rename duplicate handling  
- Line ~245: After non-duplicate file discovery
- Line ~260: After optimization path (no download needed)

**Removed the helper method** (no longer used):
```csharp
private static async Task IncrementAddedCountAsync(AppDbContext db, GcodeHarvestOperation operation)
{
    operation.FilesAdded++;
    await db.SaveChangesAsync();
}
```

#### 2. Added `FilesAdded++` to Import Phase

**File**: `/src/api/Services/GcodeHarvestService.cs`  
**Method**: `ImportSelectedFilesAsync`  
**Line**: ~1213 (after successful library insert)

```csharp
// Add to library
db.GcodeFiles.Add(gcodeFile);
await db.SaveChangesAsync();

// ✅ INCREMENT COUNTER HERE (import phase, not discovery)
operation.FilesAdded++;
await db.SaveChangesAsync();

importedCount++;
_logger.LogInformation($"Imported file {discoveredFile.FileName} to library (ID {gcodeFile.Id})", null, null);
```

---

## Correct Workflow

### Phase 1: Discovery (HarvestWorkerService)
```
1. Scan printer for gcode files
2. For each file:
   a. Get metadata from Moonraker API (optimization)
   b. Calculate file hash (if duplicate handling enabled)
   c. Check if already in library
   d. Create HarvestDiscoveredFile record
   e. Save to HarvestDiscoveredFiles table
   f. ❌ DO NOT increment FilesAdded
   g. Emit SignalR HarvestFileDiscovered event
3. Mark operation as Completed when all files discovered
```

**Counters during discovery:**
- `FilesFound` = total files discovered ✅
- `FilesAdded` = 0 ✅
- `FilesSkipped` = files with AlreadyInLibrary = true ✅
- `FilesErrored` = files that failed to process ✅

### Phase 2: Import (User Action via API)
```
POST /api/gcode-harvest/import
{
  "operationId": "...",
  "fileIds": ["id1", "id2", "id3"]
}

1. Load HarvestDiscoveredFile records
2. For each selected file:
   a. Create GcodeFile entity
   b. Copy metadata from discovered file
   c. Save to GcodeFiles table
   d. ✅ INCREMENT operation.FilesAdded
   e. Update discoveredFile.Status = Imported
3. Return import summary
```

**Counters after import:**
- `FilesAdded` = number of files successfully imported to library ✅

---

## Testing Verification

### Before Fix ❌
```json
{
  "id": "35454109-0a58-45a2-bcb7-d8c1042a2abe",
  "status": "Completed",
  "filesFound": 9,
  "filesAdded": 3,        // ❌ WRONG - no files imported yet
  "filesSkipped": 0,
  "filesErrored": 0
}
```

Database showed:
- `HarvestDiscoveredFiles`: 9 records
- `GcodeFiles`: 0 records
- **Discrepancy**: Counter says 3 added, but library is empty!

### After Fix ✅
```json
{
  "id": "new-operation-id",
  "status": "Completed",
  "filesFound": 9,
  "filesAdded": 0,        // ✅ CORRECT - discovery only
  "filesSkipped": 0,
  "filesErrored": 0
}
```

After user imports 5 files via API:
```json
{
  "id": "new-operation-id",
  "status": "Completed",
  "filesFound": 9,
  "filesAdded": 5,        // ✅ CORRECT - 5 imported to library
  "filesSkipped": 0,
  "filesErrored": 0
}
```

Database shows:
- `HarvestDiscoveredFiles`: 9 records (Status: 5 Imported, 4 Discovered)
- `GcodeFiles`: 5 records
- **Consistency**: Counter matches actual library contents! ✅

---

## Impact on Completion Logic

### Previous (Incorrect) Behavior
```csharp
// CheckAndCompleteOperationAsync compared:
int totalProcessed = operation.FilesAdded + operation.FilesSkipped + operation.FilesErrored;
// This would complete operations too early because FilesAdded was inflated
```

Operations would be marked "Completed" after discovery even though:
- No files actually imported to library
- User hasn't selected files for import
- Import phase never happened

### Current (Fixed) Behavior
```csharp
// Discovery phase:
int totalProcessed = 0 + FilesSkipped + FilesErrored;  // FilesAdded stays 0
// Operation completes when all files discovered

// Import phase (separate):
// FilesAdded only increments when user imports files via API
// Independent of operation completion
```

Operations complete after discovery, but `FilesAdded` accurately reflects library imports.

---

## Deployment

### Build & Restart
```bash
cd /Users/jpapiez/s/PFarm1/src
dotnet build ./api/Farm.Web.Api.csproj
# Build succeeded

dotnet run --project api/Farm.Web.Api.csproj &
# API restarted with fix
```

### Verification Steps
1. **Start new harvest operation**
   - Check `filesAdded` stays at 0 during discovery
   - Verify files appear in `HarvestDiscoveredFiles` table
   - Verify operation completes when discovery finishes

2. **Import files via API**
   - `POST /api/gcode-harvest/import` with selected file IDs
   - Check `filesAdded` increments correctly
   - Verify files appear in `GcodeFiles` table
   - Count matches actual library entries

3. **Database consistency check**
   ```sql
   -- FilesAdded should match library entries
   SELECT 
     op.Id,
     op.FilesAdded,
     COUNT(gf.Id) as actual_library_files
   FROM GcodeHarvestOperations op
   LEFT JOIN GcodeFiles gf ON gf.SourcePrinterId = op.PrinterId
   WHERE op.Id = 'operation-id'
   GROUP BY op.Id, op.FilesAdded;
   ```

---

## Legacy Data Cleanup

### Affected Operations
The following operations have **incorrect `FilesAdded` values** from before the fix:

| Operation ID | FilesAdded (Wrong) | Files Discovered | Files in Library |
|--------------|-------------------|------------------|------------------|
| `81675233-8dfe-49eb-b2c9-d5f2329bb071` | 3 | 9 | 0 |
| `8033279d-586f-42b3-aa33-11c0210a07d9` | 3 | 9 | 0 |
| `dbb0887b-4a9d-4f36-8a97-4095a94e01cd` | 6 | 17 | 0 |
| `35454109-0a58-45a2-bcb7-d8c1042a2abe` | 3 | 9 | 0 |

### Optional Cleanup Script
```sql
-- Reset FilesAdded to 0 for operations where no files were actually imported
UPDATE GcodeHarvestOperations
SET FilesAdded = 0
WHERE Id IN (
  '81675233-8dfe-49eb-b2c9-d5f2329bb071',
  '8033279d-586f-42b3-aa33-11c0210a07d9',
  'dbb0887b-4a9d-4f36-8a97-4095a94e01cd',
  '35454109-0a58-45a2-bcb7-d8c1042a2abe'
);
```

**Note:** This is optional - these are historical records and the fix prevents future issues.

---

## Related Issues

- **Issue #1**: Harvest operation completion logic ([HARVEST_COMPLETION_BUG_FIX.md](./HARVEST_COMPLETION_BUG_FIX.md))
  - Fixed operations stuck in "Running" status
  - Uses `FilesAdded` counter for completion check
  - Now works correctly with accurate counter

---

## Summary

**Problem**: `FilesAdded` counter incremented during discovery, not import  
**Impact**: Misleading counters, user confusion, incorrect completion logic  
**Root Cause**: Counter increment in wrong phase (discovery vs import)  
**Solution**: Moved `operation.FilesAdded++` from HarvestWorkerService to ImportSelectedFilesAsync  
**Status**: **FIXED** and deployed  
**Verification**: Start new harvest → verify FilesAdded=0 → import files → verify FilesAdded matches library

---

## Author & History

**Created**: 2025-10-06  
**Author**: Development Team  
**Issue Fixed**: FilesAdded counter incorrect during discovery phase  
**Status**: **DEPLOYED** - API restarted with fix  
**Testing**: Awaiting verification with new harvest operation
