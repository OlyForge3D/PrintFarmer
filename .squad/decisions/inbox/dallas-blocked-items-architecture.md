# Architecture & Implementation Plans for 5 Blocked/Deferred Items

**Author:** Dallas (Lead)  
**Date:** 2025-01-21  
**Status:** Proposed

---

## ITEM 1: Camera Control (Enable/Disable)

### Problem Statement
`EnableCameraAsync` and `DisableCameraAsync` in `PrintersService.cs` are stubs returning false. The `ISupportsCamera` interface only has read methods (`GetCameraStreamUrlAsync`, `GetCameraSnapshotUrlAsync`) but no control methods. Backend plugins (Moonraker, PrusaLink, OctoPrint, SDCP, FlashForge) need to implement camera enable/disable if their APIs support it.

**Why Blocked:** Missing interface contract and backend implementations.

### Proposed Architecture

**1. Extend `ISupportsCamera` interface:**
```csharp
// src/infra/Services/Printers/IBackendClientCapabilities.cs

public interface ISupportsCamera
{
    // Existing methods
    Task<string?> GetCameraStreamUrlAsync(...);
    Task<string?> GetCameraSnapshotUrlAsync(...);
    
    // NEW: Camera control methods
    /// <summary>
    /// Enables the camera feed on the printer if supported by the backend.
    /// </summary>
    Task<bool> EnableCameraAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);
    
    /// <summary>
    /// Disables the camera feed on the printer if supported by the backend.
    /// </summary>
    Task<bool> DisableCameraAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);
    
    /// <summary>
    /// Checks if the camera is currently enabled.
    /// </summary>
    Task<bool> IsCameraEnabledAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default);
}
```

**2. Backend Plugin Implementations:**
- **Moonraker:** Check if Moonraker API supports camera control (likely through power device API or crowsnest service control)
- **PrusaLink:** Check if PrusaLink has camera control endpoints
- **OctoPrint:** May have plugin-based camera control (HAProxy, etc.)
- **SDCP/FlashForge:** Likely no camera control support (return false gracefully)

**3. Update `PrintersService.cs`:**
```csharp
public async Task<bool> EnableCameraAsync(Guid id, CancellationToken ct)
{
    Printer? p = await FindByIdAsync(id, ct).ConfigureAwait(false);
    if (p == null) return false;

    PrinterBackend backend = p.Backend;
    if (_capabilityFactory.TryGetCameraClientTyped(backend, out ISupportsCamera? cameraClient) && cameraClient != null)
    {
        return await cameraClient.EnableCameraAsync(p.ServerUrl, p.Credential, ct).ConfigureAwait(false);
    }

    return false; // Backend doesn't support camera control
}
```

**4. API Endpoints (if not already present):**
Check `CamerasController.cs` for existing endpoints and add:
- `POST /api/printers/{id}/camera/enable`
- `POST /api/printers/{id}/camera/disable`
- `GET /api/printers/{id}/camera/status`

### Implementation Plan

**Files to Create/Modify:**
1. `src/infra/Services/Printers/IBackendClientCapabilities.cs` — Add 3 methods to `ISupportsCamera`
2. `src/backends/Farm.Backend.Plugin.Moonraker/MoonrakerClient.cs` — Implement camera control methods
3. `src/backends/Farm.Backend.Plugin.PrusaLink/PrusaLinkClient.cs` — Implement camera control methods
4. `src/backends/Farm.Backend.Plugin.OctoPrint/OctoPrintClient.cs` — Implement camera control methods
5. `src/backends/Farm.Backend.Plugin.Sdcp/SdcpClient.cs` — Return false (no support)
6. `src/backends/Farm.Backend.Plugin.FlashForge/FlashForgeClient.cs` — Return false (no support)
7. `src/infra/Services/Printers/PrintersService.cs` — Implement `EnableCameraAsync` and `DisableCameraAsync`
8. `src/api/Controllers/CamerasController.cs` — Add enable/disable endpoints if missing

**Ordered Steps:**
1. Research each backend API documentation for camera control capabilities
2. Update `ISupportsCamera` interface with 3 new methods
3. Implement methods in Moonraker plugin first (most likely to have support)
4. Implement in PrusaLink plugin
5. Implement in OctoPrint plugin
6. Stub out in SDCP and FlashForge (return false)
7. Update `PrintersService.cs` to delegate to capability interface
8. Add API endpoints in `CamerasController.cs` if needed
9. Write integration tests for each backend
10. Update frontend UI to show enable/disable buttons when capability is available

