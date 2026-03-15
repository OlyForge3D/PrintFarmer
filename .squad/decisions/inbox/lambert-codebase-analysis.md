# Lambert's Codebase Analysis: Blocked/Deferred Items
**Date:** 2025-01-27  
**Analyst:** Lambert (Backend Dev)  
**Purpose:** Deep code-level investigation of 5 blocked items to determine implementation feasibility

---

## Executive Summary

Analyzed codebase to understand current state and implementation requirements for 5 blocked items. Key findings:

- **Camera Control**: Interface exists but no enable/disable concept in printer firmware APIs
- **Slicer Artifacts**: Core upload flow exists; thumbnails tracked in Metadata dictionary
- **OpenAPI Migration**: Already complete — using .NET 10 native `AddOpenApi()`
- **Tag Support**: No database schema exists; needs migration for JSON column or join table
- **OrcaSlicer Types**: Stubs exist but type definitions needed for ProfileConfigType/SettingsType

---

## Item 1: Camera Control

### Current State

**Interface Exists:**
- `ISupportsCamera` in `src/infra/Services/Printers/IBackendClientCapabilities.cs` (lines 180-200)
- Two methods: `GetCameraStreamUrlAsync()` and `GetCameraSnapshotUrlAsync()`
- Used by 4 places in `PrintersService.cs` for camera URL retrieval

**Stub Implementation:**
- `PrintersService.cs` lines 2629-2670: `EnableCameraAsync()` and `DisableCameraAsync()`
- Both return `false` with TODO comments: "Camera enable/disable is not currently supported via capability interfaces"

**Backend Implementations:**

*Moonraker:*
- `IMoonrakerClient` has camera methods (lines 59-100 in `Contracts/IMoonrakerClient.cs`)
- Queries `/server/webcams/list` API to get configured camera URLs
- Discovery probe extracts camera URLs during printer discovery
- **No enable/disable API** — Moonraker only provides URL retrieval

*PrusaLink:*
- `PrusaLinkApiClient.cs` has extensive camera APIs:
  - `GetCameraConfigAsync()` — GET camera configuration
  - `SetupCameraAsync()` — POST camera setup
  - `DeleteCameraAsync()` — DELETE camera
  - `UpdateCameraConfigAsync()` — PATCH camera config
  - `SetCameraOrderAsync()` — PUT camera order
  - `TakeSnapshotAsync()` / `TriggerSnapshotAsync()` — snapshot capture
- **Does support camera configuration** but NOT on/off toggle
- Status client explicitly states "camera URLs are not supported due to encoding issues"

### Gap Analysis

**Missing Pieces:**
1. **No firmware concept** of enable/disable exists in Moonraker or PrusaLink
2. Camera control in these firmwares is about **configuration**, not power state
3. `ISupportsCamera` would need a new method: `bool SupportsCameraToggle { get; }`
4. Each backend needs to report capability (PrusaLink: maybe, Moonraker: no)

**What Actually Exists:**
- Camera **discovery** (URLs during printer setup)
- Camera **snapshot capture** (both backends)
- Camera **configuration** (PrusaLink only)
- Camera **streaming** (URL retrieval for MJPEG streams)

### Pattern to Follow

If implementing camera toggle:

1. **Add new capability interface:**
```csharp
public interface ISupportsCameraControl : ISupportsCamera
{
    Task<bool> EnableCameraAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct);
    Task<bool> DisableCameraAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct);
}
```

2. **Update `BackendCapabilityFactory.cs`:**
   - Add `ISupportsCameraControl` to capability map (line 33)
   - Add `TryGetCameraControlClientTyped()` method (similar to line 308)

3. **Implement in backends:**
   - PrusaLink: Use existing camera config APIs
   - Moonraker: Return false (not supported)
   - OctoPrint: Check if `/api/settings` supports camera control

4. **Update `PrintersService.cs`:**
   - Replace stub with actual capability check: `_capabilityFactory.TryGetCameraControlClientTyped()`

### Migration Needs

**None** — This is a pure code change, no database schema impact.

### Risk Factors

