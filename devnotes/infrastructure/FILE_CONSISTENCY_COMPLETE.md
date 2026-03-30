# File Consistency & Health Management System
## Complete Implementation Guide

---

## Executive Summary

This document consolidates the file consistency strategy and implementation for PrintFarmer's file management system. It covers the problem statement, recommended solutions, and the completed implementation of all three layers: transaction-safe operations, integrity verification, and periodic auditing with a full-featured REST API and database persistence.

**Status**: ✅ Phase 1 Complete - All layers implemented, tested, and production-ready.

---

## Problem Statement

PrintFarmer manages two critical file types that must stay synchronized with the database:
- **GCode files** (harvested from printers or uploaded manually)
- **3D Model files** (STL, 3MF, OBJ, PLY, STEP with optional thumbnails)

**Key Risk**: Files and database records can become out of sync through:
1. **Orphaned files** - File exists on disk but no DB record
2. **Missing files** - DB record exists but file not found on disk
3. **File corruption** - Hash/size mismatch
4. **Concurrent access issues** - Race conditions during operations
5. **Disk space exhaustion** - Uncontrolled file growth

---

## Recommended Strategy: Defensive Consistency with Periodic Reconciliation

A three-layer approach combining transaction safety, runtime verification, and background auditing.

---

## Implementation: Phase 1 Complete ✅

### Layer 1: Transaction-Safe Operations

#### Upload Pattern (Implemented)
- Write to temp file (`{id}.tmp{ext}`) first
- Validate format, compute hash
- DB transaction: INSERT record with **final** path
- On commit: Move temp → final
- On failure: Delete temp file

**Result**: No orphaned files from failed uploads

#### Delete Pattern (Enhanced)
- Load record and delete disk files first
- Then execute DB transaction
- If DB fails: Files gone (manual recovery needed)
- If disk fails: File remains, proper error handling

**Result**: Minimizes orphaned files

### Layer 2: File Integrity Verification

**FileIntegrityService** - Checks files before critical operations:
- **FileExistsAsync()** - Verify file on disk
- **VerifySizeAsync()** - Check size matches DB
- **VerifyHashAsync()** - Compute and compare hash
- **VerifyIntegrityAsync()** - Comprehensive check (existence → size → hash)
- **RecomputeHashAsync()** - Get current file hash
- **GetFileSizeAsync()** - Get current file size

All methods:
- Return results (no exceptions)
- Are async/cancellable
- Include detailed failure reasons

### Layer 3: Periodic Consistency Audits

**FileConsistencyAuditService** - Background service (hourly):
1. Audits all Model3D files
2. Audits all GcodeFile files
3. Scans for orphaned files
4. Persists results to database
5. Updates file health status

---

## Database Schema

### New Entities

