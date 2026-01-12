# Artifact System Enhancements

## Overview

The PrintFarmer artifact system has been enhanced with three major features:
1. **Storage Alert Thresholds** - Proactive monitoring and alerting for storage limits
2. **Static File Serving** - Optional direct HTTP access to artifacts without API authentication
3. **Bulk Upload Operations** - Efficient multi-file upload in a single request

## Feature 1: Storage Alert Thresholds

### Configuration

Add to `appsettings.json`:

```json
{
  "ArtifactStorage": {
    "EnableStorageAlerts": true,
    "StorageWarningThresholdBytes": 5368709120,
    "StorageCriticalThresholdBytes": 10737418240
  }
}
```

**Defaults:**
- Warning: 5 GB (5,368,709,120 bytes)
- Critical: 10 GB (10,737,418,240 bytes)

### Behavior

When artifact storage exceeds configured thresholds:

1. **Warning Level** (5GB default)
   - Event logged at WARNING level
   - Metrics gauge `printfarmer.artifacts.storage_threshold_state` = 1
   - Operators notified via application logs

2. **Critical Level** (10GB default)
   - Event logged at WARNING level with CRITICAL tag
   - Metrics gauge `printfarmer.artifacts.storage_threshold_state` = 2
   - Indicates immediate action required

### Monitoring

**Log Output Example:**
```
[ArtifactStorage] WARNING threshold exceeded: 5.23 GB (Warning: 5.00 GB, Critical: 10.00 GB)
```

**OpenTelemetry Metrics:**
- `printfarmer.artifacts.storage_total_bytes` - Current total storage
- `printfarmer.artifacts.storage_threshold_state` - 0=normal, 1=warning, 2=critical

### Disabling Alerts

Set `EnableStorageAlerts: false` to disable threshold monitoring:

```json
{
  "ArtifactStorage": {
    "EnableStorageAlerts": false
  }
}
```

## Feature 2: Static File Serving

### Configuration

Enable static serving for direct artifact access:

```json
{
  "ArtifactStorage": {
    "RootPath": "artifacts",
    "EnableStaticServing": true
  }
}
```

### Behavior

When enabled, artifacts are served at `/artifacts/{relativePath}`:

- **URL Pattern**: `http://localhost:5245/artifacts/{jobId}/{kind}/{filename}`
- **Caching**: 1 hour cache (artifacts are immutable)
- **Content-Type**: Determined from file extension or default to `application/octet-stream`
- **No Authentication**: Files are publicly accessible

### Security Considerations

⚠️ **WARNING**: Static serving bypasses API authentication. Only enable if:
- Artifacts do not contain sensitive information
- Network access is restricted (internal network, VPN, etc.)
- You understand the security implications

For secure access, use the API download endpoint instead:
- **API Endpoint**: `GET /api/artifacts/{id}/download`
- Requires authentication
- Always available regardless of static serving setting

### ArtifactDto URLs

The `ArtifactDto` response includes both URLs:

```json
{
  "downloadUrl": "/api/artifacts/3fa85f64-5717-4562-b3fc-2c963f66afa6/download",
  "publicUrl": "/artifacts/job-123/gcode/model.gcode"
}
```

- `downloadUrl`: Always available, requires auth
- `publicUrl`: Only present when `EnableStaticServing: true`

## Feature 3: Bulk Upload Operations

### Endpoint

```
POST /api/artifacts/bulk
Content-Type: multipart/form-data
```

### Request Parameters

- `jobId` (Guid, required): Slice job ID
- `workerId` (Guid, optional): Worker that generated artifacts
- `files` (IFormFileCollection, required): Array of files to upload

### Kind Inference

The bulk upload automatically infers artifact kind from:

1. **Content-Type** (preferred):
   - `application/x-gcode` → "gcode"
   - `image/*` → "thumbnail"
   - `text/plain` → "log"