1. **Firmware limitations**: Moonraker doesn't support camera enable/disable at all
2. **User expectations**: Feature might not work on majority of printers (Klipper/Moonraker is dominant)
3. **PrusaLink encoding issues**: Comments indicate camera URLs are problematic
4. **External camera systems**: Many users run cameras independently (e.g., mjpg-streamer, uv4l)
5. **Low value/high effort**: Camera control is typically done outside PrintFarmer

**Recommendation:** **Defer indefinitely** or close as "won't fix." The printer firmware APIs don't meaningfully support this operation, and external camera systems (the most common setup) can't be controlled via PrintFarmer anyway.

---

## Item 2: Slicer Artifact Uploads

### Current State

**Upload Flow Exists:**
- `src/worker-shared/HttpJobPollerService.cs` lines 280-345: `UploadArtifactsAsync()`
- Currently uploads primary G-code file via multipart form POST to `/api/artifacts`
- Line 340 has TODO: "Upload additional artifacts (thumbnails, metadata, etc.) if present in result.Metadata"

**SlicingResult Structure:**
- Defined in `src/slicer/Farm.Slicer.Module/Models/SlicerModels.cs` lines 311-332
- Properties:
  - `Uri? ResultFileUrl` — G-code file path
  - `Dictionary<string, string> Metadata` — **This is where thumbnails live**
  - `string? Output` / `string? Error` — console output
  - `EstimatedPrintTimeSeconds`, `EstimatedFilamentUsageGrams`, `LayerCount`

**Metadata Dictionary:**
The slicer workers populate `Metadata` with:
- Thumbnail paths (e.g., `"thumbnail_32x32": "/path/to/thumb_32x32.png"`)
- Slicer-specific settings (jitter, retraction, etc.)
- G-code metadata extracted from comments (layer height, infill, etc.)

**Artifacts Endpoint:**
- **Does not exist** — `grep` found no artifact controller in `src/api/Controllers`
- Worker sends multipart form to `/api/artifacts` but this route isn't defined
- This means the upload flow is **incomplete** — no receiver exists

### Gap Analysis

**Missing Pieces:**
1. **No `/api/artifacts` controller** exists to receive uploads
2. **No `Artifact` entity** in `src/infra/Domain` to store artifact records
3. **No artifact storage strategy** (filesystem vs. blob storage vs. database)
4. **No artifact-to-job relationship** tracking
5. Metadata dictionary format is undefined (how are thumbnail keys named?)

**What Actually Exists:**
- Worker-side upload logic (sender)
- SlicingResult Metadata dictionary (source of artifact paths)
- Multipart form construction code

### Pattern to Follow

Following existing codebase patterns:

**1. Create Entity (`src/infra/Domain/Artifact.cs`):**
```csharp
public class Artifact
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }  // Foreign key to SlicingJob or PrintJob
    public string Kind { get; set; } = string.Empty; // "gcode", "thumbnail_32x32", "metadata"
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string StoragePath { get; set; } = string.Empty; // Relative to artifacts root
    public string? MimeType { get; set; }
    public DateTime UploadedAt { get; set; }
    public Guid WorkerId { get; set; }
    
    // Navigation property
    public DistributedSlicingJob? SlicingJob { get; set; }
}
```

**2. Create Controller (`src/api/Controllers/ArtifactsController.cs`):**
```csharp
[ApiController]
[Route("api/[controller]")]
public class ArtifactsController : ControllerBase
{
    [HttpPost]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadArtifact(
        [FromForm] Guid jobId,
        [FromForm] string kind,
        [FromForm] Guid workerId,
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        // Save file to artifacts directory
        // Create Artifact entity
        // Return ArtifactResponse with ID
    }
}
```

**3. Storage Strategy:**
Follow `GcodeLibraryController.cs` pattern (existing file upload endpoint):
- Store files in `{StorageRoot}/artifacts/{jobId}/{kind}/{filename}`
- Use `IStoragePathResolver` to get base path
- Use `IWebHostEnvironment.ContentRootPath` or configuration setting