#### FileHealthAudit
```csharp
public class FileHealthAudit
{
    public Guid Id { get; set; }
    public DateTime AuditDate { get; set; }
    public FileAuditType AuditType { get; set; }  // Model3D, GcodeFile, OrphanedFiles, FullAudit
    
    // Statistics
    public int FilesChecked { get; set; }
    public int HealthyFiles { get; set; }
    public int MissingFiles { get; set; }
    public int CorruptedFiles { get; set; }
    public int OrphanedFiles { get; set; }
    
    // Details (JSON arrays)
    public string? MissingFileIds { get; set; }
    public string? CorruptedFileIds { get; set; }
    public string? OrphanedFilePaths { get; set; }
    
    // Summary
    public string? SummaryMessage { get; set; }
    public bool HasIssues { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

### Enhanced Entities

#### Model3D
```csharp
public DateTime? LastHealthCheckDate { get; set; }
public FileHealthStatus HealthStatus { get; set; } = FileHealthStatus.Unknown;
public string? LastVerificationResult { get; set; }  // JSON
```

#### GcodeFile
```csharp
public DateTime? LastHealthCheckDate { get; set; }
public FileHealthStatus HealthStatus { get; set; } = FileHealthStatus.Unknown;
public string? LastVerificationResult { get; set; }  // JSON
```

### Enums

**FileHealthStatus**: Unknown (0), Healthy (1), Missing (2), Corrupted (3), Inaccessible (4)

**FileAuditType**: Model3D (0), GcodeFile (1), OrphanedFiles (2), FullAudit (3)

### Indexing

- `FileHealthAudit.AuditDate` (DESC) - Recent queries
- `FileHealthAudit.AuditType` - Type filtering
- `FileHealthAudit.HasIssues` - Dashboard status
- `FileHealthAudit.(AuditType, AuditDate)` - Type + recent composite
- `Model3D.HealthStatus` - Dashboard queries
- `Model3D.LastHealthCheckDate` - Recent checks
- `GcodeFile.HealthStatus` - Dashboard queries
- `GcodeFile.LastHealthCheckDate` - Recent checks

---

## REST API Endpoints

### Health Summary
**`GET /api/fileconsistency/health/summary`**

Returns overall file health status for dashboard:
- Model3D stats (total, healthy, missing, corrupted)
- GcodeFile stats (total, healthy, missing, corrupted)
- Overall health percentage (0-100%)
- Last healthy audit date

```json
{
  "totalModel3DFiles": 45,
  "model3DHealthy": 44,
  "model3DMissing": 1,
  "model3DCorrupted": 0,
  "totalGcodeFiles": 128,
  "gcodeHealthy": 126,
  "gcodeMissing": 2,
  "gcodeCorrupted": 0,
  "lastHealthyAuditDate": "2025-01-15T14:30:00Z",
  "overallHealthPercentage": 98.6
}
```

### Audit History
**`GET /api/fileconsistency/audits/history?pageSize=20`**

Recent audits in reverse chronological order (newest first):
- Audit date, type, file counts
- Summary message, issue flag
- Up to `pageSize` results (default 20)

```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "auditDate": "2025-01-15T14:30:00Z",
    "auditType": "Model3D",
    "filesChecked": 45,
    "healthyFiles": 44,
    "missingFiles": 1,
    "corruptedFiles": 0,
    "orphanedFiles": 0,
    "summaryMessage": "Model3D audit: Valid=44, Missing=1, Corrupted=0",
    "hasIssues": true
  }
]
```

### Files with Issues
**`GET /api/fileconsistency/files/issues`**

All files with health problems:
- Missing Model3D files
- Corrupted Model3D files
- Inaccessible Model3D files
- Same for GcodeFile

```json
{
  "totalIssues": 5,
  "missingFiles": 2,
  "corruptedFiles": 3,
  "inaccessibleFiles": 0,
  "issues": [
    {
      "fileId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "fileName": "broken-model.stl",
      "filePath": "/models/broken-model.stl",
      "fileType": "Model3D",
      "issueType": "Missing",
      "lastCheckDate": "2025-01-15T14:30:00Z"
    }
  ]
}
```

### Model3D Health Detail
**`GET /api/fileconsistency/model3d/{modelId}/health`**

Detailed health for specific Model3D file:
- File metadata (name, path, size, hash)
- Health status
- Last check date and verification result
- Returns 404 if not found

```json
{
  "fileId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "fileName": "model.stl",
  "filePath": "/models/model.stl",
  "fileType": "Model3D",
  "fileSize": 2048000,
  "fileHash": "abc123def456...",
  "healthStatus": "Healthy",
  "lastHealthCheckDate": "2025-01-15T14:30:00Z",
  "verificationDetails": "{\"verified\": true, \"hash_match\": true}",
  "uploadedDate": "2025-01-10T10:00:00Z"
}
```

### GcodeFile Health Detail
**`GET /api/fileconsistency/gcode/{gcodeId}/health`**

Same as Model3D endpoint for GcodeFile.

### Authorization
- All endpoints require `[Authorize]`
- Admin role recommended (enforced at policy level)

---

## Data Flow

```
┌─────────────────────────────────────┐
│  Audit Service (Hourly)             │
│  - Scan Model3D files               │
│  - Scan GcodeFile files             │
│  - Scan orphaned files              │
│  - Collect statistics               │
│  - Create FileHealthAudit record    │
└──────────────┬──────────────────────┘
               │
               ▼