### Dependencies
- Backend API research must complete first
- Interface changes must be applied before plugin implementations

### Complexity: **M (Medium)**
- Interface design: straightforward extension
- Backend research: 2-3 APIs to investigate
- Plugin implementations: ~6 files × 30-50 lines each
- Testing: Integration tests for each backend

### Recommended Implementation: **Taylor (Backend)**
- Backend plugin expert
- Familiar with capability pattern and backend API integration
- Can coordinate with frontend team for UI updates

---

## ITEM 2: Slicer Artifact Uploads (Thumbnails, Metadata, Logs)

### Problem Statement
`HttpJobPollerService.cs:340` uploads only the G-code file to `/api/artifacts`. `SlicingResult.Metadata` is a `Dictionary<string, string>` with no defined format for thumbnails, logs, or metadata files. Need conventions for:
- Metadata key naming (e.g., `thumbnail_small`, `thumbnail_large`, `log_file`, `slicer_config`)
- Multi-artifact upload support
- Artifact storage and retrieval

**Why Blocked:** No schema/convention for metadata, no multi-artifact upload logic.

### Proposed Architecture

**1. Define Metadata Key Conventions:**
```csharp
// src/slicer/Farm.Slicer.Module/Models/SlicingResult.cs or new Conventions.cs

public static class SlicingArtifactKeys
{
    // Thumbnail keys (base64-encoded PNG data or file paths)
    public const string ThumbnailSmall = "thumbnail_small";   // 32x32 or 64x64
    public const string ThumbnailMedium = "thumbnail_medium"; // 220x124 (PrusaSlicer standard)
    public const string ThumbnailLarge = "thumbnail_large";   // 400x300

    // Metadata files
    public const string SlicerConfig = "slicer_config";       // INI/JSON config file path
    public const string SlicerLog = "slicer_log";             // Slicer stdout/stderr log
    public const string SlicerVersion = "slicer_version";     // e.g., "OrcaSlicer 2.3.1"
    public const string SlicerEngine = "slicer_engine";       // e.g., "OrcaSlicer"

    // Print estimates (already in SlicingResult but can be in metadata too)
    public const string EstimatedPrintTime = "estimated_print_time_seconds";
    public const string EstimatedFilamentUsage = "estimated_filament_usage_grams";
    public const string LayerCount = "layer_count";
}
```

**2. Artifact Kind Enum:**
```csharp
// src/api or src/infra/Domain
public enum ArtifactKind
{
    Gcode,          // Primary G-code file
    ThumbnailSmall, // Small preview thumbnail
    ThumbnailMedium,
    ThumbnailLarge,
    SlicerConfig,   // Slicer configuration file
    SlicerLog,      // Slicer execution log
    Metadata        // Generic metadata file
}
```

**3. Multi-Artifact Upload Logic:**
```csharp
// src/worker-shared/HttpJobPollerService.cs (around line 340)

private async Task<List<Guid>> UploadArtifactsAsync(...)
{
    List<Guid> artifactIds = [];

    // 1. Upload primary G-code file (existing logic)
    Guid gcodeArtifactId = await UploadSingleArtifactAsync(
        gcodeFilePath, 
        job.Id, 
        "gcode", 
        Path.GetFileName(gcodeFilePath), 
        ct
    );
    artifactIds.Add(gcodeArtifactId);

    // 2. Upload thumbnails if present in metadata
    await UploadThumbnailIfPresent(result.Metadata, SlicingArtifactKeys.ThumbnailSmall, "thumbnail_small", job.Id, artifactIds, ct);
    await UploadThumbnailIfPresent(result.Metadata, SlicingArtifactKeys.ThumbnailMedium, "thumbnail_medium", job.Id, artifactIds, ct);
    await UploadThumbnailIfPresent(result.Metadata, SlicingArtifactKeys.ThumbnailLarge, "thumbnail_large", job.Id, artifactIds, ct);

    // 3. Upload slicer config if present
    await UploadFileIfPresent(result.Metadata, SlicingArtifactKeys.SlicerConfig, "slicer_config", job.Id, artifactIds, ct);

    // 4. Upload slicer log if present
    await UploadFileIfPresent(result.Metadata, SlicingArtifactKeys.SlicerLog, "slicer_log", job.Id, artifactIds, ct);

    return artifactIds;
}

private async Task UploadThumbnailIfPresent(...)
{
    if (!metadata.TryGetValue(key, out string? value) || string.IsNullOrEmpty(value))
        return;

    // Value could be:
    // 1. File path: "/tmp/thumbnail_small.png"
    // 2. Base64-encoded PNG: "data:image/png;base64,iVBORw0KG..."
    
    byte[] imageBytes;
    string fileName = $"{kind}.png";

    if (value.StartsWith("data:image/"))
    {
        // Extract base64 data and decode
        string base64Data = value.Split(',')[1];
        imageBytes = Convert.FromBase64String(base64Data);
    }
    else if (File.Exists(value))
    {
        imageBytes = await File.ReadAllBytesAsync(value, ct);
        fileName = Path.GetFileName(value);
    }
    else
    {
        _logger.LogWarning("Thumbnail {Key} value is neither a file nor base64 data: {Value}", key, value);
        return;
    }

    Guid artifactId = await UploadSingleArtifactAsync(imageBytes, jobId, kind, fileName, ct);
    artifactIds.Add(artifactId);
}
```