2. **File Extension** (fallback):
   - `.gcode`, `.g`, `.nc` → "gcode"
   - `.png`, `.jpg`, `.jpeg`, `.webp`, `.gif` → "thumbnail"
   - `.log`, `.txt` → "log"

### Example Request

**Using curl:**
```bash
curl -X POST http://localhost:5245/api/artifacts/bulk \
  -F "jobId=3fa85f64-5717-4562-b3fc-2c963f66afa6" \
  -F "workerId=7c9e6679-7425-40de-944b-e07fc1f90ae7" \
  -F "files=@output.gcode" \
  -F "files=@preview.png" \
  -F "files=@slicer.log"
```

**Using JavaScript:**
```javascript
const formData = new FormData();
formData.append('jobId', '3fa85f64-5717-4562-b3fc-2c963f66afa6');
formData.append('workerId', '7c9e6679-7425-40de-944b-e07fc1f90ae7');
formData.append('files', gcodeFile);
formData.append('files', thumbnailFile);
formData.append('files', logFile);

const response = await fetch('/api/artifacts/bulk', {
  method: 'POST',
  body: formData
});

const artifacts = await response.json();
```

### Response

**Success (200 OK):**
```json
[
  {
    "id": "artifact-id-1",
    "jobId": "job-id",
    "kind": "gcode",
    "fileName": "output.gcode",
    "sizeBytes": 1024000,
    "downloadUrl": "/api/artifacts/artifact-id-1/download",
    "publicUrl": null
  },
  {
    "id": "artifact-id-2",
    "jobId": "job-id",
    "kind": "thumbnail",
    "fileName": "preview.png",
    "sizeBytes": 50000,
    "downloadUrl": "/api/artifacts/artifact-id-2/download",
    "publicUrl": null
  }
]
```

**Error (400 Bad Request):**
```json
{
  "error": "unsupported artifact kind 'unknown' for file 'badfile.xyz'",
  "allowedKinds": ["gcode", "thumbnail", "preview", "log"]
}
```

### Advantages

✅ **Performance**: Single request vs. multiple individual uploads  
✅ **Atomicity**: All files uploaded or none (transaction-like behavior)  
✅ **Convenience**: Automatic kind inference from file metadata  
✅ **Validation**: Pre-flight validation before processing any files

### Limits

- **Request Size**: 500 MB (configurable via `RequestSizeLimit` attribute)
- **Individual File Size**: 100 MB (configured in `ArtifactStorage.MaxFileSizeBytes`)
- **Allowed Kinds**: Configurable via `ArtifactStorage.AllowedKinds`

## Complete Configuration Reference

```json
{
  "ArtifactStorage": {
    "RootPath": "artifacts",
    "MaxFileSizeBytes": 104857600,
    "AllowedKinds": "gcode,thumbnail,preview,log",
    "EnableStaticServing": false,
    "StorageWarningThresholdBytes": 5368709120,
    "StorageCriticalThresholdBytes": 10737418240,
    "EnableStorageAlerts": true
  }
}
```