**4. Update Worker Upload Logic:**
In `HttpJobPollerService.cs` line 340, expand to:
```csharp
// Upload thumbnails if present in metadata
foreach (var kv in result.Metadata.Where(m => m.Key.StartsWith("thumbnail_")))
{
    string thumbnailPath = kv.Value;
    if (File.Exists(thumbnailPath))
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(job.Id.ToString()), "jobId");
        content.Add(new StringContent(kv.Key), "kind"); // e.g., "thumbnail_32x32"
        content.Add(new StringContent(_workerId.ToString()), "workerId");
        
        byte[] thumbBytes = await File.ReadAllBytesAsync(thumbnailPath, ct);
        content.Add(new ByteArrayContent(thumbBytes), "file", Path.GetFileName(thumbnailPath));
        
        var thumbResponse = await httpClient.PostAsync("/api/artifacts", content, ct);
        // Store artifact ID...
    }
}
```

### Migration Needs

**Add `Artifacts` table:**
```sql
CREATE TABLE Artifacts (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    JobId UNIQUEIDENTIFIER NOT NULL,
    Kind NVARCHAR(100) NOT NULL,
    FileName NVARCHAR(255) NOT NULL,
    FileSizeBytes BIGINT NOT NULL,
    StoragePath NVARCHAR(500) NOT NULL,
    MimeType NVARCHAR(100),
    UploadedAt DATETIME NOT NULL,
    WorkerId UNIQUEIDENTIFIER NOT NULL,
    FOREIGN KEY (JobId) REFERENCES DistributedSlicingJobs(Id)
);
CREATE INDEX IX_Artifacts_JobId ON Artifacts(JobId);
```

**EF Core migration:**
```bash
cd /Users/jpapiez/s/PFarm1/src
DB_PROVIDER=postgres dotnet ef migrations add AddArtifactSupport \
  --project ./migrations/Farm.Migrations.Postgres \
  --startup-project ./api
```

### Risk Factors

1. **Storage growth**: Thumbnails multiply storage requirements (3-5 sizes per job)
2. **Orphaned files**: If uploads fail mid-stream, cleanup is needed
3. **Thumbnail formats**: Slicer engines produce different formats (PNG vs. JPG vs. WebP)
4. **Metadata key naming**: No standard exists for `Metadata` dictionary keys
5. **Artifact retention**: No policy exists for cleaning up old artifacts

**Recommendation:** **Implement in Phase 3E** after job queue stabilizes. Start with G-code only, add thumbnails in follow-up iteration.

---

## Item 3: OpenAPI Migration

### Current State

**Already Complete!**

**Program.cs configuration (line 186):**
```csharp
// .NET 10 native OpenAPI - auto-detects JWT Bearer security from authentication configuration
builder.Services.AddOpenApi();
```

**Endpoint mapping (line 359):**
```csharp
_ = app.MapOpenApi();
// Native ASP.NET Core OpenAPI automatically exposes at /openapi/v1.json
```

**ExampleSchemaFilter.cs status:**
- File exists at `src/api/Infrastructure/Swagger/ExampleSchemaFilter.cs`
- All OpenAPI example code is **commented out** with "TODO: Update to use new API"
- Header comment (lines 11-15):
  > Using .NET 10 native ASP.NET Core OpenAPI with Document/Operation Transformers.
  > Custom examples can be added via OpenApiOperation.Examples in Program.cs transformers.
- Contains 11 DTOs with example schemas (all commented out)

**Old Swashbuckle removed:**
- No references to `Swashbuckle.AspNetCore` in Program.cs
- No `AddSwaggerGen()` call
- No `SwaggerDoc()` configuration

### Gap Analysis

**Missing Pieces:**
1. **Example schemas not re-implemented** — All DTO examples commented out
2. **No document transformers** in Program.cs to add examples via new API
3. **No operation transformers** for endpoint-specific metadata

**What Actually Exists:**
- .NET 10 native OpenAPI fully configured
- OpenAPI JSON exposed at `/openapi/v1.json`
- JWT Bearer security auto-detected and included
- All DTOs already have XML doc comments for descriptions

