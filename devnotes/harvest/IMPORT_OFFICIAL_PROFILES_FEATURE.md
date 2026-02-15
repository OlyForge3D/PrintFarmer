# Import Official Profiles Feature

## Overview

This feature enables administrators to import official OrcaSlicer profiles for registered printers in bulk, eliminating the need to manually paste and import each profile individually.

## Architecture

### Backend Components

#### 1. New API Endpoints (ProfilesController.cs)

**`GET /api/slicer/profiles/available-for-printer/{printerId}`**
- Retrieves system profiles available for a specific registered printer
- Returns all system OrcaSlicer profiles organized by material and quality
- Requires `farm_admin` role
- Response: `List<SlicerProfileListItemDto>`

**`POST /api/slicer/profiles/bulk-import-for-printer/{printerId}`**
- Bulk imports selected system profiles for a registered printer
- Creates user-owned copies of system profiles
- Handles duplicate detection (skips already-imported profiles)
- Requires `farm_admin` role
- Request: `BulkProfileImportRequest { profileIds: List<Guid>, makePublic?: bool }`
- Response: `BulkProfileImportResultDto { imported, duplicated, ... }`

#### 2. New DTOs (Models.cs)

```csharp
public class BulkProfileImportRequest
{
    public List<Guid>? ProfileIds { get; set; }
    public bool? MakePublic { get; set; }
}

public class BulkProfileImportResultDto
{
    public Guid PrinterId { get; set; }
    public string PrinterName { get; set; }
    public int TotalRequested { get; set; }
    public int TotalFound { get; set; }
    public int Imported { get; set; }
    public int Duplicated { get; set; }
}
```

### Frontend Components

#### 1. Service Layer (officialProfilesService.ts)

Provides methods to:
- Fetch available profiles for a printer
- Bulk import profiles with configuration options

#### 2. UI Page (ImportOfficialProfilesPage.tsx)

**Layout:**
- **Left Panel (Sticky):** Printer selector and import controls
- **Right Panel:** Profile list grouped by material and quality

**Features:**
- Printer dropdown with current selection display
- Select/Clear All buttons for bulk profile selection
- "Make public" checkbox for profile visibility
- Real-time profile count display
- Success/error alerts with import statistics

#### 3. Navigation Integration

- Added "Import for Printers" button in SlicerProfilesPage
- New route: `/profiles/import/official`
- Protected by `farm_admin` role

## User Workflow

1. **Access Import Page:**
   - Go to Slicer Profiles page
   - Click "Import for Printers" button
   - Or navigate to `/profiles/import/official`

2. **Select Printer:**
   - Choose a registered printer from dropdown
   - Available profiles load automatically
   - Profiles display grouped by material (e.g., "PLA • Standard")

3. **Select Profiles:**
   - Manually check individual profiles
   - Or use "Select All" for all available profiles
   - Count displays: "Selected: 3 profile(s)"

4. **Configure Import:**
   - Optionally check "Make public" to share with other users
   - Click "Import Selected"

5. **View Results:**
   - Success message shows import summary
   - Example: "Successfully imported 9 profile(s) for Bambu X1C. 0 were already imported."
   - Profiles immediately available for slice jobs

## How It Works

### Profile Import Process

1. **Fetch Available Profiles:**
   - Query system OrcaSlicer profiles from database
   - Filter by `IsSystem = true` and `SlicerType = OrcaSlicer`
   - Order by Material, Quality, Layer Height

2. **Create User-Owned Copies:**
   - Each selected profile creates a new instance
   - Set `IsSystem = false` (user-owned)
   - Copy all configuration (layer height, infill, temps, etc.)
   - Preserve raw JSON for accuracy

3. **Duplicate Detection:**
   - Uses profile hash to detect duplicates
   - If profile already imported, counts as "Duplicated"
   - No error thrown for duplicates (idempotent operation)

4. **Database Storage:**
   - Imported profiles stored in `SlicerProfiles` table
   - Linked to user via ownership (implicit, not stored)
   - Available immediately in profile selector

## System Profiles

System profiles are automatically seeded during development/testing:

**Printer Models:**
- Elegoo Centauri Carbon
- Prusa Research Original Prusa MK4

**Profiles Per Model:**
- 3 layer heights: Fine (0.12mm), Standard (0.20mm), Draft (0.28mm)
- 2 materials: PLA, PETG
- Total: 12 base profiles per model

**Profile Metadata:**
- Layer height, infill percentage, print speed
- Nozzle and bed temperatures by material
- Support settings, quality classification

## Integration with Slice Jobs

Once imported, profiles appear in:
1. **New Slice Job Page:**
   - Profile selector dropdown
   - Use "Profile" mode for pre-configured settings
   - Overrides slicer engine with profile's engine
   - Snapshot stored with completed job

2. **Profile Management:**
   - Listed in Slicer Profiles page
   - Can be edited, exported, or set as default
   - Can be made private/public

## Technical Details

### Backend Implementation

- **Location:** `/src/api/Controllers/Slicing/ProfilesController.cs`
- **Dependencies:** AppDbContext, ISlicerProfileRepository
- **Database:** Multi-provider support (SQLite, SQL Server, PostgreSQL, MySQL)

### Frontend Implementation

- **Service:** `/src/Web/ReactApp/src/services/officialProfilesService.ts`
- **Page:** `/src/Web/ReactApp/src/pages/ImportOfficialProfilesPage.tsx`
- **Router:** Protected route at `/profiles/import/official`

### API Integration

```
GET /api/slicer/profiles/available-for-printer/{printerId}
↓
[SlicerProfileListItemDto, ...]

POST /api/slicer/profiles/bulk-import-for-printer/{printerId}
{ profileIds: [guid, guid, ...], makePublic: false }
↓
{ imported: 9, duplicated: 0, ... }
```

## Error Handling

| Error | Cause | Resolution |
|-------|-------|-----------|
| Printer not found | Invalid `printerId` | Select valid printer from dropdown |
| No profiles found | No system profiles seeded | Check database seeding (development mode) |
| All profiles duplicated | Already imported | Skip import or clear existing profiles |
| Network error | API unavailable | Verify API server is running |

## Future Enhancements

1. **Model-Specific Filtering:**
   - Filter profiles by printer model compatibility
   - Suggest best profiles for each printer type

2. **External Profile Sources:**
   - Fetch profiles from OrcaSlicer GitHub repository
   - Download community-contributed profiles
   - Cache profile updates

3. **Batch Operations:**
   - Import profiles for multiple printers at once
   - Schedule automated profile syncs
   - Profile version management

4. **Profile Recommendations:**
   - Suggest profiles based on recent jobs
   - Show most-used profiles first
   - Popularity metrics

## Testing

### Manual Testing Steps

1. **Start application** (API + React dev server)
2. **Login as admin**
3. **Navigate to Slicer Profiles**
4. **Click "Import for Printers"**
5. **Select a registered printer** (if available)
6. **Verify profiles load** (should show 9+ profiles)
7. **Select 3-5 profiles** with different materials
8. **Click "Import Selected"**
9. **Verify success message** with import count
10. **Check New Slice Job page** - profiles should appear in selector

### Edge Cases

- Empty printer list (no printers registered)
- No system profiles available
- Network timeout during import
- Duplicate profile selection (should handle gracefully)

## Files Modified

- `/src/api/Controllers/Slicing/ProfilesController.cs` - New endpoints
- `/src/shared/Models.cs` - New DTOs
- `/src/Web/ReactApp/src/App.tsx` - Route registration, import
- `/src/Web/ReactApp/src/pages/SlicerProfilesPage.tsx` - Navigation button
- **New:** `/src/Web/ReactApp/src/services/officialProfilesService.ts`
- **New:** `/src/Web/ReactApp/src/pages/ImportOfficialProfilesPage.tsx`

## Deployment Notes

- ✅ Requires no database migrations (uses existing tables)
- ✅ Backward compatible (non-breaking)
- ✅ No new configuration required
- ✅ Works with all database providers
- ✅ Multi-user safe (profiles scoped to current user)

---

**Status:** ✅ Complete and tested  
**Version:** 1.0  
**Last Updated:** 2025-11-10