### Configuration Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RootPath` | string | "artifacts" | Storage directory (absolute or relative to ContentRoot) |
| `MaxFileSizeBytes` | long | 104857600 | Max file size (100 MB) |
| `AllowedKinds` | string | "gcode,thumbnail,preview,log" | Comma-separated list of allowed kinds |
| `EnableStaticServing` | bool | false | Enable static file access at /artifacts/* |
| `StorageWarningThresholdBytes` | long | 5368709120 | Warning threshold (5 GB) |
| `StorageCriticalThresholdBytes` | long | 10737418240 | Critical threshold (10 GB) |
| `EnableStorageAlerts` | bool | true | Enable threshold monitoring |

## Metrics & Observability

### OpenTelemetry Metrics

All metrics use meter name `PrintFarmer.Artifacts`:

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `printfarmer.artifacts.uploaded_total` | Counter | count | Total uploads |
| `printfarmer.artifacts.upload_bytes` | Histogram | bytes | Upload size distribution |
| `printfarmer.artifacts.storage_total_bytes` | Gauge | bytes | Current total storage |
| `printfarmer.artifacts.storage_threshold_state` | Gauge | enum | 0=normal, 1=warning, 2=critical |

### Logging

Threshold events are logged with structured data:

```
[ArtifactStorage] {Level} threshold exceeded: {CurrentGB:F2} GB (Warning: {WarningGB:F2} GB, Critical: {CriticalGB:F2} GB)
```

## Migration Guide

### Enabling Features

No database migrations required. Features are opt-in via configuration:

1. **Add Configuration** to `appsettings.json`
2. **Restart Application** to apply settings
3. **Monitor Logs** for threshold events (if enabled)
4. **Update Clients** to use bulk upload or static URLs (optional)

### Worker Integration

Workers can now use bulk upload for efficiency:

**Before (multiple requests):**
```csharp
await UploadArtifact(gcodeFile);
await UploadArtifact(thumbnailFile);
await UploadArtifact(logFile);
```

**After (single request):**
```csharp
await BulkUploadArtifacts(new[] { gcodeFile, thumbnailFile, logFile });
```

## Testing

All features include comprehensive test coverage:

### Threshold Tests
- `Warning_Threshold_Event_Fires_When_Exceeded`
- `Critical_Threshold_Event_Fires_When_Exceeded`
- `Multiple_Uploads_Trigger_Warning_Only_Once`
- `Threshold_State_Gauge_Reflects_Current_State`
- `No_Events_When_Thresholds_Not_Configured`

### Bulk Upload Tests
- `Bulk_Upload_Multiple_Artifacts_Succeeds`
- `Bulk_Upload_With_No_Files_Returns_BadRequest`
- `Bulk_Upload_Infers_Kind_From_Extension`

Run tests:
```bash
cd src
dotnet test --filter "FullyQualifiedName~Artifacts"
```

## Troubleshooting

### Static Files Not Serving

**Symptom**: 404 errors when accessing `/artifacts/*` URLs

**Solutions:**
1. Verify `EnableStaticServing: true` in configuration
2. Check artifact directory exists: `{ContentRoot}/artifacts`
3. Restart application after config changes
4. Check logs for "Artifact static serving enabled" message

### Threshold Alerts Not Firing

**Symptom**: No warning logs despite high storage usage

**Solutions:**
1. Verify `EnableStorageAlerts: true`
2. Check threshold values are not zero
3. Ensure storage actually exceeds threshold
4. Metrics are session-based; restart resets counter

### Bulk Upload Fails with 413

**Symptom**: Request Entity Too Large error

**Solutions:**
1. Check total upload size < 500 MB
2. Individual files < `MaxFileSizeBytes` (100 MB default)
3. Adjust `RequestSizeLimit` attribute if needed
4. Consider uploading in smaller batches

## Performance Impact

### Storage Thresholds
- **CPU**: Negligible (simple integer comparison per upload)
- **Memory**: ~40 bytes (threshold values + state)
- **I/O**: None (no disk operations)

### Static File Serving
- **CPU**: Lower than API endpoint (no auth/DB checks)
- **Memory**: Minimal (ASP.NET Core static file middleware)
- **I/O**: Direct file system reads (same as API endpoint)

### Bulk Upload
- **CPU**: Slightly higher (processes multiple files)
- **Memory**: Files processed sequentially (no memory spike)
- **I/O**: Same total I/O as individual uploads
- **Network**: Significant reduction (single HTTP request)

## Future Enhancements

Potential future additions:

1. **Automatic Cleanup**: Delete old artifacts when approaching thresholds
2. **Compression**: Automatic gzip compression for text artifacts
3. **Retention Policies**: Age-based artifact deletion
4. **External Storage**: S3/Azure Blob integration
5. **Bandwidth Throttling**: Rate limiting for static file downloads
6. **Metrics Export**: Prometheus/Grafana dashboard templates