### Pattern to Follow

**.NET 10 OpenAPI document transformers** (add to Program.cs after line 186):

```csharp
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info = new()
        {
            Title = "PrintFarmer API",
            Version = "v1",
            Description = "3D print farm management system",
            Contact = new() { Name = "PrintFarmer", Url = new Uri("https://github.com/jpapiz/printfarmer") }
        };
        return Task.CompletedTask;
    });

    options.AddOperationTransformer((operation, context, ct) =>
    {
        // Add examples for specific DTOs
        // This is where ExampleSchemaFilter logic goes
        return Task.CompletedTask;
    });
});
```

**Example DTO annotation** (replace ExampleSchemaFilter with attributes):

.NET 10 approach uses standard OpenAPI attributes:
```csharp
/// <summary>
/// Create a new print job
/// </summary>
[OpenApiOperation(summary: "Create print job", tags: ["Jobs"])]
[OpenApiExample<CreatePrintJobDto>(new CreatePrintJobDto 
{ 
    Name = "Calibration Cube",
    Priority = 1,
    GcodeFileId = Guid.Parse("99999999-8888-7777-6666-555555555555")
})]
public async Task<IActionResult> CreateJob([FromBody] CreatePrintJobDto dto) { }
```

However, **this is optional** — XML doc comments on DTOs already provide descriptions in OpenAPI output.

### Migration Needs

**None** — Already using .NET 10 native OpenAPI.

**Optional enhancement:**
Delete `ExampleSchemaFilter.cs` entirely since it's fully commented out and unused.

### Risk Factors

**None** — This item is already complete. The TODO comments are misleading.

**Recommendation:** **Close as complete**. Optionally delete dead code in `ExampleSchemaFilter.cs`.

---

## Item 4: Tag Support

### Current State

**Stub in UpdateJobDetailsAsync:**
- `src/api/Services/PrintQueue/PrintJobManagementService.cs` line 1817:
```csharp
// Handle tags (future phase enhancement)
if (updates.Tags != null)
{
    // TODO: Implement tag support in Phase 3D
    _logger.LogDebug("Tags update requested but not yet implemented for job {JobId}", jobId);
}
```

**PrintJob Entity:**
- File: `src/infra/Domain/PrintJob.cs`
- Contains 214 lines of properties and relationships
- **No `Tags` property exists** in the entity
- Has similar features:
  - `RequiredCapabilities` (line 46): `string[]?` — JSON array
  - `PreferredPrinterIds` (line 75): `Guid[]?` — JSON array
  - `ExcludedPrinterIds` (line 77): `Guid[]?` — JSON array

**No Shared DTO:**
The `grep` for DTOs in `/src/shared` returned empty — **shared folder only has build artifacts**, no source files.

**DTO Pattern:**
DTOs are defined inline in controllers or service layer, not in a shared project. Check `CreatePrintJobDto` in controller for pattern.

### Gap Analysis

**Missing Pieces:**
1. **No `Tags` property** in PrintJob entity
2. **No database column** for tags
3. **No Tag entity** for join table approach (if needed)
4. **No DTOs defined** for tag CRUD operations
5. No validation for tag format/length

**What Actually Exists:**
- Similar array properties (`RequiredCapabilities`, `PreferredPrinterIds`) show the pattern
- Stub code in service layer ready to be filled in
- Logging already tracks tag update attempts

### Pattern to Follow

**Option 1: JSON Array Column (Simplest)**

Following the existing `RequiredCapabilities` pattern:

**1. Add property to PrintJob.cs (after line 78):**
```csharp
/// <summary>
/// User-defined tags for organizing and filtering jobs.
/// Maximum 20 tags, each up to 50 characters.
/// </summary>
public string[]? Tags { get; set; } // JSON array
```