**4. Slicer Workers Must Populate Metadata:**
OrcaSlicer and PrusaSlicer workers must extract thumbnails from G-code comments or generate them and add to `SlicingResult.Metadata` dictionary.

**5. API Artifact Retrieval:**
Update `AssetsController.cs` or `ArtifactsController.cs` to support:
- `GET /api/artifacts/{id}` — Download artifact by ID
- `GET /api/artifacts/job/{jobId}?kind=thumbnail_small` — Get specific artifact by kind

### Implementation Plan

**Files to Create/Modify:**
1. `src/slicer/Farm.Slicer.Module/Models/SlicingArtifactKeys.cs` (new) — Define metadata key constants
2. `src/api/Domain/ArtifactKind.cs` (new or extend existing) — Enum for artifact types
3. `src/worker-shared/HttpJobPollerService.cs` — Implement multi-artifact upload logic (~100 lines)
4. `src/Slicers/Farm.Slicers.OrcaSlicer.*/` — Update worker to extract/generate thumbnails and populate metadata
5. `src/Slicers/Farm.Slicers.PrusaSlicer.*/` — Same for PrusaSlicer worker (if exists)
6. `src/api/Controllers/AssetsController.cs` or `ArtifactsController.cs` — Add query endpoints
7. Database migration for artifact metadata if needed (check existing schema)

**Ordered Steps:**
1. Define `SlicingArtifactKeys` constants in slicer module
2. Define or extend `ArtifactKind` enum
3. Implement helper methods in `HttpJobPollerService` for multi-artifact upload
4. Update OrcaSlicer worker to extract thumbnails from G-code and populate metadata
5. Update PrusaSlicer worker similarly
6. Test end-to-end: slice → upload → retrieve artifacts
7. Update API endpoints for artifact retrieval by kind
8. Update frontend to display thumbnails and download logs/configs

### Dependencies
- Artifact storage infrastructure must support multiple artifacts per job
- G-code parsing logic for thumbnail extraction must be implemented

### Complexity: **L (Large)**
- Metadata schema design: straightforward but requires coordination
- Multi-artifact upload logic: ~100-150 lines, not complex
- Slicer worker updates: Requires G-code parsing (thumbnails embedded in comments)
- Testing: End-to-end slicer → artifact storage → retrieval

### Recommended Implementation: **Taylor (Backend) + Morgan (Slicer)**
- Taylor: API and upload logic, artifact storage
- Morgan: Slicer worker updates, G-code parsing for thumbnails

---

## ITEM 3: OpenAPI/Swagger Migration to ASP.NET Core 10

### Problem Statement
`ExampleSchemaFilter.cs` has 19 TODOs with commented-out OpenAPI example code. All methods return `null`. Need to migrate from Swashbuckle (legacy) to ASP.NET Core 10's native OpenAPI support (`Microsoft.AspNetCore.OpenApi`).

**Why Deferred:** Major refactor, not blocking functionality.

### Proposed Architecture

**1. ASP.NET Core 10 Native OpenAPI:**
ASP.NET Core 10 has built-in OpenAPI support without Swashbuckle. Use `builder.Services.AddOpenApi()` and document transformers.