┌─────────────────────────────────────┐
│  Database                           │
│  - FileHealthAudits table           │
│  - Model3D/GcodeFile health status  │
│  - Optimized indexes                │
└──────────────┬──────────────────────┘
               │
               ▼
┌─────────────────────────────────────┐
│  API Controller                     │
│  - Query DB for statistics          │
│  - Aggregate results                │
│  - Return formatted DTOs            │
│  - Enforce authorization            │
└──────────────┬──────────────────────┘
               │
               ▼
┌─────────────────────────────────────┐
│  Dashboard/Admin UI                 │
│  - Call /health/summary             │
│  - Call /files/issues               │
│  - Call /audits/history             │
│  - Display metrics & trends         │
└─────────────────────────────────────┘
```

---

## Configuration

**Storage Paths** (appsettings.json):
```json
{
  "ModelStorage": {
    "Path": "./models"
  },
  "GcodeStorage": {
    "Path": "./gcode-library"
  }
}
```

**Audit Schedule**:
- Interval: 1 hour (configurable)
- Startup delay: 5 minutes
- Continuous background operation
- Survives app restarts

---

## Files Delivered

### New Files Created
1. **`/src/api/Controllers/FileConsistencyController.cs`** (350+ lines)
   - 5 REST endpoints
   - Nested DTOs
   - Authorization enforcement
   - Helper methods for statistics

2. **`/src/tests/Farm.Web.Api.Tests/FileConsistencyIntegrationTests.cs`** (320+ lines)
   - 10+ test cases
   - API endpoint validation
   - Authorization testing
   - Edge case coverage

### Files Modified
1. **`/src/infra/Domain/Entities.cs`**
   - Added `FileHealthAudit` entity
   - Added `FileHealthStatus` enum
   - Added `FileAuditType` enum
   - Enhanced `Model3D` with health columns
   - Enhanced `GcodeFile` with health columns

2. **`/src/infra/Data/AppDbContext.cs`**
   - Added `FileHealthAudits` DbSet
   - Entity configuration with indexes
   - Enhanced `Model3D` configuration
   - Enhanced `GcodeFile` configuration

3. **`/src/api/Services/FileManagement/FileConsistencyAuditService.cs`**
   - Added `AuditResults` internal class
   - Updated `RunAuditAsync()` for persistence
   - Added `SaveAuditResultsAsync()` method
   - Modified audit methods to return detailed results
   - Added JSON serialization for file lists

---

## Compilation & Testing

### Compilation Status ✅
All files compile without errors:
- ✅ Entities.cs
- ✅ AppDbContext.cs
- ✅ FileConsistencyController.cs
- ✅ FileConsistencyAuditService.cs
- ✅ FileConsistencyIntegrationTests.cs

### Test Coverage ✅
Run tests:
```bash
cd ./src
dotnet test ./farm-web.sln -c Debug --filter "FileConsistencyIntegrationTests"
```

Test cases (10+):
- Health summary calculations
- Issue aggregation
- Audit history ordering
- File existence detection
- Hash mismatch detection
- Authorization enforcement
- Edge cases & percentage calculations

---

## Current Implementation Status

### ✅ Completed
1. **Layer 1**: Transaction-safe uploads and deletes
2. **Layer 2**: File integrity verification service
3. **Layer 3**: Hourly background auditing
4. **Database**: Persistent audit history with health status
5. **API**: 5 REST endpoints for admin monitoring
6. **Tests**: Comprehensive integration test suite
7. **Compilation**: All code compiles without errors

### 📋 Planned for Next Phase
1. **React Dashboard UI**
   - Health gauge visualization
   - File statistics charts
   - Audit timeline view
   - Issue management interface
   - Exposed in Admin menu

2. **Automated Remediation**
   - Delete orphaned files automatically
   - Quarantine missing files
   - Re-verify corrupted files

3. **Advanced Alerting**
   - Email admins on critical issues
   - Webhook notifications
   - In-app alerts

4. **Reporting & Trends**
   - Historical health trends
   - File lifecycle analysis
   - Storage forecasting

---

## Operational Runbook

### Daily Operations
```bash
# Monitor dashboard
curl -X GET http://localhost:5245/api/fileconsistency/health/summary

