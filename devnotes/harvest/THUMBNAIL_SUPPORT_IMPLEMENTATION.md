# Thumbnail Support Implementation for Harvest Discovery

## Overview
Added thumbnail display support to the harvest file discovery process, allowing users to visually identify G-code files before harvesting them to the library.

## Problem Solved
Users need to see visual previews (thumbnails) of G-code files during the harvest discovery process to make informed decisions about which files to harvest. Previously, only filename and metadata were shown, making it difficult to identify files visually.

## Implementation Details

### 1. Backend Changes

#### HarvestWorkerService.cs
**File:** `/src/api/Services/HarvestWorkerService.cs`

**Change 1: Populate Thumbnail URL in Discovered File**
```csharp
// Create discovered file record
HarvestDiscoveredFile discoveredFile = new()
{
    // ... other fields ...
    
    // Convert thumbnail relative path to full URL for Moonraker
    ThumbnailUrl = !string.IsNullOrEmpty(job.ThumbnailRelativePath) 
        ? $"{job.ServerUrl}/server/files/gcodes/{job.ThumbnailRelativePath}" 
        : null
};
```

- Constructs full thumbnail URL from Moonraker's relative path
- Format: `{serverUrl}/server/files/gcodes/{relativePath}`
- Example: `http://10.0.0.80/server/files/gcodes/.thumbs/model-32x32.png`

**Change 2: Include Thumbnail in SignalR Event**
```csharp
await _harvestHub.Clients.Group($"harvest-{job.OperationId}")
    .SendAsync("HarvestFileDiscovered", new
    {
        operationId = job.OperationId,
        fileId = discoveredFile.Id,
        fileName = discoveredFile.FileName,
        filePath = discoveredFile.FilePath,
        fileSize = discoveredFile.Size,
        status = discoveredFile.AlreadyInLibrary ? "skipped" : "added",
        thumbnailUrl = discoveredFile.ThumbnailUrl,  // ← Added
        extractedSlicer = discoveredFile.ExtractedSlicerName,
        extractedMaterial = discoveredFile.ExtractedMaterial
    }, ct);
```

#### GcodeHarvestService.cs
**File:** `/src/api/Services/GcodeHarvestService.cs`

**Change: Include Thumbnail URL in DTO Mapping**
```csharp
private static DiscoveredGcodeFileDto MapToDto(HarvestDiscoveredFile file)
{
    return new DiscoveredGcodeFileDto(
        file.Id,
        file.HarvestOperationId,
        file.FilePath,
        file.FileName,
        file.Size,
        file.ModifiedAt,
        file.FileHash,
        false,
        file.AlreadyInLibrary,
        null,
        file.Status == HarvestFileStatus.Failed,
        file.Error,
        file.ThumbnailUrl,  // ← Added
        file.ExtractedSlicerName,
        // ... other fields
    );
}
```

#### Models.cs (Shared DTOs)
**File:** `/src/shared/Models.cs`

**Change: Add ThumbnailUrl to DiscoveredGcodeFileDto**
```csharp
public record DiscoveredGcodeFileDto(
    Guid Id,
    Guid HarvestOperationId,
    string PrinterPath,
    string FileName,
    long FileSizeBytes,
    DateTime? ModifiedAt = null,
    string? FileHash = null,
    bool IsSelected = false,
    bool AlreadyInLibrary = false,
    Guid? ExistingLibraryFileId = null,
    bool ProcessingFailed = false,
    string? ErrorMessage = null,
    string? ThumbnailUrl = null,  // ← Added
    // ... other fields
);
```

### 2. Frontend Changes

#### SignalR Event Type
**File:** `/src/Web/ReactApp/src/services/harvest-signalr.ts`

**Change: Add Thumbnail Fields to Event Type**
```typescript
export type HarvestFileDiscoveredEvent = {
  operationId: string;
  fileId: string;
  fileName: string;
  filePath: string;
  fileSize: number;
  status?: string;
  error?: string;
  thumbnailUrl?: string;        // ← Added
  extractedSlicer?: string;     // ← Added
  extractedMaterial?: string;   // ← Added
};
```

#### TypeScript API Types
**File:** `/src/Web/ReactApp/src/types/api.ts`

The `DiscoveredGcodeFileDto` interface already had `thumbnailUrl?: string` field, so no changes were needed.

#### UI Display
**File:** `/src/Web/ReactApp/src/components/harvest/IndexedFilesList.tsx`

The UI component already had thumbnail display logic (lines 219-226):
```tsx
<td className="p-2 border-b border-pf-border font-mono text-pf-primary flex items-center gap-2">
  {file.thumbnailUrl && (
    <img
      src={file.thumbnailUrl}
      alt={file.fileName + ' thumbnail'}
      className="w-8 h-8 min-w-[32px] min-h-[32px] rounded shadow border border-pf-border bg-pf-surface object-cover"
      loading="lazy"
    />
  )}
  <span>{file.fileName}</span>
</td>
```

No changes were needed - it automatically displays thumbnails when available!

## Data Flow