**2. Migration Strategy:**
- Remove Swashbuckle NuGet packages
- Add `Microsoft.AspNetCore.OpenApi` if not already present
- Replace `ExampleSchemaFilter` with OpenAPI document/operation transformers
- Use `WithOpenApi()` fluent API on endpoint definitions

**3. Example Schema via Transformers:**
```csharp
// src/api/Program.cs

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        // Add examples to schema definitions
        document.Components ??= new OpenApiComponents();
        
        // Example for CreatePrinterFromDiscoveryDto
        document.Components.Schemas["CreatePrinterFromDiscoveryDto"] = new OpenApiSchema
        {
            // ... schema definition
            Example = new OpenApiObject
            {
                ["name"] = new OpenApiString("Voron Trident #1"),
                ["serverUrl"] = new OpenApiString("http://voron1.local"),
                ["backend"] = new OpenApiString("Moonraker"),
                // ... more fields
            }
        };
        
        return Task.CompletedTask;
    });
    
    options.AddOperationTransformer((operation, context, cancellationToken) =>
    {
        // Add examples to specific endpoints
        if (context.Description.RelativePath == "/api/printers" && context.Description.HttpMethod == "POST")
        {
            operation.RequestBody.Content["application/json"].Examples = new Dictionary<string, OpenApiExample>
            {
                ["example1"] = new OpenApiExample
                {
                    Summary = "Moonraker printer",
                    Value = new OpenApiObject { /* ... */ }
                }
            };
        }
        
        return Task.CompletedTask;
    });
});
```

**4. Endpoint-Level Examples:**
Use `WithOpenApi()` on minimal API endpoints:
```csharp
app.MapPost("/api/slice", async (SliceRequest req, ISlicerService slicer) =>
{
    // ... implementation
})
.WithOpenApi(op =>
{
    op.Summary = "Submit a slicing job";
    op.Description = "Queues a 3D model for slicing with the specified profile";
    op.RequestBody.Content["application/json"].Example = new OpenApiObject
    {
        ["modelFileUrl"] = new OpenApiString("http://..."),
        // ...
    };
    return op;
});
```

**5. Delete Obsolete Code:**
Remove `src/api/Infrastructure/Swagger/ExampleSchemaFilter.cs` entirely once migration is complete.

### Implementation Plan

**Files to Create/Modify:**
1. `src/api/Program.cs` — Replace Swashbuckle registration with `AddOpenApi()`, add transformers
2. `src/api/Farm.Web.Api.csproj` — Remove Swashbuckle packages, add `Microsoft.AspNetCore.OpenApi`
3. Delete `src/api/Infrastructure/Swagger/ExampleSchemaFilter.cs`
4. Update all controller/minimal API endpoints with `WithOpenApi()` fluent API (if minimal APIs used)
5. Test Swagger UI at `/swagger` to ensure examples render correctly

**Ordered Steps:**
1. Audit current Swashbuckle usage in `Program.cs`
2. Add `Microsoft.AspNetCore.OpenApi` NuGet package
3. Replace Swashbuckle services with `AddOpenApi()`
4. Implement document/operation transformers for high-value DTOs (printers, jobs, gcode files)
5. Test Swagger UI for each major endpoint
6. Remove Swashbuckle packages and obsolete code
7. Update documentation to reflect new OpenAPI generation

### Dependencies
- None — standalone refactor

### Complexity: **M (Medium)**
- Straightforward API swap
- Transformer logic is repetitive but simple
- Risk: Regression if examples aren't tested thoroughly

### Recommended Implementation: **Jordan (Full-Stack) or Taylor (Backend)**
- Jordan: Familiar with both API and frontend Swagger UI usage
- Taylor: Backend lead, can coordinate with frontend for validation

---

## ITEM 4: Tag Support for Print Jobs (Phase 3D)

### Problem Statement
`PrintJobManagementService.cs:1817` logs "not yet implemented" when tags are included in job updates. Print jobs need tagging support for organization and filtering. The `Tag` entity exists, but there's no junction table, repository methods, or API endpoints.

**Why Deferred:** Phase 3D feature, not critical for MVP.

### Proposed Architecture