# Check for recent issues
curl -X GET http://localhost:5245/api/fileconsistency/audits/history?pageSize=5

# List problematic files
curl -X GET http://localhost:5245/api/fileconsistency/files/issues
```

### Incident Response

**If files are missing:**
```bash
# Get detailed list
curl -X GET http://localhost:5245/api/fileconsistency/files/issues
# Review audit history for when they disappeared
curl -X GET http://localhost:5245/api/fileconsistency/audits/history?pageSize=20
```

**If storage volume unmounted:**
1. Remount storage
2. Restart API (audit service will detect changes)
3. Run health check: `GET /health/summary`
4. Review findings and alert users if necessary

**If disk fills up:**
1. Identify large files
2. Clean old chunked uploads
3. Consider archival strategy

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────┐
│  File Upload/Delete Operations                      │
│  - Transaction-safe patterns                        │
│  - Temp file staging                                │
│  - Error recovery                                   │
└──────────────┬──────────────────────────────────────┘
               │
               ├─ FileSystem Operations ──► IFileSystem
               │                             (MoveFile, etc)
               │
               ├─ Integrity Verification ──► FileIntegrityService
               │  (before download)          (Verify, Compute, Check)
               │
               └─ Database Transaction ────► AppDbContext
                  (Atomic with file ops)     (SaveChangesAsync)

┌─────────────────────────────────────────────────────┐
│  Background Audit Service (Hourly)                  │
│  FileConsistencyAuditService                        │
│  - Scan Model3D directory                           │
│  - Scan GcodeFile directory                         │
│  - Find orphaned files                              │
│  - Collect results (AuditResults)                   │
│  - Persist to DB (FileHealthAudit)                  │
│  - Update health status on entities                 │
└──────────────┬──────────────────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────────────────┐
│  Database Tables                                    │
│  - FileHealthAudit (audit history)                  │
│  - Model3D (+ health columns)                       │
│  - GcodeFile (+ health columns)                     │
│  - Optimized indexes for queries                    │
└──────────────┬──────────────────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────────────────┐
│  REST API Controller                                │
│  FileConsistencyController                          │
│  - GET /api/fileconsistency/health/summary          │
│  - GET /api/fileconsistency/audits/history          │
│  - GET /api/fileconsistency/files/issues            │
│  - GET /api/fileconsistency/model3d/{id}/health     │
│  - GET /api/fileconsistency/gcode/{id}/health       │
│  - [Authorize] on all endpoints                     │
└──────────────┬──────────────────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────────────────┐
│  Admin Dashboard (Future Phase)                     │
│  - Health percentage gauge                          │
│  - File statistics                                  │
│  - Audit timeline                                   │
│  - Issue management                                 │
│  - Exposed in Admin menu                            │
└─────────────────────────────────────────────────────┘
```

---

## Summary

| Aspect | Status | Details |
|--------|--------|---------|
| **Upload Safety** | ✅ Complete | Temp file + atomic move pattern |
| **Delete Safety** | ✅ Complete | Disk-first deletion pattern |
| **Integrity Verification** | ✅ Complete | FileIntegrityService with 6 methods |
| **Background Auditing** | ✅ Complete | Hourly service with persistence |
| **Database Schema** | ✅ Complete | FileHealthAudit + health columns |
| **REST API** | ✅ Complete | 5 endpoints with auth |
| **Integration Tests** | ✅ Complete | 10+ test cases |
| **Compilation** | ✅ Complete | All code compiles |
| **React Dashboard UI** | 📋 Planned | Next phase - admin menu integration |

---

## Next Steps for React Dashboard

1. Create React component: `FileHealthDashboard.tsx`
2. Implement health summary visualization (gauge/percentage)
3. Add audit history timeline chart
4. Create issue list with filters
5. Add file health detail modal
6. Integrate with Admin menu navigation
7. Add real-time data refresh
8. Style with Tailwind CSS

---

## Related Documentation

- See `/docs/FILE_CONSISTENCY_STRATEGY.md` for detailed gap analysis and recommendations
- See inline code comments in FileConsistencyController for endpoint details
- See FileConsistencyIntegrationTests for usage examples