### Complete Thumbnail Flow:
1. **Discovery Phase** (GcodeHarvestService):
   - Calls `GetFileMetadataAsync()` on Moonraker
   - Receives `GCodeMetadata` with `Thumbnails[]` array
   - Extracts largest thumbnail's `RelativePath`
   - Stores in `PrinterFileInfo.ThumbnailRelativePath`

2. **Job Creation**:
   - Copies thumbnail path to `HarvestFileJob.ThumbnailRelativePath`
   - Enqueues job to worker

3. **Worker Processing** (HarvestWorkerService):
   - Receives job with thumbnail relative path
   - Constructs full URL: `{serverUrl}/server/files/gcodes/{relativePath}`
   - Stores in `HarvestDiscoveredFile.ThumbnailUrl`
   - Saves to database

4. **Real-time Update**:
   - Emits SignalR event with `thumbnailUrl`
   - React UI receives event
   - Updates file list state

5. **UI Display**:
   - IndexedFilesList component renders thumbnail image
   - 32x32px with lazy loading
   - Displayed next to filename

## Thumbnail URL Format

### Moonraker Thumbnail Path Structure:
- **Relative Path:** `.thumbs/filename-32x32.png`
- **Full URL:** `http://{printer-ip}/server/files/gcodes/.thumbs/filename-32x32.png`

### Example:
```
Original File:     gcodes/model.gcode
Thumbnail Path:    .thumbs/model-32x32.png
Full URL:         http://10.0.0.80/server/files/gcodes/.thumbs/model-32x32.png
```

## Benefits

1. **Visual Identification**: Users can instantly recognize files by their visual appearance
2. **Informed Decisions**: Thumbnails help users decide which files to harvest
3. **Better UX**: Visual feedback makes the discovery process more intuitive
4. **No Performance Impact**: Thumbnails are already generated by the slicer and served by Moonraker
5. **Lazy Loading**: Images load only when scrolled into view

## Testing Checklist

- [x] Build succeeds with no errors
- [x] Server starts without issues
- [ ] Start harvest operation on Moonraker printer
- [ ] Verify files appear in UI with thumbnails
- [ ] Check thumbnail URLs are correct format
- [ ] Verify lazy loading works (inspect network tab)
- [ ] Test with files that have no thumbnails
- [ ] Test with different thumbnail sizes

## Technical Notes

### Database Schema
The `HarvestDiscoveredFiles` table already had a `ThumbnailUrl` column (varchar), so no migration was needed.

### Entity Model
The `HarvestDiscoveredFile` entity already had the property:
```csharp
public string? ThumbnailUrl { get; set; }
```

### Error Handling
- If `job.ThumbnailRelativePath` is null/empty, `ThumbnailUrl` is set to null
- UI gracefully handles null thumbnails (doesn't display image)
- No exceptions thrown for missing thumbnails

## Related Issues Fixed

While implementing thumbnails, we also fixed a critical DateTime UTC issue:

**Problem:** `job.ModifiedAt` had `DateTimeKind.Unspecified`, causing PostgreSQL error:
```
System.ArgumentException: Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time zone', only UTC is supported.
```

**Solution:** Convert to UTC using `DateTime.SpecifyKind()`:
```csharp
ModifiedAt = job.ModifiedAt.HasValue 
    ? DateTime.SpecifyKind(job.ModifiedAt.Value, DateTimeKind.Utc) 
    : null
```

This fixed the database save failures that were preventing discovered files from being persisted.

## Files Modified

### Backend:
1. `/src/api/Services/HarvestWorkerService.cs` - Added thumbnail URL construction and SignalR event field
2. `/src/api/Services/GcodeHarvestService.cs` - Added thumbnail URL to DTO mapping
3. `/src/shared/Models.cs` - Added ThumbnailUrl to DiscoveredGcodeFileDto

### Frontend:
4. `/src/Web/ReactApp/src/services/harvest-signalr.ts` - Added thumbnail fields to event type
5. `/src/Web/ReactApp/src/types/api.ts` - Already had thumbnailUrl (no change needed)
6. `/src/Web/ReactApp/src/components/harvest/IndexedFilesList.tsx` - Already had display logic (no change needed)

## Performance Considerations

- **No Additional API Calls**: Thumbnail paths come from existing `GetFileMetadataAsync()` call
- **Lazy Loading**: Images load only when visible (native browser feature)
- **Small Images**: Thumbnails are typically 32x32 or 64x64 pixels (~1-5KB each)
- **Cached by Browser**: Once loaded, thumbnails are cached for subsequent views
- **No Server Processing**: Moonraker serves thumbnails directly, no backend processing

## Future Enhancements

1. **Multiple Thumbnail Sizes**: Allow users to choose thumbnail size (32x32, 64x64, 256x256)
2. **Thumbnail Preview Modal**: Click thumbnail to view larger preview
3. **Thumbnail Caching**: Pre-cache thumbnails during discovery
4. **PrusaLink Support**: Add thumbnail support for PrusaLink printers
5. **Fallback Images**: Show default placeholder for files without thumbnails