**1. Junction Table:**
Create `PrintJobTag` entity:
```csharp
// src/infra/Domain/PrintJobTag.cs

public class PrintJobTag
{
    public Guid PrintJobId { get; set; }
    public PrintJob PrintJob { get; set; } = null!;
    
    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;
    
    public DateTime CreatedAt { get; set; }
}

// In ApplicationDbContext.cs:
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<PrintJobTag>()
        .HasKey(pjt => new { pjt.PrintJobId, pjt.TagId });
    
    modelBuilder.Entity<PrintJobTag>()
        .HasOne(pjt => pjt.PrintJob)
        .WithMany() // or .WithMany(pj => pj.Tags) if navigation property added
        .HasForeignKey(pjt => pjt.PrintJobId);
    
    modelBuilder.Entity<PrintJobTag>()
        .HasOne(pjt => pjt.Tag)
        .WithMany()
        .HasForeignKey(pjt => pjt.TagId);
}
```

**2. Update `PrintJob` Entity:**
```csharp
// src/infra/Domain/PrintJob.cs

public class PrintJob
{
    // ... existing properties
    
    // Navigation property for tags (EF Core will auto-populate)
    public ICollection<PrintJobTag> JobTags { get; set; } = new List<PrintJobTag>();
}
```

**3. Repository Methods:**
```csharp
// src/infra/Services/Tags/ITagService.cs (or new IPrintJobTagService.cs)

Task AddTagsToJobAsync(Guid jobId, IEnumerable<Guid> tagIds, CancellationToken ct = default);
Task RemoveTagsFromJobAsync(Guid jobId, IEnumerable<Guid> tagIds, CancellationToken ct = default);
Task<IReadOnlyList<TagDto>> GetJobTagsAsync(Guid jobId, CancellationToken ct = default);
Task ReplaceJobTagsAsync(Guid jobId, IEnumerable<Guid> tagIds, CancellationToken ct = default);
```

**4. Update `PrintJobManagementService`:**
```csharp
// src/api/Services/PrintQueue/PrintJobManagementService.cs (line 1815)

if (updates.Tags != null)
{
    // Replace existing tags with new tags
    await _printJobTagService.ReplaceJobTagsAsync(jobId, updates.Tags, ct);
    _logger.LogInformation("Updated tags for job {JobId}", jobId);
}
```

**5. API Endpoints:**
Extend `JobQueueController.cs` or create `PrintJobTagsController.cs`:
- `POST /api/jobs/{id}/tags` — Add tags to a job
- `DELETE /api/jobs/{id}/tags` — Remove tags from a job
- `GET /api/jobs/{id}/tags` — Get all tags for a job
- `PUT /api/jobs/{id}/tags` — Replace all tags for a job

**6. Database Migration:**
```bash
cd src/migrations/Farm.Migrations.Postgres
dotnet ef migrations add AddPrintJobTags --context ApplicationDbContext
```

**7. DTO Updates:**
Ensure `PrintJobDto` and `UpdatePrintJobStatusDto` have a `Tags` property:
```csharp
public List<Guid>? Tags { get; set; } // Tag IDs
```

### Implementation Plan

**Files to Create/Modify:**
1. `src/infra/Domain/PrintJobTag.cs` (new) — Junction table entity
2. `src/infra/Domain/PrintJob.cs` — Add `JobTags` navigation property
3. `src/infra/Data/ApplicationDbContext.cs` — Configure `PrintJobTag` relationship
4. `src/infra/Services/Tags/IPrintJobTagService.cs` (new or extend ITagService) — Interface
5. `src/infra/Services/Tags/PrintJobTagService.cs` (new) — Implementation
6. `src/api/Services/PrintQueue/PrintJobManagementService.cs` — Implement tag update logic
7. `src/api/Controllers/JobQueueController.cs` or new `PrintJobTagsController.cs` — API endpoints
8. Database migration files (Postgres, SQLite, SQL Server, MySQL)
9. `src/infra/Contracts/PrintJob/PrintJobDto.cs` — Add `Tags` property
10. Frontend: Update job queue UI to show/edit tags

**Ordered Steps:**
1. Create `PrintJobTag` entity
2. Update `PrintJob` with navigation property
3. Configure EF Core relationships in `ApplicationDbContext`
4. Create and run database migrations for all providers
5. Implement `PrintJobTagService` with CRUD methods
6. Update `PrintJobManagementService` to call tag service
7. Add API endpoints for tag management
8. Write integration tests for tagging operations
9. Update frontend UI to display and edit job tags