**2. Update UpdateJobDetailsDto** (wherever it's defined):
```csharp
public class UpdateJobDetailsDto
{
    // Existing properties...
    
    /// <summary>
    /// Optional tags for job organization (max 20 tags, 50 chars each)
    /// </summary>
    public string[]? Tags { get; set; }
}
```

**3. Update PrintJobManagementService.cs (line 1817):**
```csharp
if (updates.Tags != null)
{
    // Validate tags
    if (updates.Tags.Length > 20)
    {
        throw new ArgumentException("Maximum 20 tags allowed", nameof(updates));
    }
    
    if (updates.Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 50))
    {
        throw new ArgumentException("Tags must be 1-50 characters", nameof(updates));
    }
    
    job.Tags = updates.Tags;
}
```

**Option 2: Join Table (More Complex)**

Only needed if tags need:
- Autocomplete/suggestions across jobs
- Tag usage statistics
- Tag renaming/merging
- Tag-level permissions

Schema:
```csharp
public class JobTag
{
    public Guid JobId { get; set; }
    public string Tag { get; set; } = string.Empty;
    public PrintJob Job { get; set; } = null!;
}
```

**Recommendation:** Use Option 1 (JSON array) for simplicity. Only switch to join table if tag analytics are needed.

### Migration Needs

**Add Tags column to PrintJobs table:**

PostgreSQL:
```sql
ALTER TABLE "PrintJobs" ADD COLUMN "Tags" TEXT[] NULL;
CREATE INDEX IX_PrintJobs_Tags ON "PrintJobs" USING GIN("Tags");
```

SQL Server:
```sql
ALTER TABLE [PrintJobs] ADD [Tags] NVARCHAR(MAX) NULL;
-- JSON array stored as string; index created on computed column if needed
```

**EF Core migration:**
```bash
cd /Users/jpapiez/s/PFarm1/src
DB_PROVIDER=postgres dotnet ef migrations add AddTagsToJobs \
  --project ./migrations/Farm.Migrations.Postgres \
  --startup-project ./api

DB_PROVIDER=sqlserver dotnet ef migrations add AddTagsToJobs \
  --project ./migrations/Farm.Migrations.SqlServer \
  --startup-project ./api
```

**Configuration in `PrintFarmerDbContext.cs`:**
```csharp
modelBuilder.Entity<PrintJob>(entity =>
{
    // Existing configuration...
    
    entity.Property(e => e.Tags)
        .HasConversion(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null))
        .HasColumnType("jsonb") // PostgreSQL
        .HasColumnName("Tags");
});
```

### Risk Factors

1. **Tag bloat**: No limit enforced on total tags across system
2. **Tag naming conventions**: No validation on format (alphanumeric? special chars?)
3. **Case sensitivity**: "bug" vs. "Bug" vs. "BUG" — are these same or different?
4. **Tag migration**: If users have external tag systems, no import strategy
5. **Search performance**: PostgreSQL GIN index required for array searching

**Recommendation:** **Implement in Phase 3D** as planned. Use JSON array for simplicity.

---

## Item 5: OrcaSlicer Types

### Current State

**Directory Structure:**
```
src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/
├── lib/
│   ├── Assets/
│   │   ├── OrcaSlicerAssetRegistry.cs
│   │   └── manifest.json
│   └── Core/
│       ├── OrcaSlicerUIProvider.cs
│       ├── OrcaSlicerLibrary.cs
│       └── NullProfilesProvider.cs
└── Farm.Slicers.OrcaSlicer.v2_3_1.csproj
```

**OrcaSlicerUIProvider.cs (lines 27-29):**
```csharp
// TODO: Update these to actual OrcaSlicer-specific types when available
public Type ProfileConfigType => typeof(object);
public Type SettingsType => typeof(object);
```

**ISlicerUIProvider Interface:**
From `Farm.Slicer.Module.Contracts.Libraries`:
```csharp
public interface ISlicerUIProvider
{
    string SlicerName { get; }
    string SlicerVersion { get; }
    bool HasBundleSupport { get; }
    bool HasAssetCustomization { get; }
    bool HasEngineSpecificSettings { get; }
    Type ProfileConfigType { get; }  // Currently typeof(object)
    Type SettingsType { get; }        // Currently typeof(object)
    string GetDescription();
}
```

**Asset Registry:**
- `OrcaSlicerAssetRegistry.cs` implements `ISlicerAssetRegistry`
- Has methods: `GetAssetAsync()`, `ListAssetsAsync()`, `GetBedModelStream()`, etc.
- TODO at line 75: "Parse manifest and populate _assetsCache"
- Manifest file (`manifest.json`) is empty: `{ "version": "1.0.0", "assets": [] }`

### Gap Analysis

**Missing Pieces:**
1. **No concrete types** for `ProfileConfigType` and `SettingsType`
2. **No OrcaSlicer profile schema** defined (layer height, infill, supports, etc.)
3. **No OrcaSlicer settings schema** (jitter, retraction, acceleration, etc.)
4. **No manifest parsing logic** — asset registry is stubbed
5. **No asset definitions** in manifest.json

**What Actually Exists:**
- Interface definitions and plugin structure
- Stub implementations returning `typeof(object)`
- Asset registry with stream access methods (GetBedModelStream, etc.)
- Empty manifest file ready to be populated

### Pattern to Follow

**1. Define Profile Type (`lib/Models/OrcaSlicerProfile.cs`):**

```csharp
namespace Farm.Slicers.OrcaSlicer.v2_3_1.Models;

/// <summary>
/// OrcaSlicer v2.3.1 profile configuration.
/// Maps to OrcaSlicer's .json profile format.
/// </summary>
public class OrcaSlicerProfile
{
    // Print Settings
    public double LayerHeight { get; set; } = 0.2;
    public double FirstLayerHeight { get; set; } = 0.2;
    public int InfillPercentage { get; set; } = 15;
    public string InfillPattern { get; set; } = "grid";
    public int TopSolidLayers { get; set; } = 5;
    public int BottomSolidLayers { get; set; } = 5;
    
    // Speed Settings
    public int PrintSpeed { get; set; } = 80;
    public int FirstLayerSpeed { get; set; } = 30;
    public int InfillSpeed { get; set; } = 80;
    public int TravelSpeed { get; set; } = 150;
    
    // Temperature
    public int NozzleTemperature { get; set; } = 210;
    public int BedTemperature { get; set; } = 60;
    public int FirstLayerNozzleTemp { get; set; } = 215;
    public int FirstLayerBedTemp { get; set; } = 65;
    
    // Material
    public string FilamentType { get; set; } = "PLA";
    
    // Support Settings
    public bool EnableSupports { get; set; } = false;
    public string SupportPattern { get; set; } = "rectilinear";
    public int SupportAngle { get; set; } = 45;
    
    // Advanced
    public double SeamPosition { get; set; } = 0.0; // 0 = nearest, 1 = random
    public string GCodeFlavor { get; set; } = "marlin";
}
```

**2. Define Settings Type (`lib/Models/OrcaSlicerSettings.cs`):**

```csharp
namespace Farm.Slicers.OrcaSlicer.v2_3_1.Models;

/// <summary>
/// OrcaSlicer v2.3.1 engine-specific settings.
/// These are advanced tuning parameters beyond standard profiles.
/// </summary>
public class OrcaSlicerSettings
{
    // Retraction
    public double RetractionLength { get; set; } = 0.8;
    public double RetractionSpeed { get; set; } = 40;
    public double RetractionZHop { get; set; } = 0.0;
    
    // Acceleration
    public int DefaultAcceleration { get; set; } = 500;
    public int InfillAcceleration { get; set; } = 1000;
    public int TravelAcceleration { get; set; } = 1000;
    
    // Jitter (OrcaSlicer-specific)
    public double JitterAmount { get; set; } = 0.05;
    public bool EnableJitter { get; set; } = false;
    
    // Arc Welder
    public bool EnableArcWelder { get; set; } = false;
    public double ArcPrecision { get; set; } = 0.1;
    
    // Quality
    public bool EnablePressureAdvance { get; set; } = false;
    public double PressureAdvanceValue { get; set; } = 0.05;
    
    // Extras
    public bool EnableIroningLayerTime { get; set; } = false;
    public int MinimalLayerTime { get; set; } = 5;
}
```

**3. Update OrcaSlicerUIProvider.cs:**

```csharp
public Type ProfileConfigType => typeof(OrcaSlicerProfile);
public Type SettingsType => typeof(OrcaSlicerSettings);
```

**4. Populate manifest.json:**

```json
{
  "version": "1.0.0",
  "assets": [
    {
      "manufacturer": "Voron",
      "model": "Trident",
      "bedModel": "voron_trident_bed.stl",
      "bedTexture": "voron_trident_texture.svg",
      "coverImage": "voron_trident_cover.png"
    },
    {
      "manufacturer": "Prusa",
      "model": "MK3S",
      "bedModel": "prusa_mk3s_bed.stl",
      "bedTexture": "prusa_mk3s_texture.png",
      "coverImage": "prusa_mk3s_cover.png"
    }
  ]
}
```

**5. Implement manifest parsing (OrcaSlicerAssetRegistry.cs line 75):**

```csharp
private async Task EnsureInitializedAsync(CancellationToken ct = default)
{
    if (_initialized) return;
    _initialized = true;

    var assembly = typeof(OrcaSlicerLibrary_v2_3_1).Assembly;
    const string manifestResource = "OrcaSlicer_v2_3_1_Assets_manifest.json";
    
    var manifestStream = assembly.GetManifestResourceStream(manifestResource);
    if (manifestStream == null) return;
    
    var manifest = await JsonSerializer.DeserializeAsync<ManifestJson>(manifestStream, ct);
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
            CoverImagePath = asset.CoverImage
        };
    }
}

private class ManifestJson
{
    public string Version { get; set; } = string.Empty;
    public List<AssetEntry> Assets { get; set; } = [];
}

private class AssetEntry
{
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string BedModel { get; set; } = string.Empty;
    public string BedTexture { get; set; } = string.Empty;
    public string CoverImage { get; set; } = string.Empty;
}
```

### Migration Needs

**None** — This is purely code-level changes in the slicer plugin.

### Risk Factors

1. **Profile format mismatch**: OrcaSlicer's actual .json profile format might differ from defined types
2. **Version incompatibility**: OrcaSlicer 2.3.1 settings might not match newer/older versions
3. **Asset embedding**: Bed models/textures need to be embedded as resources in assembly
4. **Manifest maintenance**: Adding new printer models requires manual manifest updates
5. **Type validation**: No runtime validation that profile/settings types match expected schema

**Recommendation:** **Implement in Phase 3E** after slicer integration is stable. Reference actual OrcaSlicer profile JSON files from documentation for accurate schema.

---

## Summary of Recommendations

| Item | Status | Recommendation | Priority | Effort |
|------|--------|----------------|----------|--------|
| **Camera Control** | Firmware doesn't support | **Defer indefinitely** or close as won't-fix | Low | High |
| **Slicer Artifacts** | Core flow incomplete | **Implement in Phase 3E** (G-code first, thumbnails later) | Medium | Medium |
| **OpenAPI Migration** | Already complete | **Close as complete** (optionally delete dead code) | N/A | None |
| **Tag Support** | Schema missing | **Implement in Phase 3D** (JSON array approach) | High | Low |
| **OrcaSlicer Types** | Stubs need types | **Implement in Phase 3E** (define profile/settings types) | Medium | Medium |

---

## Next Steps

1. **Close Item 3** (OpenAPI) — Already using .NET 10 native API
2. **Defer Item 1** (Camera) — Firmware APIs don't support enable/disable
3. **Prioritize Item 4** (Tags) — Simplest to implement, high user value
4. **Queue Items 2 & 5** for Phase 3E — After job queue stabilizes

**Questions for Team:**
- Should camera control be closed entirely, or kept as "nice-to-have for external cameras"?
- Is tag autocomplete/analytics needed (join table) or is filtering sufficient (JSON array)?
- What's the timeline for Phase 3E when artifact uploads can be completed?

---

**Analysis completed:** 2025-01-27  
**Files analyzed:** 23 source files across 4 backend plugins, slicer module, API layer, and infrastructure
