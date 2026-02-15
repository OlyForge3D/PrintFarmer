# Harvest Metadata Optimization Plan

## Problem Statement

Currently, the harvest system downloads every G-code file over the network to extract metadata (slicer info, print time, thumbnails, etc.). For Moonraker backends, this is unnecessary because the API already provides this metadata.

## Current Flow (Inefficient)

```
GcodeHarvestService.StartHarvest()
  ↓
1. Discovery Phase:
   - List files from printer API (basic info only: path, size, modified)
   - Enqueue HarvestFileJob for each file
  ↓
2. Processing Phase (HarvestWorkerService):
   - Dequeue job
   - Download entire file over network ❌ INEFFICIENT
   - Extract metadata from file content
   - Calculate file hash
   - Save to HarvestDiscoveredFiles table

3. User reviews discovered files and confirms which to harvest
4. Confirmed files moved to GcodeFiles table
```

**Problems:**
- Downloads every file (could be 100s of MB) just to read metadata
- Slow discovery phase
- High network bandwidth usage
- Unnecessary strain on printer's network/storage

## Optimized Flow (Recommended)

```
GcodeHarvestService.StartHarvest()
  ↓
1. Discovery Phase (BACKEND-AWARE):
   
   For Moonraker:
   - List files: GetDirectoryAsync(extended=true)
   - For each file: GetFileMetadataAsync() ✅ NO DOWNLOAD
   - Extract: slicer, version, print time, material, thumbnails
   - Enqueue minimal job info (no file content needed yet)
  ↓
2. Processing Phase (HarvestWorkerService):
   - Dequeue job
   - Save metadata to HarvestDiscoveredFiles (already have it)
   - NO file download needed ✅ FAST
  ↓
3. User reviews discovered files with full metadata
4. User confirms which files to harvest
  ↓
5. Harvest Phase (NEW):
   - Download ONLY confirmed files
   - Calculate hash
   - Move to GcodeFiles library
```

## API Availability

### Moonraker ✅ Available
- **Method:** `IMoonrakerClient.GetFileMetadataAsync(serverUrl, filename)`
- **Returns:** `GCodeMetadata` with:
  - `Slicer` (e.g., "PrusaSlicer")
  - `SlicerVersion` (e.g., "2.6.1")
  - `EstimatedTime` (seconds)
  - `FilamentTotal` (mm)
  - `LayerHeight` (mm)
  - `ObjectHeight` (mm)
  - `Thumbnails[]` (with paths)
  - `FirstLayerBedTemp`, `FirstLayerExtrTemp`
  - etc.

### PrusaLink ❓ Research Needed
- Need to verify if PrusaLink API provides metadata endpoint
- Fallback: Download file if no API available

### SDCP ❓ Research Needed  
- Need to verify protocol capabilities
- Fallback: Download file if no API available

## Implementation Changes

### 1. Enhance PrinterFileInfo (GcodeHarvestService.cs)

```csharp
private sealed class PrinterFileInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime? ModifiedAt { get; set; }
    
    // NEW: Metadata from API (populated during discovery)
    public GCodeMetadata? Metadata { get; set; }
    public string? ThumbnailRelativePath { get; set; } // Largest thumbnail
}
```

### 2. Update GetMoonrakerFilesAsync (GcodeHarvestService.cs)

```csharp
private async Task CollectFilesRecursivelyWithRetryAsync(...)
{
    foreach (MoonrakerFileInfo file in directory.Files)
    {
        PrinterFileInfo printerFileInfo = new()
        {
            Name = Path.GetFileName(file.Path),
            Path = file.Path,
            Size = file.Size,
            ModifiedAt = DateTimeOffset.FromUnixTimeSeconds((long)file.Modified).DateTime,
            
            // NEW: Fetch metadata from API instead of downloading file
            Metadata = await client.GetFileMetadataAsync(serverUrl, file.Path, ct)
        };
        
        // Extract largest thumbnail path if available
        if (printerFileInfo.Metadata?.Thumbnails?.Length > 0)
        {
            var largest = printerFileInfo.Metadata.Thumbnails
                .OrderByDescending(t => t.Width * t.Height)
                .First();
            printerFileInfo.ThumbnailRelativePath = largest.RelativePath;
        }
        
        files.Add(printerFileInfo);
    }
}
```

### 3. Update DiscoverAndQueueFilesAsync (GcodeHarvestService.cs)

Pass metadata to HarvestFileJob:

```csharp
HarvestFileJob job = new()
{
    OperationId = operation.Id,
    PrinterId = printer.Id,
    FileName = file.Name,
    FilePath = file.Path,
    FileSize = file.Size,
    ModifiedAt = file.ModifiedAt,
    
    // NEW: Pass metadata from discovery phase
    SlicerName = file.Metadata?.Slicer,
    SlicerVersion = file.Metadata?.SlicerVersion,
    EstimatedTimeSeconds = file.Metadata?.EstimatedTime,
    FilamentLengthMm = file.Metadata?.FilamentTotal,
    LayerHeight = file.Metadata?.LayerHeight,
    ThumbnailPath = file.ThumbnailRelativePath,
    // ... other metadata fields
};

await _harvestQueue.EnqueueAsync(job, ct);
```

### 4. Enhance HarvestFileJob Model (Services/Models/HarvestFileJob.cs)

```csharp
public class HarvestFileJob
{
    public Guid OperationId { get; set; }
    public Guid PrinterId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime? ModifiedAt { get; set; }
    
    // NEW: Metadata from API (no file download needed)
    public string? SlicerName { get; set; }
    public string? SlicerVersion { get; set; }
    public int? EstimatedTimeSeconds { get; set; }
    public double? FilamentLengthMm { get; set; }
    public double? LayerHeight { get; set; }
    public string? ThumbnailPath { get; set; }
    public string? Material { get; set; }
    // ... other fields from GCodeMetadata
}
```

### 5. Update HarvestWorkerService.ProcessFileJobAsync

```csharp
private async Task ProcessFileJobAsync(HarvestFileJob job, CancellationToken ct)
{
    // Create discovered file record
    HarvestDiscoveredFile discoveredFile = new()
    {
        Id = Guid.NewGuid(),
        HarvestOperationId = job.OperationId,
        FilePath = job.FilePath,
        FileName = job.FileName,
        Size = job.FileSize,
        ModifiedAt = job.ModifiedAt,
        
        // NEW: Use metadata from job (no download needed!)
        ExtractedSlicerName = job.SlicerName,
        ExtractedSlicerVersion = job.SlicerVersion,
        ExtractedPrintTime = job.EstimatedTimeSeconds / 60, // Convert to minutes
        ExtractedFilamentLength = job.FilamentLengthMm,
        ExtractedMaterial = job.Material,
        // ... other metadata fields
    };
    
    // Only download file if we need to calculate hash for duplicate detection
    if (operation.DuplicateHandling != "skip" || !job.FileHash)
    {
        using MemoryStream? fileContent = await DownloadFileAsync(...);
        if (fileContent != null)
        {
            discoveredFile.FileHash = await CalculateFileHashAsync(fileContent);
            // Check for duplicates...
        }
    }
    
    // Save discovered file - NO DOWNLOAD NEEDED!
    db.HarvestDiscoveredFiles.Add(discoveredFile);
    await db.SaveChangesAsync(ct);
}
```

## Performance Impact

**Before Optimization:**
- 100 files × 5MB avg = 500MB network transfer during discovery
- Time: ~10-30 minutes depending on network speed
- Printer network load: High

**After Optimization:**
- 100 files × ~5KB metadata = 500KB network transfer
- Time: ~30-60 seconds  
- Printer network load: Minimal
- **~99% reduction in network usage** ✅
- **~95% reduction in discovery time** ✅

## Migration Path

1. **Phase 1:** Add metadata fields to models (non-breaking)
2. **Phase 2:** Update Moonraker discovery to use API metadata
3. **Phase 3:** Update worker to use pre-fetched metadata
4. **Phase 4:** Test with real printers
5. **Phase 5:** Add similar optimization for PrusaLink (if API supports)

## Backwards Compatibility

- Keep fallback to file download if API metadata unavailable
- Support mixed scenarios (some files with API metadata, some without)
- Gracefully handle API errors

## Additional Benefits

1. **Thumbnail URLs:** Can provide thumbnail URLs immediately without downloading files
2. **Better Filtering:** User can filter by slicer, material, print time before downloading
3. **Faster UI:** Discovered files display instantly with full metadata
4. **Network Reliability:** Less chance of timeout on slow networks

## Future Enhancements

1. **Lazy Hash Calculation:** Only calculate hash when user confirms harvest
2. **Batch Metadata Requests:** Request multiple file metadata in parallel
3. **Metadata Caching:** Cache metadata to avoid re-fetching on subsequent harvests
4. **Progressive Discovery:** Stream discovered files to UI as they're found