### Dependencies
- `Tag` entity and `TagsController` already exist (functional)
- Junction table requires database migration

### Complexity: **M (Medium)**
- Entity and relationship setup: straightforward
- Service layer: ~100-150 lines
- API endpoints: ~50-75 lines per endpoint
- Frontend: Tag selector component
- Testing: Integration tests for tag CRUD

### Recommended Implementation: **Taylor (Backend) + Jordan (Full-Stack for UI)**
- Taylor: Entity setup, migrations, service layer, API endpoints
- Jordan: Frontend UI for tag selector and job queue tag display

---

## ITEM 5: OrcaSlicer Asset/UI Types (Manifest Parsing)

### Problem Statement
- `OrcaSlicerAssetRegistry.cs:75` — "Parse manifest and populate _assetsCache" is TODO
- `OrcaSlicerUIProvider.cs:26` — "Update to actual OrcaSlicer-specific types" — `ProfileConfigType` and `SettingsType` are `typeof(object)`

Need OrcaSlicer-specific type definitions for:
1. **Profile configuration** — OrcaSlicer `.json` profile format
2. **Settings types** — OrcaSlicer-specific settings (jitter, flow calibration, etc.)
3. **Asset manifest parsing** — JSON manifest listing bed models, textures, cover images

**Why Blocked:** Requires OrcaSlicer documentation/reverse engineering.

### Proposed Architecture

**1. Profile Configuration Type:**
```csharp
// src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/Models/OrcaSlicerProfile.cs

public class OrcaSlicerProfile
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    
    // Print settings
    public double LayerHeight { get; set; }
    public int InfillPercentage { get; set; }
    public int PrintSpeed { get; set; }
    public int TravelSpeed { get; set; }
    
    // Temperature settings
    public int NozzleTemperature { get; set; }
    public int BedTemperature { get; set; }
    
    // OrcaSlicer-specific
    public double? FlowRatio { get; set; }
    public bool EnableJitter { get; set; }
    public string? FlowCalibrationMode { get; set; }
    
    // Supports
    public bool EnableSupports { get; set; }
    public string? SupportPattern { get; set; }
    
    // Material
    public string? Filament { get; set; }
    public string? FilamentType { get; set; }
    
    // Advanced
    public Dictionary<string, object> AdvancedSettings { get; set; } = new();
}
```

**2. Settings Type:**
```csharp
// src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/Models/OrcaSlicerSettings.cs

public class OrcaSlicerSettings
{
    public OrcaSlicerProfile Profile { get; set; } = new();
    public Dictionary<string, string> Overrides { get; set; } = new();
    
    // OrcaSlicer-specific engine settings
    public bool EnablePressureAdvance { get; set; }
    public double PressureAdvanceValue { get; set; }
    public bool EnableArcWelder { get; set; }
}
```

**3. Asset Manifest Schema:**
```json
{
  "version": "2.3.1",
  "assets": [
    {
      "manufacturer": "Voron",
      "model": "Trident",
      "bedModel": "bed-models/Voron/Trident.stl",
      "bedTexture": "bed-textures/Voron/Trident_texture.svg",
      "coverImage": "cover-images/Voron/Trident_cover.png",
      "buildVolume": { "x": 300, "y": 300, "z": 250 }
    },
    {
      "manufacturer": "Prusa",
      "model": "MK4",
      "bedModel": "bed-models/Prusa/MK4.stl",
      "bedTexture": "bed-textures/Prusa/MK4_texture.png",
      "coverImage": "cover-images/Prusa/MK4_cover.png",
      "buildVolume": { "x": 250, "y": 210, "z": 220 }
    }
  ]
}
```

**4. Manifest Parsing Implementation:**
```csharp
// src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/lib/Assets/OrcaSlicerAssetRegistry.cs

private async Task EnsureInitializedAsync(CancellationToken ct = default)
{
    if (_initialized) return;
    _initialized = true;

    var assembly = typeof(OrcaSlicerLibrary_v2_3_1).Assembly;
    const string manifestResource = "OrcaSlicer_v2_3_1_Assets_manifest.json";

    var manifestStream = assembly.GetManifestResourceStream(manifestResource);
    if (manifestStream == null) return; // No manifest embedded yet

    using var reader = new StreamReader(manifestStream);
    string manifestJson = await reader.ReadToEndAsync(ct);
    
    var manifest = JsonSerializer.Deserialize<AssetManifest>(manifestJson);
    if (manifest?.Assets == null) return;

    foreach (var asset in manifest.Assets)
    {
        var key = $"{asset.Manufacturer}:{asset.Model}".ToLowerInvariant();
        _assetsCache[key] = new SlicerAsset
        {
            ManufacturerName = asset.Manufacturer,
            ModelName = asset.Model,
            BedModelPath = asset.BedModel,
            BedTexturePath = asset.BedTexture,
            CoverImagePath = asset.CoverImage,
            BuildVolumeX = asset.BuildVolume?.X ?? 0,
            BuildVolumeY = asset.BuildVolume?.Y ?? 0,
            BuildVolumeZ = asset.BuildVolume?.Z ?? 0
        };
    }
}

private class AssetManifest
{
    public string? Version { get; set; }
    public List<AssetEntry>? Assets { get; set; }
}

private class AssetEntry
{
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? BedModel { get; set; }
    public string? BedTexture { get; set; }
    public string? CoverImage { get; set; }
    public BuildVolumeInfo? BuildVolume { get; set; }
}

private class BuildVolumeInfo
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
```

**5. Update `OrcaSlicerUIProvider`:**
```csharp
// src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/lib/Core/OrcaSlicerUIProvider.cs

public Type ProfileConfigType => typeof(OrcaSlicerProfile);
public Type SettingsType => typeof(OrcaSlicerSettings);
```

### Implementation Plan

**Files to Create/Modify:**
1. `src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/Models/OrcaSlicerProfile.cs` (new) — Profile type
2. `src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/Models/OrcaSlicerSettings.cs` (new) — Settings type
3. `src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/lib/Assets/OrcaSlicerAssetRegistry.cs` — Implement manifest parsing
4. `src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/lib/Core/OrcaSlicerUIProvider.cs` — Update type references
5. Create `manifest.json` file with embedded resource build action
6. Unit tests for manifest parsing

**Ordered Steps:**
1. Research OrcaSlicer profile JSON format (inspect sample profiles)
2. Define `OrcaSlicerProfile` and `OrcaSlicerSettings` types
3. Create `manifest.json` with sample assets (Voron, Prusa, Bambu, etc.)
4. Implement manifest parsing in `OrcaSlicerAssetRegistry`
5. Update `OrcaSlicerUIProvider` with type references
6. Write unit tests for asset retrieval
7. Test with embedded resources
8. Expand manifest with more printer models

### Dependencies
- OrcaSlicer profile format documentation/sample files
- Embedded resource build configuration
- `ISlicerAssetRegistry` interface contract (already defined)

### Complexity: **M (Medium)**
- Profile type definition: Requires reverse engineering OrcaSlicer JSON format
- Manifest parsing: Straightforward JSON deserialization (~50 lines)
- Asset registry: Already scaffolded, just needs implementation
- Testing: Unit tests for parsing and asset retrieval

### Recommended Implementation: **Morgan (Slicer) or Jordan (Full-Stack)**
- Morgan: Slicer domain expert, familiar with OrcaSlicer
- Jordan: Can reverse engineer profile format and create types

---

## Summary Table

| Item | Title | Complexity | Estimated Effort | Owner | Dependencies |
|------|-------|------------|------------------|-------|--------------|
| 1 | Camera Control | M | 2-3 days | Taylor | Backend API research |
| 2 | Slicer Artifacts | L | 4-5 days | Taylor + Morgan | G-code parsing, storage |
| 3 | OpenAPI Migration | M | 2-3 days | Jordan or Taylor | None |
| 4 | Tag Support | M | 3-4 days | Taylor + Jordan | Database migration |
| 5 | OrcaSlicer Types | M | 2-3 days | Morgan or Jordan | OrcaSlicer docs |

**Total Estimated Effort:** 13-18 days across team

---

## Next Steps

1. **Prioritize items** based on user impact and dependencies
2. **Assign owners** per recommendations above
3. **Schedule sprints** to distribute work evenly
4. **Research phase** for Items 1, 2, and 5 (backend APIs, G-code format, OrcaSlicer profiles)
5. **Create tracking issues** in issue tracker for each item

---

**Decision Status:** Awaiting team review and approval.
